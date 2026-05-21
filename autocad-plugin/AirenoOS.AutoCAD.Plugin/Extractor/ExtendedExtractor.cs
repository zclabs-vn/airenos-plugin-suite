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
                                TryAddHatch(payload, h);
                                break;
                        }
                    }

                    EnrichXrefContext(tr, bt, payload);
                    EnrichDynamicBlocks(tr, ms, payload);
                    AttachNearbyText(payload, texts);

                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                    // never rethrow — Layer 2 must not break the save flow
                }
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
            bucket.Add((content!, pos, layer, handle));
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
                if (obj.SpatialPosition == null) continue;
                var origin = new Point3d(obj.SpatialPosition.X, obj.SpatialPosition.Y, obj.SpatialPosition.Z);

                var radius = 2000.0;
                if (obj.BoundingBox is { } bb)
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
                    obj.NearbyTextLabels = labels;
                    // Annotation.NearbyIds is the inverse, populated below in a second pass for symmetry
                }
            }

            // Inverse mapping: for each annotation, list object native_ids that include it
            foreach (var ann in payload.Annotations)
            {
                if (ann.Position == null) continue;
                ann.NearbyIds = payload.Objects
                    .Where(o => o.NearbyTextLabels != null && o.NearbyTextLabels.Contains(ann.Content ?? ""))
                    .Select(o => o.NativeId ?? "")
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
            payload.Hatches.Add(new HatchSignal
            {
                NativeId    = h.Handle.Value.ToString("X"),
                PatternName = h.PatternName,
                PatternType = h.PatternType.ToString(),
                Layer       = h.Layer,
                Scale       = h.PatternScale,
                BoundaryIds = null
            });
        }

        // ── Xref context ─────────────────────────────────────────────────────────────

        private static void EnrichXrefContext(Transaction tr, BlockTable bt, ExtractionPayload payload)
        {
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;

                // Mark all objects whose DefinitionId points into this xref's BTR as xref-origin
                var defKey = btr.Id.Handle.Value.ToString("X");
                foreach (var o in payload.Objects)
                {
                    if (o.DefinitionId == defKey)
                    {
                        o.IsXrefOrigin = true;
                        o.XrefFileName = btr.PathName;
                        o.XrefStatus   = btr.IsResolved ? "resolved" : "unresolved";
                    }
                }
            }
        }

        // ── Dynamic blocks ───────────────────────────────────────────────────────────

        private static void EnrichDynamicBlocks(Transaction tr, BlockTableRecord ms, ExtractionPayload payload)
        {
            foreach (ObjectId id in ms)
            {
                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || !br.IsDynamicBlock) continue;

                var target = payload.Objects.FirstOrDefault(o =>
                    string.Equals(o.DefinitionId, br.BlockTableRecord.Handle.Value.ToString("X"))
                    && o.SpatialPosition != null
                    && Approx(o.SpatialPosition.X, br.Position.X)
                    && Approx(o.SpatialPosition.Y, br.Position.Y));
                if (target == null) continue;

                var dyn = new DynamicBlockState
                {
                    BlockName  = ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name,
                    Properties = new Dictionary<string, string?>()
                };

                // Identify the Visibility state parameter robustly. Prior implementation matched
                // only on PropertyName == "Visibility", which fails when the user names the parameter
                // "Visibility1" / "DoorState" / etc. We detect by structural signature instead.
                var allProps = new List<DynamicBlockReferenceProperty>();
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    allProps.Add(p);

                var visibilityParam =
                    allProps.FirstOrDefault(p => string.Equals(p.PropertyName, "Visibility", StringComparison.OrdinalIgnoreCase))
                    ?? allProps.FirstOrDefault(LooksLikeVisibilityState);

                foreach (var p in allProps)
                {
                    if (ReferenceEquals(p, visibilityParam))
                        dyn.VisibilityState = p.Value?.ToString();
                    else
                        dyn.Properties![p.PropertyName] = p.Value?.ToString();
                }

                target.DynamicBlockState = dyn;
            }
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
