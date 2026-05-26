using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AirenoOS.AutoCAD.Plugin.Schema;

namespace AirenoOS.AutoCAD.Plugin.Communicator
{
    /// <summary>
    /// Async HTTP POST to the AirenoOS MCP endpoint.
    /// Rules (per Canonical Schema v0.2 §HTTP):
    ///   - Bearer auth, JSON body, 30s timeout, retry once.
    ///   - Offline: persist payload to %TEMP%\AirenoOS\pending\*.json — replayed by RetryPending().
    ///   - Never throws — failures are swallowed so AutoCAD save can never crash.
    /// </summary>
    internal static class HttpSender
    {
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            // Canonical Schema v0.2 expects the full envelope shape: null-valued fields
            // (e.g. ifc_class for 2D CAD) must serialize as explicit JSON null, not omitted.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            // Round every numeric dimension to 2 decimals — CAD positions, bounding-box
            // sides, room areas, etc. AutoCAD doubles otherwise serialize at full precision
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

            // Echo bearer in body per Canonical Schema v0.2 envelope spec.
            // The HTTP Authorization header still carries it for actual auth;
            // body duplication is what the spec example shows.
            payload.Authentication = new Authentication { BearerToken = token };

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            // First attempt + one retry on transient failure
            if (await TrySend(endpoint, token, json).ConfigureAwait(false)) return;
            await Task.Delay(1000).ConfigureAwait(false);
            if (await TrySend(endpoint, token, json).ConfigureAwait(false)) return;

            PersistOffline(payload, reason: "http_failed");
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
                    string json;
                    using (var reader = new StreamReader(file))
                    {
                        json = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                    if (await TrySend(endpoint, token, json).ConfigureAwait(false))
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
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                })
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using (var resp = await Client.SendAsync(req).ConfigureAwait(false))
                    {
                        return resp.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
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

            // /v1/extract  →  /v1/writeback (replace trailing segment, case-insensitive)
            var writebackUrl = System.Text.RegularExpressions.Regex.Replace(
                endpoint, "/v1/extract", "/v1/writeback",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var docToken = ProjectTokenManager.GetProjectToken(db);
            var url = $"{writebackUrl}?document_project_token={Uri.EscapeDataString(docToken)}";

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var resp = await Client.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return new List<ServerWriteback>();
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var list = JsonSerializer.Deserialize<List<ServerWriteback>>(body, JsonOpts);
                        return list ?? new List<ServerWriteback>();
                    }
                }
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
