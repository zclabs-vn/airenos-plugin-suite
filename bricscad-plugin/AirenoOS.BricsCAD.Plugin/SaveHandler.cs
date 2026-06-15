using System;
using System.IO;
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

                // Manual EXTRACT: assign XDATA-stored UUID to any tracked entity missing one
                // (Brian #7 — extended from blocks to polylines/hatches/dimensions).
                if (manualMode)
                {
                    EnsureNativeIds(doc.Database);
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
        /// Brian feedback #5 (2026-06-05) — session-end sync at Application.BeginQuit.
        /// Called once when BricsCAD is closing so the MCP server gets a final payload
        /// reflecting any unsaved edits before the document tree tears down.
        ///
        /// Constraints (Brian #6 shutdown guard already set IsShuttingDown=true):
        ///   - Read-only: never write XDATA, never call EnsureNativeIdsOnBlocks
        ///   - Blocking POST with 2s cap so BricsCAD shutdown isn't perceptibly delayed
        ///   - HTTP failure falls back to the offline queue (next session retries)
        /// </summary>
        public static void OnSessionEnd(Document doc)
        {
            SessionLog("OnSessionEnd entered");
            if (doc == null) { SessionLog("doc is null — bail"); return; }
            SessionLog($"doc OK: {doc.Name}");
            try
            {
                ProjectTokenManager.EnsureProjectToken(doc.Database);
                SessionLog("EnsureProjectToken OK");

                var payload = Extractor.CoreExtractor.Extract(doc, trigger: "session_end");
                SessionLog($"Extract OK: {payload?.Objects?.Count ?? -1} objects");

                try { ExtendedExtractor.Enrich(doc, payload!); SessionLog("Enrich OK"); }
                catch (Exception enrichEx) { SessionLog($"Enrich threw (continuing): {enrichEx.GetType().Name}: {enrichEx.Message}"); }

                // Skip POST when the doc is truly empty — avoids MCP noise from default
                // empty drawings that close every time the user opens a real DWG.
                if (payload!.Objects.Count == 0 && payload.Rooms.Count == 0)
                {
                    SessionLog("Skipping PostSync — empty drawing (no objects, no rooms)");
                    return;
                }

                SessionLog("PostSync starting");
                HttpSender.PostSync(doc.Database, payload, timeoutMs: 2000);
                SessionLog("PostSync returned");
            }
            catch (Exception ex)
            {
                SessionLog($"OnSessionEnd threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        internal static void SessionLog(string message)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "AirenoOS");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "session_end.log"),
                    $"[{DateTime.UtcNow:O}] {message}\n");
            }
            catch { }
        }

        /// <summary>
        /// Brian feedback #7 (2026-06-05) — XDATA for non-block entities.
        /// Iterates ModelSpace and writes AIRENO XDATA UUID on every tracked entity
        /// that doesn't already have one. Tracked types: BlockReference, closed Polyline,
        /// Hatch, Dimension. Result: room/hatch/dimension native_ids become stable across
        /// save/reopen, copy-paste and purge.
        /// </summary>
        private static void EnsureNativeIds(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!IsTrackedEntity(ent)) continue;

                var existing = XdataHelper.ReadField(ent, fieldIndex: 0);
                if (!string.IsNullOrEmpty(existing)) continue;

                ent.UpgradeOpen();
                XdataHelper.EnsureNativeId(tr, db, ent);
            }
            tr.Commit();
        }

        private static bool IsTrackedEntity(Entity ent) => ent switch
        {
            BlockReference _              => true,
            Polyline pl when pl.Closed    => true,
            Hatch _                       => true,
            Dimension _                   => true,
            _                             => false
        };
    }
}
