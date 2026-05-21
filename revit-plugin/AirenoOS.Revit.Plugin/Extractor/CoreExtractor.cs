using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AirenoOS.Revit.Plugin.Schema;
using AirenoOS.Revit.Plugin.Storage;

namespace AirenoOS.Revit.Plugin.Extractor
{
    /// <summary>
    /// Layer 1 — Core extraction. Synchronous, runs while we still hold the API
    /// context. Walks model elements once and emits an ObjectSignal each, plus the
    /// rooms and levels tables.
    ///
    /// Fast path only — parameter-group enumeration, nearby-element scans, and
    /// linked-model resolution are deferred to ExtendedExtractor (L2). Anything
    /// that touches the Document must happen here.
    /// </summary>
    internal static class CoreExtractor
    {
        public static ExtractionPayload Extract(Document doc, string trigger)
        {
            var payload = new ExtractionPayload
            {
                DocumentProjectToken  = ProjectTokenManager.GetProjectToken(doc),
                SourceSoftwareVersion = doc.Application.VersionNumber,
                FileNameHash          = HashFilename(doc.PathName),
                ExtractedAt           = DateTime.UtcNow.ToString("o"),
                ExtractionTrigger     = trigger
            };

            ExtractLevels(doc, payload);
            ExtractRooms(doc, payload);
            ExtractElements(doc, payload);

            payload.Summary.TotalObjects = payload.Objects.Count;
            payload.Summary.TotalRooms   = payload.Rooms.Count;
            CountQuality(payload);

            return payload;
        }

        // ── Levels ───────────────────────────────────────────────────────────────────

