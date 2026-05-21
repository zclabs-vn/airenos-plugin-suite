using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.AutoCAD.Plugin.Schema
{
    /// <summary>
    /// Top-level payload — Canonical Plugin Schema v0.2
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
        public string SourceSoftware { get; set; } = "autocad";

        [JsonPropertyName("source_software_version")]
        public string SourceSoftwareVersion { get; set; } = string.Empty;

        [JsonPropertyName("source_software_type")]
        public string SourceSoftwareType { get; set; } = "2d_cad";

        [JsonPropertyName("plugin_version")]
        public string PluginVersion { get; set; } = "1.0.0";

        [JsonPropertyName("file_name_hash")]
        public string FileNameHash { get; set; } = string.Empty;

        [JsonPropertyName("extracted_at")]
        public string ExtractedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("extraction_trigger")]
        public string ExtractionTrigger { get; set; } = "on_save";

        [JsonPropertyName("objects")]
        public List<ObjectSignal> Objects { get; set; } = new List<ObjectSignal>();

        [JsonPropertyName("rooms")]
        public List<RoomSignal> Rooms { get; set; } = new List<RoomSignal>();

        [JsonPropertyName("layer_table")]
        public List<LayerSignal> LayerTable { get; set; } = new List<LayerSignal>();

        [JsonPropertyName("annotations")]
        public List<AnnotationSignal> Annotations { get; set; } = new List<AnnotationSignal>();

        [JsonPropertyName("dimensions")]
        public List<DimensionSignal> Dimensions { get; set; } = new List<DimensionSignal>();

        [JsonPropertyName("hatches")]
        public List<HatchSignal> Hatches { get; set; } = new List<HatchSignal>();

        [JsonPropertyName("extraction_summary")]
        public ExtractionSummary Summary { get; set; } = new ExtractionSummary();
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
