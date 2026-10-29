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

    /// <summary>
    /// The sensitivity that makes a full turn take <paramref name="cm360"/> of hand travel.
    /// Inverse of <see cref="CmPer360"/>; the calibration window uses it to answer "想要 30cm 转一圈，
    /// 游戏里该填多少".
    /// </summary>
    public static double SensForCm360(double cm360, double dpi)
    {
        if (cm360 <= 0 || dpi <= 0) return 0;
        double countsPer360 = cm360 / 2.54 * dpi;
        return countsPer360 > 0 ? 360.0 / (M_YAW * countsPer360) : 0;
    }

    /// <summary>
    /// The DPI implied by a known cm/360 and sensitivity. This is the one direction that can catch a
    /// wrong DPI: a player who measured their real cm/360 in game and got a different number than
    /// <see cref="CmPer360"/> predicts is not moving the mouse the way the setting says.
    /// </summary>
    public static double DpiForCm360(double cm360, double sensitivity)
    {
        if (cm360 <= 0 || sensitivity <= 0) return 0;
        return 360 * CountsPerDegree(sensitivity) / cm360 * 2.54;
    }
}
