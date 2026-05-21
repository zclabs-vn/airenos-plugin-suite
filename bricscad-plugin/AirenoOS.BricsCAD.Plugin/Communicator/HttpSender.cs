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
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            // First attempt + one retry on transient failure
            if (await TrySend(endpoint, token, json)) return;
            await Task.Delay(1000);
            if (await TrySend(endpoint, token, json)) return;

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
                return resp.IsSuccessStatusCode;
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
    }
}
