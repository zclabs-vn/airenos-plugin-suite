# AirenoOS Plugin Suite

> CAD/BIM metadata extraction and identity sync plugins for **AutoCAD**, **Revit**, and **BricsCAD** — built for the [AirenoOS](https://airenos.io) renovation project management platform.

---

## Overview

The AirenoOS Plugin Suite connects design tools to the AirenoOS backend, enabling renovation and interior design firms to track every element across drawings and building models with a governed, stable identity.

Each plugin:
- Extracts structured metadata from all objects on save
- Assigns a **stable unique identity** per object (XDATA UUID for CAD, native UniqueId for Revit)
- Serializes the data to the **Canonical Plugin Schema v0.2** (JSON)
- Posts the payload asynchronously to the **AirenoOS MCP endpoint**
- Supports manual **identity writeback** — writing confirmed IDs back to object XDATA/parameters

---

## Repository Structure

```
airenos-plugin-suite/
├── autocad-plugin/       # AutoCAD .NET plugin (C#) — Phase 1 & 2
├── revit-plugin/         # Revit API plugin (C#) — Phase 1 & 2
├── bricscad-plugin/      # BricsCAD .NET plugin (C#) — Phase 1 & 2
└── docs/                 # Technical specs, schema reference, project notes
```

---

## Platform Support

| Platform  | Versions              | Runtime                    | Status          |
|-----------|-----------------------|----------------------------|-----------------|
| AutoCAD   | 2024 / 2025 / 2026    | .NET Fx 4.8 / .NET 8       | In development  |
| Revit     | 2024 / 2025 / 2026    | .NET Fx 4.8 / .NET 8       | In development  |
| BricsCAD  | V26                   | .NET 8                     | In development  |

---

## Technical Architecture

### Phase 1 — Core Extraction & Sync

**AutoCAD & BricsCAD**
- Hook: `Database.SaveComplete` event
- Traverses all entities in the drawing on save
- Stable object ID: XDATA-stored UUID registered under app name `AIRENO`
- Copy-paste collision detection: UUID + spatial position hash
- Observer cascade prevention: `isWritebackInProgress` lock flag
- Shutdown guard: `Application.BeginQuit` → `isShuttingDown` flag
- Room detection: closed Polyline/Region boundary inference + nearby Text/MText
- Menu commands: **Connect**, **Extract Now**, **Apply Writeback**
- Writeback: manual command only, never inside save callback

**Revit**
- Hook: `DocumentSaved` event
- Traverses elements via `FilteredElementCollector`
- Stable object ID: native `UniqueId` (always stable across saves, purge, sessions)
- Room detection: native Room/Space objects via Revit API
- Rich BIM properties: Category, Phase, Structural Usage, fire rating, manufacturer, etc.
- Writeback: `IExternalCommand` triggered manually

**All platforms**
- JSON payload serialized to **Canonical Plugin Schema v0.2**
- HTTP POST async to AirenoOS MCP endpoint (non-blocking, never delays save)
- Offline queue: payload saved locally if network unavailable, retried on next trigger
- Timeout: 30s, retry once

### Phase 2 — Panel UI & Library Placement

- Dockable WPF panel UI
- Backpack library browser — browse and place 2D/3D objects from AirenoOS
- Source-linked metadata attached to placed objects
- Change detection and object highlight
- Built on Phase 1 architecture — no rewrite required

---

## Signal Groups (Canonical Plugin Schema v0.2)

The plugin extracts data across 8 signal groups per object:

| Group | Name | Key Fields |
|-------|------|------------|
| G1 | Native Object Identity | `native_id`, `aireno_backpack_id`, `identity_state` |
| G2 | Naming Signal Stack | `visible_label`, `definition_name`, `attribute_text`, `nearby_text_labels` |
| G3 | Spatial & Container Context | `room_or_zone_name`, `level_or_floor`, `spatial_position`, `package_hint` |
| G4 | Geometry Signal | `units`, `bounding_box`, `area`, `geometry_type`, `is_closed_boundary` |
| G5 | Classification & Function Hint | `native_category`, `native_type`, `ifc_class`, `renovation_status` |
| G6 | Existing Metadata Passthrough | `existing_metadata`, `bim_properties` (fire rating, manufacturer, etc.) |
| G7 | Source & Extraction Context | `source_software`, `plugin_version`, `document_project_token`, `extracted_at` |
| G8 | Signal Quality | `naming_confidence`, `stable_id_confidence`, `overall_signal_quality` |

Signal quality routing:
- `high` → candidate for auto-confirm
- `medium` → batch user review
- `low` → Unresolved Queue

---

## HTTP Communication

```
POST https://mcp.airenos.io/v1/extract
Content-Type: application/json
Authorization: Bearer <token>

Response 200: { "status": "accepted", "extraction_id": "string" }
```

Data collection happens **synchronously** during the save event. The HTTP POST fires **asynchronously** after save completes — the user is never blocked by a network call.

---

## Key Technical Decisions

| Decision | Rationale |
|----------|-----------|
| XDATA UUID (not handle) for CAD stable ID | Handles break on purge; XDATA survives |
| `document_project_token` per file | Differentiates files sharing the same element UniqueIds (Revit Save As) |
| Writeback never inside save callback | Prevents observer cascade and crash risk |
| `isWritebackInProgress` lock flag | Idempotent, re-entrant cascade safe |
| Shutdown guard via `BeginQuit` / `ApplicationClosing` | Prevents writeback on invalid document state |
| .NET 8 only (2025/2026) | Single build target, no Framework 4.8 maintenance overhead |

---

## About ZCLabs

**ZCLabs Co., Ltd** is a Vietnam-based software company specializing in CAD/BIM plugin development and Autodesk Platform Services (APS) integration for the AEC industry.

**Technical expertise:**
- AutoCAD .NET API (C#) — plugin development, XDATA, custom commands
- Revit API (C#) — element traversal, parameters, IExternalCommand
- BricsCAD .NET API (C#) — V26 / .NET 8
- Autodesk Platform Services (APS) — Viewer, Design Automation, Data Management (BIM 360/ACC/Docs), 3-legged OAuth
- 3D web visualization — Three.js, WebGL
- Azure cloud services — Functions, Blob Storage, Azure AD / Entra ID
- .NET / Azure backend integration

**Links:**
- GitHub: [github.com/zclabs-vn](https://github.com/zclabs-vn)
- Website: [zclabs.net](https://zclabs.net) *(in development)*
- Contact: cuon
