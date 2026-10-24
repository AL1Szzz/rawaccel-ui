namespace RaCore;

/// <summary>
/// Source-engine aim conversions, used to say what a curve means in units you can feel:
/// degrees per mouse count = m_yaw * sensitivity (m_yaw is 0.022), and the vertical FOV is
/// fixed, so cm/360 = 360 * counts-per-degree / DPI * 2.54.
///
/// Pure arithmetic on the numbers you type in - no sampling, no measurement of anything.
/// </summary>
public static class SourceAim
{
    public const double M_YAW = 0.022;

    public static double CountsPerDegree(double sensitivity) =>
        sensitivity > 0 ? 1.0 / (M_YAW * sensitivity) : 0;

    public static double DegreesPerCount(double sensitivity) => M_YAW * sensitivity;

    /// <summary>cm of hand movement for a full 360° turn at the given DPI.</summary>
    public static double CmPer360(double sensitivity, double dpi)
    {
        if (sensitivity <= 0 || dpi <= 0) return 0;
        return 360 * CountsPerDegree(sensitivity) / dpi * 2.54;
    }
}
