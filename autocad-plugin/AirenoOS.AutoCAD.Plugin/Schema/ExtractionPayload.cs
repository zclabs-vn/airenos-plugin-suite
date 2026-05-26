using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.AutoCAD.Plugin.Schema
{
    /// <summary>
    /// Top-level envelope — exactly matches Canonical Plugin Schema v0.2 "Full Payload Envelope".
    /// Document-level Group 7 fields (source_software, plugin_version, extracted_at, etc.)
    /// live inside each object's `group_7_source` per spec — NOT at top-level.
    /// `annotations`, `dimensions`, `hatches` are Layer 2 CAD extensions confirmed by Brian
    /// in Round 2 (out of the base envelope but within agreed scope).
    /// </summary>
    internal class ExtractionPayload
    {
        [JsonPropertyName("aireno_schema_version")]
        public string SchemaVersion { get; set; } = "0.2";

        [JsonPropertyName("payload_type")]
        public string PayloadType { get; set; } = "extraction";

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("authentication")]
        public Authentication? Authentication { get; set; }

        /// <summary>
        /// Per spec note "same as Group 7 field" — top-level convenience duplicate
        /// of objects[].group_7_source.document_project_token.
        /// </summary>
        [JsonPropertyName("document_project_token")]
        public string DocumentProjectToken { get; set; } = string.Empty;

        [JsonPropertyName("objects")]
        public List<ObjectSignal> Objects { get; set; } = new List<ObjectSignal>();

        [JsonPropertyName("rooms")]
        public List<RoomSignal> Rooms { get; set; } = new List<RoomSignal>();

        [JsonPropertyName("layers_or_tags")]
        public List<LayerSignal> LayersOrTags { get; set; } = new List<LayerSignal>();

        // ── Layer 2 CAD extensions (Brian Round 2 — not in base envelope but in scope) ──
        [JsonPropertyName("annotations")]
        public List<AnnotationSignal> Annotations { get; set; } = new List<AnnotationSignal>();

        [JsonPropertyName("dimensions")]
        public List<DimensionSignal> Dimensions { get; set; } = new List<DimensionSignal>();

        [JsonPropertyName("hatches")]
        public List<HatchSignal> Hatches { get; set; } = new List<HatchSignal>();

        [JsonPropertyName("unresolved_objects")]
        public List<UnresolvedObjectSignal> UnresolvedObjects { get; set; } = new List<UnresolvedObjectSignal>();

        [JsonPropertyName("extraction_summary")]
        public ExtractionSummary Summary { get; set; } = new ExtractionSummary();
    }

    internal class Authentication
    {
        [JsonPropertyName("bearer_token")]
        public string? BearerToken { get; set; }
    }

    internal class ExtractionSummary
    {
        [JsonPropertyName("total_objects")]         public int TotalObjects        { get; set; }
        [JsonPropertyName("total_rooms")]           public int TotalRooms          { get; set; }
        [JsonPropertyName("total_layers")]          public int TotalLayers         { get; set; }
        [JsonPropertyName("unresolved_count")]      public int UnresolvedCount     { get; set; }
        [JsonPropertyName("high_quality_count")]    public int HighQualityCount    { get; set; }
        [JsonPropertyName("medium_quality_count")]  public int MediumQualityCount  { get; set; }
        [JsonPropertyName("low_quality_count")]     public int LowQualityCount     { get; set; }
    }
}
