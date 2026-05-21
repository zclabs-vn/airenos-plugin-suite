using System.Text.Json.Serialization;

namespace AirenoOS.Revit.Plugin.Schema
{
    /// <summary>
    /// One element — all 8 signal groups per Canonical Schema v0.2 (Revit variant).
    /// G5/G6 carry rich BIM data Revit exposes natively; G8 quality flips to "high"
    /// because native_id is stable and rooms/category are native (not inferred).
    /// </summary>
    internal class ObjectSignal
    {
        // G1 — Native Object Identity
        [JsonPropertyName("native_id")]
        public string? NativeId { get; set; }

        [JsonPropertyName("native_id_type")]
        public string NativeIdType { get; set; } = "guid";

        [JsonPropertyName("native_id_stability")]
        public string NativeIdStability { get; set; } = "native_stable";

        [JsonPropertyName("aireno_backpack_id")]
        public string? AirenoBackpackId { get; set; }

        [JsonPropertyName("identity_state")]
        public string IdentityState { get; set; } = "raw";

        // Revit copies always get a new UniqueId — collision flag stays false.
        [JsonPropertyName("collision_flag")]
        public bool CollisionFlag { get; set; } = false;

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

        // Revit groups elements by Category, not by a 1:1 layer.
        [JsonPropertyName("layer_or_tag_name")]
        public string? LayerOrTagName { get; set; }

        [JsonPropertyName("attribute_text")]
        public Dictionary<string, string?>? AttributeText { get; set; }

        // G3 — Spatial & Container Context
        [JsonPropertyName("room_or_zone_native_id")]
        public string? RoomOrZoneNativeId { get; set; }

        [JsonPropertyName("room_or_zone_name")]
        public string? RoomOrZoneName { get; set; }

        [JsonPropertyName("level_or_floor")]
        public string? LevelOrFloor { get; set; }

        [JsonPropertyName("spatial_position")]
        public SpatialPosition? SpatialPosition { get; set; }

        [JsonPropertyName("container_type")]
        public string? ContainerType { get; set; }

        // G4 — Geometry
        [JsonPropertyName("units")]
        public string? Units { get; set; }

        [JsonPropertyName("bounding_box")]
        public BoundingBox? BoundingBox { get; set; }

        [JsonPropertyName("area")]
        public double? Area { get; set; }

        [JsonPropertyName("volume")]
        public double? Volume { get; set; }

        [JsonPropertyName("geometry_type")]
        public string? GeometryType { get; set; }

        // G5 — Classification & Function Hint
        [JsonPropertyName("native_category")]
        public string? NativeCategory { get; set; }

        [JsonPropertyName("native_type")]
        public string? NativeType { get; set; }

        [JsonPropertyName("renovation_status")]
        public RenovationStatus? RenovationStatus { get; set; }

        [JsonPropertyName("structural_flag")]
        public bool? StructuralFlag { get; set; }

        [JsonPropertyName("ifc_class")]
        public string? IfcClass { get; set; }

        [JsonPropertyName("omniclass_code")]
        public string? OmniclassCode { get; set; }

        [JsonPropertyName("uniclass_code")]
        public string? UniclassCode { get; set; }

        [JsonPropertyName("assembly_code")]
        public string? AssemblyCode { get; set; }

        // G6 — Existing Metadata (Revit parameters grouped by Definition.ParameterGroup)
        [JsonPropertyName("metadata_format")]
        public string MetadataFormat { get; set; } = "parameter";

        [JsonPropertyName("parameter_groups")]
        public Dictionary<string, Dictionary<string, string?>>? ParameterGroups { get; set; }

        [JsonPropertyName("bim_properties")]
        public BimProperties? BimProperties { get; set; }

        // G7 — Source Context (workset is per-element; document-level fields on payload)
        [JsonPropertyName("workset_name")]
        public string? WorksetName { get; set; }

        // G8 — Signal Quality
        [JsonPropertyName("element_type_origin")]
        public string ElementTypeOrigin { get; set; } = "native";

        [JsonPropertyName("room_origin")]
        public string RoomOrigin { get; set; } = "native";

        [JsonPropertyName("naming_confidence")]
        public string NamingConfidence { get; set; } = "strong";

        [JsonPropertyName("stable_id_confidence")]
        public string StableIdConfidence { get; set; } = "native_stable";

        [JsonPropertyName("overall_signal_quality")]
        public string OverallSignalQuality { get; set; } = "high";

        // V2 placeholders — schema must stay forward-compatible
        [JsonPropertyName("v2_change_detection")]
        public V2ChangeDetection V2ChangeDetection { get; set; } = new();

        [JsonPropertyName("v2_selection_state")]
        public V2SelectionState V2SelectionState { get; set; } = new();

        [JsonPropertyName("v2_visual_marker_state")]
        public V2VisualMarkerState V2VisualMarkerState { get; set; } = new();
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

    internal class RenovationStatus
    {
        [JsonPropertyName("phase_created")]    public string? PhaseCreated    { get; set; }
        [JsonPropertyName("phase_demolished")] public string? PhaseDemolished { get; set; }
    }

    internal class BimProperties
    {
        [JsonPropertyName("fire_rating")]     public string? FireRating     { get; set; }
        [JsonPropertyName("acoustic_rating")] public string? AcousticRating { get; set; }
        [JsonPropertyName("manufacturer")]    public string? Manufacturer   { get; set; }
        [JsonPropertyName("model")]           public string? Model          { get; set; }
        [JsonPropertyName("phase")]           public string? Phase          { get; set; }
        [JsonPropertyName("description")]     public string? Description    { get; set; }
        [JsonPropertyName("comments")]        public string? Comments       { get; set; }
    }

    internal class RoomSignal
    {
        [JsonPropertyName("native_id")]   public string? NativeId { get; set; }
        [JsonPropertyName("name")]        public string? Name      { get; set; }
        [JsonPropertyName("number")]      public string? Number    { get; set; }
        [JsonPropertyName("level")]       public string? Level     { get; set; }
        [JsonPropertyName("area")]        public double? Area      { get; set; }
        [JsonPropertyName("volume")]      public double? Volume    { get; set; }
        [JsonPropertyName("phase")]       public string? Phase     { get; set; }
        [JsonPropertyName("boundary_box")] public BoundingBox? BoundaryBox { get; set; }
        [JsonPropertyName("room_origin")] public string RoomOrigin { get; set; } = "native";
    }

    internal class LevelSignal
    {
        [JsonPropertyName("native_id")] public string? NativeId { get; set; }
        [JsonPropertyName("name")]      public string? Name      { get; set; }
        [JsonPropertyName("elevation")] public double? Elevation { get; set; }
    }

    internal class V2ChangeDetection
    {
        [JsonPropertyName("changed_since_last_sync")] public bool?   ChangedSinceLastSync { get; set; }
        [JsonPropertyName("change_type")]             public string? ChangeType            { get; set; }
        [JsonPropertyName("previous_value")]          public string? PreviousValue         { get; set; }
    }

    internal class V2SelectionState
    {
        [JsonPropertyName("is_selected_in_tool")] public bool?   IsSelectedInTool { get; set; }
        [JsonPropertyName("selection_source")]    public string? SelectionSource  { get; set; }
    }

    internal class V2VisualMarkerState
    {
        [JsonPropertyName("marker_type")]  public string? MarkerType  { get; set; }
        [JsonPropertyName("marker_color")] public string? MarkerColor { get; set; }
    }
}
