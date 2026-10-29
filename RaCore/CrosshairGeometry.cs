namespace RaCore;

/// <summary>
/// Where the four crosshair arms and the centre dot go, in DIPs, for a crosshair whose arm
/// length, gap and thickness are given in physical pixels. Pure arithmetic on purpose: the two
/// fullscreen windows (ball test, drills) must draw the same crosshair, and a pixel can only be
/// checked offscreen if the maths is separable from the Canvas that applies it.
/// </summary>
public readonly record struct CrossRect(double X, double Y, double W, double H);

public static class CrosshairGeometry
{
    public readonly record struct Parts(CrossRect Left, CrossRect Right, CrossRect Top, CrossRect Bottom, CrossRect Dot, double Thickness);

    /// <param name="x">pointer X in physical pixels, relative to the window</param>
    /// <param name="y">pointer Y in physical pixels, relative to the window</param>
    /// <param name="scale">device pixels per DIP for the window's current monitor</param>
    public static Parts Place(double x, double y, double armPx, double gapPx, double scale, double thicknessPx = 2.0)
    {
        if (scale <= 0) scale = 1;
        double cx = x / scale, cy = y / scale;
        double arm = Math.Max(0, armPx) / scale;
        double gap = Math.Max(0, gapPx) / scale;
        double t = Math.Max(0.5, thicknessPx) / scale;

        var left = new CrossRect(cx - gap - arm, cy - t / 2, arm, t);
        var right = new CrossRect(cx + gap, cy - t / 2, arm, t);
        var top = new CrossRect(cx - t / 2, cy - gap - arm, t, arm);
        var bottom = new CrossRect(cx - t / 2, cy + gap, t, arm);
        var dot = new CrossRect(cx - t / 2, cy - t / 2, t, t);
        return new Parts(left, right, top, bottom, dot, t);
    }
}
