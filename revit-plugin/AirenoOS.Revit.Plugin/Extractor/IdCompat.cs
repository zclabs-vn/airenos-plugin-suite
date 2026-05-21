using Autodesk.Revit.DB;

namespace AirenoOS.Revit.Plugin.Extractor
{
    /// <summary>
    /// ElementId numeric access bridge.
    /// Revit 2024: IntegerValue (int) — preferred, Value also exists.
    /// Revit 2025+: IntegerValue removed; Value (long) is the only option.
    /// Code paths that only need to check validity use ElementId.InvalidElementId.
    /// </summary>
    internal static class IdCompat
    {
        public static bool IsValid(this ElementId? id)
            => id != null && id != ElementId.InvalidElementId;

        public static long AsLong(this ElementId id)
        {
#if REVIT2024
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }
    }
}
