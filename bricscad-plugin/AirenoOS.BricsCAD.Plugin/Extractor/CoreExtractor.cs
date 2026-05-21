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
        public static ExtractionPayload Extract(Document doc)
        {
            var db = doc.Database;
            var payload = new ExtractionPayload
            {
                DocumentProjectToken   = ProjectTokenManager.GetProjectToken(db),
                SourceSoftwareVersion  = Application.Version.ToString(),
                FileNameHash           = HashFilename(db.Filename),
                ExtractedAt            = DateTime.UtcNow.ToString("o"),
                ExtractionTrigger      = "on_save"
            };

            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                ExtractLayerTable(tr, db, payload);
                ExtractObjects(tr, db, payload);
                ExtractRoomsFromPolylines(tr, db, payload);

                payload.Summary.TotalObjects = payload.Objects.Count;
                payload.Summary.TotalRooms   = payload.Rooms.Count;
                CountQuality(payload);

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
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
                payload.LayerTable.Add(new LayerSignal
                {
                    Name        = lrec.Name,
                    Color       = lrec.Color.ColorNameForDisplay,
                    Linetype    = LinetypeName(tr, lrec.LinetypeObjectId),
                    IsFrozen    = lrec.IsFrozen,
                    IsLocked    = lrec.IsLocked,
                    IsVisible   = !lrec.IsOff,
                    ObjectCount = n
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

        private static void ExtractObjects(Transaction tr, Database db, ExtractionPayload payload)
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

                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);

                var sig = new ObjectSignal
                {
                    NativeId           = string.IsNullOrEmpty(nativeId) ? null : nativeId,
                    NativeIdType       = "xdata_uuid",
                    NativeIdStability  = "stable",
                    AirenoBackpackId   = string.IsNullOrEmpty(backpackId) ? null : backpackId,
                    IdentityState      = identityState,
                    LinkState          = string.IsNullOrEmpty(backpackId) ? "unlinked" : "linked",
                    IsDefinition       = false,
                    DefinitionId       = btr.Id.Handle.Value.ToString("X"),

                    VisibleLabel       = string.IsNullOrEmpty(confirmedLabel) ? btr.Name : confirmedLabel,
                    DefinitionName     = btr.Name,
                    LayerOrTagName     = br.Layer,
                    AttributeText      = ReadAttributes(tr, br),

                    SpatialPosition    = new SpatialPosition
                    {
                        X = br.Position.X,
                        Y = br.Position.Y,
                        Z = br.Position.Z,
                        Unit = "mm"
                    },

                    Units              = "mm",
                    BoundingBox        = TryBoundingBox(br),
                    GeometryType       = "block_reference",
                    IsClosedBoundary   = null,

                    NativeCategory     = "block",
                    NativeType         = btr.Name,

                    NamingConfidence       = string.IsNullOrEmpty(confirmedLabel) ? "medium" : "high",
                    StableIdConfidence     = "plugin_generated",
                    OverallSignalQuality   = string.IsNullOrEmpty(confirmedLabel) ? "medium" : "high"
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

        // ── Rooms (inferred from closed polylines) ──────────────────────────────────

        private static void ExtractRoomsFromPolylines(Transaction tr, Database db, ExtractionPayload payload)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || !pl.Closed) continue;

                payload.Rooms.Add(new RoomSignal
                {
                    NativeId    = pl.Handle.Value.ToString("X"),
                    Name        = null,
                    Area        = SafeArea(pl),
                    BoundaryBox = TryBoundingBox(pl),
                    RoomOrigin  = "boundary_inference"
                });
            }
        }

        private static double? SafeArea(Polyline pl)
        {
            try { return pl.Area; } catch { return null; }
        }

        // ── Misc ─────────────────────────────────────────────────────────────────────

        private static string HashFilename(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant());
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash)[..16];
        }

        private static void CountQuality(ExtractionPayload payload)
        {
            foreach (var o in payload.Objects)
            {
                switch (o.OverallSignalQuality)
                {
                    case "high":   payload.Summary.HighQualityCount++;   break;
                    case "medium": payload.Summary.MediumQualityCount++; break;
                    default:       payload.Summary.LowQualityCount++;    break;
                }
            }
        }
    }
}
