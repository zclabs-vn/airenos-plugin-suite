# AiRenoOS — Canonical Plugin Schema v0.2
**Document:** AiRenoOS__Canonical_Plugin_Schema__v0.2__2026-05.md
**Folder:** 30_SPECIALIST_BUILD_MANUALS / Level 3 — Specialist Implementation
**Status:** Active Working Document
**Audience:** Kyanon MCP team, all plugin developers
**Version:** 0.2
**Date:** May 2026

---

## Important Separation Notice

This document defines the **plugin extraction schema** — the signal payload that plugins send from design tools to AirenoOS via the MCP server.

This is **not** the Backpack data structure.

The Backpack record is defined separately and is significantly richer. It contains everything the plugin sends plus: approval history, cost data from BOQ, timing data from Gantt, supplier quotes, procurement status, communication threads, decision receipts, conflict history, source trace, library linkage, temporal trail, cross-file references, readiness scores, evidence attachments, and phase status.

**The relationship is:**
```
Plugin extraction schema
→ sends raw signals to AirenoOS via MCP
→ AirenoOS interprets and enriches
→ Backpack record holds the governed project truth
→ Backpack grows richer over the project lifetime
→ Plugin schema stays lean and consistent
```

The plugin captures signals. The Backpack governs truth. They are related but never the same thing.

---

## Schema Purpose

The canonical plugin schema is the **contract between all plugin developers and the AirenoOS MCP server**.

Every plugin — regardless of which design software it is built for — must output data conforming to this schema. The MCP server receives one consistent payload format. AirenoOS does not need to understand the internal APIs of each design tool.

**Core principle:** The plugin sends native signals. AirenoOS decides what they mean. Never the other way around.

---

## BIM vs Non-BIM Signal Quality

Different design software provides different quality signals. This affects what AirenoOS can do with the data, not what the schema looks like. The schema is identical across all platforms.

| Software Type | Identity | Classification | Room/Space | Rich Properties |
|---|---|---|---|---|
| BIM (Revit, ArchiCAD, Vectorworks) | Native stable GUID | Native element type | Native space object | Fire rating, acoustic, manufacturer, renovation status — native |
| 3D Non-BIM (SketchUp, Rhino) | Plugin-generated UUID | Inferred from name/tag | Inferred from geometry | None native — user metadata only |
| 2D CAD (AutoCAD, BricsCAD) | Handle or plugin-generated UUID | Inferred from layer/block name | Inferred from closed polyline | Block attributes only |

For BIM software, the `existing_metadata` group carries structured property groups (fire rating, acoustic rating, manufacturer, renovation status, structural function, IFC properties etc.) as a nested dictionary. AirenoOS promotes relevant fields into the Backpack record.

For non-BIM software, the `existing_metadata` group carries whatever the user manually attached to objects. AirenoOS must infer more from the naming signal stack.

---

## Stable Native ID — Per Software Rules

The `native_id` field must be **stable across saves, sessions, copy-paste, and file moves**.

| Software | Stable ID Source | Rule |
|---|---|---|
| ArchiCAD | Native Unique ID (GUID) | Use directly. Always stable. |
| Revit | Native Element UniqueId | Use directly. Always stable. |
| Vectorworks | Native 128-bit UUID per object | Use directly. Reliable. See template note below. |
| Rhino | Object GUID | Generally stable. Can regenerate in some operations — confirm with developer. |
| SketchUp | Plugin-generated UUID stored in attribute dictionary | Do NOT use entityID — it resets every session. Generate UUID on first encounter, store in `aireno` attribute dictionary, read back on all subsequent extractions. |
| AutoCAD | XDATA-stored UUID | Do NOT rely on handle alone — can break on purge. Generate UUID in XDATA on first extraction, read back subsequently. |

**Vectorworks template note:** Object UUIDs are the same across document copies created from the same template. The plugin must check for a document-level AirenoOS project token on first connection. If none exists, generate and store one in the document. Use this token to differentiate files that share template UUIDs.

