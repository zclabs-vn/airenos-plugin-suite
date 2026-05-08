# Upwork Project — Brian Sia (Hong Kong)
**Client:** Brian Sia  
**Platform:** Upwork  
**Project:** Integrated Metadata Sync Engine — CAD/BIM Plugin Suite  
**Status:** 🟡 Awaiting quote & technical answers from Cuong

---

## 📋 Project Overview

Build lightweight plugins for **AutoCAD**, **Revit**, and **BricsCAD** that:
- Extract BIM/drawing metadata
- Sync structured data to a backend API (MCP endpoint)
- Handle placement of linked 2D/3D library items from "Backpack" (client's visual library)
- Write hidden metadata (XDATA/UniqueId) back to objects
- Use a lightweight panel UI (Phase 2 only)

**Backend stack:** .NET / Azure  
**Schema:** Canonical Plugin Schema v0.2 (provided by Brian — same schema, same MCP endpoint, same signal structure across all 3 plugins)

---

## 🗓️ Conversation Timeline

### Jul 18, 2025 — Initial Contact
- Brian introduced the project: metadata sync engine for renovation workflows
- Two plugins: **Revit** (object selection → sync metadata + camera view) and **AutoCAD** (drag-and-drop 2D library items with sync back)
- Cuong confirmed availability and familiarity with .NET/Azure

### Jul 19, 2025 — Technical Clarification & First Estimate
Cuong's response:
- **Revit:** async/await pattern to avoid UI blocking
- **AutoCAD:** WPF/PaletteSet UI, scaling rules per template
- **Revit estimate:** 7–9 days / $1,400–$1,800
- **AutoCAD estimate:** 8–10 days / $1,600–$2,000

### Jul 20, 2025 — Scope Clarification from Brian
Brian asked for:
1. Deliverable preference (DLL vs installer)
2. Version support (2–3 recent versions)
3. Long-term collaboration / retainer interest
4. Bug support with Vietnam-based dev agency

Cuong responded:
- Installer available via WiX Toolset (+1 day/plugin)
- Version support: **2023, 2024, 2025** for both plugins
- Open to retainer and collaboration with Vietnam agency
- Estimate unchanged

### Jul 26, 2025 — Brian on hold
- Brian acknowledged, said they're finalizing internal preparations

---

### Apr 10, 2026 — Brian Returns (New Upwork Account)
> Brian's original account was blocked. Started fresh account.

- Secured funding, partnered with a .NET agency in Vietnam
- **Decided to start with AutoCAD only (before Revit)**
- Cuong confirmed availability

### Apr 13, 2026 — AutoCAD Scope Recap from Brian
Core features requested:
1. Read blocks, attributes, layers, text, hatches, dimensions, object positions → JSON → backend
2. Linked 2D item placement from Backpack library
3. Preserve source/project item linkage
4. Write hidden metadata (XDATA) back to objects
5. Lightweight panel UI
6. **Checkpoint / manual-sync-first** (no live sync in Phase 1)

### Apr 14, 2026 — Cuong's AutoCAD Quote
| Item | Detail |
|---|---|
| **Scope** | Extract + JSON output + Backpack placement + XDATA writeback + WPF panel + manual sync |
| **AutoCAD versions** | 2024, 2025, 2026 |
| **Duration** | 2 weeks |
| **Cost** | $2,500 USD |
| **Deliverables** | Source code, installer, user guide |
| **Key risk** | Room detection via heuristic (closed polylines/hatches) — accuracy depends on drawing quality |
| **Sample DWG** | Requested from Brian |

### Apr 15, 2026 — Brian clarifies scope intent
- Confirmed $2,500 is for plugin development only
- Expressed interest in longer-term "practical AutoCAD thinking" collaboration
- Asked Cuong's background with real AutoCAD production workflows
- Mentioned no preferred sample DWG file available

### Apr 16, 2026 — Cuong's background clarification
- Software developer, not architect/engineer
- AutoCAD experience is from **plugin/API side** — drawing structure, data flow, extraction
- Offered to prepare a test DWG on his own end first
- Open to deeper working discussion

### Apr 19, 2026 — Brian positive, asks about Upwork process
- Affirmed value of Cuong's API-side AutoCAD expertise
- Asked about Upwork payment process
- Cuong explained: Fixed-price with milestones recommended

### May 4, 2026 — BricsCAD question
- Brian asked if Cuong knows BricsCAD
- Cuong: **"Yes, almost the same as AutoCAD"**
- Brian asked about other plugins → Cuong: only AutoCAD, Revit, BricsCAD

---

### May 4, 2026 — MAJOR SCOPE EXPANSION (Latest Message from Brian)

> Brian's original Upwork account was blocked, restarted with a new account.

**Key changes:**
- Scope is **NOT reduced — it's expanded** and split into Phase 1 + Phase 2
- Now covers **all 3 platforms**: AutoCAD, Revit, BricsCAD
- Phase 2 begins immediately after Phase 1 is accepted

---

## 🔧 Full Technical Scope (As of May 4, 2026)

### AutoCAD & BricsCAD — Phase 1 (Extract + Minimal Writeback)
- Extract blocks, attributes, layers, text, hatches, dimensions, positions → JSON → POST to MCP endpoint on save
- Stable object ID via **XDATA-stored UUID** (not handle alone — handles break on purge)
- Save hook: `Database.SaveComplete`
- Minimal identity writeback via **manual menu command only**
  - Fields: confirmed label, backpack ID, identity state, room ID, timestamp → XDATA
- **3 menu items only:** Connect, Extract Now, Apply Writeback
- ❌ No panel UI in Phase 1
- ❌ No library placement in Phase 1

### AutoCAD & BricsCAD — Phase 2 (Panel UI + Library Placement)
- Lightweight dockable **WPF panel UI**
- Browse and place 2D library objects from Backpack into the drawing
- Source-linked metadata attached to placed objects
- Bidirectional event detection and object highlight

### Revit — Phase 1
- Extract object metadata on save → JSON → POST to MCP endpoint
- Stable object ID: native `UniqueId` (to be confirmed by Cuong for edge cases)
- Manual sync trigger

### Revit — Phase 2
- Panel UI
- Library item placement
- Change detection and object highlight

---

## ❓ 8 Technical Questions Brian Needs Answered (Per Software)

> Brian explicitly asked Cuong to answer these **before finalizing the quote**

1. **Stable native ID** — Does it survive file saves, session close/reopen, copy-paste, and purge? Where is `aireno_backpack_id` and `identity_state` stored?
2. **Safe display name update** — Can you update an object's visible name outside a save callback without triggering issues?
3. **Observer cascade** — What is the safe writeback pattern to prevent cascading into other callbacks or infinite save loops?
4. **Shutdown detection** — Can the plugin detect the application is closing and skip writeback cleanly?
5. **Signal group coverage** — For each of the 8 signal groups in the schema: which fields are native, which need inference, which are unavailable?
6. **Phase 2 readiness** — Will Phase 1 architecture support Phase 2 (panel UI, library placement, change detection, highlight) without a rewrite?
7. **Room detection** — Most reliable method in each software to get room name, area, and contained objects?
8. **Copy-paste collision** — How to detect and handle duplicate IDs created by copy-paste?

---

## ⚠️ Key Technical Risks Brian Flagged

| Risk | Description | Required Handling |
|---|---|---|
| **Copy-paste ID collision** | Copied object inherits full XDATA incl. UUID → two objects same identity | Detect during traversal, flag as `identity_state: "collision"`, send to AirenoOS for user confirmation |
| **Observer cascade** | Writing XDATA inside transaction can fire other registered event handlers | Make writeback **idempotent** — consistent result regardless of how many times it fires |
| **Shutdown guard** | Writeback must check document is still in valid state before firing | Design in from start, not patched later |
| **Transaction safety** | All writeback must use `TransactionManager` — start → apply → commit → abort on fail | Non-negotiable |
| **Stable ID** | Handle alone is not sufficient (breaks on purge) | XDATA-stored UUID confirmed approach |

---

## 💰 Quotes Needed (6 Items)

| # | Item | Status |
|---|---|---|
| 1 | AutoCAD Phase 1 | ⏳ Pending |
| 2 | AutoCAD Phase 2 | ⏳ Pending |
| 3 | Revit Phase 1 | ⏳ Pending |
| 4 | Revit Phase 2 | ⏳ Pending |
| 5 | BricsCAD Phase 1 | ⏳ Pending |
| 6 | BricsCAD Phase 2 | ⏳ Pending |

**Previous estimates (for reference):**
- AutoCAD (combined): $2,500 / 2 weeks
- Revit (original): $1,400–$1,800 / 7–9 days
- BricsCAD: ~same as AutoCAD (significant code reuse)

Brian expects BricsCAD to be cheaper given shared logic with AutoCAD.

---

## 🔁 Sequencing Question from Brian

- Can AutoCAD, Revit, BricsCAD Phase 1 run **in parallel**?
- Can **AutoCAD + BricsCAD** run simultaneously given code sharing?

> ⬅️ Cuong needs to confirm capacity and preferred sequencing.

---

## ✅ Action Items for Cuong

- [ ] Answer the 8 technical questions above (per software)
- [ ] Provide fixed quotes + timelines for all 6 items
- [ ] Provide best **package rate** (reflecting BricsCAD/AutoCAD overlap and volume)
- [ ] Confirm parallel vs sequential delivery capacity
- [ ] Keep an eye out for a **SketchUp plugin developer** (Brian's separate need)

---

---

## 📄 Canonical Plugin Schema v0.2 — Reference

> **File:** `AOS_L3_PLUG_SCHEMA_01_SPEC_Canonical-Plugin-Schema_v0.2.md`  
> **Issued by:** Brian / AirenoOS team  
> **Audience:** All plugin developers (AutoCAD, Revit, BricsCAD, SketchUp, Rhino, etc.)

### Core Principle
> "The plugin sends native signals. AirenoOS decides what they mean. Never the other way around."

The plugin schema is the **contract between all plugin developers and the AirenoOS MCP server**. All 3 plugins (AutoCAD, Revit, BricsCAD) must output the same JSON format. The MCP server handles one consistent payload — it does NOT need to understand each design tool's internal API.

**Important separation:**
```
Plugin extraction schema
→ sends raw signals to AirenoOS via MCP
→ AirenoOS interprets and enriches
→ Backpack record holds the governed project truth
```
The plugin captures signals. The Backpack governs truth. They are NOT the same thing.

---

### Signal Quality by Software Type

| Software | Identity | Classification | Room/Space | Rich Properties |
|---|---|---|---|---|
| **BIM** (Revit) | Native stable GUID | Native element type | Native space object | Fire rating, acoustic, manufacturer — all native |
| **2D CAD** (AutoCAD, BricsCAD) | Handle or plugin-generated UUID | Inferred from layer/block name | Inferred from closed polyline | Block attributes only |

---

### Stable ID Rules Per Software

| Software | Stable ID Source | Rule |
|---|---|---|
| **Revit** | Native Element `UniqueId` | Use directly. Always stable. |
| **AutoCAD** | XDATA-stored UUID | ❌ Do NOT rely on handle — breaks on purge. Generate UUID in XDATA on first extraction. |
| **BricsCAD** | XDATA-stored UUID | Same approach as AutoCAD. |

**Duplicate / copy-paste rule:** When the same `native_id` appears at two different spatial positions → treat as separate objects using `native_id + spatial_position_hash`. The copy is a new candidate — AirenoOS assigns a new `backpack_id`.

---

### The 8 Signal Groups (Summary)

| Group | Name | Key Fields |
|---|---|---|
| **1** | Native Object Identity | `native_id`, `native_id_type`, `aireno_backpack_id`, `identity_state`, `is_definition` |
| **2** | Naming Signal Stack | `visible_label`, `definition_name`, `layer_or_tag_name`, `attribute_text`, `nearby_text_labels`, `raw_aliases` |
| **3** | Spatial & Container Context | `room_or_zone_name`, `level_or_floor`, `parent_native_id`, `spatial_position` (x/y/z/unit), `package_hint` |
| **4** | Geometry Signal | `units`, `bounding_box`, `area`, `length`, `volume`, `geometry_type`, `is_closed_boundary` |
| **5** | Classification & Function Hint | `native_category`, `native_type`, `ifc_class`, `renovation_status`, `structural_flag`, `function_hint` |
| **6** | Existing Metadata Passthrough | `metadata_format`, `existing_metadata` (nested), `bim_properties` (fire_rating, acoustic_rating, manufacturer, etc.) |
| **7** | Source & Extraction Context | `source_software`, `plugin_version`, `document_project_token`, `extracted_at`, `extraction_trigger` |
| **8** | Signal Quality | `element_type_origin`, `room_origin`, `naming_confidence`, `stable_id_confidence`, `overall_signal_quality` |

**Quality routing:**
- `high` → candidate for auto-confirm threshold
- `medium` → batch user review
- `low` → Unresolved Queue with reason code

---

### Full Payload Envelope (Top-level Structure)

```json
{
  "aireno_schema_version": "0.2",
  "payload_type": "extraction",
  "project_id": "...",
  "authentication": { "bearer_token": "..." },
  "document_project_token": "...",
  "objects": [ { /* 8 groups per object */ } ],
  "rooms": [ { "native_id", "raw_name", "boundary_area", "contained_object_native_ids", ... } ],
  "layers_or_tags": [ { "name", "visible", "locked", "object_count" } ],
  "unresolved_objects": [ { "native_id", "raw_name", "reason", "detail" } ],
  "extraction_summary": { "total_objects", "total_rooms", "unresolved_count", "high/medium/low_quality_count" }
}
```

---

### V1 Writeback Payload (Identity Writeback)

Triggered by **manual menu command only** — NEVER during save callback.

Fields written back to object's XDATA/metadata:
- `aireno_backpack_id`
- `confirmed_visible_label` (also update visible display name)
- `confirmed_room_id`
- `confirmed_package_id`
- `identity_state` → `"confirmed"`
- `last_synced` timestamp
- Previous label preserved under `aireno_previous_label`

**Writeback rules (non-negotiable):**
- ❌ Never trigger during save callback (crash + cascade risk)
- ❌ Never trigger during application shutdown
- ✅ Must use `TransactionManager` — start → apply → commit → abort on fail
- ✅ Must implement writeback lock flag (re-entrant cascade prevention)
- ✅ Must be idempotent — same result no matter how many times it fires
- ✅ `do_not_overwrite_if_locked: true`

---

### V2 Placeholder Fields (Do NOT implement in Phase 1)

```json
{
  "v2_change_detection": { "changed_since_last_sync", "change_type", "previous_value" },
  "v2_selection_state": { "is_selected_in_tool", "selection_source" },
  "v2_visual_marker_state": { "marker_type", "marker_color" }
}
```
These fields are declared now so the schema doesn't need redesigning for Phase 2.

---

### HTTP Communication

```
Method:       HTTP POST
Content-Type: application/json
Auth:         Bearer token in Authorization header
Test endpoint: https://mcp.airenos.io/v1/extract
Response 200: { "status": "accepted", "extraction_id": "string" }
Timeout:      30 seconds, retry once
Offline:      Save payload to local temp file, retry on next trigger
```

**Async rule:** Data collection happens synchronously during save event. HTTP POST fires **asynchronously after save completes**. User is never blocked by network call.

---

### Per-Software Implementation Notes (from Schema)

**AutoCAD (C# .NET):**
- Stable ID: XDATA UUID under registered app name `AIRENO`
- Room detection: Closed Polyline or Region + nearby Text/MText for room name
- Save hook: `Database.SaveComplete`
- Blocks: `BlockReference` insertion points + attribute values

**Revit:**
- Stable ID: Native `UniqueId` — always stable
- Room detection: Native Room/Space objects
- BIM properties: Category, Phase, Structural Usage — all native
- `renovation_status` and `structural_flag` from native fields

**BricsCAD:** Same approach as AutoCAD (XDATA UUID, `Database.SaveComplete`, closed polyline room detection)

---

## 📌 Relationship Notes

- Brian is based in **Hong Kong**
- He has a development agency in **Vietnam** for core infrastructure (lacks AutoCAD API knowledge)
- Cuong is positioned as the **specialist plugin developer**, potentially collaborating directly with the Vietnam agency
- Brian sees this as a **long-term engagement** — open to retainer/ongoing support
- Brian's original Upwork account was blocked; he reconnected via new account
