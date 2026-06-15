using System;
using System.Collections.Generic;
using System.Linq;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using AirenoOS.BricsCAD.Plugin.Schema;

namespace AirenoOS.BricsCAD.Plugin.Extractor
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
            using var tr = db.TransactionManager.StartTransaction();
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
                            TryAddDimension(payload, dim);  // dim XDATA read via dim.GetXDataForApplication — no tr needed
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

                // v0.3 Developer doc Groups 13-15 — mirror to top-level arrays
                PopulateTopLevelXrefReferences(tr, bt, payload);
                PopulateTopLevelDynamicBlocks(payload);
                PopulateExtendedLayerProperties(tr, db, payload);
                PopulateEntitySourceSummary(payload);
                ComputeExtractionTier(payload);

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                // never rethrow — Layer 2 must not break the save flow
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
                LayoutContext      = null,
                AnnotationTypeHint = InferAnnotationTypeHint(content!, layer)
            });
            bucket.Add((content, pos, layer, handle));
        }

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
                // Angular dims measure in degrees; everything else in drawing units (mm).
                Unit              = dimType == "angular" ? "deg" : "mm",
                DimensionType     = dimType,
                Layer             = dim.Layer,
                Location          = new Point2D { X = dim.TextPosition.X, Y = dim.TextPosition.Y },
                MeasuredObjectIds = null
            });
        }

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
            // v0.3 Group 12: single boundary native_id (first associative source). After
            // Brian #7, the boundary points at the polyline's XDATA UUID when available,
            // falling back to the Teigha handle until AIRENO_EXTRACT has written it.
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
                NearbyFinishNote = null,
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
        // See AutoCAD plugin for the design rationale. Same algorithm, BricsCAD types.
        private static void EnrichRoomLabels(
            Transaction tr,
            BlockTableRecord ms,
            ExtractionPayload payload,
            List<(string Content, Point3d Pos, string Layer, string Handle)> texts)
        {
            if (payload.Rooms.Count == 0 || payload.Objects.Count == 0) return;

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

                string? hostRoomId = null;
                Extents3d hostBbox = default;
                foreach (var r in rooms)
                {
                    if (ContainsXY(r.Bbox, p)) { hostRoomId = r.Id; hostBbox = r.Bbox; break; }
                }
                if (hostRoomId == null) continue;

                obj.Group3Spatial.RoomOrZoneNativeId = hostRoomId;
                obj.Group8Quality.RoomOrigin = "spatial_inference";

                string? label = null;
                foreach (var t in texts)
                {
                    if (ContainsXY(hostBbox, t.Pos)) { label = t.Content; break; }
                }
                if (string.IsNullOrEmpty(label)) continue;

                obj.Group2Naming.RoomLabelSignal = label;
                obj.Group3Spatial.RoomOrZoneName = label;
                obj.Group8Quality.RoomOrigin     = "naming_inference";

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
        /// Detect the Visibility-state parameter on a dynamic block. The Teigha API does NOT
        /// expose AllowedValues, so we fall back to a name heuristic:
        ///   - PropertyName starts with or contains "Visibility" (covers "Visibility", "Visibility1", ...)
        ///   - PropertyName ends with "State" AND Value is string (catches "DoorState", "WindowState").
        /// </summary>
        private static bool LooksLikeVisibilityState(DynamicBlockReferenceProperty p)
        {
            if (p.Value is not string) return false;
            var name = p.PropertyName ?? string.Empty;
            if (name.IndexOf("Visibility", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.EndsWith("State", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── v0.3 Layer 2 top-level mirrors ──────────────────────────────────────────

        // Group 13 — document-level xref summary
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

        // Group 15 — top-level dynamic_blocks mirror (reads what EnrichDynamicBlocks
        // already pushed into existing_metadata["dynamic_block"])
        private static void PopulateTopLevelDynamicBlocks(ExtractionPayload payload)
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

        // Group 14 — extended per-layer properties
        private static void PopulateExtendedLayerProperties(Transaction tr, Database db, ExtractionPayload payload)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId lid in lt)
            {
                var lrec = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                var c = lrec.Color;
                var basic = payload.LayersOrTags.FirstOrDefault(l =>
                    string.Equals(l.Name, lrec.Name, StringComparison.OrdinalIgnoreCase));

                // ACI colors carry zeros in Color.Red/Green/Blue (those fields hold *explicit*
                // RGB only). ColorValue is the System.Drawing.Color the SDK already resolved
                // via the standard ACI palette — works uniformly for ACI, true-color, book color.
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
                    IsXrefLayer = lrec.Name.Contains("|")
                });
            }
        }

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
    }
}