**Duplicate object rule:** When the same `native_id` appears at two different spatial positions (copy-paste), the plugin must treat them as separate objects using `native_id + spatial_position_hash` as the combined unique identifier. The copy inherits definition metadata as naming hints only — not as confirmed identity. AirenoOS assigns a new `backpack_id` to the copy.

---

## The Eight Signal Groups

### Group 1 — Native Object Identity

```json
{
  "native_id": "string — stable ID per software rules above",
  "native_id_type": "guid | handle | xdata_uuid | plugin_generated_uuid",
  "native_id_stability": "stable | session_only | unknown",
  "aireno_backpack_id": null,
  "identity_state": "raw | candidate | confirmed | locked",
  "link_state": "unlinked | linked | unknown",
  "is_definition": false,
  "definition_id": "string or null — source definition/family/block this instance came from",
  "instance_id": "string or null — this specific placed instance"
}
```

**Notes:**
- `aireno_backpack_id` is null in V1 until confirmed by user in cockpit. In V1 writeback, the plugin writes the confirmed backpack_id here.
- `is_definition` distinguishes reusable source objects from placed instances. A SketchUp component definition is `is_definition: true`. A placed instance is `is_definition: false`.
- `identity_state` begins as `raw` on first extraction. Progresses through confirmation.

---

### Group 2 — Naming Signal Stack

This is the most important group for non-BIM software. Send all available signals. Do not send only the raw name.

```json
{
  "visible_label": "string — raw name exactly as it appears in the software",
  "definition_name": "string or null — component/block/family/library part name",
  "layer_or_tag_name": "string or null — layer or tag this object belongs to",
  "parent_container_name": "string or null — immediate parent group/block/assembly name",
  "hierarchy_path_names": ["Level 1", "Kitchen", "Sink Group"],
  "room_label_signal": "string or null — room name if detectable near or containing this object",
  "nearby_text_labels": ["string"] ,
  "attribute_text": {"key": "value"},
  "file_or_sheet_name_signal": "string or null — drawing/sheet/file name context",
  "scene_or_view_name": "string or null — active scene or view name if available",
  "naming_origin": "user_typed | native_classification | layer_derived | block_definition | inherited | unknown",
  "raw_aliases": ["string — any shorthand, code, or alternate name found"]
}
```

**Notes:**
- `nearby_text_labels` — for 2D CAD: text entities spatially near this object. For 3D: scene names, group names in hierarchy.
- `attribute_text` — for AutoCAD block attributes, ArchiCAD element IDs, Vectorworks record fields, SketchUp attribute dictionaries.
- `raw_aliases` — office shorthand found in names (e.g. "FD" for floor drain, "K-SINK" for kitchen sink). Send these separately so Aireno can match against the primitive library.
- For BIM software, `naming_origin` is typically `native_classification`. For SketchUp/Rhino it is usually `user_typed` or `layer_derived`.

---

### Group 3 — Spatial and Container Context

```json
{
  "room_or_zone_native_id": "string or null",
  "room_or_zone_name": "string or null",
  "level_or_floor": "string or null — storey, level, or floor name",
  "parent_native_id": "string or null — stable ID of immediate parent container",
  "children_native_ids": ["string"],
  "container_type": "group | component | block | symbol | zone | layer | xref | family | unknown",
  "package_hint": "fixture | cabinet | wet_area | structural | electrical | mechanical | unknown",
  "spatial_position": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0,
    "unit": "mm"
  }
}
```

**Notes:**
- `spatial_position` is the insertion point or centroid. Used for duplicate object detection and room inference.
- `package_hint` is the plugin's best guess at trade category from layer name or classification. Aireno confirms or overrides.
- For BIM software, `room_or_zone_native_id` comes from native zone/space object. For non-BIM, it is inferred from spatial containment — the plugin checks if the object's position is inside a known room boundary.

