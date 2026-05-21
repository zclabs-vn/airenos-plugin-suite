using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin
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

                using var tr = doc.Database.TransactionManager.StartTransaction();
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

            var xdata = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XdataHelper.AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.AirenoBackpackId ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "confirmed"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.ConfirmedLabel ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.ConfirmedRoomId ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, DateTime.UtcNow.ToString("o"))
            );

            entity.XData = xdata;
        }
    }
}
