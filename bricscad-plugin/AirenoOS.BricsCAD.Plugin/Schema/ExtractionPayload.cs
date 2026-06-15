using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.BricsCAD.Plugin.Schema
{
    /// <summary>
    /// Top-level envelope — Canonical Plugin Schema v0.3 (Developer reference, May 2026).
    /// Mirror of the AutoCAD plugin envelope. See AutoCAD ExtractionPayload.cs for the
    /// full rationale (Brian feedback item #3 removes the body authentication block; the
    /// bearer token travels in the HTTP Authorization header only).
    /// </summary>
    internal class ExtractionPayload
    {
        [JsonPropertyName("aireno_schema_version")]
        public string SchemaVersion { get; set; } = "0.3";

        [JsonPropertyName("payload_type")]
        public string PayloadType { get; set; } = "extraction";

        [JsonPropertyName("extraction_tier")]
        public string ExtractionTier { get; set; } = "layer_1";

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

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

        // ── BIM-only top-level fields — emitted empty by 2D CAD plugins ────────────────

        [JsonPropertyName("bim_schedules")]
        public List<object> BimSchedules { get; set; } = new List<object>();

        [JsonPropertyName("model_health")]
        public Dictionary<string, object>? ModelHealth { get; set; }

        [JsonPropertyName("unresolved_objects")]
        public List<UnresolvedObjectSignal> UnresolvedObjects { get; set; } = new List<UnresolvedObjectSignal>();

        [JsonPropertyName("extraction_summary")]
        public ExtractionSummary Summary { get; set; } = new ExtractionSummary();

        // Brian #11 — BricsCAD-specific host environment probe. Lets the MCP server know
        // whether the host can fulfil BIM-tagged requests (BIM module licensed) without
        // having to wait for a parse error downstream.
        [JsonPropertyName("host_environment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HostEnvironment? HostEnvironment { get; set; }
    }

    /// <summary>
    /// Snapshot of the runtime host (BricsCAD edition + licensed modules) at extraction
    /// time. Emitted only by the BricsCAD plugin — AutoCAD plugin leaves this absent.
    /// </summary>
    internal class HostEnvironment
    {
        [JsonPropertyName("product_name")]        public string? ProductName       { get; set; }
        [JsonPropertyName("product_variant")]     public string? ProductVariant    { get; set; }
        [JsonPropertyName("bim_module_available")] public bool   BimModuleAvailable { get; set; }
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

        [JsonPropertyName("extraction_tier")]
        public string ExtractionTier { get; set; } = "layer_1";

        [JsonPropertyName("layer_2_groups_included")]
        public List<string> Layer2GroupsIncluded { get; set; } = new List<string>();

        [JsonPropertyName("extraction_duration_ms")]
        public long ExtractionDurationMs { get; set; }
    }
}
