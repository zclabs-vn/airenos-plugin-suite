using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// XDATA helpers for the AIRENO registered application.
    /// XDATA layout written by extraction / writeback:
    ///   [0] RegAppName     = "AIRENO"
    ///   [1] AsciiString    = native_id (UUID, plugin-generated, stable across purge)
    ///   [2] AsciiString    = aireno_backpack_id (empty until confirmed)
    ///   [3] AsciiString    = identity_state ("raw" | "confirmed" | "collision")
    ///   [4] AsciiString    = confirmed_label
    ///   [5] AsciiString    = confirmed_room_id
    ///   [6] AsciiString    = last_synced (ISO-8601 UTC)
    /// </summary>
    internal static class XdataHelper
    {
        public const string AppName = "AIRENO";

        public static void EnsureAppRegistered(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(AppName)) return;

            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = AppName };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        /// <summary>
        /// Returns the existing AIRENO native_id (UUID) on an entity, or generates and writes one if missing.
        /// Caller must already be inside a write transaction.
        /// </summary>
        public static string EnsureNativeId(Transaction tr, Database db, Entity entity)
        {
            EnsureAppRegistered(db, tr);

            var existing = ReadField(entity, fieldIndex: 0);
            if (!string.IsNullOrEmpty(existing)) return existing!;

            var newId = Guid.NewGuid().ToString();
            WriteFreshXdata(entity, newId);
            return newId;
        }

        /// <summary>
        /// Reads one of the AIRENO XDATA string fields (0..5). Returns null if XDATA not present.
        /// </summary>
        public static string? ReadField(Entity entity, int fieldIndex)
        {
            var rb = entity.GetXDataForApplication(AppName);
            if (rb == null) return null;

            var values = rb.AsArray();
            // values[0] is the RegAppName marker; skip it
            var stringFields = values.Skip(1).Where(v => v.TypeCode == (short)DxfCode.ExtendedDataAsciiString).ToArray();
            if (fieldIndex < 0 || fieldIndex >= stringFields.Length) return null;
            return stringFields[fieldIndex].Value as string;
        }

        /// <summary>
        /// Writes a fresh AIRENO XDATA block. Existing AIRENO XDATA is overwritten.
        /// All non-AIRENO XDATA on the entity is preserved by AutoCAD (per-app scoping).
        /// </summary>
        public static void WriteFreshXdata(
            Entity entity,
            string nativeId,
            string airenoBackpackId = "",
            string identityState = "raw",
            string confirmedLabel = "",
            string confirmedRoomId = "",
            string? lastSynced = null)
        {
            var ts = lastSynced ?? DateTime.UtcNow.ToString("o");
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName,    AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   nativeId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   airenoBackpackId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   identityState),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   confirmedLabel),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   confirmedRoomId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,   ts)
            );
        }
    }
}
