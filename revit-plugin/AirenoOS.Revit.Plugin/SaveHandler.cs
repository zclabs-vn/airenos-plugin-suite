using System.Text.Json;
using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Communicator;
using AirenoOS.Revit.Plugin.Extractor;
using AirenoOS.Revit.Plugin.Schema;

namespace AirenoOS.Revit.Plugin
{
    /// <summary>
    /// Coordinates extraction and HTTP POST. Called from DocumentSaved and the
    /// AIRENO_EXTRACT command.
    ///
    /// Threading note (differs from CAD): Revit forbids Document API access on
    /// non-API threads, even reads. So BOTH L1 (core) and L2 (extended) run
    /// synchronously on the API thread that received the DocumentSaved event,
    /// which fires AFTER the save is complete — the user is not blocked from
    /// continuing to edit while extraction runs, and the save itself was never
    /// blocked.
    ///
    /// Only the HTTP POST (which takes a serialised payload, not a Document)
    /// fires on a background thread, so network latency never freezes Revit.
    /// </summary>
    internal static class SaveHandler
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static void OnDocumentSaved(Document doc, string trigger = "on_save")
        {
            if (PluginApplication.IsShuttingDown) return;
            if (doc == null || doc.IsFamilyDocument) return;

            try
            {
                // Token initialisation (needs Transaction).
                ProjectTokenManager.EnsureProjectToken(doc);

                // L1 — synchronous, fast subset.
                var payload = CoreExtractor.Extract(doc, trigger);

                // L2 — synchronous extension on the API thread. Adds parameter
                // groups + nearby-element refs. If this is too slow on a given
                // model the user can disable it via the next-phase settings UI.
                ExtendedExtractor.Enrich(doc, payload);

                // Snapshot endpoint + bearer before leaving the API thread.
                var (endpoint, bearer) = ConnectionConfig.Load(doc);

                // Serialise NOW (still on API thread) — no more Document touches needed.
                var json = JsonSerializer.Serialize(payload, JsonOpts);

                // Fire-and-forget POST. The string payload is thread-safe; the
                // HttpSender never touches Document.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HttpSender.PostAsync(endpoint, bearer, json).ConfigureAwait(false);
                        await HttpSender.RetryPending(endpoint, bearer).ConfigureAwait(false);
                    }
                    catch { /* swallow — never crash Revit */ }
                });
            }
            catch { /* swallow — never crash Revit */ }
        }
    }
}
