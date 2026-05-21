using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AirenoOS.Revit.Plugin.Schema;

namespace AirenoOS.Revit.Plugin.Communicator
{
    /// <summary>
    /// Async HTTP POST to the AirenoOS MCP endpoint.
    /// Bearer auth, JSON body, 30 s timeout, retry once.
    /// Offline: persist payload to %TEMP%\AirenoOS\pending\*.json; replay on next trigger.
    /// Never throws — every failure becomes a "stash for later" so a save can never crash Revit.
    /// </summary>
    internal static class HttpSender
    {
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static string PendingDir => Path.Combine(Path.GetTempPath(), "AirenoOS", "pending");

        /// <summary>
        /// Send a payload already serialised to JSON. Called from SaveHandler.
        /// </summary>
        public static async Task PostAsync(string endpoint, string token, string json)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
            {
                PersistOffline(json, reason: "no_endpoint_or_token");
                return;
            }

            if (await TrySend(endpoint, token, json).ConfigureAwait(false)) return;
            await Task.Delay(1000).ConfigureAwait(false);
            if (await TrySend(endpoint, token, json).ConfigureAwait(false)) return;

            PersistOffline(json, reason: "http_failed");
        }

        /// <summary>
        /// Object-based overload — kept for callers that haven't serialised yet.
        /// </summary>
        public static Task PostAsync(string endpoint, string token, ExtractionPayload payload)
            => PostAsync(endpoint, token, JsonSerializer.Serialize(payload, JsonOpts));

        /// <summary>
        /// Replay payloads stored while offline. Best-effort; leaves files in place
        /// if the network is still down so they will be retried again later.
        /// </summary>
        public static async Task RetryPending(string endpoint, string token)
        {
            if (!Directory.Exists(PendingDir)) return;
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token)) return;

            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try
                {
#if NET48
                    var json = File.ReadAllText(file);
                    await Task.Yield();
#else
                    var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
#endif
                    if (await TrySend(endpoint, token, json).ConfigureAwait(false))
                    {
                        File.Delete(file);
                    }
                }
                catch { /* leave file for next pass */ }
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

        private static void PersistOffline(string json, string reason)
        {
            try
            {
                Directory.CreateDirectory(PendingDir);
                var fileName = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{reason}.json";
                File.WriteAllText(Path.Combine(PendingDir, fileName), json);
            }
            catch { /* nothing to fall back to — drop on the floor */ }
        }
    }
}
