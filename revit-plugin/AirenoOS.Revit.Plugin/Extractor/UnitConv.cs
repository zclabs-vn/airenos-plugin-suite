namespace AirenoOS.Revit.Plugin.Extractor
{
    /// <summary>
    /// Revit internal units: feet for length, square feet for area, cubic feet for
    /// volume. AirenoOS reports millimetres / mm² / mm³ on the wire. Constants used
    /// here are exact (not floating-point approximations) — 1 foot is defined as
    /// 304.8 mm by international agreement.
    ///
    /// We avoid UnitUtils so the same code compiles on Revit 2024/2025/2026 where
    /// the ForgeTypeId API surface differs.
    /// </summary>
    internal static class UnitConv
    {
        private const double FootToMm   = 304.8;
        private const double Foot2ToMm2 = FootToMm * FootToMm;
        private const double Foot3ToMm3 = FootToMm * FootToMm * FootToMm;

        public static double LengthMm(double feet)  => feet  * FootToMm;
        public static double AreaMm2(double feet2)  => feet2 * Foot2ToMm2;
        public static double VolumeMm3(double feet3)=> feet3 * Foot3ToMm3;
    }
}
