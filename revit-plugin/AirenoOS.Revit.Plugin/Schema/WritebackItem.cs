using Autodesk.Revit.DB;

namespace AirenoOS.Revit.Plugin.Schema
{
    /// <summary>
    /// One pending writeback row. UniqueId is captured at enqueue time and resolved
    /// back to an ElementId at apply time — the same element survives reopen.
    /// </summary>
    internal class WritebackItem
    {
        public string ElementUniqueId { get; set; } = string.Empty;
        public string? AirenoBackpackId { get; set; }
        public string? ConfirmedLabel { get; set; }
        public string? ConfirmedRoomId { get; set; }
    }
}
