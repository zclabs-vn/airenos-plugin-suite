using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.AutoCAD.Plugin.Schema
{
    /// <summary>
    /// Top-level envelope — Canonical Plugin Schema v0.3 (Developer reference, May 2026).
    /// Document-level Group 7 fields live inside each object's `group_7_source` per spec.
    /// `annotations`, `dimensions`, `hatches`, `cad_tables`, `xref_references`,
    /// `dynamic_blocks`, `layer_properties` are Layer 2 extended groups (Brian R2 + v0.3
    /// Groups 9-15). `bim_schedules`, `levels`, `model_health` are BIM-only — emitted
    /// empty by the 2D CAD plugin so the envelope shape is uniform.
    ///
    /// Per Brian feedback item #3 (2026-06-05): the bearer token travels only in the HTTP
    /// `Authorization` header — never in the payload body. The `authentication` block is
    /// intentionally removed from the envelope.
    /// </summary>
    internal class ExtractionPayload
    {
        [JsonPropertyName("aireno_schema_version")]
        public string SchemaVersion { get; set; } = "0.3";

        [JsonPropertyName("payload_type")]
        public string PayloadType { get; set; } = "extraction";

        /// <summary>
        /// `layer_1` (core only) / `layer_2` (with extended groups) / `layer_3` (diagnostic).
        /// AutoCAD/BricsCAD plugin emits `layer_2` whenever any of the extended arrays carry
        /// data — annotations, dimensions, hatches, dynamic_blocks, xref_references,
        /// layer_properties.
        /// </summary>
        [JsonPropertyName("extraction_tier")]
        public string ExtractionTier { get; set; } = "layer_1";

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

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

        [JsonPropertyName("levels")]
        public List<LevelSignal> Levels { get; set; } = new List<LevelSignal>();

        [JsonPropertyName("layers_or_tags")]
        public List<LayerSignal> LayersOrTags { get; set; } = new List<LayerSignal>();

        // ── Layer 2 extended groups (v0.3 Developer doc Groups 9–15) ───────────────────

        [JsonPropertyName("layer_properties")]
        public List<LayerPropertiesSignal> LayerProperties { get; set; } = new List<LayerPropertiesSignal>();

        [JsonPropertyName("cad_tables")]
        public List<CadTableSignal> CadTables { get; set; } = new List<CadTableSignal>();

        [JsonPropertyName("annotations")]
        public List<AnnotationSignal> Annotations { get; set; } = new List<AnnotationSignal>();

        [JsonPropertyName("dimensions")]
        public List<DimensionSignal> Dimensions { get; set; } = new List<DimensionSignal>();

        [JsonPropertyName("hatches")]
        public List<HatchSignal> Hatches { get; set; } = new List<HatchSignal>();

        [JsonPropertyName("xref_references")]
        public List<XrefReferenceSignal> XrefReferences { get; set; } = new List<XrefReferenceSignal>();

        [JsonPropertyName("entity_source_summary")]
        public EntitySourceSummary? EntitySourceSummary { get; set; }

        [JsonPropertyName("dynamic_blocks")]
        public List<DynamicBlockTopLevel> DynamicBlocks { get; set; } = new List<DynamicBlockTopLevel>();

        // ── BIM-only top-level fields — emitted empty by 2D CAD plugins for uniform shape ──

        [JsonPropertyName("bim_schedules")]
        public List<object> BimSchedules { get; set; } = new List<object>();

        [JsonPropertyName("model_health")]
        public Dictionary<string, object>? ModelHealth { get; set; }

        [JsonPropertyName("unresolved_objects")]
        public List<UnresolvedObjectSignal> UnresolvedObjects { get; set; } = new List<UnresolvedObjectSignal>();

        [JsonPropertyName("extraction_summary")]
        public ExtractionSummary Summary { get; set; } = new ExtractionSummary();
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

        // ── v0.3 additions ────────────────────────────────────────────────────────────
        [JsonPropertyName("extraction_tier")]
        public string ExtractionTier { get; set; } = "layer_1";

        [JsonPropertyName("layer_2_groups_included")]
        public List<string> Layer2GroupsIncluded { get; set; } = new List<string>();

        [JsonPropertyName("extraction_duration_ms")]
        public long ExtractionDurationMs { get; set; }
    }
}
