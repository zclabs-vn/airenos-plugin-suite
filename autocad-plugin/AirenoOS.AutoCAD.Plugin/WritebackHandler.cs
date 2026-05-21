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
            XdataHelper.WriteFreshXdata(
                entity,
                nativeId:         existingUuid,
                airenoBackpackId: item.AirenoBackpackId ?? string.Empty,
                identityState:    "confirmed",
                confirmedLabel:   item.ConfirmedLabel ?? string.Empty,
                confirmedRoomId:  item.ConfirmedRoomId ?? string.Empty,
                lastSynced:       DateTime.UtcNow.ToString("o")
            );
        }
    }
}
