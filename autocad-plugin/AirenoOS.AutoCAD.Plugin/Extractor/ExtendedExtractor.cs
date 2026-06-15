using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AirenoOS.AutoCAD.Plugin.Schema;

namespace AirenoOS.AutoCAD.Plugin.Extractor
{
    /// <summary>
    /// Layer 2 — Extended extraction. Runs async after save, never blocks the user.
    /// Adds annotations, dimensions, hatches, dynamic-block state, and xref context
    /// to a payload already populated by CoreExtractor.
    ///
    /// Defensive: any sub-section that fails leaves the corresponding array empty
    /// rather than crashing the whole enrichment.
    /// </summary>
    internal static class ExtendedExtractor
    {
        public static void Enrich(Document doc, ExtractionPayload payload)
        {
            var db = doc.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    var texts = new List<(string Content, Point3d Pos, string Layer, string Handle)>();

                    foreach (ObjectId id in ms)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        switch (ent)
                        {
                            case DBText t:
                                TryAddText(payload, texts, t.TextString, t.Position, t.Layer, t.Handle, "Text");
                                break;
                            case MText mt:
                                TryAddText(payload, texts, mt.Contents, mt.Location, mt.Layer, mt.Handle, "MText");
                                break;
                            case Dimension dim:
                                TryAddDimension(payload, dim);
                                break;
                            case Hatch h:
                                TryAddHatch(tr, payload, h);
                                break;
                        }
                    }

                    EnrichXrefContext(tr, bt, payload);
                    EnrichDynamicBlocks(tr, ms, payload);
                    AttachNearbyText(payload, texts);
                    EnrichRoomLabels(tr, ms, payload, texts);

                    // v0.3 Developer doc Groups 13-15 — mirror per-object xref/dynamic
                    // context to top-level arrays so the backend can scan without
                    // iterating every object. Also produce the extended layer_properties
                    // array (Group 14) alongside the basic layers_or_tags.
                    PopulateTopLevelXrefReferences(tr, bt, payload);
                    PopulateTopLevelDynamicBlocks(tr, ms, payload);
                    PopulateExtendedLayerProperties(tr, payload);
                    PopulateEntitySourceSummary(payload);

