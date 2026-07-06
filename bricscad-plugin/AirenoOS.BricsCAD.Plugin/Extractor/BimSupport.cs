using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;

namespace AirenoOS.BricsCAD.Plugin.Extractor
{
    /// <summary>
    /// Bridge to BricsCAD's BIM .NET API (Bricscad.Bim in BrxMgd.dll).
    ///
    /// Customer feedback #10 (2026-06-17): when the host has the BIM module
    /// licensed and active, the plugin should use BIM-native signals — BIM
    /// Room/Space entities as rooms (instead of closed polylines), BIM property
    /// sets for richer object properties, BIM GlobalGuid as a stable_bim_guid
    /// identity, and source_software_type="bricsCAD_bim" in the payload.
    ///
    /// BricsCAD V25 exposes the BIM API via STATIC METHODS on BIMClassification,
    /// BIMRoom, and BIMPropertySet — NOT through instance constructors taking
    /// ObjectId (verified 2026-06-17 in bim_diag.log: GetConstructor(ObjectId)
    /// returns null for BIMClassification, so the previous instance-ctor probe
    /// always fell through to BIMRoom's default-Category="Room" path).
    ///
    /// Every call goes through reflection so the plugin compiles + runs against
    /// V25 and V26 BrxMgd without a managed reference, and gracefully no-ops on
    /// hosts that don't have the BIM module.
    /// </summary>
    internal static class BimSupport
    {
        private static bool?  _bimAvailable;
        private static Type?  _bimClassificationType;
        private static Type?  _bimRoomType;
        private static Type?  _bimPropertySetType;
        private static bool   _typesProbed;
        private static readonly string DiagLogPath = Path.Combine(Path.GetTempPath(), "AirenoOS", "bim_diag.log");

        /// <summary>True when RUNASLEVEL indicates a BIM-capable license (BIM=3, Ultimate=5).</summary>
        public static bool IsBimAvailable
        {
            get
            {
                if (_bimAvailable.HasValue) return _bimAvailable.Value;
                try
                {
                    var raw = Application.GetSystemVariable("RUNASLEVEL");
                    int level = raw switch
                    {
                        short s => s,
                        int   i => i,
                        long  l => (int)l,
                        string s when int.TryParse(s, out var n) => n,
                        _ => -1
                    };
                    _bimAvailable = level == 3 || level == 5;
                }
                catch { _bimAvailable = false; }
                return _bimAvailable.Value;
            }
        }

        private static void ProbeTypesOnce()
        {
            if (_typesProbed) return;
            _typesProbed = true;
            try
            {
                _bimClassificationType = Type.GetType("Bricscad.Bim.BIMClassification, BrxMgd");
                _bimRoomType           = Type.GetType("Bricscad.Bim.BIMRoom, BrxMgd");
                _bimPropertySetType    = Type.GetType("Bricscad.Bim.BIMPropertySet, BrxMgd");
            }
            catch { /* leave nulls */ }
        }

