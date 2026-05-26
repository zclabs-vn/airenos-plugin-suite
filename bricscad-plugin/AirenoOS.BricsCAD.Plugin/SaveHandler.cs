using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
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
        /// <param name="manualMode">
        /// True when invoked from AIRENO_EXTRACT command (manual). Enables XDATA UUID
        /// generation for blocks that don't yet have a native_id. False when invoked
        /// from Database.SaveComplete event — XDATA writes are forbidden there per
        /// Brian's cascade-prevention rule.
        /// </param>
        public static void OnSaveComplete(Document doc, bool manualMode = false)
        {
            if (PluginApplication.IsShuttingDown) return;

            try
            {
                // Ensure document has a project token
                ProjectTokenManager.EnsureProjectToken(doc.Database);

                // Manual EXTRACT: assign XDATA-stored UUID to any block missing one,
                // so the resulting payload has populated native_id values that the
                // mock cockpit can confirm. Never run during save callback.
                if (manualMode)
                {
                    EnsureNativeIdsOnBlocks(doc.Database);
                }

                // Layer 1 — core extraction (synchronous, fast)
                var payload = Extractor.CoreExtractor.Extract(doc, trigger: manualMode ? "manual_command" : "on_save");

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

        /// <summary>
        /// One transaction-scoped pass: for every BlockReference in ModelSpace without
        /// AIRENO XDATA, generate a UUID and write it. Existing AIRENO XDATA is preserved.
        /// </summary>
        private static void EnsureNativeIdsOnBlocks(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br)
                {
                    var existing = XdataHelper.ReadField(br, fieldIndex: 0);
                    if (!string.IsNullOrEmpty(existing)) continue;

                    br.UpgradeOpen();
                    XdataHelper.EnsureNativeId(tr, db, br);
                }
            }
            tr.Commit();
        }
    }
}
