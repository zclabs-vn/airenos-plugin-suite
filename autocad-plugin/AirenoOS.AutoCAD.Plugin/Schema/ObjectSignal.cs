using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.AutoCAD.Plugin.Schema
{
    /// <summary>
    /// Represents one object — all 8 signal groups per Canonical Schema v0.2
    /// </summary>
    internal class ObjectSignal
    {
        // G1 — Native Object Identity
        [JsonPropertyName("native_id")]
        public string? NativeId { get; set; }

        [JsonPropertyName("native_id_type")]
        public string NativeIdType { get; set; } = "xdata_uuid";

        [JsonPropertyName("native_id_stability")]
        public string NativeIdStability { get; set; } = "stable";

        [JsonPropertyName("aireno_backpack_id")]
        public string? AirenoBackpackId { get; set; }

        [JsonPropertyName("identity_state")]
        public string IdentityState { get; set; } = "raw";

        [JsonPropertyName("collision_flag")]
        public bool CollisionFlag { get; set; } = false;

        [JsonPropertyName("collision_reason")]
        public string? CollisionReason { get; set; }

        [JsonPropertyName("link_state")]
        public string LinkState { get; set; } = "unlinked";

        [JsonPropertyName("is_definition")]
        public bool IsDefinition { get; set; }

        [JsonPropertyName("definition_id")]
        public string? DefinitionId { get; set; }

        // G2 — Naming Signal Stack
        [JsonPropertyName("visible_label")]
        public string? VisibleLabel { get; set; }

        [JsonPropertyName("definition_name")]
        public string? DefinitionName { get; set; }

        [JsonPropertyName("layer_or_tag_name")]
        public string? LayerOrTagName { get; set; }

        [JsonPropertyName("attribute_text")]
        public Dictionary<string, string>? AttributeText { get; set; }

        [JsonPropertyName("nearby_text_labels")]
        public List<string>? NearbyTextLabels { get; set; }

        // G3 — Spatial & Container Context
        [JsonPropertyName("room_or_zone_native_id")]
        public string? RoomOrZoneNativeId { get; set; }

        [JsonPropertyName("room_or_zone_name")]
        public string? RoomOrZoneName { get; set; }

        [JsonPropertyName("level_or_floor")]
        public string? LevelOrFloor { get; set; }

        [JsonPropertyName("spatial_position")]
        public SpatialPosition? SpatialPosition { get; set; }

        [JsonPropertyName("package_hint")]
        public string? PackageHint { get; set; }

        // G4 — Geometry
        [JsonPropertyName("units")]
        public string? Units { get; set; }

        [JsonPropertyName("bounding_box")]
        public BoundingBox? BoundingBox { get; set; }

        [JsonPropertyName("area")]
        public double? Area { get; set; }

        [JsonPropertyName("geometry_type")]
        public string? GeometryType { get; set; }

        [JsonPropertyName("is_closed_boundary")]
        public bool? IsClosedBoundary { get; set; }

        // G5 — Classification
        [JsonPropertyName("native_category")]
        public string? NativeCategory { get; set; }

        [JsonPropertyName("native_type")]
        public string? NativeType { get; set; }

        [JsonPropertyName("renovation_status")]
        public string? RenovationStatus { get; set; }

        [JsonPropertyName("function_hint")]
        public string? FunctionHint { get; set; }

        // G6 — Existing Metadata
        [JsonPropertyName("existing_metadata")]
        public Dictionary<string, string?>? ExistingMetadata { get; set; }

        // G7 — Source Context (document-level fields are on ExtractionPayload)
        [JsonPropertyName("is_xref_origin")]
        public bool? IsXrefOrigin { get; set; }

        [JsonPropertyName("xref_file_name")]
        public string? XrefFileName { get; set; }

        [JsonPropertyName("xref_file_hash")]
        public string? XrefFileHash { get; set; }

        [JsonPropertyName("xref_status")]
        public string? XrefStatus { get; set; }

        // G8 — Signal Quality
        [JsonPropertyName("naming_confidence")]
        public string NamingConfidence { get; set; } = "medium";

        [JsonPropertyName("stable_id_confidence")]
        public string StableIdConfidence { get; set; } = "plugin_generated";

        [JsonPropertyName("overall_signal_quality")]
        public string OverallSignalQuality { get; set; } = "medium";

        // Dynamic block state (Layer 2)
        [JsonPropertyName("dynamic_block_state")]
        public DynamicBlockState? DynamicBlockState { get; set; }
    }

    internal class SpatialPosition
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    }

    internal class BoundingBox
    {
        [JsonPropertyName("width")]  public double Width  { get; set; }
        [JsonPropertyName("height")] public double Height { get; set; }
        [JsonPropertyName("depth")]  public double Depth  { get; set; }
    }

    internal class DynamicBlockState
    {
        [JsonPropertyName("block_name")]       public string? BlockName      { get; set; }
        [JsonPropertyName("visibility_state")] public string? VisibilityState { get; set; }
        [JsonPropertyName("properties")]       public Dictionary<string, string?>? Properties { get; set; }
    }

    internal class RoomSignal
    {
        [JsonPropertyName("native_id")]   public string? NativeId  { get; set; }
        [JsonPropertyName("name")]        public string? Name       { get; set; }
        [JsonPropertyName("area")]        public double? Area       { get; set; }
        [JsonPropertyName("boundary_box")] public BoundingBox? BoundaryBox { get; set; }
        [JsonPropertyName("room_origin")] public string RoomOrigin { get; set; } = "boundary_inference";
    }

    internal class LayerSignal
    {
        [JsonPropertyName("name")]         public string? Name        { get; set; }
        [JsonPropertyName("color")]        public string? Color       { get; set; }
        [JsonPropertyName("linetype")]     public string? Linetype    { get; set; }
        [JsonPropertyName("is_frozen")]    public bool    IsFrozen    { get; set; }
        [JsonPropertyName("is_locked")]    public bool    IsLocked    { get; set; }
        [JsonPropertyName("is_visible")]   public bool    IsVisible   { get; set; }
        [JsonPropertyName("object_count")] public int     ObjectCount { get; set; }
    }

    internal class AnnotationSignal
    {
        [JsonPropertyName("native_id")]      public string? NativeId     { get; set; }
        [JsonPropertyName("type")]           public string? Type          { get; set; } // "Text" or "MText"
        [JsonPropertyName("content")]        public string? Content       { get; set; }
        [JsonPropertyName("layer")]          public string? Layer         { get; set; }
        [JsonPropertyName("position")]       public SpatialPosition? Position { get; set; }
        [JsonPropertyName("nearby_ids")]     public List<string>? NearbyIds { get; set; }
    }

    internal class DimensionSignal
    {
        [JsonPropertyName("native_id")]       public string? NativeId      { get; set; }
        [JsonPropertyName("measurement")]     public double? Measurement   { get; set; }
        [JsonPropertyName("dimension_type")]  public string? DimensionType { get; set; }
        [JsonPropertyName("layer")]           public string? Layer         { get; set; }
        [JsonPropertyName("position")]        public SpatialPosition? Position { get; set; }
    }

    internal class HatchSignal
    {
        [JsonPropertyName("native_id")]      public string? NativeId     { get; set; }
        [JsonPropertyName("pattern_name")]   public string? PatternName  { get; set; }
        [JsonPropertyName("pattern_type")]   public string? PatternType  { get; set; }
        [JsonPropertyName("layer")]          public string? Layer        { get; set; }
        [JsonPropertyName("scale")]          public double? Scale        { get; set; }
        [JsonPropertyName("boundary_ids")]   public List<string>? BoundaryIds { get; set; }
    }
}
