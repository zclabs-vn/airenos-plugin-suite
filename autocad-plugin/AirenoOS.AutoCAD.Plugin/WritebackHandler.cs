using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AirenoOS.AutoCAD.Plugin.Schema;

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// Writes confirmed identity data back to object XDATA.
    /// ONLY called from AIRENO_WRITEBACK command — never inside SaveComplete.
    /// </summary>
    internal static class WritebackHandler
    {
        private static bool _isWritebackInProgress = false;

        public static void Apply(Document doc)
        {
            if (_isWritebackInProgress) return;
            if (PluginApplication.IsShuttingDown) return;

            _isWritebackInProgress = true;
            try
            {
                var pending = WritebackQueue.GetPending(doc.Database);
                if (pending.Count == 0)
                {
                    doc.Editor.WriteMessage("\nAirenoOS: No pending writeback items.\n");
                    return;
                }

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    try
                    {
                        foreach (var item in pending)
                        {
                            ApplyToEntity(tr, doc.Database, item);
                        }
                        tr.Commit();
                        doc.Editor.WriteMessage($"\nAirenoOS: Writeback applied to {pending.Count} object(s).\n");
                        WritebackQueue.Clear(doc.Database);
                    }
                    catch
                    {
                        tr.Abort();
                        throw;
                    }
                }
            }
            finally
            {
                _isWritebackInProgress = false;
            }
        }

        private static void ApplyToEntity(Transaction tr, Database db, WritebackItem item)
        {
            // Register AIRENO app name if needed
            XdataHelper.EnsureAppRegistered(db, tr);

            var entity = tr.GetObject(item.EntityId, OpenMode.ForWrite) as Entity;
            if (entity == null) return;

            // Preserve existing native_id (UUID at field 0); rewrite fields 1..5
            var existingUuid = XdataHelper.ReadField(entity, fieldIndex: 0) ?? Guid.NewGuid().ToString();

            // Brian feedback #8 (2026-06-05) — preserve the label the user saw immediately
            // before this writeback. On subsequent writebacks this is the previous
            // confirmed_label (slot 3); on the very first writeback the user saw the block
            // definition name (or the entity type name for non-block entities). Without
            // this snapshot every writeback would erase the prior label and break audit.
            var previousLabel = XdataHelper.ReadField(entity, fieldIndex: 3);
            if (string.IsNullOrEmpty(previousLabel))
            {
                if (entity is BlockReference br)
                {
                    var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    previousLabel = btr.Name;
                }
                else
                {
                    previousLabel = entity.GetType().Name;
                }
            }

            XdataHelper.WriteFreshXdata(
                entity,
                nativeId:            existingUuid,
                airenoBackpackId:    item.AirenoBackpackId ?? string.Empty,
                identityState:       "confirmed",
                confirmedLabel:      item.ConfirmedLabel ?? string.Empty,
                confirmedRoomId:     item.ConfirmedRoomId ?? string.Empty,
                lastSynced:          DateTime.UtcNow.ToString("o"),
                airenoPreviousLabel: previousLabel ?? string.Empty
            );
        }
    }
}
