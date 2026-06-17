using System;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin.Extractor
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

            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                ExtractLayerTable(tr, db, payload);
                ExtractObjects(tr, db, payload, docContext);
                // Customer feedback #10 — prefer BIM Room/Space entities when the
                // host has the BIM module active. Polylines are the legacy 2D
                // fallback used only when no BIM rooms are found in ModelSpace.
                int bimRooms = ExtractBimRooms(tr, db, payload);
                if (bimRooms == 0) ExtractRoomsFromPolylines(tr, db, payload);
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

            // v0.3 — ExtendedExtractor upgrades to layer_2 if any Layer 2 array is populated.
            payload.ExtractionTier         = "layer_1";
            payload.Summary.ExtractionTier = "layer_1";

            // Brian #11 — runtime probe for BIM module so the MCP server knows whether
            // BIM-tagged work can be served by this host (Lite/Pro vs BIM/Ultimate).
            payload.HostEnvironment = ProbeHostEnvironment();

            stopwatch.Stop();
            // Ceil from TotalMilliseconds so sub-ms extractions surface as 1ms instead of 0.
            payload.Summary.ExtractionDurationMs = (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds);
            return payload;
        }

        /// <summary>
        /// Brian #11 / customer feedback #10 — probe the BricsCAD host so the MCP
        /// server knows whether BIM-tagged work can be served by this client.
        ///
        /// Detection is driven by RUNASLEVEL (per Bricsys help — SV_runaslevel):
        ///   0 = Lite,  1 = Pro,  3 = BIM,  4 = Mechanical,  5 = Ultimate.
        /// LICFLAGS mirrors RUNASLEVEL after restart, so RUNASLEVEL alone is the
        /// canonical source. Earlier code probed a "BIMLIC" sysvar that doesn't
        /// exist in any documented BricsCAD version — verified 2026-06-17 against
        /// V25 (FullVersion 25.2.10, registry RUNASLEVEL=5) the legacy probe
        /// silently returned bim_module_available=false on a known-Ultimate host.
        ///
        /// PRODUCT name read separately (BricsCAD always returns "BricsCAD"; kept
        /// for forward-compat if Bricsys ever forks the binary).
        /// </summary>
        private static HostEnvironment ProbeHostEnvironment()
        {
            var env = new HostEnvironment();
            try { env.ProductName = Application.GetSystemVariable("PRODUCT") as string; } catch { }
            try
            {
                var raw = Application.GetSystemVariable("RUNASLEVEL");
                int? level = raw switch
                {
                    short s => s,
                    int   i => i,
                    long  l => (int)l,
                    string s when int.TryParse(s, out var n) => n,
                    _ => (int?)null
                };
                env.RunAsLevel = level;
                env.ProductVariant = level switch
                {
                    0 => "Lite",
                    1 => "Pro",
                    3 => "BIM",
                    4 => "Mechanical",
                    5 => "Ultimate",
                    _ => null
                };
                // 3 = BIM, 5 = Ultimate (Ultimate is a superset that includes BIM).
                // Mechanical (4) is a parallel module, not a BIM superset.
                env.BimModuleAvailable = level == 3 || level == 5;
            }
            catch { /* RUNASLEVEL missing on this version → leave defaults */ }
            return env;
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
            FileNameDisplay       = string.IsNullOrEmpty(db.Filename) ? null : System.IO.Path.GetFileName(db.Filename),
            ExtractedAt           = DateTime.UtcNow.ToString("o")
        };

        private static Group7Source NewGroup7(DocContext ctx) => new Group7Source
        {
            SourceSoftware        = "bricscad",
            SourceSoftwareVersion = ctx.SourceSoftwareVersion,
            // Customer feedback #10 — flag BIM-licensed hosts so the MCP server
            // can route the payload through its BIM pipeline. BimSupport caches
            // the answer (RUNASLEVEL lookup happens once per session).
            SourceSoftwareType    = BimSupport.IsBimAvailable ? "bricsCAD_bim" : "2d_cad",
            PluginVersion         = "1.0.2",
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
                var previousLabel = XdataHelper.ReadField(br, fieldIndex: 6);

                // Customer feedback #10 — when BIM module is active, prefer the BIM
                // entity's GlobalGuid + property sets over our XDATA UUID and block
                // attribute scan. BimSupport.TryReadEntity returns null for blocks
                // that aren't BIM-classified, so non-BIM blocks fall through
                // unchanged (verified by reflection-only API access).
                var bim = BimSupport.TryReadEntity(id);
                var bimNativeId   = bim?.GlobalGuid;
                var preferBimId   = !string.IsNullOrEmpty(bimNativeId);

                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);

                var fileName = string.IsNullOrEmpty(db.Filename) ? null : System.IO.Path.GetFileNameWithoutExtension(db.Filename);

                var sig = new ObjectSignal
                {
                    Group1Identity = new Group1Identity
                    {
                        NativeId          = preferBimId
                                              ? bimNativeId
                                              : (string.IsNullOrEmpty(nativeId) ? null : nativeId),
                        NativeIdType      = preferBimId ? "bim_guid"        : "xdata_uuid",
                        NativeIdStability = preferBimId ? "stable_bim_guid" : "stable",
                        IdentityState     = identityState,
                        LinkState         = string.IsNullOrEmpty(backpackId) ? "unlinked" : "linked",
                        IsDefinition      = false,
                        DefinitionId      = btr.Id.Handle.Value.ToString("X"),
                        InstanceId        = br.Handle.Value.ToString("X")
                    },
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
                        Units        = "mm",
                        BoundingBox  = TryBoundingBox(br),
                        GeometryType = "block"
                    },
                    Group5Classification = new Group5Classification
                    {
                        NativeCategory   = preferBimId ? "bim_object" : "block",
                        NativeType       = bim?.Category ?? bim?.IfcClass ?? btr.Name,
                        IfcClass         = bim?.IfcClass,
                        RenovationStatus = "unknown",
                        StructuralFlag   = "unknown"
                    },
                    Group6Metadata = new Group6Metadata
                    {
                        // bim_property_set when we successfully read BIM-side properties;
                        // legacy xdata when only XDATA attributes were available.
                        MetadataFormat = bim?.Properties != null ? "bim_property_set" : "xdata",
                        BimProperties  = BuildBimProperties(btr, br, tr, bim?.Properties)
                    },
                    Group7Source = NewGroup7(docContext),
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

            // Customer feedback #10 — BIM walls/slabs/doors usually live in
            // ModelSpace as AcDb3dSolids (not BlockReferences). Second pass picks
            // up any non-block entity that has BIM data so a typical BIM drawing
            // doesn't surface zero objects. Skipped on non-BIM hosts so the legacy
            // 2D extraction path stays a pure block scan.
            if (BimSupport.IsBimAvailable)
            {
                BimSupport.DumpModelSpaceDiagnostic(tr, db);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (ent is BlockReference) continue;   // already handled above

                    var bim = BimSupport.TryReadEntity(id);
                    if (bim == null) continue;
                    // BIM_SPACE / BIM_ROOM classifications are emitted as rooms,
                    // not objects (ExtractBimRooms picks them up). Skip here so
                    // the same entity doesn't appear in both arrays.
                    if (IsBimRoomClassification(bim.Category)) continue;
                    // Stable identity: prefer GlobalGuid (BIM Room/Space) else
                    // entity Handle (any BIM-classified solid).
                    if (string.IsNullOrEmpty(bim.GlobalGuid))
                        bim.GlobalGuid = "h" + ent.Handle.Value.ToString("X");

                    payload.Objects.Add(new ObjectSignal
                    {
                        Group1Identity = new Group1Identity
                        {
                            NativeId          = bim.GlobalGuid,
                            NativeIdType      = "bim_guid",
                            NativeIdStability = "stable_bim_guid",
                            IdentityState     = "raw",
                            LinkState         = "unlinked",
                            IsDefinition      = false,
                            DefinitionId      = ent.Handle.Value.ToString("X"),
                            InstanceId        = ent.Handle.Value.ToString("X")
                        },
                        Group2Naming = new Group2Naming
                        {
                            VisibleLabel          = bim.Name ?? ent.GetRXClass().Name,
                            DefinitionName        = bim.Category ?? ent.GetRXClass().Name,
                            LayerOrTagName        = ent.Layer,
                            SceneOrViewName       = "Model",
                            NamingOrigin          = "bim_classification"
                        },
                        Group3Spatial = new Group3Spatial
                        {
                            SpatialPosition = TryEntityPosition(ent),
                            ContainerType   = "bim_object",
                            PackageHint     = bim.Category?.ToLowerInvariant()
                        },
                        Group4Geometry = new Group4Geometry
                        {
                            Units        = "mm",
                            BoundingBox  = TryBoundingBox(ent),
                            GeometryType = ent.GetRXClass().Name
                        },
                        Group5Classification = new Group5Classification
                        {
                            NativeCategory   = "bim_object",
                            NativeType       = bim.Category ?? bim.IfcClass ?? ent.GetRXClass().Name,
                            IfcClass         = bim.IfcClass,
                            RenovationStatus = "unknown",
                            StructuralFlag   = "unknown"
                        },
                        Group6Metadata = new Group6Metadata
                        {
                            MetadataFormat = bim.Properties != null ? "bim_property_set" : "xdata",
                            BimProperties  = BuildBimPropertiesFromBimOnly(bim.Properties)
                        },
                        Group7Source  = NewGroup7(docContext),
                        Group8Quality = new Group8Quality
                        {
                            ElementTypeOrigin    = "native",
                            RoomOrigin           = "unknown",
                            AreaOrigin           = "calculated",
                            ClassificationOrigin = "native",   // BIM classification is authoritative
                            NamingConfidence     = string.IsNullOrEmpty(bim.Name) ? "medium" : "strong",
                            StableIdConfidence   = "host_native",   // BIM GlobalGuid > our XDATA UUID
                            OverallSignalQuality = "high"
                        }
                    });
                }
            }
        }

        // ── BIM-entity helpers (Customer feedback #10) ──────────────────────────────

        private static SpatialPosition TryEntityPosition(Entity ent)
        {
            try
            {
                var ext = ent.GeometricExtents;
                return new SpatialPosition
                {
                    X    = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    Y    = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                    Z    = (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0,
                    Unit = "mm"
                };
            }
            catch
            {
                return new SpatialPosition { X = 0, Y = 0, Z = 0, Unit = "mm" };
            }
        }

        private static BimProperties BuildBimPropertiesFromBimOnly(Dictionary<string, string>? bimProps)
        {
            var props = new BimProperties();
            if (bimProps != null) FoldIntoProps(bimProps, props);
            return props;
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
        //  • bimPropertySets — flat dict pulled from BricsCAD's BIM property sets via
        //    BimSupport (customer feedback #10). When present, it overrides any attribute-
        //    derived field with the BIM-native value because property sets are the
        //    authoritative source on a BIM-classified entity.
        // Returns a BimProperties bag with whatever fields were found (others stay null).
        private static BimProperties BuildBimProperties(
            BlockTableRecord btr, BlockReference br, Transaction tr,
            Dictionary<string, string>? bimPropertySets)
        {
            var props = new BimProperties();

            if (!string.IsNullOrWhiteSpace(btr.Comments))
                props.Description = btr.Comments.Trim();

            // 1. Block attributes (existing logic).
            var attrs = ReadAttributes(tr, br);
            if (attrs != null) FoldIntoProps(attrs, props);

            // 2. BIM property sets — overlay on top so they win where they overlap.
            if (bimPropertySets != null) FoldIntoProps(bimPropertySets, props);

            return props;
        }

        private static void FoldIntoProps(Dictionary<string, string> source, BimProperties props)
        {
            foreach (var kv in source)
            {
                var tag = kv.Key.ToUpperInvariant();
                switch (tag)
                {
                    case "MFR":
                    case "MFG":
                    case "MAKER":
                    case "MANUFACTURER":
                        props.Manufacturer = kv.Value;
                        break;
                    case "MODEL":
                    case "MFR_MODEL":
                    case "MFG_MODEL":
                    case "MODEL_NO":
                    case "MODEL_NUMBER":
                        props.ManufacturerModel = kv.Value;
                        break;
                    case "PHASE":
                    case "WORK_PHASE":
                        props.Phase = kv.Value;
                        break;
                    case "MATERIAL":
                    case "MAT":
                    case "MATERIAL_NAME":
                        props.MaterialName = kv.Value;
                        break;
                    case "FIRE_RATING":
                    case "FIRE":
                        props.FireRating = kv.Value;
                        break;
                    case "DESCRIPTION":
                    case "DESC":
                        if (string.IsNullOrWhiteSpace(props.Description))
                            props.Description = kv.Value;
                        break;
                }
            }
        }

        private static BoundingBox? TryBoundingBox(Entity ent)
        {
            try
            {
                var ext = ent.GeometricExtents;
                return new BoundingBox
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

        // ── Rooms (BIM Room/Space when available, polylines as 2D fallback) ─────

        /// <summary>
        /// Customer feedback #10 — primary room extraction path on BIM hosts.
        ///
        /// Two BIM-side sources for rooms on V25:
        ///   1. Legacy V20-era BIMRoom entities — enumerated via
        ///      BIMRoom.GetAllRooms(db).
        ///   2. V21+ Spaces created by the BIMSPACE command — these live in
        ///      ModelSpace as 3D solids classified "BIM_SPACE" / "BIM_ROOM",
        ///      so we scan the modelspace second-pass loop's results and
        ///      hoist any BIM_SPACE-classified entry from objects → rooms.
        ///
        /// Returns total room count so the caller can decide whether to run
        /// the legacy polyline fallback (only on drawings with neither path).
        /// </summary>
        private static int ExtractBimRooms(Transaction tr, Database db, ExtractionPayload payload)
        {
            if (!BimSupport.IsBimAvailable) return 0;
            int count = 0;

            // 1) Legacy BIMRoom enumeration (V20 path).
            var rooms = BimSupport.EnumerateRooms(db);
            foreach (var r in rooms)
            {
                payload.Rooms.Add(new RoomSignal
                {
                    NativeId         = "h" + r.ObjectHandleHex,
                    RawName          = r.Name ?? r.Number,
                    LayerOrTag       = null,
                    BoundaryArea     = r.AreaSqm,
                    Volume           = null,
                    Height           = null,
                    Unit             = "sqm",
                    IsClosedBoundary = true,
                    ZoneNumber       = r.Number,
                    ZoneCategory     = r.Department,
                    ContainedObjectNativeIds = null,
                    AirenoosRef      = null
                });
                count++;
            }

            // 2) V21+ BIM Spaces — scan ModelSpace for any entity whose BIM
            //    classification name is BIM_SPACE / BIM_ROOM and emit it as a
            //    RoomSignal instead of an object. Done here (not in the object
            //    loop) so polyline fallback gating sees the real room count.
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                var bim = BimSupport.TryReadEntity(id);
                if (bim == null) continue;
                if (!IsBimRoomClassification(bim.Category)) continue;

                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                payload.Rooms.Add(new RoomSignal
                {
                    NativeId         = "h" + ent.Handle.Value.ToString("X"),
                    RawName          = bim.Name,
                    LayerOrTag       = ent.Layer,
                    BoundaryArea     = bim.Area,
                    Volume           = null,
                    Height           = null,
                    Unit             = "sqm",
                    IsClosedBoundary = true,
                    ZoneNumber       = null,
                    ZoneCategory     = bim.Category,
                    ContainedObjectNativeIds = null,
                    AirenoosRef      = null
                });
                count++;
            }

            return count;
        }

        private static bool IsBimRoomClassification(string? category)
        {
            if (string.IsNullOrEmpty(category)) return false;
            var c = category!.Trim().ToUpperInvariant();
            return c == "BIM_SPACE" || c == "BIM_ROOM" || c == "SPACE" || c == "ROOM";
        }

        private static void ExtractRoomsFromPolylines(Transaction tr, Database db, ExtractionPayload payload)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || !pl.Closed) continue;

                // Brian #7: prefer AIRENO XDATA UUID (stable across save/copy/purge);
                // fall back to the Teigha handle until AIRENO_EXTRACT has written it.
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

        private static string HashFilename(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant());
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