        /// <summary>
        /// Read BIM signals from an arbitrary BIM-classified entity (Wall, Slab,
        /// Door, Window, …). Uses BIMClassification static methods. Returns null
        /// when the entity isn't BIM-classified or the host has no BIM module.
        /// </summary>
        public static BimEntityInfo? TryReadEntity(ObjectId entId)
        {
            if (!IsBimAvailable) return null;
            ProbeTypesOnce();
            if (_bimClassificationType == null) return null;

            try
            {
                // Skip if not classified — saves wasted reflection on every solid.
                var isUnclass = InvokeStatic<bool>(_bimClassificationType, "IsUnclassified", new object[] { entId });
                if (isUnclass) return null;

                // localName=false → canonical English name (locale-independent).
                var classification = InvokeStatic<string>(_bimClassificationType, "GetClassificationName", new object[] { entId, false });
                if (string.IsNullOrEmpty(classification)) return null;

                var info = new BimEntityInfo
                {
                    Category   = classification,
                    IfcClass   = GuessIfcFromType(classification),
                    Name       = InvokeStatic<string>(_bimClassificationType, "GetName",        new object[] { entId }),
                    // GetDescription often returns empty — caller falls through if null.
                    Properties = ReadAllProperties(entId)
                };
                return info;
            }
            catch (Exception ex)
            {
                DiagLog("TryReadEntity threw for handle " + entId.Handle + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Enumerate every BIM Room/Space in the database via
        /// BIMRoom.GetAllRooms(database). Each entry gets resolved into a
        /// BimRoomInfo by calling the per-room getters. Returns an empty list
        /// when the host has no BIM module or the database has no rooms.
        /// </summary>
        public static List<BimRoomInfo> EnumerateRooms(Database db)
        {
            var result = new List<BimRoomInfo>();
            if (!IsBimAvailable) return result;
            ProbeTypesOnce();
            if (_bimRoomType == null) return result;

            try
            {
                // Single-arg GetAllRooms(Database) — that's the no-building/no-story overload.
                var rooms = InvokeStatic<object>(_bimRoomType, "GetAllRooms", new object[] { db });
                if (rooms is IEnumerable roomEnum)
                {
                    foreach (var roomObj in roomEnum)
                    {
                        if (!(roomObj is ObjectId roomId)) continue;
                        var name = InvokeStatic<string>(_bimRoomType, "GetRoomName",        new object[] { roomId });
                        var num  = InvokeStatic<string>(_bimRoomType, "GetRoomNumber",      new object[] { roomId });
                        var area = InvokeStatic<double>(_bimRoomType, "GetRoomArea",        new object[] { roomId });
                        var desc = InvokeStatic<string>(_bimRoomType, "GetRoomDescription", new object[] { roomId });
                        var dept = InvokeStatic<string>(_bimRoomType, "GetRoomDepartment",  new object[] { roomId });
                        result.Add(new BimRoomInfo
                        {
                            ObjectHandleHex = roomId.Handle.Value.ToString("X"),
                            Name        = string.IsNullOrEmpty(name) ? null : name,
                            Number      = string.IsNullOrEmpty(num)  ? null : num,
                            AreaSqm     = area > 0 ? (double?)area : null,
                            Description = string.IsNullOrEmpty(desc) ? null : desc,
                            Department  = string.IsNullOrEmpty(dept) ? null : dept
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("EnumerateRooms threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Pull every BIM property set value on an entity via
        /// BIMPropertySet.ListAllProperties(objId, showInvisible=false,
        /// nameSpace=null). The return is a flat name → string-value dict.
        /// </summary>
        public static Dictionary<string, string>? ReadAllProperties(ObjectId entId)
        {
            if (!IsBimAvailable) return null;
            ProbeTypesOnce();
            if (_bimPropertySetType == null) return null;

            try
            {
                var raw = InvokeStatic<object>(_bimPropertySetType, "ListAllProperties",
                                new object[] { entId, /*showInvisible*/ false, /*nameSpace*/ null! });
                if (raw == null) return null;

                // ListAllProperties returns Dictionary<string, Dictionary<string, object>> —
                // outer key is property-set name, inner is property name → value. Flatten.
                var result = new Dictionary<string, string>();
                if (raw is IDictionary outer)
                {
                    foreach (DictionaryEntry setEntry in outer)
                    {
                        if (setEntry.Value is IDictionary inner)
                        {
                            foreach (DictionaryEntry propEntry in inner)
                            {
                                var name  = propEntry.Key?.ToString();
                                var value = propEntry.Value?.ToString();
                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                                    result[name!] = value!;
                            }
                        }
                    }
                }
                return result.Count == 0 ? null : result;
            }
            catch (Exception ex)
            {
                DiagLog("ReadAllProperties threw: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // ── Reflection helpers ──────────────────────────────────────────────────

        private static T? InvokeStatic<T>(Type type, string methodName, object[] args)
        {
            try
            {
                // Match by name+arg count first to handle overloads like
                // GetAllRooms(Database) vs GetAllRooms(BIMStory, Database).
                MethodInfo? method = null;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    if (m.GetParameters().Length != args.Length) continue;
                    method = m;
                    break;
                }
                if (method == null) return default;
                var ret = method.Invoke(null, args);
                if (ret == null) return default;
                if (ret is T tret) return tret;
                // Allow enum → string coercion.
                if (typeof(T) == typeof(string) && ret.GetType().IsEnum) return (T)(object)ret.ToString()!;
                return default;
            }
            catch (Exception ex)
            {
                DiagLog("InvokeStatic " + methodName + " threw: " + ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        // BIM-classified entities often lack an explicit IFC tag — derive from
        // the classification type name as a sensible default so the customer's
        // MCP pipeline gets an ifc_class for every Wall/Slab/Door/Window/etc.
        // V25 returns names with a BIM_ prefix (e.g. "BIM_WALL"); strip it.
        private static string? GuessIfcFromType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var t = typeName.Trim().ToLowerInvariant();
            if (t.StartsWith("bim_"))     t = t.Substring(4);
            else if (t.StartsWith("bim")) t = t.Substring(3);
            return t switch
            {
                "wall"           => "IfcWall",
                "slab"           => "IfcSlab",
                "door"           => "IfcDoor",
                "window"         => "IfcWindow",
                "column"         => "IfcColumn",
                "beam"           => "IfcBeam",
                "roof"           => "IfcRoof",
                "stair"          => "IfcStair",
                "railing"        => "IfcRailing",
                "covering"       => "IfcCovering",
                "curtainwall"    => "IfcCurtainWall",
                "space"          => "IfcSpace",
                "room"           => "IfcSpace",
                "building"       => "IfcBuilding",
                "story"          => "IfcBuildingStorey",
                "floor"          => "IfcBuildingStorey",
                "site"           => "IfcSite",
                _                => null
            };
        }

        // ── Diagnostic ──────────────────────────────────────────────────────────

        private static bool _diagDumped;

        /// <summary>One-shot: list every entity in ModelSpace with its runtime
        /// type, layer, and BIM classification (or "unclassified"). Lets us see
        /// what the BIM API reports for each solid the customer drew.</summary>
        public static void DumpModelSpaceDiagnostic(Transaction tr, Database db)
        {
            if (_diagDumped) return;
            _diagDumped = true;
            ProbeTypesOnce();
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int idx = 0;
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string classification = "unclassified";
                    try
                    {
                        if (_bimClassificationType != null)
                        {
                            var isUnclass = InvokeStatic<bool>(_bimClassificationType, "IsUnclassified", new object[] { id });
                            if (!isUnclass)
                            {
                                classification = InvokeStatic<string>(_bimClassificationType, "GetClassificationName", new object[] { id, false }) ?? "<classified-no-name>";
                            }
                        }
                    }
                    catch (Exception cx) { classification = "<err:" + cx.Message + ">"; }
                    DiagLog("[" + idx + "] type=" + ent.GetRXClass().Name + " layer=" + ent.Layer + " handle=" + ent.Handle.Value.ToString("X") + " classification=" + classification);
                    idx++;
                }
                DiagLog("=== DumpModelSpaceDiagnostic finished (" + idx + " entities) ===");
            }
            catch (Exception ex)
            {
                DiagLog("DumpModelSpaceDiagnostic threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void DiagLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagLogPath)!);
                File.AppendAllText(DiagLogPath, "[" + DateTime.UtcNow.ToString("O") + "] " + msg + "\n");
            }
            catch { }
        }
    }

    internal class BimEntityInfo
    {
        public string?                       Name       { get; set; }
        public string?                       GlobalGuid { get; set; }
        public double?                       Area       { get; set; }
        public string?                       Category   { get; set; }
        public string?                       IfcClass   { get; set; }
        public Dictionary<string, string>?   Properties { get; set; }
    }

    internal class BimRoomInfo
    {
        public string  ObjectHandleHex { get; set; } = string.Empty;
        public string? Name        { get; set; }
        public string? Number      { get; set; }
        public double? AreaSqm     { get; set; }
        public string? Description { get; set; }
        public string? Department  { get; set; }
    }
}