---

### Group 4 — Geometry Signal

```json
{
  "units": "mm | cm | m | ft | in",
  "bounding_box": {
    "width": 0.0,
    "height": 0.0,
    "depth": 0.0
  },
  "area": null,
  "length": null,
  "volume": null,
  "geometry_type": "mesh | brep | block | component | polyline | wall | door | window | zone | object | unknown",
  "is_closed_boundary": null
}
```

**Notes:**
- `area` — for rooms/zones: floor area. For walls: surface area. Send null if not applicable.
- `is_closed_boundary` — true if this is a closed room boundary (relevant for 2D CAD room detection from closed polylines).
- For BIM software, geometry values are calculated natively. For non-BIM, the plugin calculates from the bounding box.

---

### Group 5 — Classification and Function Hint

```json
{
  "native_category": "string or null — software-native category/type label",
  "native_type": "string or null — element type (Wall, Door, Zone, ComponentInstance etc.)",
  "ifc_class": "string or null — IFC classification if available",
  "omniclass_code": "string or null",
  "uniclass_code": "string or null",
  "renovation_status": "existing | new | demolished | unknown",
  "structural_flag": "structural | non_structural | unknown",
  "function_hint": "string or null — Aireno-mapped function guess from layer/type/name"
}
```

**Notes:**
- For BIM software, `renovation_status` and `structural_flag` are native fields. For non-BIM, send `unknown`.
- `function_hint` is the plugin's best interpretation. Aireno will refine against the primitive library.
- ArchiCAD: populate `renovation_status` from Renovation Status property, `structural_flag` from Structural Function property.
- Revit: populate from Category, Phase, and Structural Usage parameters.

---

### Group 6 — Existing Metadata Passthrough

```json
{
  "metadata_format": "attribute_dictionary | xdata | record_format | user_string | ifc_property_set | parameter | unknown",
  "existing_metadata": {
    "PropertyGroupName": {
      "PropertyName": "value"
    }
  },
  "bim_properties": {
    "fire_rating": "string or null",
    "acoustic_rating": "string or null",
    "manufacturer": "string or null",
    "manufacturer_model": "string or null",
    "description": "string or null",
    "material_name": "string or null",
    "surface_finish": "string or null",
    "thermal_resistance": null,
    "load_bearing": null,
    "phase": "string or null"
  }
}
```

**Notes:**
- `existing_metadata` is a nested passthrough. For ArchiCAD, the outer keys are property group names (Window/Door, Wall, Zone, General Parameters, Building Material, Components etc.) as provided by Agi's property list. For Revit, they are parameter group names. For SketchUp, they are attribute dictionary names.
- `bim_properties` extracts the most workflow-relevant BIM fields into explicit named fields so Kyanon does not need to parse the full metadata passthrough to find fire rating or manufacturer. For non-BIM software, all fields are null.
- Do not attempt to send ALL ArchiCAD calculated properties. Send only the properties listed in the confirmed scope agreed with Agi. Use `existing_metadata` as the passthrough for everything else.

---

### Group 7 — Source and Extraction Context

```json
{
  "source_software": "sketchup | autocad | rhino | archicad | vectorworks | revit | bricscad | chief_architect",
  "source_software_version": "string",
  "source_software_type": "2d_cad | 3d_non_bim | bim",
  "plugin_version": "string",
  "file_name_hash": "string — SHA256 hash of full file path",
  "file_name_display": "string or null — for testing only, omit in production",
  "document_project_token": "string — AirenoOS project token stored in document on first connection",
  "extracted_at": "ISO 8601 timestamp with timezone",
  "extraction_scope": "active_model | selection | active_layer | full_file",
  "layout_or_view_context": "string or null — sheet, layout, or view name if applicable",
  "extraction_trigger": "on_save | manual_command | api_request"
}
```

