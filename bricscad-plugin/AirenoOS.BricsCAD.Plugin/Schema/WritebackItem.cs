using Teigha.DatabaseServices;

namespace AirenoOS.BricsCAD.Plugin.Schema
{
    internal class WritebackItem
    {
        public ObjectId EntityId { get; set; }
        public string? AirenoBackpackId { get; set; }
        public string? ConfirmedLabel { get; set; }
        public string? ConfirmedRoomId { get; set; }
    }
}
