using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AirenoOS.AutoCAD.Plugin.Communicator;
using AirenoOS.AutoCAD.Plugin.Schema;

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// Bridge between server-returned writeback confirmations and the per-document
    /// WritebackQueue (NOD-persisted). Resolves each `native_id` to a live ObjectId
    /// by scanning ModelSpace XDATA, then enqueues a WritebackItem so the standard
    /// WritebackHandler.Apply path can commit the XDATA in a single transaction.
    /// </summary>
    internal static class WritebackQueueLoader
    {
        /// <summary>
        /// Returns the count actually enqueued (server items whose native_id matched a live entity).
        /// </summary>
        public static int EnqueueFromServer(Database db, List<ServerWriteback> serverItems)
        {
            if (serverItems == null || serverItems.Count == 0) return 0;

            // Build native_id → ServerWriteback lookup for O(1) match while scanning entities.
            var byNativeId = new Dictionary<string, ServerWriteback>();
            foreach (var s in serverItems)
            {
                if (!string.IsNullOrEmpty(s.NativeId)) byNativeId[s.NativeId!] = s;
            }
            if (byNativeId.Count == 0) return 0;

            var toEnqueue = new List<WritebackItem>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var nativeId = XdataHelper.ReadField(ent, fieldIndex: 0);
                    if (string.IsNullOrEmpty(nativeId)) continue;

                    if (!byNativeId.TryGetValue(nativeId!, out var s)) continue;

                    toEnqueue.Add(new WritebackItem
                    {
                        EntityId         = id,
                        AirenoBackpackId = s.AirenoBackpackId,
                        ConfirmedLabel   = s.ConfirmedLabel,
                        ConfirmedRoomId  = s.ConfirmedRoomId
                    });
                }
                tr.Commit();
            }

            if (toEnqueue.Count > 0)
                WritebackQueue.Enqueue(db, toEnqueue);

            return toEnqueue.Count;
        }
    }
}
