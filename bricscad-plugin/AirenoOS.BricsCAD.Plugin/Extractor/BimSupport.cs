using System;
using System.Collections.Generic;
using System.Reflection;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;

namespace AirenoOS.BricsCAD.Plugin.Extractor
{
    /// <summary>
    /// Defensive bridge to BricsCAD's BIM .NET API (Bricscad.Bim in BrxMgd.dll).
    ///
    /// Customer feedback #10 (2026-06-17): when the host has the BIM module
    /// licensed and active, the plugin should use BIM-native signals — BIM
    /// Room/Space entities as rooms (instead of closed polylines), BIM property
    /// sets for richer object properties, BIM GlobalGuid as a stable_bim_guid
    /// identity, and source_software_type="bricsCAD_bim" in the payload.
    ///
    /// Every BIM API call goes through reflection so:
    ///  - the plugin compiles + runs against V25 and V26 BrxMgd without a
    ///    direct reference to types that may be renamed across versions
    ///  - hosts without the BIM module (RUNASLEVEL not 3 / 5) silently skip
    ///    BIM probing and fall back to the original polyline-based logic
    ///  - any malformed entity (BIM extension dictionary missing, etc.) is
    ///    caught at the call site and the object falls through to the legacy
    ///    extraction path with no payload regression
    /// </summary>
    internal static class BimSupport
    {
        private static bool?  _bimAvailable;
        private static Type?  _bimRoomType;
        private static Type?  _bimAttributeSetType;
        private static bool   _typesProbed;

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
                        int i   => i,
                        long l  => (int)l,
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
                // BrxMgd is loaded by BricsCAD before our plugin — Type.GetType with
                // a fully-qualified name (incl. assembly) finds it without a
                // managed reference and survives BrxMgd version drift across V25/V26.
                _bimRoomType         = Type.GetType("Bricscad.Bim.BIMRoom, BrxMgd")
                                    ?? Type.GetType("Bricscad.Bim.BimRoom, BrxMgd");
                _bimAttributeSetType = Type.GetType("Bricscad.Bim.BIMAttributeSet, BrxMgd")
                                    ?? Type.GetType("Bricscad.Bim.BimAttributeSet, BrxMgd");
            }
            catch { /* either type may be absent on a given build — leave nulls */ }
        }

        /// <summary>
        /// Try to read BIM-side signals from an entity. Returns null when the entity
        /// isn't a BIM Room/Space or when the host has no BIM module — callers fall
        /// back to the legacy block/polyline path.
        /// </summary>
        public static BimEntityInfo? TryReadEntity(ObjectId entId)
        {
            if (!IsBimAvailable) return null;
            ProbeTypesOnce();
            if (_bimRoomType == null) return null;

            try
            {
                var ctor = _bimRoomType.GetConstructor(new[] { typeof(ObjectId) });
                if (ctor == null) return null;
                var instance = ctor.Invoke(new object[] { entId });
                if (instance == null) return null;

                return new BimEntityInfo
                {
                    Name       = ReadStringProp(instance, "Name", "RoomName", "DisplayName"),
                    GlobalGuid = ReadStringProp(instance, "GlobalGuid", "GlobalId", "Guid"),
                    Area       = ReadDoubleProp(instance, "Area", "FloorArea"),
                    Category   = ReadStringProp(instance, "Category", "ClassificationName"),
                    IfcClass   = ReadStringProp(instance, "IfcClass", "EntityType"),
                    Properties = ReadPropertySets(entId)
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Try to read BIM property sets (name → value, flattened across all sets).
        /// Used to enrich Group6Metadata.bim_properties beyond what XDATA attributes
        /// expose. Returns null when no property data is available.
        /// </summary>
        public static Dictionary<string, string>? ReadPropertySets(ObjectId entId)
        {
            if (!IsBimAvailable) return null;
            ProbeTypesOnce();
            if (_bimAttributeSetType == null) return null;

            try
            {
                var ctor = _bimAttributeSetType.GetConstructor(new[] { typeof(ObjectId) });
                if (ctor == null) return null;
                var instance = ctor.Invoke(new object[] { entId });
                if (instance == null) return null;

                var dict = new Dictionary<string, string>();
                // BIMAttributeSet exposes per-property accessors. Iterate any
                // System.Collections.IEnumerable returned by Properties/Items, OR
                // fall back to known string accessors. Best-effort: anything we
                // can pull, we add; the rest stays null in Group6Metadata.
                if (instance is System.Collections.IEnumerable directEnum)
                {
                    PopulateFromEnumerable(directEnum, dict);
                }
                else
                {
                    foreach (var propName in new[] { "Properties", "Items", "Attributes" })
                    {
                        if (instance.GetType().GetProperty(propName)?.GetValue(instance) is System.Collections.IEnumerable seq)
                        {
                            PopulateFromEnumerable(seq, dict);
                            break;
                        }
                    }
                }

                return dict.Count == 0 ? null : dict;
            }
            catch
            {
                return null;
            }
        }

        private static void PopulateFromEnumerable(System.Collections.IEnumerable seq, Dictionary<string, string> dict)
        {
            foreach (var item in seq)
            {
                if (item == null) continue;
                var name  = ReadStringProp(item, "Name", "Key", "PropertyName");
                var value = ReadStringProp(item, "Value", "StringValue")
                         ?? item.GetType().GetProperty("Value")?.GetValue(item)?.ToString();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                    dict[name!] = value!;
            }
        }

        private static string? ReadStringProp(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v == null) continue;
                    var s = v.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { /* try next */ }
            }
            return null;
        }

        private static double? ReadDoubleProp(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v is double d) return d;
                    if (v is float f)  return (double)f;
                }
                catch { /* try next */ }
            }
            return null;
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
}
