namespace RaCore;

/// <summary>
/// Counts one turn from the raw mouse stream and turns it into the two numbers a player can act on:
/// how far the hand moved, and what that means as cm/360.
///
/// What this can and cannot know, because the difference matters:
///   * The counts are real. They come from WM_INPUT - the same stream a game reads - so the
///     distance in centimetres is measured, not assumed, once the DPI is right.
///   * The angle is NOT measured. It is counts x (m_yaw x sensitivity), the same mapping the game
///     would apply, so "rotate until the dial is back where it started" is guaranteed to happen at
///     exactly 360 degrees no matter how the mouse is moved. That makes the dial a faithful picture
///     of the game camera and a useless measuring device: it cannot discover a sensitivity it was
///     told. Calibration therefore needs one number from outside - either the sensitivity you
///     already play with, or a cm/360 you measured in game. <see cref="DpiFor"/> is what that
///     outside number buys: with a real cm/360 the DPI the tool was told can be checked against the
///     DPI the mouse is actually delivering.
///
/// Deliberately free of timers and drawing so a turn can be replayed in the self tests.
/// </summary>
public sealed class TurnMeasure
{
    /// <summary>Sensitivity the dial is drawn with. Changing it changes the picture, never the hand.</summary>
    public double Sensitivity = 1.5;
    public double Dpi = 600;

    /// <summary>Signed horizontal counts since the last reset. Yaw only - a turn is a horizontal act.</summary>
    public long Counts;
    /// <summary>Raw packets seen. Tells "the mouse did not move" apart from "raw input is not arriving".</summary>
    public long Packets;

    public double AngleDeg => Counts * SourceAim.DegreesPerCount(Sensitivity);
    public double AngleAbsDeg => Math.Abs(AngleDeg);
    public double Cm => CmOf(Counts, Dpi);

    /// <summary>0..1, for the arc on screen. A partial turn still reports, it just is not finished.</summary>
    public double Progress => Math.Clamp(AngleAbsDeg / 360.0, 0, 1);
    public bool Full => AngleAbsDeg >= 360.0;

    public void Add(long dx)
    {
        Packets++;
        Counts += dx;
    }

    public void Reset()
    {
        Counts = 0;
        Packets = 0;
    }

    /// <summary>cm of hand travel for a signed count total.</summary>
    public static double CmOf(long counts, double dpi) => dpi > 0 ? counts / dpi * 2.54 : 0;

    /// <summary>
    /// cm/360 implied by the current sensitivity and DPI. Constant by construction - it is the
    /// conversion, not a measurement - so the window shows it as "你现在的设置" rather than a result.
    /// </summary>
    public double CmPer360Implied => SourceAim.CmPer360(Sensitivity, Dpi);

    /// <summary>
    /// DPI implied by a cm/360 the player measured elsewhere (in game, with a ruler). Comparing it
    /// with <see cref="Dpi"/> is the only check here that can be wrong in a useful way.
    /// </summary>
    public static double DpiFor(double cm360, double sensitivity) => SourceAim.DpiForCm360(cm360, sensitivity);

    /// <summary>The sensitivity to type into the game so a full turn takes <paramref name="cm360"/>.</summary>
    public static double SensFor(double cm360, double dpi) => SourceAim.SensForCm360(cm360, dpi);
}