                    // Tier upgrade: if ANY Layer 2 array carries data, the payload is
                    // layer_2. Otherwise it stays layer_1 (set by CoreExtractor).
                    ComputeExtractionTier(payload);

                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                    // never rethrow — Layer 2 must not break the save flow
                }
            }
        }

        // ── v0.3 Layer 2 top-level mirrors ──────────────────────────────────────────

        // Group 13 — document-level xref summary: one entry per resolved xref BTR
        // with object count rolled up from the per-object group_7_source population.
        private static void PopulateTopLevelXrefReferences(Transaction tr, BlockTable bt, ExtractionPayload payload)
        {
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;

                var defKey = btr.Id.Handle.Value.ToString("X");
                var count  = payload.Objects.Count(o => o.Group1Identity.DefinitionId == defKey);

                payload.XrefReferences.Add(new XrefReferenceSignal
                {
                    XrefName          = btr.Name,
                    XrefFileHash      = HashXrefPath(btr.PathName),
                    XrefPathStatus    = btr.IsResolved ? "loaded" : "unresolved",
                    ObjectCountInXref = count
                });
            }
        }

        // Group 15 — top-level dynamic_blocks mirror. Reads what EnrichDynamicBlocks
        // already stuffed into existing_metadata["dynamic_block"] on each object.
        private static void PopulateTopLevelDynamicBlocks(Transaction tr, BlockTableRecord ms, ExtractionPayload payload)
        {
            foreach (var obj in payload.Objects)
            {
                var meta = obj.Group6Metadata?.ExistingMetadata;
                if (meta == null || !meta.TryGetValue("dynamic_block", out var dyn) || dyn == null) continue;

                dyn.TryGetValue("block_name", out var blockName);
                dyn.TryGetValue("visibility_state", out var visState);

                var props = dyn
                    .Where(kv => kv.Key != "block_name" && kv.Key != "visibility_state")
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                payload.DynamicBlocks.Add(new DynamicBlockTopLevel
                {
                    NativeId             = obj.Group1Identity.NativeId,
                    BlockDefinitionName  = blockName,
                    ActiveVisibilityState = visState,
                    DynamicProperties    = props.Count > 0 ? props : null
                });
            }
        }

        // Group 14 — Extended layer properties. Run alongside the basic layers_or_tags[]
        // which keeps the v1.0.0 shape (4 spec fields + 3 Brian R2 extras).
        private static void PopulateExtendedLayerProperties(Transaction tr, ExtractionPayload payload)
        {
            var db = payload.Objects.Count > 0 || payload.LayersOrTags.Count > 0
                ? Application.DocumentManager.MdiActiveDocument?.Database
                : null;
            if (db == null) return;

            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId lid in lt)
            {
                var lrec = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                var c = lrec.Color;
                var basic = payload.LayersOrTags.FirstOrDefault(l =>
                    string.Equals(l.Name, lrec.Name, StringComparison.OrdinalIgnoreCase));

                // ACI colors (e.g. ColorIndex=10 red) carry zeros in Color.Red/Green/Blue
                // because those fields hold *explicit* RGB only. Color.ColorValue is the
                // System.Drawing.Color the SDK already resolved via the standard ACI palette,
                // so it works uniformly for ACI, true-color, and book colors.
                var rv = c.ColorValue;
                payload.LayerProperties.Add(new LayerPropertiesSignal
                {
                    Name        = lrec.Name,
                    ColorIndex  = c.ColorIndex,
                    ColorRgb    = new RgbColor { R = rv.R, G = rv.G, B = rv.B },
                    Linetype    = basic?.Linetype,
                    Lineweight  = (int)lrec.LineWeight,
                    Frozen      = lrec.IsFrozen,
                    Locked      = lrec.IsLocked,
                    Visible     = !lrec.IsOff,
                    ObjectCount = basic?.ObjectCount ?? 0,
                    IsXrefLayer = lrec.Name.Contains("|")  // xref layers are prefixed "xrefname|layer"
                });
            }
        }

        // Group 13 companion — counts native vs xref-sourced objects and lists distinct
        // xref source filenames. Runs after EnrichXrefContext has tagged objects.
        private static void PopulateEntitySourceSummary(ExtractionPayload payload)
        {
            var xrefObjects = payload.Objects.Where(o => o.Group7Source.IsXrefOrigin == true).ToList();
            var xrefSources = xrefObjects
                .Select(o => o.Group7Source.XrefFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            payload.EntitySourceSummary = new EntitySourceSummary
            {
                NativeCount = payload.Objects.Count - xrefObjects.Count,
                XrefCount   = xrefObjects.Count,
                XrefSources = xrefSources
            };
        }

        private static void ComputeExtractionTier(ExtractionPayload payload)
        {
            var layer2 = new List<string>();
            if (payload.Annotations.Count       > 0) layer2.Add("annotations");
            if (payload.Dimensions.Count        > 0) layer2.Add("dimensions");
            if (payload.Hatches.Count           > 0) layer2.Add("hatches");
            if (payload.XrefReferences.Count    > 0) layer2.Add("xref_references");
            if (payload.DynamicBlocks.Count     > 0) layer2.Add("dynamic_blocks");
            if (payload.LayerProperties.Count   > 0) layer2.Add("layer_properties");
            if (payload.CadTables.Count         > 0) layer2.Add("cad_tables");

            payload.Summary.Layer2GroupsIncluded = layer2;
            if (layer2.Count > 0)
            {
                payload.ExtractionTier         = "layer_2";
                payload.Summary.ExtractionTier = "layer_2";
            }
        }

        // ── Annotations ──────────────────────────────────────────────────────────────

        private static void TryAddText(
            ExtractionPayload payload,
            List<(string, Point3d, string, string)> bucket,
            string? content, Point3d pos, string layer, Handle h, string kind)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var handle = h.Value.ToString("X");
            payload.Annotations.Add(new AnnotationSignal
            {
                NativeId           = handle,
                EntityType         = kind,
                TextContent        = content,
                Layer              = layer,
                Location           = new Point2D { X = pos.X, Y = pos.Y },
                LayoutContext      = null,                          // ModelSpace-only today
                AnnotationTypeHint = InferAnnotationTypeHint(content!, layer)
            });
            bucket.Add((content!, pos, layer, handle));
        }

        // Heuristic for v0.3 Group 10 `annotation_type_hint` — pattern-match content/layer
        // against the standard hint vocabulary. Returns null when nothing matches.
        private static string? InferAnnotationTypeHint(string content, string layer)
        {
            var l = layer.ToUpperInvariant();
            if (l.Contains("ROOM") || l.Contains("AREA"))  return "room_label";
            if (l.Contains("DOOR"))                         return "door_tag";
            if (l.Contains("GLAZ") || l.Contains("WIN"))    return "window_tag";
            if (l.Contains("DIM") || l.Contains("ANNO-DIM")) return "dimension_note";
            if (l.Contains("FNSH") || l.Contains("FINISH")) return "finish_note";
            return null;
        }

        /// <summary>
        /// For each object, find nearby annotation text and store their native_ids.
        /// "Nearby" = within a radius proportional to the object's bounding-box diagonal,
        /// capped at 2000mm to keep the cost bounded.
        /// </summary>
        private static void AttachNearbyText(
            ExtractionPayload payload,
            List<(string Content, Point3d Pos, string Layer, string Handle)> texts)
        {
            if (texts.Count == 0) return;

            foreach (var obj in payload.Objects)
            {
                if (obj.Group3Spatial.SpatialPosition == null) continue;
                var origin = new Point3d(obj.Group3Spatial.SpatialPosition.X, obj.Group3Spatial.SpatialPosition.Y, obj.Group3Spatial.SpatialPosition.Z);

                var radius = 2000.0;
                if (obj.Group4Geometry.BoundingBox is { } bb)
                {
                    var diag = Math.Sqrt(bb.Width * bb.Width + bb.Height * bb.Height);
                    radius = Math.Min(2000.0, Math.Max(500.0, diag * 0.75));
                }
                var r2 = radius * radius;

                var ids = new List<string>();
                var labels = new List<string>();
                foreach (var (content, pos, _, handle) in texts)
                {
                    var dx = pos.X - origin.X;
                    var dy = pos.Y - origin.Y;
                    if (dx * dx + dy * dy > r2) continue;
                    ids.Add(handle);
                    labels.Add(content);
                }
                if (ids.Count > 0)
                {
                    obj.Group2Naming.NearbyTextLabels = labels;
                }
            }

            // Inverse mapping: for each annotation, list object native_ids that include it
            foreach (var ann in payload.Annotations)
            {
                if (ann.Location == null) continue;
                ann.NearbyObjectIds = payload.Objects
                    .Where(o => o.Group2Naming.NearbyTextLabels != null && o.Group2Naming.NearbyTextLabels.Contains(ann.TextContent ?? ""))
                    .Select(o => o.Group1Identity.NativeId ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
        }

        // ── Dimensions ───────────────────────────────────────────────────────────────

        private static void TryAddDimension(ExtractionPayload payload, Dimension dim)
        {
            var dimXdataId = XdataHelper.ReadField(dim, 0);
            var dimType = ClassifyDimensionType(dim);
            payload.Dimensions.Add(new DimensionSignal
            {
                NativeId          = !string.IsNullOrEmpty(dimXdataId) ? dimXdataId : dim.Handle.Value.ToString("X"),
                MeasuredValue     = SafeMeasurement(dim),
                // Angular dimensions measure in degrees, every other dim subclass in drawing
                // units (mm by convention here). Don't hard-code "mm" or downstream consumers
                // will interpret "2.2 mm" for what's actually "2.2°".
                Unit              = dimType == "angular" ? "deg" : "mm",
                DimensionType     = dimType,
                Layer             = dim.Layer,
                Location          = new Point2D { X = dim.TextPosition.X, Y = dim.TextPosition.Y },
                MeasuredObjectIds = null     // AutoCAD dimensions don't expose their target IDs reliably
            });
        }

        // Map the AutoCAD subclass to the v0.3 vocabulary
        // (linear | angular | radial | diametric | ordinate | unknown).
        private static string ClassifyDimensionType(Dimension dim)
        {
            var name = dim.GetType().Name;
            if (name.Contains("Aligned") || name.Contains("Rotated")) return "linear";
            if (name.Contains("Angular"))                              return "angular";
            if (name.Contains("Radial"))                               return "radial";
            if (name.Contains("Diametric"))                            return "diametric";
            if (name.Contains("Ordinate"))                             return "ordinate";
            return "unknown";
        }

        private static double? SafeMeasurement(Dimension dim)
        {
            try { return dim.Measurement; } catch { return null; }
        }

        // ── Hatches ──────────────────────────────────────────────────────────────────

        private static void TryAddHatch(Transaction tr, ExtractionPayload payload, Hatch h)
        {
            // v0.3 Group 12 carries one boundary native_id (first associative source). Hatches
            // built from non-associative or multi-boundary picks emit null and rely on the
            // backend's spatial lookup. After Brian #7 the boundary points at the polyline's
            // AIRENO XDATA UUID (stable across save/copy/purge), falling back to the AutoCAD
            // handle only when XDATA hasn't been written yet.
            string? boundaryNativeId = null;
            try
            {
                if (h.Associative)
                {
                    var ids = h.GetAssociatedObjectIds();
                    if (ids != null && ids.Count > 0)
                    {
                        var boundaryEnt = tr.GetObject(ids[0], OpenMode.ForRead) as Entity;
                        if (boundaryEnt != null)
                        {
                            var bx = XdataHelper.ReadField(boundaryEnt, 0);
                            boundaryNativeId = !string.IsNullOrEmpty(bx)
                                ? bx
                                : ids[0].Handle.Value.ToString("X");
                        }
                    }
                }
            }
            catch { /* leave null */ }

            // location_centroid — best-effort using the hatch's geometric extents.
            Point2D? centroid = null;
            try
            {
                var ext = h.GeometricExtents;
                centroid = new Point2D
                {
                    X = (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                    Y = (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5
                };
            }
            catch { /* leave null */ }

            var hatchXdataId = XdataHelper.ReadField(h, 0);
            payload.Hatches.Add(new HatchSignal
            {
                NativeId         = !string.IsNullOrEmpty(hatchXdataId) ? hatchXdataId : h.Handle.Value.ToString("X"),
                PatternName      = h.PatternName,
                Layer            = h.Layer,
                BoundaryNativeId = boundaryNativeId,
                NearbyFinishNote = null,    // would need text-proximity scan; left for backend
                LocationCentroid = centroid
            });
        }

        // ── Xref context ─────────────────────────────────────────────────────────────

        private static void EnrichXrefContext(Transaction tr, BlockTable bt, ExtractionPayload payload)
        {
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;

                var pathName = btr.PathName;
                var fileHash = HashXrefPath(pathName);

                // Mark all objects whose DefinitionId points into this xref's BTR as xref-origin
                var defKey = btr.Id.Handle.Value.ToString("X");
                foreach (var o in payload.Objects)
                {
                    if (o.Group1Identity.DefinitionId == defKey)
                    {
                        o.Group7Source.IsXrefOrigin = true;
                        o.Group7Source.XrefFileName = pathName;
                        o.Group7Source.XrefFileHash = fileHash;
                        o.Group7Source.XrefStatus   = btr.IsResolved ? "resolved" : "unresolved";
                    }
                }
            }
        }

        // SHA256-hash the xref file name (matches CoreExtractor's HashFilename: 16-hex prefix
        // of UTF-8(lowercased basename) digest). Returns null if path is empty.
        private static string? HashXrefPath(string? pathName)
        {
            if (string.IsNullOrEmpty(pathName)) return null;
            try
            {
                var basename = System.IO.Path.GetFileName(pathName!).ToLowerInvariant();
                var bytes = System.Text.Encoding.UTF8.GetBytes(basename);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var hash = sha.ComputeHash(bytes);
                    var hex = BitConverter.ToString(hash).Replace("-", string.Empty);
                    return hex.Substring(0, 16);
                }
            }
            catch { return null; }
        }

        // ── Dynamic blocks ───────────────────────────────────────────────────────────

        private static void EnrichDynamicBlocks(Transaction tr, BlockTableRecord ms, ExtractionPayload payload)
        {
            foreach (ObjectId id in ms)
            {
                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || !br.IsDynamicBlock) continue;

                var target = payload.Objects.FirstOrDefault(o =>
                    string.Equals(o.Group1Identity.DefinitionId, br.BlockTableRecord.Handle.Value.ToString("X"))
                    && o.Group3Spatial.SpatialPosition != null
                    && Approx(o.Group3Spatial.SpatialPosition.X, br.Position.X)
                    && Approx(o.Group3Spatial.SpatialPosition.Y, br.Position.Y));
                if (target == null) continue;

                // Identify the Visibility state parameter robustly. Prior implementation matched
                // only on PropertyName == "Visibility", which fails when the user names the parameter
                // "Visibility1" / "DoorState" / etc. We detect by structural signature instead.
                var allProps = new List<DynamicBlockReferenceProperty>();
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    allProps.Add(p);

                var visibilityParam =
                    allProps.FirstOrDefault(p => string.Equals(p.PropertyName, "Visibility", StringComparison.OrdinalIgnoreCase))
                    ?? allProps.FirstOrDefault(LooksLikeVisibilityState);

                // Pack into Group6 existing_metadata under the "dynamic_block" group key,
                // per Canonical Schema v0.2 existing_metadata passthrough design.
                var dynBag = new Dictionary<string, string?>
                {
                    ["block_name"] = ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name
                };
                if (visibilityParam != null)
                    dynBag["visibility_state"] = visibilityParam.Value?.ToString();

                foreach (var p in allProps)
                {
                    if (ReferenceEquals(p, visibilityParam)) continue;
                    dynBag[p.PropertyName] = p.Value?.ToString();
                }

                target.Group6Metadata.ExistingMetadata ??= new Dictionary<string, Dictionary<string, string?>>();
                target.Group6Metadata.ExistingMetadata["dynamic_block"] = dynBag;
            }
        }

        // ── Room label inference ─────────────────────────────────────────────────────
        //
        // For each block in ModelSpace, find the closed Polyline that contains its
        // insertion point, then locate a Text/MText annotation inside that polyline.
        // Use that text as the room label and propagate to:
        //   • object.group_2_naming.room_label_signal
        //   • object.group_3_spatial.room_or_zone_native_id / room_or_zone_name
        //   • room.raw_name (back-fills the RoomSignal that was extracted with no name)
        //
        // Containment uses axis-aligned bounding boxes — fast and good enough for
        // rectangular rooms. L-shaped or concave rooms can mis-attribute neighbours;
        // that's acceptable for Phase 1 (spec only asks for "if detectable").
        private static void EnrichRoomLabels(
            Transaction tr,
            BlockTableRecord ms,
            ExtractionPayload payload,
            List<(string Content, Point3d Pos, string Layer, string Handle)> texts)
        {
            if (payload.Rooms.Count == 0 || payload.Objects.Count == 0) return;

            // Collect closed polylines with absolute (world-space) bounding boxes.
            // Brian #7: prefer the AIRENO XDATA UUID over the handle so that
            // room_or_zone_native_id stays consistent with rooms[].native_id.
            var rooms = new List<(string Id, Extents3d Bbox)>();
            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || !pl.Closed) continue;
                try
                {
                    var ux = XdataHelper.ReadField(pl, 0);
                    var roomId = !string.IsNullOrEmpty(ux) ? ux! : pl.Handle.Value.ToString("X");
                    rooms.Add((roomId, pl.GeometricExtents));
                }
                catch { /* no extents → skip */ }
            }
            if (rooms.Count == 0) return;

            foreach (var obj in payload.Objects)
            {
                var sp = obj.Group3Spatial.SpatialPosition;
                if (sp == null) continue;
                var p = new Point3d(sp.X, sp.Y, sp.Z);

                // Find first room containing this object's position.
                string? hostRoomId = null;
                Extents3d hostBbox = default;
                foreach (var r in rooms)
                {
                    if (ContainsXY(r.Bbox, p)) { hostRoomId = r.Id; hostBbox = r.Bbox; break; }
                }
                if (hostRoomId == null) continue;

                obj.Group3Spatial.RoomOrZoneNativeId = hostRoomId;
                // Link via AABB containment → spatial inference (upgraded to naming_inference below if label found).
                obj.Group8Quality.RoomOrigin = "spatial_inference";

                // First text whose position falls inside the same room → label.
                string? label = null;
                foreach (var t in texts)
                {
                    if (ContainsXY(hostBbox, t.Pos)) { label = t.Content; break; }
                }
                if (string.IsNullOrEmpty(label)) continue;

                obj.Group2Naming.RoomLabelSignal   = label;
                obj.Group3Spatial.RoomOrZoneName   = label;
                obj.Group8Quality.RoomOrigin       = "naming_inference";

                // Back-fill the RoomSignal's raw_name (G2-equivalent for rooms).
                foreach (var room in payload.Rooms)
                {
                    if (room.NativeId == hostRoomId && string.IsNullOrEmpty(room.RawName))
                    {
                        room.RawName = label;
                        break;
                    }
                }
            }
        }

        private static bool ContainsXY(Extents3d bbox, Point3d p)
        {
            return p.X >= bbox.MinPoint.X && p.X <= bbox.MaxPoint.X
                && p.Y >= bbox.MinPoint.Y && p.Y <= bbox.MaxPoint.Y;
        }

        private static bool Approx(double a, double b) => Math.Abs(a - b) < 1e-6;

        /// <summary>
        /// Detect the Visibility-state parameter on a dynamic block. The .NET API does NOT
        /// expose AllowedValues (only COM does), so we fall back to a name heuristic:
        ///   - PropertyName starts with or contains "Visibility" (covers default auto-numbered
        ///     names like "Visibility1", "Visibility2", and the standard "Visibility"); OR
        ///   - PropertyName ends with "State" AND Value is string (catches custom names like
        ///     "DoorState", "WindowState").
        /// String-typed value is required either way — linear/angular params hold numerics.
        /// </summary>
        private static bool LooksLikeVisibilityState(DynamicBlockReferenceProperty p)
        {
            if (p.Value is not string) return false;
            var name = p.PropertyName ?? string.Empty;
            if (name.IndexOf("Visibility", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.EndsWith("State", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
