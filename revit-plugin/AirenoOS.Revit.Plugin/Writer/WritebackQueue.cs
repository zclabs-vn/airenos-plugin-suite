using System.Text.Json;
using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Schema;
using AirenoOS.Revit.Plugin.Storage;

namespace AirenoOS.Revit.Plugin.Writer
{
    /// <summary>
    /// Persists pending writeback items in ExtensibleStorage on ProjectInformation
    /// so they survive document close/reopen until the user runs AIRENO_WRITEBACK.
    ///
    /// We store UniqueId (string, native-stable across sessions) — not ElementId,
    /// which is session-local. At apply time we resolve UniqueId → Element via
    /// Document.GetElement(string).
    /// </summary>
    internal static class WritebackQueue
    {
        public static void Enqueue(Document doc, IEnumerable<WritebackItem> items)
        {
            var queued = LoadRaw(doc);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.ElementUniqueId)) continue;
                queued.RemoveAll(q => q.ElementUniqueId == item.ElementUniqueId);
                queued.Add(item);
            }
            SaveRaw(doc, queued);
        }

        public static IReadOnlyList<WritebackItem> GetPending(Document doc) => LoadRaw(doc);

        public static void Clear(Document doc) => SaveRaw(doc, new List<WritebackItem>());

        // ── ExtensibleStorage plumbing ───────────────────────────────────────────────

        private static List<WritebackItem> LoadRaw(Document doc)
        {
            try
            {
                var json = AirenoDataStore.Read(doc).Queue;
                if (string.IsNullOrWhiteSpace(json)) return new List<WritebackItem>();
                return JsonSerializer.Deserialize<List<WritebackItem>>(json) ?? new List<WritebackItem>();
            }
            catch
            {
                return new List<WritebackItem>();
            }
        }

        private static void SaveRaw(Document doc, List<WritebackItem> items)
        {
            var json = JsonSerializer.Serialize(items);
            using var tr = new Transaction(doc, "AirenoOS — update writeback queue");
            try
            {
                tr.Start();
                AirenoDataStore.Write(doc, queueJson: json);
                tr.Commit();
            }
            catch
            {
                if (tr.HasStarted()) tr.RollBack();
                throw;
            }
        }
    }
}
