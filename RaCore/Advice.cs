namespace RaCore;

/// <summary>One attempt at an explicitly highlighted target. Distances are physical desktop pixels, not mouse counts or game angles.</summary>
public sealed class Trial
{
    public double Degrees; // legacy log compatibility; new tests use actual pixel distances
    public double DistancePx;
    public double AngleRad;
    public double StartMs, EndMs;
    public double OriginX, OriginY, TargetX, TargetY;
    public double ClickX, ClickY;
    public double RadialPx, TangentialPx, RadialPct, ErrorPx;
    public double PeakSpeed; // approximate POST-driver output counts/ms; never input hand speed
    public bool Hit;
    public int TargetId, VisibleTargets, SampleCount;
    public bool TelemetryTruncated;
    /// <summary>False when the click landed on no ball and was attributed to the nearest one.</summary>
    public bool BallUnderCursor = true;
    public long SpeedStartSeq;

    // ---- open loop: the FIRST ballistic movement, before any correction ----
    public bool HasPrimary;
    public double PrimaryRadialPx, PrimaryTangentialPx, PrimaryRadialPct, PrimaryErrorPx;
    public double PrimaryTimeMs, PrimaryPeakCms;
    public bool Corrected;              // the pointer kept advancing towards the target after the impulse
    public bool PrimaryScaled;          // path rescaled from counts to real pointer displacement
    /// <summary>Segmentation-free: furthest along-axis point reached, as % of the aim distance.</summary>
    public double MaxExtentPct;
    public bool Overshot;               // it went past the far edge of the ball at some point
    public double TimeToTargetMs = double.NaN;   // first moment the pointer was inside the ball
    public double OnsetMs;              // when the movement started

    // ---- path shape: how much the pointer curved instead of going straight (see OpenLoop) ----
    public double LateralPathPx;        // total travel perpendicular to the aim line
    public double ArcRatio;             // lateral path / aim distance; 0 = straight line
    public int PathReversals;           // perpendicular direction changes: 1 = out and back (a hook)
    public double PerpPeakPx;           // signed furthest distance from the aim line during the impulse
    public double AsymCoef = double.NaN; // + = Y over-driven vs X, - = X over-driven vs Y, 0 = noise
    /// <summary>Raw least-squares pieces behind AsymCoef; the round pools these and fits once,
    /// because averaging per-flick ratios lets one near-axis flick dominate the answer.</summary>
    public double AsymNum, AsymDen;
}

public readonly record struct AxisProfile(double Threshold, double CapSpeed, double PeakGain);

public static class CurveAnalysis
{
    /// <summary>
    /// Where acceleration starts and where it stops rising, in the driver's own units.
    /// Computed with one 1ms packet and a fresh state, so it describes the static curve:
    /// with smoothing halflives set the live response lags behind this, and with DPI
    /// normalization the driver compares in/s rather than counts/ms.
    /// </summary>
    public static AxisProfile Analyze(ra_profile prof, bool horizontal)
    {
        var ms = new ModifierSettings(prof);
        var mod = new Modifier(ms);
        const double time = 1.0, max = 30;
        const int n = 600;

        double OutAt(double v)
        {
            var sp = new SpeedProcessor();
            sp.Init(prof.speed_processor_args);
            var input = horizontal ? new vec2d { x = v * time, y = 0 } : new vec2d { x = 0, y = v * time };
            var o = mod.Modify(input, sp, 1, time);
            return horizontal ? o.x / time : o.y / time;
        }

        double GainAt(double v)
        {
            double h = v * 1e-3 + 1e-6;
            return (OutAt(v + h) - OutAt(Math.Max(v - h, 1e-6))) / (2 * h);
        }

        var gains = new double[n + 1];
        double best = 0;
        for (int i = 0; i <= n; i++)
        {
            gains[i] = GainAt(Math.Max(max * i / n, 1e-4));
            if (gains[i] > best) best = gains[i];
        }

        double threshold = 0, capSpeed = double.PositiveInfinity;

        int tIdx = -1;
        for (int i = 0; i <= n; i++) if (Math.Abs(gains[i] - 1) > 1e-3) { tIdx = i; break; }
        if (tIdx > 0)
        {
            double lo = max * (tIdx - 1) / n, hi = max * tIdx / n;
            for (int k = 0; k < 26; k++)
            {
                double mid = (lo + hi) / 2;
                if (Math.Abs(GainAt(Math.Max(mid, 1e-4)) - 1) > 1e-4) hi = mid; else lo = mid;
            }
            threshold = (lo + hi) / 2;
        }

        int cIdx = -1;
        for (int i = n; i > 0; i--) if (Math.Abs(gains[i] - best) > best * 0.002) { cIdx = i; break; }
        if (cIdx == -1) capSpeed = double.PositiveInfinity;
        else if (cIdx < n)
        {
            double lo = max * cIdx / n, hi = max * (cIdx + 1) / n;
            for (int k = 0; k < 26; k++)
            {
                double mid = (lo + hi) / 2;
                if (Math.Abs(GainAt(mid) - best) > best * 1e-3) lo = mid; else hi = mid;
            }
            capSpeed = (lo + hi) / 2;
        }

        return new AxisProfile(threshold, capSpeed, best);
    }
}