**Notes:**
- `document_project_token` — stored in the document by the plugin on first connection. Used to differentiate files that share template UUIDs (Vectorworks) and to re-link objects on subsequent sessions (all platforms).
- `extraction_trigger` — `on_save` is the primary trigger. `manual_command` for user-initiated extraction. `api_request` for MCP-initiated pull requests (Phase 2).

---

### Group 8 — Signal Quality

```json
{
  "element_type_origin": "native | inferred | unknown",
  "room_origin": "native | spatial_inference | boundary_inference | naming_inference | unknown",
  "area_origin": "native | calculated | estimated | unknown",
  "classification_origin": "native | inferred | unknown",
  "naming_confidence": "strong | medium | weak | unknown",
  "stable_id_confidence": "native_stable | plugin_generated | session_only | unknown",
  "overall_signal_quality": "high | medium | low | unknown"
}
```

**Notes:**
- These fields tell Kyanon and Aireno how much to trust each signal group before presenting candidates to the user.
- `high` overall quality → candidate proposed for auto-confirm threshold consideration
- `medium` → proposed for batch user review
- `low` → routed to Unresolved Queue with reason code
- For BIM software, most fields will be `native` and `strong`. For non-BIM, most will be `inferred` or `unknown`.

---

## Full Payload Envelope

```json
{
  "aireno_schema_version": "0.2",
  "payload_type": "extraction",
  "project_id": "string — AirenoOS project ID provided at registration",
  "authentication": {
    "bearer_token": "string — provided by AirenoOS at project registration"
  },
  "document_project_token": "string — same as Group 7 field",
  "objects": [
    {
      "group_1_identity": { },
      "group_2_naming": { },
      "group_3_spatial": { },
      "group_4_geometry": { },
      "group_5_classification": { },
      "group_6_metadata": { },
      "group_7_source": { },
      "group_8_quality": { }
    }
  ],
  "rooms": [
    {
      "native_id": "string",
      "raw_name": "string",
      "layer_or_tag": "string",
      "boundary_area": 0.0,
      "volume": null,
      "height": null,
      "unit": "sqm",
      "is_closed_boundary": true,
      "zone_number": "string or null",
      "zone_category": "string or null",
      "contained_object_native_ids": ["string"],
      "aireno_backpack_id": null
    }
  ],
  "layers_or_tags": [
    {
      "name": "string",
      "visible": true,
      "locked": false,
      "object_count": 0
    }
  ],
  "unresolved_objects": [
    {
      "native_id": "string or null",
      "raw_name": "string or null",
      "reason": "no_name | no_layer | nested_ambiguity | unstable_id | no_geometry | other",
      "detail": "string — human readable explanation"
    }
  ],
  "extraction_summary": {
    "total_objects": 0,
    "total_rooms": 0,
    "total_layers": 0,
    "unresolved_count": 0,
    "high_quality_count": 0,
    "medium_quality_count": 0,
    "low_quality_count": 0
  }
}
```

---

## V1 Writeback Payload

After a user confirms an object's identity in the AirenoOS cockpit, the MCP server dispatches a writeback payload to the plugin. The plugin applies it through a manual trigger command, never during a save callback.

```json
{
  "aireno_schema_version": "0.2",
  "payload_type": "identity_writeback",
  "project_id": "string",
  "writebacks": [
    {
      "target": {
        "source_software": "sketchup | autocad | rhino | archicad | vectorworks | revit",
        "native_id": "string",
        "document_project_token": "string"
      },
      "writeback": {
        "aireno_backpack_id": "IT_024",
        "confirmed_visible_label": "RM_KIT_SINK_01",
        "confirmed_room_id": "RM_KIT_01",
        "confirmed_package_id": "PKG_KIT_FIXTURE_01",
        "identity_state": "confirmed",
        "last_synced": "ISO 8601 timestamp"
      },
      "rules": {
        "requires_user_confirmation": true,
        "do_not_overwrite_if_locked": true,
        "preserve_previous_label_in_metadata": true,
        "do_not_trigger_during_shutdown": true,
        "do_not_cascade_observer_callbacks": true
      }
    }
  ]
}
```

