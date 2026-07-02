using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AirenoOS.AutoCAD.Plugin.Schema;

namespace AirenoOS.AutoCAD.Plugin.Extractor
{
    /// <summary>
    /// Layer 1 — Core extraction. Runs synchronously during the save callback.
    /// Walks ModelSpace once and emits one ObjectSignal per BlockReference, plus
    /// layer_table and rooms (inferred from closed polylines).
    ///
    /// Fast path only — no nearby-text scans, no XREF resolution, no dynamic block
    /// property enumeration (those are Layer 2).
    /// </summary>
    internal static class CoreExtractor
    {
        public static ExtractionPayload Extract(Document doc, string trigger = "on_save")
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var db = doc.Database;

            // Document-level Group 7 context — copied into every object's group_7_source per envelope spec.
            var docContext = BuildDocContext(db);
            docContext.ExtractionTrigger = trigger;

            var payload = new ExtractionPayload
            {
                DocumentProjectToken = docContext.DocumentProjectToken
            };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    ExtractLayerTable(tr, db, payload);
                    ExtractObjects(tr, db, payload, docContext);
                    ExtractRoomsFromPolylines(tr, db, payload);
                    DetectCollisions(payload);

                    payload.Summary.TotalObjects    = payload.Objects.Count;
                    payload.Summary.TotalRooms      = payload.Rooms.Count;
                    payload.Summary.TotalLayers     = payload.LayersOrTags.Count;
                    payload.Summary.UnresolvedCount = payload.UnresolvedObjects.Count;
                    CountQuality(payload);

                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                    throw;
                }
            }

            // v0.3 envelope: extraction_tier promoted out of the document so the backend
            // can route the payload by tier without parsing per-object source groups.
            // ExtendedExtractor populates the Layer 2 arrays, so we compute the tier
            // AFTER both passes — here we set the layer_1 default; ExtendedExtractor will
            // upgrade to layer_2 if any extended group ends up populated.
            payload.ExtractionTier = "layer_1";
            payload.Summary.ExtractionTier = "layer_1";

            stopwatch.Stop();
            // Stopwatch.ElapsedMilliseconds truncates: a 0.4ms extraction reports "0" even
            // though work happened. Use the high-resolution TotalMilliseconds and ceil so a
            // sub-millisecond pass at least surfaces as 1ms. Layer 2 extends this after Enrich.
            payload.Summary.ExtractionDurationMs = (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds);
            return payload;
        }

        // ── Layers ───────────────────────────────────────────────────────────────────

        private static void ExtractLayerTable(Transaction tr, Database db, ExtractionPayload payload)
        {
            var counts = CountEntitiesByLayer(tr, db);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId lid in lt)
            {
                var lrec = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                counts.TryGetValue(lrec.Name, out var n);
                payload.LayersOrTags.Add(new LayerSignal
                {
                    Name        = lrec.Name,
                    Visible     = !lrec.IsOff,
                    Locked      = lrec.IsLocked,
                    ObjectCount = n,
                    Color       = lrec.Color.ColorNameForDisplay,
                    Linetype    = LinetypeName(tr, lrec.LinetypeObjectId),
                    IsFrozen    = lrec.IsFrozen
                });
            }
        }

        private static Dictionary<string, int> CountEntitiesByLayer(Transaction tr, Database db)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                counts[ent.Layer] = counts.TryGetValue(ent.Layer, out var c) ? c + 1 : 1;
            }
            return counts;
        }

        private static string? LinetypeName(Transaction tr, ObjectId ltId)
        {
            if (ltId.IsNull) return null;
            var lt = tr.GetObject(ltId, OpenMode.ForRead) as LinetypeTableRecord;
            return lt?.Name;
        }

        // ── Objects (BlockReferences in ModelSpace) ─────────────────────────────────

        // Snapshot of document-level Group 7 fields — built once, copied into every object.
        private sealed class DocContext
        {
            public string  DocumentProjectToken  { get; set; } = string.Empty;
            public string  SourceSoftwareVersion { get; set; } = string.Empty;
            public string  FileNameHash          { get; set; } = string.Empty;
            public string? FileNameDisplay       { get; set; }
            public string  ExtractedAt           { get; set; } = string.Empty;
            public string  ExtractionTrigger     { get; set; } = "on_save";
        }

        private static DocContext BuildDocContext(Database db) => new DocContext
        {
            DocumentProjectToken  = ProjectTokenManager.GetProjectToken(db),
            SourceSoftwareVersion = Application.Version.ToString(),
            FileNameHash          = HashFilename(db.Filename),
            FileNameDisplay       = string.IsNullOrEmpty(db.Filename) ? null : Path.GetFileName(db.Filename),
            ExtractedAt           = DateTime.UtcNow.ToString("o")
        };

        private static Group7Source NewGroup7(DocContext ctx) => new Group7Source
        {
            SourceSoftware        = "autocad",
            SourceSoftwareVersion = ctx.SourceSoftwareVersion,
            SourceSoftwareType    = "2d_cad",
            PluginVersion         = "1.0.3",
            FileNameHash          = ctx.FileNameHash,
            FileNameDisplay       = ctx.FileNameDisplay,
            DocumentProjectToken  = ctx.DocumentProjectToken,
            ExtractedAt           = ctx.ExtractedAt,
            ExtractionScope       = "active_model",
            LayoutOrViewContext   = null,
            ExtractionTrigger     = ctx.ExtractionTrigger
        };

        private static void ExtractObjects(Transaction tr, Database db, ExtractionPayload payload, DocContext docContext)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) continue;

                // Read-only path: peek at existing AIRENO XDATA; do NOT write here
                // (extraction must not mutate the drawing during save callback).
                var nativeId = XdataHelper.ReadField(br, fieldIndex: 0) ?? string.Empty;
                var backpackId = XdataHelper.ReadField(br, fieldIndex: 1);
                var identityState = XdataHelper.ReadField(br, fieldIndex: 2) ?? "raw";
                var confirmedLabel = XdataHelper.ReadField(br, fieldIndex: 3);
                // Brian #8: slot 6 carries the label the user saw before the last writeback.
                // Empty for entities that haven't been written-back yet — schema emits null.
                var previousLabel = XdataHelper.ReadField(br, fieldIndex: 6);

                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);

                var fileName = string.IsNullOrEmpty(db.Filename) ? null : Path.GetFileNameWithoutExtension(db.Filename);

                var sig = new ObjectSignal
                {
                    Group1Identity = new Group1Identity
                    {
                        NativeId          = string.IsNullOrEmpty(nativeId) ? null : nativeId,
                        NativeIdType      = "xdata_uuid",
                        NativeIdStability = "stable",
                        IdentityState     = identityState,
                        LinkState         = string.IsNullOrEmpty(backpackId) ? "unlinked" : "linked",
                        IsDefinition      = false,
                        DefinitionId      = btr.Id.Handle.Value.ToString("X"),
                        InstanceId        = br.Handle.Value.ToString("X")
                    },
                    // Brian #2: emit airenoos_ref readback block ONLY when an AirenoOS-assigned
                    // backpack_id is present in this entity's local XDATA. Absent for raw blocks.
                    AirenoosRef = string.IsNullOrEmpty(backpackId)
                        ? null
                        : new AirenoosRef { BackpackId = backpackId },
                    Group2Naming = new Group2Naming
                    {
                        VisibleLabel          = string.IsNullOrEmpty(confirmedLabel) ? btr.Name : confirmedLabel,
                        DefinitionName        = btr.Name,
                        LayerOrTagName        = br.Layer,
                        AttributeText         = ReadAttributes(tr, br),
                        FileOrSheetNameSignal = fileName,
                        // We only extract ModelSpace today — fixed string. When layout/paperspace
                        // extraction is added, switch to LayoutManager.Current.CurrentLayout.
                        SceneOrViewName       = "Model",
                        NamingOrigin          = "block_definition",
                        AirenoPreviousLabel   = string.IsNullOrEmpty(previousLabel) ? null : previousLabel
                        // ParentContainerName, HierarchyPathNames, RoomLabelSignal, NearbyTextLabels,
                        // RawAliases — populated by Layer 2 (ExtendedExtractor) or N/A for 2D CAD
                    },
                    Group3Spatial = new Group3Spatial
                    {
                        SpatialPosition = new SpatialPosition
                        {
                            X = br.Position.X, Y = br.Position.Y, Z = br.Position.Z, Unit = "mm"
                        },
                        ContainerType = "block",
                        PackageHint   = InferPackageHint(br.Layer)
                    },
                    Group4Geometry = new Group4Geometry
                    {
                        Units            = "mm",
                        BoundingBox      = TryBoundingBox(br),
                        GeometryType     = "block"
                    },
                    Group5Classification = new Group5Classification
                    {
                        NativeCategory   = "block",
                        NativeType       = btr.Name,
                        RenovationStatus = "unknown",
                        StructuralFlag   = "unknown"
                    },
                    Group6Metadata = new Group6Metadata
                    {
                        MetadataFormat = "xdata",
                        BimProperties  = BuildBimProperties(btr, br, tr)
                        // ExistingMetadata stays null; dynamic-block state is added by ExtendedExtractor
                    },
                    Group7Source = NewGroup7(docContext),  // xref enrichment happens in ExtendedExtractor (Layer 2)
                    Group8Quality = new Group8Quality
                    {
                        ElementTypeOrigin    = "native",
                        RoomOrigin           = "unknown",
                        AreaOrigin           = "calculated",
                        ClassificationOrigin = "inferred",
                        NamingConfidence     = string.IsNullOrEmpty(confirmedLabel) ? "medium" : "strong",
                        StableIdConfidence   = "plugin_generated",
                        OverallSignalQuality = string.IsNullOrEmpty(confirmedLabel) ? "medium" : "high"
                    }
                };

                payload.Objects.Add(sig);
            }
        }

        private static Dictionary<string, string>? ReadAttributes(Transaction tr, BlockReference br)
        {
            if (br.AttributeCollection == null || br.AttributeCollection.Count == 0) return null;
            var dict = new Dictionary<string, string>();
            foreach (ObjectId aid in br.AttributeCollection)
            {
                var att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (att == null || string.IsNullOrEmpty(att.Tag)) continue;
                dict[att.Tag] = att.TextString ?? string.Empty;
            }
            return dict.Count == 0 ? null : dict;
        }

        // Surface what BIM-style metadata we can extract from a block:
        //  • description from the block definition's "Description" field (BLOCK command)
        //  • manufacturer / model / phase from attributes whose tag matches a known alias
        // Returns a BimProperties bag with whatever fields were found (others stay null).
        private static BimProperties BuildBimProperties(BlockTableRecord btr, BlockReference br, Transaction tr)
        {
            var props = new BimProperties();

            if (!string.IsNullOrWhiteSpace(btr.Comments))
                props.Description = btr.Comments.Trim();

            var attrs = ReadAttributes(tr, br);
            if (attrs != null)
            {
                foreach (var kv in attrs)
                {
                    var tag = kv.Key.ToUpperInvariant();
                    switch (tag)
                    {
                        case "MFR":
                        case "MFG":
                        case "MAKER":
                        case "MANUFACTURER":
                            props.Manufacturer ??= kv.Value;
                            break;
                        case "MODEL":
                        case "MFR_MODEL":
                        case "MFG_MODEL":
                        case "MODEL_NO":
                        case "MODEL_NUMBER":
                            props.ManufacturerModel ??= kv.Value;
                            break;
                        case "PHASE":
                        case "WORK_PHASE":
                            props.Phase ??= kv.Value;
                            break;
                        case "MATERIAL":
                        case "MAT":
                        case "MATERIAL_NAME":
                            props.MaterialName ??= kv.Value;
                            break;
                        case "FIRE_RATING":
                        case "FIRE":
                            props.FireRating ??= kv.Value;
                            break;
                    }
                }
            }

            return props;
        }

        private static Schema.BoundingBox? TryBoundingBox(Entity ent)
        {
            try
            {
                var ext = ent.GeometricExtents;
                return new Schema.BoundingBox
                {
                    Width  = ext.MaxPoint.X - ext.MinPoint.X,
                    Height = ext.MaxPoint.Y - ext.MinPoint.Y,
                    Depth  = ext.MaxPoint.Z - ext.MinPoint.Z
                };
            }
            catch
            {
                return null;
            }
        }

        // ── Rooms (inferred from closed polylines) ──────────────────────────────────

        private static void ExtractRoomsFromPolylines(Transaction tr, Database db, ExtractionPayload payload)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || !pl.Closed) continue;

                // Brian #7: prefer the AIRENO XDATA UUID (stable across save/copy/purge)
                // and fall back to the AutoCAD handle when XDATA hasn't been written yet
                // (i.e. before the first AIRENO_EXTRACT manual command).
                var roomXdataId = XdataHelper.ReadField(pl, 0);
                payload.Rooms.Add(new RoomSignal
                {
                    NativeId          = !string.IsNullOrEmpty(roomXdataId) ? roomXdataId : pl.Handle.Value.ToString("X"),
                    RawName           = null,            // 2D CAD: no native room name
                    LayerOrTag        = pl.Layer,
                    BoundaryArea      = SafeArea(pl),
                    Volume            = null,            // 2D only
                    Height            = null,            // 2D only
                    Unit              = "sqm",
                    IsClosedBoundary  = true,            // we only emit when pl.Closed
                    ZoneNumber        = null,
                    ZoneCategory      = null,
                    ContainedObjectNativeIds = null,     // requires spatial-containment pass (Layer 2)
                    AirenoosRef       = null             // rooms have no XDATA backpack_id readback yet
                });
            }
        }

        // pl.Area returns area in drawing units squared (mm² for our default drawings).
        // Spec emits `unit: "sqm"` (square metres), so convert mm² → m² by dividing 1e6.
        private static double? SafeArea(Polyline pl)
        {
            try { return pl.Area / 1_000_000.0; } catch { return null; }
        }

        // Map AIA layer-name prefixes (A-DOOR-..., M-DUCT-..., etc) to a package hint
        // so the MCP backend can group objects by trade without parsing layer names itself.
        // Returns null when the prefix is unknown — better to be silent than guess wrong.
        private static string? InferPackageHint(string? layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return null;
            var upper = layerName!.ToUpperInvariant();
            var parts = upper.Split('-');
            if (parts.Length < 2) return null;

            // Match on first 2 parts: discipline prefix + major group (e.g. "A-DOOR")
            var key = parts[0] + "-" + parts[1];
            return key switch
            {
                "A-DOOR"  => "door",
                "A-GLAZ"  => "window",
                "A-WALL"  => "wall",
                "A-FLOR"  => "floor",
                "A-CLNG"  => "ceiling",
                "A-ROOF"  => "roof",
                "A-FURN"  => "furniture",
                "A-EQPM"  => "equipment",
                "A-AREA"  => "area",
                "A-ANNO"  => "annotation",
                "M-DUCT"  => "duct",
                "M-PIPE"  => "pipe",
                "M-HVAC"  => "hvac",
                "M-EQPM"  => "mechanical_equipment",
                "P-FIXT"  => "plumbing_fixture",
                "P-SANR"  => "sanitary",
                "P-DOMW"  => "domestic_water",
                "E-LITE"  => "lighting",
                "E-POWR"  => "power",
                "E-COMM"  => "communication",
                "E-EQPM"  => "electrical_equipment",
                "S-COLS"  => "column",
                "S-BEAM"  => "beam",
                "S-FNDN"  => "foundation",
                "S-SLAB"  => "slab",
                "C-TOPO"  => "topography",
                "C-ROAD"  => "road",
                _ => null
            };
        }

        // ── Misc ─────────────────────────────────────────────────────────────────────

        private static string HashFilename(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path!).ToLowerInvariant());
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var hex = BitConverter.ToString(hash).Replace("-", string.Empty);
                return hex.Substring(0, 16);
            }
        }

        /// <summary>
        /// Per Brian Round 2 confirmation: when two BlockReferences share an XDATA-stored
        /// native_id (i.e. user did Ctrl+C/V on a previously-confirmed block, both copies
        /// inherit the same UUID), all but one must be flagged as collisions so the MCP
        /// server doesn't double-link them to the same Backpack record.
        ///
        /// Anchor selection: prefer the object whose identity_state is already "confirmed"
        /// (Brian's note: "Original retains confirmed if previously confirmed"); otherwise
        /// the object with the lowest instance handle (oldest entity in DWG history).
        /// </summary>
        private static void DetectCollisions(ExtractionPayload payload)
        {
            var groups = payload.Objects
                .Where(o => !string.IsNullOrEmpty(o.Group1Identity.NativeId))
                .GroupBy(o => o.Group1Identity.NativeId);

            foreach (var group in groups)
            {
                var dupes = group.ToList();
                if (dupes.Count < 2) continue;

                var anchor = dupes.FirstOrDefault(o => o.Group1Identity.IdentityState == "confirmed")
                             ?? dupes.OrderBy(o => HandleHexToLong(o.Group1Identity.InstanceId)).First();

                foreach (var dup in dupes)
                {
                    if (ReferenceEquals(dup, anchor)) continue;

                    var samePos = SamePosition(anchor.Group3Spatial.SpatialPosition, dup.Group3Spatial.SpatialPosition);
                    dup.Group1Identity.IdentityState   = "collision";
                    dup.Group1Identity.CollisionFlag   = true;
                    dup.Group1Identity.CollisionReason = samePos
                        ? "uuid_duplicate_same_position"
                        : "uuid_duplicate_at_distinct_position";
                }
            }
        }

        private static long HandleHexToLong(string? hex)
        {
            return long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue;
        }

        private static bool SamePosition(SpatialPosition? a, SpatialPosition? b)
        {
            if (a == null || b == null) return false;
            return Math.Abs(a.X - b.X) < 1e-6
                && Math.Abs(a.Y - b.Y) < 1e-6
                && Math.Abs(a.Z - b.Z) < 1e-6;
        }

        private static void CountQuality(ExtractionPayload payload)
        {
            foreach (var o in payload.Objects)
            {
                switch (o.Group8Quality.OverallSignalQuality)
                {
                    case "high":   payload.Summary.HighQualityCount++;   break;
                    case "medium": payload.Summary.MediumQualityCount++; break;
                    default:       payload.Summary.LowQualityCount++;    break;
                }
            }
        }
    }
}
