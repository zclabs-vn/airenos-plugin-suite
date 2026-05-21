using Bricscad.ApplicationServices;
using AirenoOS.BricsCAD.Plugin.Communicator;
using AirenoOS.BricsCAD.Plugin.Extractor;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin
{
    /// <summary>
    /// Coordinates extraction and HTTP POST on save.
    /// Called from SaveComplete event and AIRENO_EXTRACT command.
    /// </summary>
    internal static class SaveHandler
    {
        public static void OnSaveComplete(Document doc)
        {
            if (PluginApplication.IsShuttingDown) return;

            try
            {
                // Ensure document has a project token
                ProjectTokenManager.EnsureProjectToken(doc.Database);

                // Layer 1 — core extraction (synchronous, fast)
                var payload = Extractor.CoreExtractor.Extract(doc);

                // Layer 2 — extended signals (async, non-blocking)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ExtendedExtractor.Enrich(doc, payload);
                        await HttpSender.PostAsync(doc.Database, payload).ConfigureAwait(false);
                        await HttpSender.RetryPending(doc.Database).ConfigureAwait(false);
                    }
                    catch { /* never crash BricsCAD */ }
                });
            }
            catch { /* never crash BricsCAD */ }
        }
    }
}