        private static void ExtractLevels(Document doc, ExtractionPayload payload)
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(Level));
            foreach (Level lvl in collector)
            {
                payload.Levels.Add(new LevelSignal
                {
                    NativeId  = lvl.UniqueId,
                    Name      = lvl.Name,
                    Elevation = UnitConv.LengthMm(lvl.Elevation)
                });
            }
        }

        // ── Rooms (native) ───────────────────────────────────────────────────────────

        private static void ExtractRooms(Document doc, ExtractionPayload payload)
        {
            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType();

            foreach (Element e in collector)
            {
                if (e is not Room room) continue;
                // Unplaced rooms have zero area and no boundary — skip; they confuse
                // downstream consumers expecting valid geometry.
                if (room.Area <= 0) continue;

                payload.Rooms.Add(new RoomSignal
                {
                    NativeId    = room.UniqueId,
                    Name        = room.Name,
                    Number      = room.Number,
                    Level       = room.Level?.Name,
                    Area        = UnitConv.AreaMm2(room.Area),
                    Volume      = SafeVolumeMm3(room),
                    Phase       = SafePhaseName(doc, room.get_Parameter(BuiltInParameter.ROOM_PHASE)),
                    BoundaryBox = TryBoundingBoxMm(room),
                    RoomOrigin  = "native"
                });
            }
        }

        // ── Elements ─────────────────────────────────────────────────────────────────

        private static void ExtractElements(Document doc, ExtractionPayload payload)
        {
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();

            foreach (Element e in collector)
            {
                // Drop elements without a category (system bookkeeping, internal types).
                if (e.Category == null) continue;
                // Drop categories that aren't model/annotation worth syncing.
                if (!IsExtractable(e)) continue;

                var stored = ElementAirenoData.Read(e);

                var fi = e as FamilyInstance;
                var sym = fi?.Symbol;

                var sig = new ObjectSignal
                {
                    // G1
                    NativeId          = e.UniqueId,
                    NativeIdType      = "guid",
                    NativeIdStability = "native_stable",
                    AirenoBackpackId  = NullIfEmpty(stored.BackpackId),
                    IdentityState     = NullIfEmpty(stored.IdentityState) ?? "raw",
                    LinkState         = string.IsNullOrEmpty(stored.BackpackId) ? "unlinked" : "linked",
                    IsDefinition      = false,
                    DefinitionId      = sym?.UniqueId,

                    // G2
                    VisibleLabel      = NullIfEmpty(stored.ConfirmedLabel) ?? e.Name,
                    DefinitionName    = sym != null ? $"{sym.Family?.Name}: {sym.Name}" : e.Name,
                    LayerOrTagName    = null, // Revit has no layers — Category is in G5
                    AttributeText     = null, // Phase 1: param dump is L2

                    // G3
                    RoomOrZoneNativeId = SafeRoomUniqueId(fi),
                    RoomOrZoneName     = SafeRoomName(fi),
                    LevelOrFloor       = SafeLevelName(doc, e),
                    SpatialPosition    = TryPosition(e),
                    ContainerType      = fi != null ? "family_instance" : e.GetType().Name,

                    // G4
                    Units            = "mm",
                    BoundingBox      = TryBoundingBoxMm(e),
                    Area             = SafeAreaMm2(e),
                    Volume           = SafeVolumeMm3(e),
                    GeometryType     = e.GetType().Name,

                    // G5
                    NativeCategory   = e.Category.Name,
                    NativeType       = sym?.Name,
                    RenovationStatus = SafeRenovationStatus(doc, e),
                    StructuralFlag   = SafeStructuralFlag(e),
                    IfcClass         = SafeParamString(e, BuiltInParameter.IFC_EXPORT_ELEMENT_AS),
                    // OMNICLASS / UNIFORMAT enum names have shifted between Revit versions
                    // (and disappeared from BuiltInParameter in 2025+). Look up by display
                    // name, which is stable across versions, and accept null if missing.
                    OmniclassCode    = LookupString(sym, "OmniClass Number") ?? LookupString(sym, "OmniClass Code"),
                    UniclassCode     = LookupString(e,   "Uniclass Code")   ?? LookupString(sym, "Uniclass Code"),
                    AssemblyCode     = LookupString(e,   "Assembly Code"),

                    // G6 — lightweight subset; full param dump in L2.
                    MetadataFormat   = "parameter",
                    ParameterGroups  = null,
                    BimProperties    = ReadBimProperties(e, sym),

                    // G7
                    WorksetName      = SafeWorksetName(doc, e),

                    // G8 — Revit always gets the "native" pedigree.
                    ElementTypeOrigin    = "native",
                    RoomOrigin           = "native",
                    NamingConfidence     = "strong",
                    StableIdConfidence   = "native_stable",
                    OverallSignalQuality = "high"
                };

                payload.Objects.Add(sig);
            }
        }

        /// <summary>
        /// Filter to model-worth-extracting categories. Drops sketch lines, dimensions,
        /// detail items, etc. — those flow into L2 if needed.
        /// </summary>
        private static bool IsExtractable(Element e)
        {
            // No location AND no bounding box → not a placed element → skip.
            if (e.Location == null && e.get_BoundingBox(null) == null) return false;

            var bic = (BuiltInCategory)e.Category.Id.AsLong();
            return bic switch
            {
                BuiltInCategory.OST_Cameras
                  or BuiltInCategory.OST_Lines
                  or BuiltInCategory.OST_SketchLines
                  or BuiltInCategory.OST_CLines
                  or BuiltInCategory.OST_Constraints
                  or BuiltInCategory.OST_RvtLinks
                  or BuiltInCategory.OST_SectionBox
                  or BuiltInCategory.OST_Viewports
                  or BuiltInCategory.OST_Views
                  or BuiltInCategory.OST_Sheets
                  or BuiltInCategory.OST_TitleBlocks
                  or BuiltInCategory.OST_DetailComponents => false,
                _ => true
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────

        private static BimProperties ReadBimProperties(Element e, FamilySymbol? sym)
        {
            return new BimProperties
            {
                FireRating     = LookupString(e, "Fire Rating") ?? LookupString(sym, "Fire Rating"),
                AcousticRating = LookupString(e, "Acoustic Rating") ?? LookupString(sym, "Acoustic Rating"),
                Manufacturer   = SafeParamString(sym, BuiltInParameter.ALL_MODEL_MANUFACTURER)
                                  ?? SafeParamString(e, BuiltInParameter.ALL_MODEL_MANUFACTURER),
                Model          = SafeParamString(sym, BuiltInParameter.ALL_MODEL_MODEL)
                                  ?? SafeParamString(e, BuiltInParameter.ALL_MODEL_MODEL),
                Phase          = SafePhaseName(e.Document, e.get_Parameter(BuiltInParameter.PHASE_CREATED)),
                Description    = SafeParamString(sym, BuiltInParameter.ALL_MODEL_DESCRIPTION)
                                  ?? SafeParamString(e, BuiltInParameter.ALL_MODEL_DESCRIPTION),
                Comments       = SafeParamString(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            };
        }

        private static SpatialPosition? TryPosition(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp)
                    return new SpatialPosition
                    {
                        X = UnitConv.LengthMm(lp.Point.X),
                        Y = UnitConv.LengthMm(lp.Point.Y),
                        Z = UnitConv.LengthMm(lp.Point.Z),
                        Unit = "mm"
                    };

                if (e.Location is LocationCurve lc)
                {
                    var mid = lc.Curve.Evaluate(0.5, true);
                    return new SpatialPosition
                    {
                        X = UnitConv.LengthMm(mid.X),
                        Y = UnitConv.LengthMm(mid.Y),
                        Z = UnitConv.LengthMm(mid.Z),
                        Unit = "mm"
                    };
                }

                // Fallback: centre of bounding box.
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    return new SpatialPosition
                    {
                        X = UnitConv.LengthMm((bb.Min.X + bb.Max.X) / 2),
                        Y = UnitConv.LengthMm((bb.Min.Y + bb.Max.Y) / 2),
                        Z = UnitConv.LengthMm((bb.Min.Z + bb.Max.Z) / 2),
                        Unit = "mm"
                    };
                }
            }
            catch { }
            return null;
        }

        private static BoundingBox? TryBoundingBoxMm(Element e)
        {
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                return new BoundingBox
                {
                    Width  = UnitConv.LengthMm(bb.Max.X - bb.Min.X),
                    Height = UnitConv.LengthMm(bb.Max.Y - bb.Min.Y),
                    Depth  = UnitConv.LengthMm(bb.Max.Z - bb.Min.Z)
                };
            }
            catch { return null; }
        }

        private static double? SafeAreaMm2(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (p != null && p.StorageType == StorageType.Double)
                    return UnitConv.AreaMm2(p.AsDouble());
            }
            catch { }
            return null;
        }

        private static double? SafeVolumeMm3(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (p != null && p.StorageType == StorageType.Double)
                    return UnitConv.VolumeMm3(p.AsDouble());
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Version-safe parameter lookup by display name. Survives renames and removed
        /// BuiltInParameter enum values (Revit 2025+ removed several we relied on).
        /// </summary>
        private static string? LookupString(Element? e, string displayName)
        {
            if (e == null) return null;
            try
            {
                var p = e.LookupParameter(displayName);
                if (p == null) return null;
                return p.StorageType switch
                {
                    StorageType.String   => string.IsNullOrEmpty(p.AsString()) ? null : p.AsString(),
                    StorageType.Integer  => p.AsValueString() ?? p.AsInteger().ToString(),
                    StorageType.Double   => p.AsValueString() ?? p.AsDouble().ToString("R"),
                    StorageType.ElementId=> p.AsValueString(),
                    _ => p.AsValueString()
                };
            }
            catch { return null; }
        }

        private static string? SafeParamString(Element? e, BuiltInParameter bip)
        {
            if (e == null) return null;
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null) return null;
                return p.StorageType switch
                {
                    StorageType.String   => string.IsNullOrEmpty(p.AsString()) ? null : p.AsString(),
                    StorageType.Integer  => p.AsInteger().ToString(),
                    StorageType.Double   => p.AsDouble().ToString("R"),
                    StorageType.ElementId=> p.AsValueString(),
                    _ => p.AsValueString()
                };
            }
            catch { return null; }
        }

        private static RenovationStatus? SafeRenovationStatus(Document doc, Element e)
        {
            try
            {
                var created = SafePhaseName(doc, e.get_Parameter(BuiltInParameter.PHASE_CREATED));
                var demoed  = SafePhaseName(doc, e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED));
                if (created == null && demoed == null) return null;
                return new RenovationStatus { PhaseCreated = created, PhaseDemolished = demoed };
            }
            catch { return null; }
        }

        private static string? SafePhaseName(Document doc, Parameter? p)
        {
            try
            {
                if (p == null || p.StorageType != StorageType.ElementId) return null;
                var id = p.AsElementId();
                if (!id.IsValid()) return null;
                return doc.GetElement(id)?.Name;
            }
            catch { return null; }
        }

        private static bool? SafeStructuralFlag(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.INSTANCE_STRUCT_USAGE_PARAM);
                if (p == null) return null;
                // Any non-default value = structural usage assigned.
                return p.AsInteger() != 0;
            }
            catch { return null; }
        }

        private static string? SafeRoomUniqueId(FamilyInstance? fi)
        {
            try
            {
                var r = fi?.Room ?? fi?.FromRoom ?? fi?.ToRoom;
                return r?.UniqueId;
            }
            catch { return null; }
        }

        private static string? SafeRoomName(FamilyInstance? fi)
        {
            try
            {
                var r = fi?.Room ?? fi?.FromRoom ?? fi?.ToRoom;
                return r?.Name;
            }
            catch { return null; }
        }

        private static string? SafeLevelName(Document doc, Element e)
        {
            try
            {
                if (e.LevelId.IsValid())
                    return doc.GetElement(e.LevelId)?.Name;
            }
            catch { }
            return null;
        }

        private static string? SafeWorksetName(Document doc, Element e)
        {
            try
            {
                if (!doc.IsWorkshared) return null;
                var ws = doc.GetWorksetTable().GetWorkset(e.WorksetId);
                return ws?.Name;
            }
            catch { return null; }
        }

        // ── Misc ─────────────────────────────────────────────────────────────────────

        private static string HashFilename(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
            var bytes = Encoding.UTF8.GetBytes(name);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

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
