using System.Text.Json.Serialization;

namespace AirenoOS.Revit.Plugin.Schema
{
    /// <summary>
    /// Top-level payload — Canonical Plugin Schema v0.2 (Revit variant).
    /// source_software_type = "bim". Document-level rooms are first-class (native).
    /// </summary>
    internal class ExtractionPayload
    {
        [JsonPropertyName("aireno_schema_version")]
        public string SchemaVersion { get; set; } = "0.2";

        [JsonPropertyName("payload_type")]
        public string PayloadType { get; set; } = "extraction";

        [JsonPropertyName("document_project_token")]
        public string DocumentProjectToken { get; set; } = string.Empty;

        [JsonPropertyName("source_software")]
        public string SourceSoftware { get; set; } = "revit";

        [JsonPropertyName("source_software_version")]
        public string SourceSoftwareVersion { get; set; } = string.Empty;

        [JsonPropertyName("source_software_type")]
        public string SourceSoftwareType { get; set; } = "bim";

        [JsonPropertyName("plugin_version")]
        public string PluginVersion { get; set; } = "1.0.0";

        [JsonPropertyName("file_name_hash")]
        public string FileNameHash { get; set; } = string.Empty;

        [JsonPropertyName("extracted_at")]
        public string ExtractedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("extraction_trigger")]
        public string ExtractionTrigger { get; set; } = "on_save";

        [JsonPropertyName("objects")]
        public List<ObjectSignal> Objects { get; set; } = new();

        [JsonPropertyName("rooms")]
        public List<RoomSignal> Rooms { get; set; } = new();

        [JsonPropertyName("levels")]
        public List<LevelSignal> Levels { get; set; } = new();

        [JsonPropertyName("extraction_summary")]
        public ExtractionSummary Summary { get; set; } = new();
    }

    internal class ExtractionSummary
    {
        [JsonPropertyName("total_objects")]
        public int TotalObjects { get; set; }

        [JsonPropertyName("total_rooms")]
        public int TotalRooms { get; set; }

        [JsonPropertyName("high_quality_count")]
        public int HighQualityCount { get; set; }

        [JsonPropertyName("medium_quality_count")]
        public int MediumQualityCount { get; set; }

        [JsonPropertyName("low_quality_count")]
        public int LowQualityCount { get; set; }
    }
}