**Writeback rules:**
- Plugin writes `aireno_backpack_id` and `identity_state` into the object's native metadata store (attribute dictionary / XDATA / record format / user string)
- Plugin updates the object's visible display name to `confirmed_visible_label`
- Plugin preserves the previous visible label in metadata under key `aireno_previous_label`
- Plugin must NOT trigger writeback during a save callback — risk of crash and observer cascade
- Plugin must NOT trigger writeback during application shutdown sequence
- Writeback is triggered by manual menu command in V1 only
- Plugin must guard against triggering saves which trigger callbacks which trigger saves (infinite loop)

**SketchUp-specific writeback warning (confirmed by DanRathbun):**
Manual writeback can trigger observer change callbacks in other installed extensions. The plugin must implement a writeback lock flag to prevent re-entrant callback cascades.

---

## V2 Placeholder Fields

The following fields are declared now but not populated in V1. Plugin developers must not build V2 behaviour in V1. These fields exist so the schema does not need to be redesigned for Phase 2.

```json
{
  "v2_change_detection": {
    "changed_since_last_sync": null,
    "change_type": null,
    "previous_value": null,
    "tolerance_exceeded": null
  },
  "v2_selection_state": {
    "is_selected_in_tool": null,
    "selection_source": null
  },
  "v2_visual_marker_state": {
    "marker_type": null,
    "marker_color": null
  }
}
```

---

## HTTP Communication

```
Method:          HTTP POST
Content-Type:    application/json
Authentication:  Bearer token in Authorization header
Endpoint:        Provided by AirenoOS at project registration
                 Test endpoint: https://mcp.airenos.io/v1/extract (during integration testing)
Response 200:    { "status": "accepted", "extraction_id": "string" }
Response 4xx:    { "error": "string", "detail": "string" }
Retry:           Retry once on timeout (30 second timeout)
Offline:         Save payload to local temp file, retry on next extraction trigger
```

**Async rule:** Data collection and JSON building happen synchronously during the save event. The HTTP POST fires asynchronously after the save completes. The user is never blocked by the network call.

---

## Per-Software Implementation Notes

### SketchUp (Ruby .rbz)
- Stable ID: Plugin-generated UUID in `aireno` attribute dictionary. Do NOT use `entityID` — it resets every session.
- Room detection: Closed face loops or groups named with room-like patterns. Spatial position check against known room boundaries.
- Writeback metadata: `entity.set_attribute('aireno', 'backpack_id', value)` and `entity.set_attribute('aireno', 'identity_state', value)`
- Save hook: `onPostSaveModel` observer. Collect synchronously, POST asynchronously.
- Writeback timing: Manual command only. Never in observer callback. Guard against shutdown save prompt. Guard against cascading callbacks from other extensions.

### AutoCAD (C# .NET or Python)
- Stable ID: XDATA-stored UUID generated on first extraction. Handle alone is not sufficient — can break on purge.
- Room detection: Closed Polyline or Region entities. Text/MText labels near boundary for room name.
- Writeback metadata: XDATA on entity using registered application name `AIRENO`.
- Blocks: BlockReference insertion points plus attribute values.
- Save hook: `Database.SaveComplete` event.

### Rhino (Python via RhinoCommon)
- Stable ID: `obj.Attributes.GetUserString("aireno_id")`. Check if exists on extraction. Generate and store UUID if missing.
- Room detection: Closed planar curves. Layer name inference.
- Writeback metadata: `obj.Attributes.SetUserString("aireno_backpack_id", value)`
- Groups: `doc.Groups` for group membership.
- Save hook: File save event callback.

