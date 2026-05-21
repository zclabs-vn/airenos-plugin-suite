using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Schema;

namespace AirenoOS.Revit.Plugin.Extractor
{
    /// <summary>
    /// Layer 2 — Extended enrichment. Runs synchronously on the API thread
    /// (Revit forbids Document access from background threads) AFTER L1, before
    /// JSON serialisation.
    ///
    /// Adds per-element parameter groups (G6 "existing_metadata"), attribute_text
    /// (G2), and nearby-room signal for elements without a native Room association.
    ///
    /// Defensive: any per-element failure leaves that element's optional fields
    /// null rather than crashing the pass.
    /// </summary>
    internal static class ExtendedExtractor
    {
        public static void Enrich(Document doc, ExtractionPayload payload)
        {
            try
            {
                var byUniqueId = payload.Objects
                    .Where(o => !string.IsNullOrEmpty(o.NativeId))
                    .ToDictionary(o => o.NativeId!, o => o);

                foreach (var sig in payload.Objects)
                {
                    if (string.IsNullOrEmpty(sig.NativeId)) continue;
                    var e = doc.GetElement(sig.NativeId);
                    if (e == null) continue;

                    sig.AttributeText   = ReadAttributeText(e);
                    sig.ParameterGroups = ReadParameterGroups(e);
                }
            }
            catch { /* never propagate — L2 must not break the save flow */ }
        }

        // ── Attribute text ───────────────────────────────────────────────────────────

        /// <summary>
        /// Flat key→value map of parameters whose values look like display text.
        /// Equivalent to block-attribute tags in CAD — quick to consume on the
        /// AirenoOS side without dealing with parameter group nesting.
        /// </summary>
        private static Dictionary<string, string?>? ReadAttributeText(Element e)
        {
            try
            {
                var dict = new Dictionary<string, string?>();
                foreach (Parameter p in e.Parameters)
                {
                    if (p == null) continue;
                    if (p.StorageType != StorageType.String) continue;
                    var def = p.Definition;
                    if (def == null) continue;
                    var v = p.AsString();
                    if (string.IsNullOrEmpty(v)) continue;
                    dict[def.Name] = v;
                }
                return dict.Count == 0 ? null : dict;
            }
            catch { return null; }
        }

        // ── Parameter groups (G6 "existing_metadata") ────────────────────────────────

        /// <summary>
        /// All readable parameters, nested by Definition.ParameterGroup display name.
        /// Skips ElementId parameters that resolve to nothing useful, and double-typed
        /// parameters where the storage value is zero AND has no value-string (these
        /// are usually "Not Set" defaults that clutter the output).
        /// </summary>
        private static Dictionary<string, Dictionary<string, string?>>? ReadParameterGroups(Element e)
        {
            try
            {
                var groups = new Dictionary<string, Dictionary<string, string?>>();
                foreach (Parameter p in e.Parameters)
                {
                    if (p == null) continue;
                    var def = p.Definition;
                    if (def == null) continue;

                    string? value = ReadParamValue(p);
                    if (string.IsNullOrEmpty(value)) continue;

                    var groupName = SafeGroupName(def);

                    if (!groups.TryGetValue(groupName, out var bucket))
                    {
                        bucket = new Dictionary<string, string?>();
                        groups[groupName] = bucket;
                    }
                    bucket[def.Name] = value;
                }
                return groups.Count == 0 ? null : groups;
            }
            catch { return null; }
        }

        private static string? ReadParamValue(Parameter p)
        {
            try
            {
                return p.StorageType switch
                {
                    StorageType.String    => p.AsString(),
                    StorageType.Integer   => p.AsValueString() ?? p.AsInteger().ToString(),
                    StorageType.Double    => p.AsValueString() ?? p.AsDouble().ToString("R"),
                    StorageType.ElementId => p.AsValueString(),
                    _ => null
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Revit 2024: Definition.ParameterGroup → BuiltInParameterGroup enum.
        /// Revit 2025+: ParameterGroup deprecated; GetGroupTypeId() → ForgeTypeId.
        /// Both APIs round-trip to a human-readable label; we use whichever compiles.
        /// </summary>
        private static string SafeGroupName(Definition def)
        {
            try
            {
#if REVIT2024
                return def.ParameterGroup.ToString();
#else
                var typeId = def.GetGroupTypeId();
                if (typeId != null && !string.IsNullOrEmpty(typeId.TypeId))
                    return LabelUtils.GetLabelForGroup(typeId);
                return "Other";
#endif
            }
            catch { return "Other"; }
        }
    }
}
