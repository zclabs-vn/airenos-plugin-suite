using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirenoOS.BricsCAD.Plugin.Schema
{
    /// <summary>
    /// One object's payload following the Canonical Plugin Schema v0.2 Full Payload Envelope,
    /// where each of the 8 signal groups is a nested object — NOT flat fields.
    /// All optional fields are nullable; serializer keeps explicit nulls.
    /// </summary>
    internal class ObjectSignal
    {
        [JsonPropertyName("group_1_identity")]       public Group1Identity       Group1Identity       { get; set; } = new();
        // Brian feedback (2026-06-05) item #2 — see AutoCAD ObjectSignal.cs for rationale.
        [JsonPropertyName("airenoos_ref")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AirenoosRef? AirenoosRef { get; set; }
        [JsonPropertyName("group_2_naming")]         public Group2Naming         Group2Naming         { get; set; } = new();
        [JsonPropertyName("group_3_spatial")]        public Group3Spatial        Group3Spatial        { get; set; } = new();
        [JsonPropertyName("group_4_geometry")]       public Group4Geometry       Group4Geometry       { get; set; } = new();
        [JsonPropertyName("group_5_classification")] public Group5Classification Group5Classification { get; set; } = new();
        [JsonPropertyName("group_6_metadata")]       public Group6Metadata       Group6Metadata       { get; set; } = new();
        [JsonPropertyName("group_7_source")]         public Group7Source         Group7Source         { get; set; } = new();
        [JsonPropertyName("group_8_quality")]        public Group8Quality        Group8Quality        { get; set; } = new();
        // Dynamic block state flows through Group6Metadata.ExistingMetadata["dynamic_block"]
        // per spec's existing_metadata passthrough design. No standalone field at object level.
    }

    internal class AirenoosRef
    {
        [JsonPropertyName("backpack_id")]     public string? BackpackId    { get; set; }
        [JsonPropertyName("ref_source")]      public string  RefSource     { get; set; } = "xdata_readback";
        [JsonPropertyName("ref_assigned_by")] public string  RefAssignedBy { get; set; } = "airenoos";
    }

    internal class Group1Identity
    {
        [JsonPropertyName("native_id")]            public string?  NativeId          { get; set; }
        [JsonPropertyName("native_id_type")]       public string   NativeIdType      { get; set; } = "xdata_uuid";
        [JsonPropertyName("native_id_stability")]  public string   NativeIdStability { get; set; } = "stable";
        // aireno_backpack_id moved out per Brian #2 — see ObjectSignal.AirenoosRef.
        [JsonPropertyName("identity_state")]       public string   IdentityState     { get; set; } = "raw";
        [JsonPropertyName("link_state")]           public string   LinkState         { get; set; } = "unlinked";
        [JsonPropertyName("is_definition")]        public bool     IsDefinition      { get; set; }
        [JsonPropertyName("definition_id")]        public string?  DefinitionId      { get; set; }
        [JsonPropertyName("instance_id")]          public string?  InstanceId        { get; set; }
        [JsonPropertyName("collision_flag")]       public bool     CollisionFlag     { get; set; }
        [JsonPropertyName("collision_reason")]     public string?  CollisionReason   { get; set; }
    }

    internal class Group2Naming
    {
        [JsonPropertyName("visible_label")]             public string?             VisibleLabel          { get; set; }
        [JsonPropertyName("definition_name")]           public string?             DefinitionName        { get; set; }
        [JsonPropertyName("layer_or_tag_name")]         public string?             LayerOrTagName        { get; set; }
        [JsonPropertyName("parent_container_name")]     public string?             ParentContainerName   { get; set; }
        [JsonPropertyName("hierarchy_path_names")]      public List<string>?       HierarchyPathNames    { get; set; }
        [JsonPropertyName("room_label_signal")]         public string?             RoomLabelSignal       { get; set; }
        [JsonPropertyName("nearby_text_labels")]        public List<string>?       NearbyTextLabels      { get; set; }
        [JsonPropertyName("attribute_text")]            public Dictionary<string, string>? AttributeText { get; set; }
        [JsonPropertyName("file_or_sheet_name_signal")] public string?             FileOrSheetNameSignal { get; set; }
        [JsonPropertyName("scene_or_view_name")]        public string?             SceneOrViewName       { get; set; }
        [JsonPropertyName("naming_origin")]             public string?             NamingOrigin          { get; set; }
        [JsonPropertyName("raw_aliases")]               public List<string>?       RawAliases            { get; set; }
        // Brian #8: snapshot of the visible label the user saw before the most recent
        // writeback overwrote it. Null/absent when no writeback has happened yet.
        [JsonPropertyName("aireno_previous_label")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string?             AirenoPreviousLabel  { get; set; }
    }

    internal class Group3Spatial
    {
        [JsonPropertyName("room_or_zone_native_id")] public string?            RoomOrZoneNativeId  { get; set; }
        [JsonPropertyName("room_or_zone_name")]      public string?            RoomOrZoneName      { get; set; }
        [JsonPropertyName("level_or_floor")]         public string?            LevelOrFloor        { get; set; }
        [JsonPropertyName("parent_native_id")]       public string?            ParentNativeId      { get; set; }
        [JsonPropertyName("children_native_ids")]    public List<string>?      ChildrenNativeIds   { get; set; }
        [JsonPropertyName("container_type")]         public string?            ContainerType       { get; set; }
        [JsonPropertyName("package_hint")]           public string?            PackageHint         { get; set; }
        [JsonPropertyName("spatial_position")]       public SpatialPosition?   SpatialPosition     { get; set; }
    }

    internal class Group4Geometry
    {
        [JsonPropertyName("units")]              public string?       Units            { get; set; } = "mm";
        [JsonPropertyName("bounding_box")]       public BoundingBox?  BoundingBox      { get; set; }
        [JsonPropertyName("area")]               public double?       Area             { get; set; }
        [JsonPropertyName("length")]             public double?       Length           { get; set; }
        [JsonPropertyName("volume")]             public double?       Volume           { get; set; }
        [JsonPropertyName("geometry_type")]      public string?       GeometryType     { get; set; }
        [JsonPropertyName("is_closed_boundary")] public bool?         IsClosedBoundary { get; set; }
    }

    internal class Group5Classification
    {
        [JsonPropertyName("native_category")]   public string?  NativeCategory   { get; set; }
        [JsonPropertyName("native_type")]       public string?  NativeType       { get; set; }
        [JsonPropertyName("ifc_class")]         public string?  IfcClass         { get; set; }
        [JsonPropertyName("omniclass_code")]    public string?  OmniclassCode    { get; set; }
        [JsonPropertyName("uniclass_code")]     public string?  UniclassCode     { get; set; }
        [JsonPropertyName("renovation_status")] public string?  RenovationStatus { get; set; } = "unknown";
        [JsonPropertyName("structural_flag")]   public string?  StructuralFlag   { get; set; } = "unknown";
        [JsonPropertyName("function_hint")]     public string?  FunctionHint     { get; set; }
    }

    internal class Group6Metadata
    {
        [JsonPropertyName("metadata_format")]   public string?  MetadataFormat   { get; set; } = "xdata";
        [JsonPropertyName("existing_metadata")] public Dictionary<string, Dictionary<string, string?>>? ExistingMetadata { get; set; }
        [JsonPropertyName("bim_properties")]    public BimProperties? BimProperties { get; set; } = new();
    }

    /// <summary>
    /// Per Canonical Schema v0.2 envelope, Group 7 is duplicated into EVERY object
    /// (the same document-level context plus the per-object xref bits).
    /// Top-level ExtractionPayload still carries `document_project_token` for
    /// envelope convenience; spec note: "same as Group 7 field".
    /// </summary>
    internal class Group7Source
    {
        // Document-level context (same value on every object in a payload)
        [JsonPropertyName("source_software")]         public string  SourceSoftware        { get; set; } = "bricscad";
        [JsonPropertyName("source_software_version")] public string  SourceSoftwareVersion { get; set; } = string.Empty;
        [JsonPropertyName("source_software_type")]    public string  SourceSoftwareType    { get; set; } = "2d_cad";
        [JsonPropertyName("plugin_version")]          public string  PluginVersion         { get; set; } = "1.0.3";
        [JsonPropertyName("file_name_hash")]          public string  FileNameHash          { get; set; } = string.Empty;
        [JsonPropertyName("file_name_display")]       public string? FileNameDisplay       { get; set; }
        [JsonPropertyName("document_project_token")]  public string  DocumentProjectToken  { get; set; } = string.Empty;
        [JsonPropertyName("extracted_at")]            public string  ExtractedAt           { get; set; } = string.Empty;
        [JsonPropertyName("extraction_scope")]        public string  ExtractionScope       { get; set; } = "active_model";
        [JsonPropertyName("layout_or_view_context")]  public string? LayoutOrViewContext   { get; set; }
        [JsonPropertyName("extraction_trigger")]      public string  ExtractionTrigger     { get; set; } = "on_save";

        // Per-object xref context
        [JsonPropertyName("is_xref_origin")]  public bool?    IsXrefOrigin { get; set; }
        [JsonPropertyName("xref_file_name")]  public string?  XrefFileName { get; set; }
        [JsonPropertyName("xref_file_hash")]  public string?  XrefFileHash { get; set; }
        [JsonPropertyName("xref_status")]     public string?  XrefStatus   { get; set; }
    }

    internal class Group8Quality
    {
        [JsonPropertyName("element_type_origin")]    public string  ElementTypeOrigin    { get; set; } = "native";
        [JsonPropertyName("room_origin")]            public string  RoomOrigin           { get; set; } = "unknown";
        [JsonPropertyName("area_origin")]            public string  AreaOrigin           { get; set; } = "calculated";
        [JsonPropertyName("classification_origin")]  public string  ClassificationOrigin { get; set; } = "inferred";
        [JsonPropertyName("naming_confidence")]      public string  NamingConfidence     { get; set; } = "medium";
        [JsonPropertyName("stable_id_confidence")]   public string  StableIdConfidence   { get; set; } = "plugin_generated";
        [JsonPropertyName("overall_signal_quality")] public string  OverallSignalQuality { get; set; } = "medium";
    }

    internal class SpatialPosition
    {
        [JsonPropertyName("x")]    public double X    { get; set; }
        [JsonPropertyName("y")]    public double Y    { get; set; }
        [JsonPropertyName("z")]    public double Z    { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    }

    internal class BoundingBox
    {
        [JsonPropertyName("width")]  public double Width  { get; set; }
        [JsonPropertyName("height")] public double Height { get; set; }
        [JsonPropertyName("depth")]  public double Depth  { get; set; }
    }

    internal class BimProperties
    {
        [JsonPropertyName("fire_rating")]         public string?  FireRating        { get; set; }
        [JsonPropertyName("acoustic_rating")]     public string?  AcousticRating    { get; set; }
        [JsonPropertyName("manufacturer")]        public string?  Manufacturer      { get; set; }
        [JsonPropertyName("manufacturer_model")]  public string?  ManufacturerModel { get; set; }
        [JsonPropertyName("description")]         public string?  Description       { get; set; }
        [JsonPropertyName("material_name")]       public string?  MaterialName      { get; set; }
        [JsonPropertyName("surface_finish")]      public string?  SurfaceFinish     { get; set; }
        [JsonPropertyName("thermal_resistance")]  public double?  ThermalResistance { get; set; }
        [JsonPropertyName("load_bearing")]        public bool?    LoadBearing       { get; set; }
        [JsonPropertyName("phase")]               public string?  Phase             { get; set; }
    }

    internal class DynamicBlockState
    {
        [JsonPropertyName("block_name")]       public string? BlockName       { get; set; }
        [JsonPropertyName("visibility_state")] public string? VisibilityState { get; set; }
        [JsonPropertyName("properties")]       public Dictionary<string, string?>? Properties { get; set; }
    }

    internal class RoomSignal
    {
        [JsonPropertyName("native_id")]                  public string?        NativeId                  { get; set; }
        [JsonPropertyName("raw_name")]                   public string?        RawName                   { get; set; }
        [JsonPropertyName("layer_or_tag")]               public string?        LayerOrTag                { get; set; }
        [JsonPropertyName("boundary_area")]              public double?        BoundaryArea              { get; set; }
        [JsonPropertyName("volume")]                     public double?        Volume                    { get; set; }
        [JsonPropertyName("height")]                     public double?        Height                    { get; set; }
        [JsonPropertyName("unit")]                       public string         Unit                      { get; set; } = "sqm";
        [JsonPropertyName("is_closed_boundary")]         public bool?          IsClosedBoundary          { get; set; }
        [JsonPropertyName("zone_number")]                public string?        ZoneNumber                { get; set; }
        [JsonPropertyName("zone_category")]              public string?        ZoneCategory              { get; set; }
        [JsonPropertyName("contained_object_native_ids")] public List<string>? ContainedObjectNativeIds  { get; set; }
        [JsonPropertyName("airenoos_ref")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AirenoosRef? AirenoosRef { get; set; }
    }

    internal class LayerSignal
    {
        // Spec §3.2.4
        [JsonPropertyName("name")]         public string?  Name        { get; set; }
        [JsonPropertyName("visible")]      public bool     Visible     { get; set; }
        [JsonPropertyName("locked")]       public bool     Locked      { get; set; }
        [JsonPropertyName("object_count")] public int      ObjectCount { get; set; }
        // Brian Round 2 signal #6 — "layer properties" extras within agreed scope
        [JsonPropertyName("color")]        public string?  Color       { get; set; }
        [JsonPropertyName("linetype")]     public string?  Linetype    { get; set; }
        [JsonPropertyName("is_frozen")]    public bool     IsFrozen    { get; set; }
    }

    // v0.3 Developer doc Group 10
    internal class AnnotationSignal
    {
        [JsonPropertyName("native_id")]            public string?       NativeId           { get; set; }
        [JsonPropertyName("text_content")]         public string?       TextContent        { get; set; }
        [JsonPropertyName("entity_type")]          public string?       EntityType         { get; set; }
        [JsonPropertyName("layer")]                public string?       Layer              { get; set; }
        [JsonPropertyName("location")]             public Point2D?      Location           { get; set; }
        [JsonPropertyName("layout_context")]       public string?       LayoutContext      { get; set; }
        [JsonPropertyName("nearby_object_ids")]    public List<string>? NearbyObjectIds    { get; set; }
        [JsonPropertyName("annotation_type_hint")] public string?       AnnotationTypeHint { get; set; }
    }

    // v0.3 Developer doc Group 11
    internal class DimensionSignal
    {
        [JsonPropertyName("native_id")]           public string?       NativeId          { get; set; }
        [JsonPropertyName("measured_value")]      public double?       MeasuredValue     { get; set; }
        [JsonPropertyName("unit")]                public string?       Unit              { get; set; }
        [JsonPropertyName("dimension_type")]      public string?       DimensionType     { get; set; }
        [JsonPropertyName("layer")]               public string?       Layer             { get; set; }
        [JsonPropertyName("location")]            public Point2D?      Location          { get; set; }
        [JsonPropertyName("measured_object_ids")] public List<string>? MeasuredObjectIds { get; set; }
    }

    // v0.3 Developer doc Group 12
    internal class HatchSignal
    {
        [JsonPropertyName("native_id")]           public string?  NativeId          { get; set; }
        [JsonPropertyName("pattern_name")]        public string?  PatternName       { get; set; }
        [JsonPropertyName("layer")]               public string?  Layer             { get; set; }
        [JsonPropertyName("boundary_native_id")]  public string?  BoundaryNativeId  { get; set; }
        [JsonPropertyName("nearby_finish_note")]  public string?  NearbyFinishNote  { get; set; }
        [JsonPropertyName("location_centroid")]   public Point2D? LocationCentroid  { get; set; }
    }

    internal class UnresolvedObjectSignal
    {
        [JsonPropertyName("native_id")] public string? NativeId { get; set; }
        [JsonPropertyName("raw_name")]  public string? RawName  { get; set; }
        [JsonPropertyName("reason")]    public string? Reason   { get; set; }
        [JsonPropertyName("detail")]    public string? Detail   { get; set; }
    }

    // ── v0.3 Layer 2 extended top-level signal classes (Developer doc Groups 9–15) ────

    internal class CadTableSignal
    {
        [JsonPropertyName("table_type_hint")]      public string?       TableTypeHint     { get; set; }
        [JsonPropertyName("title_text")]           public string?       TitleText         { get; set; }
        [JsonPropertyName("layer")]                public string?       Layer             { get; set; }
        [JsonPropertyName("location")]             public Point2D?      Location          { get; set; }
        [JsonPropertyName("layout_context")]       public string?       LayoutContext     { get; set; }
        [JsonPropertyName("rows")]                 public List<CadTableRow>? Rows         { get; set; }
        [JsonPropertyName("nearby_block_tag_refs")] public List<string>? NearbyBlockTagRefs { get; set; }
    }

    internal class CadTableRow
    {
        [JsonPropertyName("cells")] public List<string>? Cells { get; set; }
    }

    internal class XrefReferenceSignal
    {
        [JsonPropertyName("xref_name")]            public string? XrefName            { get; set; }
        [JsonPropertyName("xref_file_hash")]       public string? XrefFileHash        { get; set; }
        [JsonPropertyName("xref_path_status")]     public string? XrefPathStatus      { get; set; }
        [JsonPropertyName("object_count_in_xref")] public int     ObjectCountInXref   { get; set; }
    }

    internal class EntitySourceSummary
    {
        [JsonPropertyName("native_count")] public int          NativeCount  { get; set; }
        [JsonPropertyName("xref_count")]   public int          XrefCount    { get; set; }
        [JsonPropertyName("xref_sources")] public List<string> XrefSources  { get; set; } = new List<string>();
    }

    internal class LayerPropertiesSignal
    {
        [JsonPropertyName("name")]         public string?     Name         { get; set; }
        [JsonPropertyName("color_index")]  public int?        ColorIndex   { get; set; }
        [JsonPropertyName("color_rgb")]    public RgbColor?   ColorRgb     { get; set; }
        [JsonPropertyName("linetype")]     public string?     Linetype     { get; set; }
        [JsonPropertyName("lineweight")]   public int?        Lineweight   { get; set; }
        [JsonPropertyName("frozen")]       public bool        Frozen       { get; set; }
        [JsonPropertyName("locked")]       public bool        Locked       { get; set; }
        [JsonPropertyName("visible")]      public bool        Visible      { get; set; }
        [JsonPropertyName("object_count")] public int         ObjectCount  { get; set; }
        [JsonPropertyName("is_xref_layer")] public bool       IsXrefLayer  { get; set; }
    }

    internal class RgbColor
    {
        [JsonPropertyName("r")] public int R { get; set; }
        [JsonPropertyName("g")] public int G { get; set; }
        [JsonPropertyName("b")] public int B { get; set; }
    }

    internal class DynamicBlockTopLevel
    {
        [JsonPropertyName("native_id")]               public string? NativeId             { get; set; }
        [JsonPropertyName("block_definition_name")]   public string? BlockDefinitionName  { get; set; }
        [JsonPropertyName("active_visibility_state")] public string? ActiveVisibilityState { get; set; }
        [JsonPropertyName("dynamic_properties")]      public Dictionary<string, string?>? DynamicProperties { get; set; }
        [JsonPropertyName("is_dynamic")]              public bool    IsDynamic            { get; set; } = true;
    }

    internal class LevelSignal
    {
        [JsonPropertyName("native_id")]              public string? NativeId            { get; set; }
        [JsonPropertyName("name")]                   public string? Name                { get; set; }
        [JsonPropertyName("elevation")]              public double  Elevation           { get; set; }
        [JsonPropertyName("unit")]                   public string  Unit                { get; set; } = "mm";
        [JsonPropertyName("is_building_story")]      public bool    IsBuildingStory     { get; set; }
        [JsonPropertyName("object_count_on_level")]  public int     ObjectCountOnLevel  { get; set; }
    }

    internal class Point2D
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
    }
}