### ArchiCAD (Python via AC API)
- Stable ID: Native `Unique ID` property — always stable. Use directly.
- Room detection: Zone objects via `ac.commands.GetAllElements()` filtered by Zone type.
- Properties: Use `GetElementsProperties()`. Send property group structure preserved (Window/Door, Wall, Zone, General Parameters, Building Material, Components etc.)
- BIM properties: Fire Rating, Acoustic Rating, Renovation Status, Structural Function, Manufacturer — all native. Include in `bim_properties` group.
- Writeback metadata: Write backpack_id to custom property set or IFC property via `SetElementsProperties()`.
- Confirmed ArchiCAD property scope: Per Agi's highlighted property list (Unique ID, Element Type, Library Part Name, Zone Name, Related Zone Name, Layer Name, Renovation Status, Fire Rating, Acoustic Rating, Manufacturer, Description, Surface Area, Volume, Length, Width, Height, Locked).

### Vectorworks (C++ SDK)
- Stable ID: Native 128-bit UUID per object — always stable. Use directly.
- Template file issue: Check for document-level AirenoOS project token on first connection. If absent, generate and store.
- Room detection: Space objects via Space object API. Name, area, associated walls available.
- Records: Read and write via standard record API. IFC data also accessible.
- HTTP: C++ SDK WebCallbacks. Do not use Python — cannot handle save events.
- Plugin signing: Contact Vectorworks for Partner Products listing to avoid startup dialogs in VW2026+.
- Writeback: Writing data back confirmed possible. Undo/redo may affect change detection in V2.

---

## Standard Question Set for Plugin Developers

Before providing a final quote, all plugin developers must answer these questions:

1. What is the stable native ID in your software, and does it survive: file saves, session close/reopen, copy-paste, and purge operations?
2. Where can we store our `aireno_backpack_id` and `identity_state` in the object's metadata so it persists between sessions?
3. Can we safely update the object's visible display name from outside a save callback?
4. How do we detect if our writeback triggers callbacks in other extensions, and how do we prevent cascade loops?
5. Can the plugin detect if the application is in a shutdown or closing state, and skip writeback in that case?
6. For each of the eight signal groups, which fields can your software provide natively, which require inference or calculation, and which are genuinely not available?
7. Does your V1 architecture support Phase 2 additions — writeback, change detection, object selection/highlight, library placement — without requiring a rewrite?
8. For room/space objects: what is the most reliable method to get room name, area, and contained objects?

---

## Build Rules for Kyanon MCP Ingestion

1. The MCP server receives one schema format regardless of source software. Software-specific differences are handled by the `source_software_type` and `group_8_quality` fields.

2. The MCP server must validate every incoming payload against this schema before passing to the backend. Invalid payloads are rejected with a 400 response and logged.

3. The MCP server creates a Backpack candidate record for every object in the `objects` array. The Backpack record is richer than the plugin payload — the MCP server must not treat the plugin payload as the complete Backpack structure.

4. Objects with `overall_signal_quality: low` or reason codes in `unresolved_objects` are routed to the Unresolved Queue in the cockpit. They are never silently dropped.

5. The `aireno_backpack_id` field in the extraction payload is read by the MCP server to detect already-linked objects. If populated, the object is a re-extraction of a previously confirmed object. If null, it is a new candidate.

6. The MCP server dispatches writeback payloads to plugins only after explicit user confirmation in the cockpit. Never automatically, never during extraction processing.

7. Room objects are processed separately from other objects and seeded into Backpack as room records. Room confirmation always requires user action — rooms are never auto-confirmed regardless of signal quality.

8. The `extraction_summary` counts are used by the cockpit to display extraction status to the user. Always populate accurately.

9. The `document_project_token` must be checked against the registered project on every extraction. Mismatched tokens are flagged and the user is notified.

10. The BIM `bim_properties` fields (fire_rating, acoustic_rating, manufacturer etc.) should be promoted into dedicated Backpack fields when present. Do not leave them only in the metadata passthrough.

---

