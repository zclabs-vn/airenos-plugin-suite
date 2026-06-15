using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Teigha.DatabaseServices;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin.Communicator
{
    /// <summary>
    /// Async HTTP POST to the AirenoOS MCP endpoint.
    /// Rules (per Canonical Schema v0.2 §HTTP):
    ///   - Bearer auth, JSON body, 30s timeout, retry once.
    ///   - Offline: persist payload to %TEMP%\AirenoOS\pending\*.json — replayed by RetryPending().
    ///   - Never throws — failures are swallowed so BricsCAD save can never crash.
    /// </summary>
    internal static class HttpSender
    {
        static HttpSender()
        {
            // BricsCAD V26 runs on .NET 8 where TLS defaults are fine — but a static ctor
            // here keeps parity with the AutoCAD plugin's net48 path and guards against
            // any future downgrade. Safe no-op on .NET 8.
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            }
            catch { }
        }

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            // Canonical Schema v0.2 expects the full envelope shape: null-valued fields
            // (e.g. ifc_class for 2D CAD) must serialize as explicit JSON null, not omitted.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            // Round every numeric dimension to 2 decimals — CAD positions, bounding-box
            // sides, room areas, etc. BricsCAD doubles otherwise serialize at full precision
            // (e.g. 730.027617799361) which is noise for downstream consumers.
            Converters = { new RoundedDoubleConverter(), new RoundedNullableDoubleConverter() }
        };

        private sealed class RoundedDoubleConverter : System.Text.Json.Serialization.JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => reader.GetDouble();
            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
                => writer.WriteNumberValue(Math.Round(value, 2));
        }

        private sealed class RoundedNullableDoubleConverter : System.Text.Json.Serialization.JsonConverter<double?>
        {
            public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
            public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
            {
                if (value.HasValue) writer.WriteNumberValue(Math.Round(value.Value, 2));
                else writer.WriteNullValue();
            }
        }

        private static string PendingDir => Path.Combine(Path.GetTempPath(), "AirenoOS", "pending");

        public static async Task PostAsync(Database db, ExtractionPayload payload)
        {
            string endpoint, token;
            try
            {
                (endpoint, token) = ConnectionConfig.Load(db);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
            {
                PersistOffline(payload, reason: "no_endpoint_or_token");
                return;
            }

            // v0.3 + Brian feedback (2026-06-05) item #3: the bearer token travels in
            // the HTTP Authorization header ONLY. Never duplicate it into the JSON body.
            var json = JsonSerializer.Serialize(payload, JsonOpts);

            // First attempt + one retry on transient failure
            if (await TrySend(endpoint, token, json)) return;
            await Task.Delay(1000);
            if (await TrySend(endpoint, token, json)) return;

            PersistOffline(payload, reason: "http_failed");
        }

        /// <summary>
        /// Blocking POST with a tight timeout — used only from the Application.BeginQuit
        /// handler (Brian feedback #5: session-end sync). The async PostAsync path can't
        /// be used at shutdown because BricsCAD tears down the runtime before fire-and-forget
        /// tasks can complete. On any failure the payload is persisted to the offline queue
        /// with a 'session_end_*' reason suffix so the next session retries it.
        /// </summary>
        public static void PostSync(Database db, ExtractionPayload payload, int timeoutMs)
        {
            string endpoint, token;
            try
            {
                (endpoint, token) = ConnectionConfig.Load(db);
            }
            catch
            {
                PersistOffline(payload, reason: "session_end_no_config");
                return;
            }

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
            {
                PersistOffline(payload, reason: "session_end_no_endpoint_or_token");
                return;
            }

            string json;
            try { json = JsonSerializer.Serialize(payload, JsonOpts); }
            catch { PersistOffline(payload, reason: "session_end_serialize_failed"); return; }

            try
            {
                var task = TrySend(endpoint, token, json);
                if (task.Wait(timeoutMs) && task.Result) return;
                PersistOffline(payload, reason: "session_end_http_failed");
            }
            catch
            {
                PersistOffline(payload, reason: "session_end_threw");
            }
        }

        /// <summary>
        /// Replays any payloads saved while offline. Called on next successful trigger.
        /// </summary>
        public static async Task RetryPending(Database db)
        {
            if (!Directory.Exists(PendingDir)) return;
            string endpoint, token;
            try { (endpoint, token) = ConnectionConfig.Load(db); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token)) return;

            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    if (await TrySend(endpoint, token, json))
                    {
                        File.Delete(file);
                    }
                }
                catch { /* leave file for next retry */ }
            }
        }

        private static async Task<bool> TrySend(string endpoint, string token, string json)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await Client.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                LogError($"POST {endpoint} → {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
                return false;
            }
            catch (Exception ex)
            {
                LogError($"POST {endpoint} threw: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}");
                return false;
            }
        }

        private static void LogError(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingDir)!);
                var path = Path.Combine(Path.GetDirectoryName(PendingDir)!, "last_error.txt");
                File.WriteAllText(path, $"[{DateTime.UtcNow:O}]\n{message}");
            }
            catch { }
        }

        private static void PersistOffline(ExtractionPayload payload, string reason)
        {
            try
            {
                Directory.CreateDirectory(PendingDir);
                var fileName = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{reason}.json";
                var path = Path.Combine(PendingDir, fileName);
                File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts));
            }
            catch { /* nowhere to fall back to */ }
        }

        /// <summary>
        /// Fetches pending writeback confirmations from the MCP server for this drawing's
        /// document_project_token. Returns an empty list on any error (never throws).
        /// Called from AIRENO_WRITEBACK command — never during save callback.
        /// </summary>
        public static async Task<List<ServerWriteback>> FetchPendingWritebacksAsync(Database db)
        {
            string endpoint, token;
            try
            {
                (endpoint, token) = ConnectionConfig.Load(db);
            }
            catch
            {
                return new List<ServerWriteback>();
            }
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
                return new List<ServerWriteback>();

            var writebackUrl = System.Text.RegularExpressions.Regex.Replace(
                endpoint, "/v1/extract", "/v1/writeback",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var docToken = ProjectTokenManager.GetProjectToken(db);
            var url = $"{writebackUrl}?document_project_token={System.Uri.EscapeDataString(docToken)}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await Client.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<ServerWriteback>();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<ServerWriteback>>(body, JsonOpts);
                return list ?? new List<ServerWriteback>();
            }
            catch
            {
                return new List<ServerWriteback>();
            }
        }
    }

    /// <summary>Shape of one item returned by GET /v1/writeback.</summary>
    internal class ServerWriteback
    {
        [System.Text.Json.Serialization.JsonPropertyName("document_project_token")]
        public string? DocumentProjectToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("native_id")]
        public string? NativeId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("aireno_backpack_id")]
        public string? AirenoBackpackId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("confirmed_label")]
        public string? ConfirmedLabel { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("confirmed_room_id")]
        public string? ConfirmedRoomId { get; set; }
    }
}
