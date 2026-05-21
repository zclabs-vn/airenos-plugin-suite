using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Schema;
using AirenoOS.Revit.Plugin.Storage;

namespace AirenoOS.Revit.Plugin.Writer
{
    /// <summary>
    /// Writes confirmed identity data back onto elements via ExtensibleStorage.
    /// Per §12 of the spec:
    ///   - Manual command only (re-entrant cascade prevented via _inProgress flag).
    ///   - Never during shutdown.
    ///   - Idempotent — applying twice produces the same on-disk state.
    ///   - Preserves the previous label under PreviousLabel on first writeback.
    /// </summary>
    internal static class WritebackHandler
    {
        private static bool _inProgress = false;

        public static int Apply(Document doc)
        {
            if (_inProgress) return 0;
            if (PluginApplication.IsShuttingDown) return 0;

            _inProgress = true;
            try
            {
                var pending = WritebackQueue.GetPending(doc);
                if (pending.Count == 0) return 0;

                int applied = 0;
                using var tr = new Transaction(doc, "AirenoOS — apply writeback");
                try
                {
                    tr.Start();
                    foreach (var item in pending)
                    {
                        if (ApplyToElement(doc, item)) applied++;
                    }
                    tr.Commit();
                }
                catch
                {
                    if (tr.HasStarted()) tr.RollBack();
                    throw;
                }

                WritebackQueue.Clear(doc);
                return applied;
            }
            finally
            {
                _inProgress = false;
            }
        }

        private static bool ApplyToElement(Document doc, WritebackItem item)
        {
            if (string.IsNullOrEmpty(item.ElementUniqueId)) return false;

            var e = doc.GetElement(item.ElementUniqueId);
            if (e == null) return false;
            if (PluginApplication.IsShuttingDown) return false;

            var existing = ElementAirenoData.Read(e);

            // Preserve the original label only on the FIRST writeback. Subsequent
            // writebacks update the confirmed label without losing history.
            var previousLabel = !string.IsNullOrEmpty(existing.PreviousLabel)
                ? existing.PreviousLabel
                : e.Name;

            var snapshot = new ElementAirenoData.Snapshot
            {
                BackpackId     = item.AirenoBackpackId,
                IdentityState  = "confirmed",
                ConfirmedLabel = item.ConfirmedLabel,
                ConfirmedRoom  = item.ConfirmedRoomId,
                LastSynced     = DateTime.UtcNow.ToString("o"),
                PreviousLabel  = previousLabel
            };

            ElementAirenoData.Write(e, snapshot);

            // Update the element's display name as required by §14.
            if (!string.IsNullOrEmpty(item.ConfirmedLabel))
            {
                try { e.Name = item.ConfirmedLabel; }
                catch { /* Some element types reject renames — fall through silently. */ }
            }

            return true;
        }
    }
}
