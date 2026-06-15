using System;
using System.IO;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AirenoOS.AutoCAD.Plugin.Communicator;
using AirenoOS.AutoCAD.Plugin.Extractor;

namespace AirenoOS.AutoCAD.Plugin
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
            if (doc == null) return;

            try
            {
                // Ensure document has a project token
                ProjectTokenManager.EnsureProjectToken(doc.Database);

                // Manual EXTRACT: assign XDATA-stored UUID to any tracked entity missing one,
                // so the resulting payload has populated native_id values that the mock cockpit
                // can confirm. Never run during save callback (would cascade-dirty the drawing).
                if (manualMode)
                {
                    EnsureNativeIds(doc.Database);
                }

                // Layer 1 — core extraction (synchronous, fast)
                var payload = CoreExtractor.Extract(doc, trigger: manualMode ? "manual_command" : "on_save");

                // Layer 2 — extended signals (async, non-blocking)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ExtendedExtractor.Enrich(doc, payload);
                        await HttpSender.PostAsync(doc.Database, payload).ConfigureAwait(false);
                        await HttpSender.RetryPending(doc.Database).ConfigureAwait(false);
                    }
                    catch { /* never crash AutoCAD */ }
                });
            }
            catch { /* never crash AutoCAD */ }
        }

        /// <summary>
        /// Brian feedback #5 (2026-06-05) — session-end sync at Application.BeginQuit.
        /// Called once when AutoCAD is closing so the MCP server gets a final payload
        /// reflecting any unsaved edits before the document tree tears down.
        ///
        /// Constraints (Brian #6 shutdown guard already set IsShuttingDown=true):
        ///   - Read-only: never write XDATA, never call EnsureNativeIdsOnBlocks
        ///   - Blocking POST with 2s cap so AutoCAD shutdown isn't perceptibly delayed
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

                var payload = CoreExtractor.Extract(doc, trigger: "session_end");
                SessionLog($"Extract OK: {payload?.Objects?.Count ?? -1} objects");

                try { ExtendedExtractor.Enrich(doc, payload!); SessionLog("Enrich OK"); }
                catch (Exception enrichEx) { SessionLog($"Enrich threw (continuing): {enrichEx.GetType().Name}: {enrichEx.Message}"); }

                // Skip POST when the doc is truly empty — AutoCAD's default Drawing1 closes
                // every time the user opens a real DWG and would otherwise spam MCP with a
                // 0-object payload that the cockpit can't even identify (source_software is
                // nested inside objects[0], which doesn't exist when objects[] is empty).
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

        /// <summary>
        /// Append-only diagnostic log for the BeginQuit codepath. AutoCAD tears down the
        /// runtime quickly at shutdown, so each step writes a line synchronously to disk
        /// — if extraction throws mid-way the log still has the last successful step.
        /// </summary>
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
        ///
        /// One transaction-scoped pass: for every tracked entity in ModelSpace without
        /// AIRENO XDATA, generate a UUID and write it. Tracked entity types are:
        ///   - BlockReference     (Phase 1 — already shipped)
        ///   - Closed Polyline    (#7 — room candidates: closed boundary on any layer)
        ///   - Hatch              (#7)
        ///   - Dimension subclass (#7 — linear, angular, radial, diametric, ordinate)
        ///
        /// Result: room/hatch/dimension native_ids are stable across save/reopen,
        /// copy-paste between drawings, and purge — same guarantee that blocks already have.
        /// Existing AIRENO XDATA is preserved (idempotent).
        /// </summary>
        private static void EnsureNativeIds(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
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
