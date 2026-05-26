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
                            TryAddDimension(payload, dim);
                            break;
                        case Hatch h:
                            TryAddHatch(payload, h);
                            break;
                    }
                }

                EnrichXrefContext(tr, bt, payload);
                EnrichDynamicBlocks(tr, ms, payload);
                AttachNearbyText(payload, texts);
                EnrichRoomLabels(tr, ms, payload, texts);

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
                NativeId = handle,
                Type     = kind,
                Content  = content,
                Layer    = layer,
                Position = new SpatialPosition { X = pos.X, Y = pos.Y, Z = pos.Z, Unit = "mm" }
            });
            bucket.Add((content, pos, layer, handle));
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
                if (ann.Position == null) continue;
                ann.NearbyIds = payload.Objects
                    .Where(o => o.Group2Naming.NearbyTextLabels != null && o.Group2Naming.NearbyTextLabels.Contains(ann.Content ?? ""))
                    .Select(o => o.Group1Identity.NativeId ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
        }

        // ── Dimensions ───────────────────────────────────────────────────────────────

        private static void TryAddDimension(ExtractionPayload payload, Dimension dim)
        {
            payload.Dimensions.Add(new DimensionSignal
            {
                NativeId      = dim.Handle.Value.ToString("X"),
                Measurement   = SafeMeasurement(dim),
                DimensionType = dim.GetType().Name,
                Layer         = dim.Layer,
                Position      = new SpatialPosition
                {
                    X = dim.TextPosition.X, Y = dim.TextPosition.Y, Z = dim.TextPosition.Z, Unit = "mm"
                }
            });
        }

        private static double? SafeMeasurement(Dimension dim)
        {
            try { return dim.Measurement; } catch { return null; }
        }

        // ── Hatches ──────────────────────────────────────────────────────────────────

        private static void TryAddHatch(ExtractionPayload payload, Hatch h)
        {
            // Associative hatches track their source-boundary entities; pull their handles
            // so Brian's MCP can correlate hatch → polyline/circle/etc that defines the area.
            List<string>? boundaryIds = null;
            try
            {
                if (h.Associative)
                {
                    var ids = h.GetAssociatedObjectIds();
                    if (ids != null && ids.Count > 0)
                    {
                        boundaryIds = new List<string>(ids.Count);
                        foreach (ObjectId bid in ids)
                            boundaryIds.Add(bid.Handle.Value.ToString("X"));
                    }
                }
            }
            catch { /* fall through with null boundaryIds */ }

            payload.Hatches.Add(new HatchSignal
            {
                NativeId    = h.Handle.Value.ToString("X"),
                PatternName = h.PatternName,
                PatternType = h.PatternType.ToString(),
                Layer       = h.Layer,
                Scale       = h.PatternScale,
                BoundaryIds = boundaryIds
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

            var rooms = new List<(string Handle, Extents3d Bbox)>();
            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || !pl.Closed) continue;
                try { rooms.Add((pl.Handle.Value.ToString("X"), pl.GeometricExtents)); }
                catch { /* no extents → skip */ }
            }
            if (rooms.Count == 0) return;

            foreach (var obj in payload.Objects)
            {
                var sp = obj.Group3Spatial.SpatialPosition;
                if (sp == null) continue;
                var p = new Point3d(sp.X, sp.Y, sp.Z);

                string? hostRoomHandle = null;
                Extents3d hostBbox = default;
                foreach (var r in rooms)
                {
                    if (ContainsXY(r.Bbox, p)) { hostRoomHandle = r.Handle; hostBbox = r.Bbox; break; }
                }
                if (hostRoomHandle == null) continue;

                obj.Group3Spatial.RoomOrZoneNativeId = hostRoomHandle;
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
                    if (room.NativeId == hostRoomHandle && string.IsNullOrEmpty(room.RawName))
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
    }
}
