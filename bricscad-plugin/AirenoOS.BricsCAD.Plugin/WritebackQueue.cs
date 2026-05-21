using System.Text.Json;
using Teigha.DatabaseServices;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin
{
    /// <summary>
    /// Stores pending writeback items in the Named Object Dictionary so they survive
    /// drawing close/reopen until the user explicitly runs AIRENO_WRITEBACK.
    ///
    /// NOD key:   AIRENO_WRITEBACK_QUEUE
    /// Value:     JSON array of QueuedItem (entity Handle as hex string + confirmed fields)
    ///
    /// We persist Handle (not ObjectId — ObjectId is session-only). At apply time we
    /// resolve Handle → ObjectId against the current Database.
    /// </summary>
    internal static class WritebackQueue
    {
        private const string QueueKey = "AIRENO_WRITEBACK_QUEUE";

        private class QueuedItem
        {
            public string Handle { get; set; } = "";
            public string? AirenoBackpackId { get; set; }
            public string? ConfirmedLabel { get; set; }
            public string? ConfirmedRoomId { get; set; }
        }

        public static void Enqueue(Database db, IEnumerable<WritebackItem> items)
        {
            var queued = new List<QueuedItem>(LoadRaw(db));
            foreach (var item in items)
            {
                var handle = ResolveHandle(db, item.EntityId);
                if (handle == null) continue;
                queued.RemoveAll(q => q.Handle == handle);
                queued.Add(new QueuedItem
                {
                    Handle = handle,
                    AirenoBackpackId = item.AirenoBackpackId,
                    ConfirmedLabel = item.ConfirmedLabel,
                    ConfirmedRoomId = item.ConfirmedRoomId
                });
            }
            SaveRaw(db, queued);
        }

        public static IReadOnlyList<WritebackItem> GetPending(Database db)
        {
            var raw = LoadRaw(db);
            var result = new List<WritebackItem>(raw.Count);
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var q in raw)
            {
                if (!long.TryParse(q.Handle, System.Globalization.NumberStyles.HexNumber, null, out var hVal)) continue;
                var handle = new Handle(hVal);
                if (!db.TryGetObjectId(handle, out var oid) || oid.IsErased || !oid.IsValid) continue;
                result.Add(new WritebackItem
                {
                    EntityId = oid,
                    AirenoBackpackId = q.AirenoBackpackId,
                    ConfirmedLabel = q.ConfirmedLabel,
                    ConfirmedRoomId = q.ConfirmedRoomId
                });
            }
            tr.Commit();
            return result;
        }

        public static void Clear(Database db) => SaveRaw(db, new List<QueuedItem>());

        // ── NOD plumbing ─────────────────────────────────────────────────────────────

        private static List<QueuedItem> LoadRaw(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(QueueKey)) { tr.Commit(); return new List<QueuedItem>(); }

            var xr = (Xrecord)tr.GetObject(nod.GetAt(QueueKey), OpenMode.ForRead);
            var json = xr.Data?.AsArray()
                .FirstOrDefault(v => v.TypeCode == (short)DxfCode.Text).Value as string;
            tr.Commit();

            if (string.IsNullOrWhiteSpace(json)) return new List<QueuedItem>();
            try { return JsonSerializer.Deserialize<List<QueuedItem>>(json) ?? new List<QueuedItem>(); }
            catch { return new List<QueuedItem>(); }
        }

        private static void SaveRaw(Database db, List<QueuedItem> items)
        {
            var json = JsonSerializer.Serialize(items);
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            var rb = new ResultBuffer(new TypedValue((int)DxfCode.Text, json));
            if (nod.Contains(QueueKey))
            {
                var xr = (Xrecord)tr.GetObject(nod.GetAt(QueueKey), OpenMode.ForWrite);
                xr.Data = rb;
            }
            else
            {
                var xr = new Xrecord { Data = rb };
                nod.SetAt(QueueKey, xr);               // attach to NOD first
                tr.AddNewlyCreatedDBObject(xr, true);  // then register with the transaction
            }
            tr.Commit();
        }

        private static string? ResolveHandle(Database db, ObjectId id)
        {
            if (id.IsNull || !id.IsValid) return null;
            using var tr = db.TransactionManager.StartTransaction();
            var obj = tr.GetObject(id, OpenMode.ForRead);
            var h = obj.Handle.Value.ToString("X");
            tr.Commit();
            return h;
        }
    }
}
