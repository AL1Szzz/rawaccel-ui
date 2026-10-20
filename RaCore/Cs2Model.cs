namespace RaCore;

/// <summary>
/// Source-engine aim model, used to express the acceleration curve in CS2 terms.
///   degrees per mouse count = m_yaw * sensitivity            (m_yaw default 0.022)
///   vertical FOV is fixed, so horizontal FOV follows aspect:  hfov = 2*atan(0.75*aspect)
/// That constant makes hfov exactly 90 deg at 4:3, which is the Source convention.
/// These are modelled conversions, not measurements of the game binary.
/// </summary>
public static class Cs2
{
    public const double M_YAW = 0.022;
    const double HalfTanVfov = 0.75;   // tan(vfov/2) with vfov = 73.74 deg

    public static double CountsPerDegree(double sensitivity) =>
        sensitivity > 0 ? 1.0 / (M_YAW * sensitivity) : 0;

    public static double DegreesPerCount(double sensitivity) => M_YAW * sensitivity;

    public static double HorizontalFov(double aspect) => 2 * Math.Atan(HalfTanVfov * aspect) * 180 / Math.PI;

    public static double Aspect(int w, int h) => h > 0 ? (double)w / h : 16.0 / 9.0;

    /// <summary>Pixels on the CS2 screen that one mouse count turns the crosshair.</summary>
    public static double PixelsPerCount(int width, int height, double sensitivity)
    {
        if (sensitivity <= 0 || width <= 0) return 0;
        return DegreesPerCount(sensitivity) * (width / HorizontalFov(Aspect(width, height)));
    }

    /// <summary>cm of hand movement for a full 360 turn at the given DPI.</summary>
    public static double CmPer360(double sensitivity, double dpi)
    {
        if (sensitivity <= 0 || dpi <= 0) return 0;
        return 360 * CountsPerDegree(sensitivity) / dpi * 2.54;
    }

    /// <summary>counts needed for a given angle: this is the amplitude we ask for in the test.</summary>
    public static double CountsForDegrees(double degrees, double sensitivity) =>
        degrees * CountsPerDegree(sensitivity);

    /// <summary>counts/ms -> physical hand speed at the real DPI.</summary>
    public static double CmsToInPerSec(double countsPerMs, double dpi) =>
        dpi > 0 ? countsPerMs * 1000.0 / dpi : countsPerMs;

    public static double CmsToCmPerSec(double countsPerMs, double dpi) => CmsToInPerSec(countsPerMs, dpi) * 2.54;

    public static double InPerSecToCms(double ips, double dpi) => dpi > 0 ? ips * dpi / 1000.0 : ips;
}
