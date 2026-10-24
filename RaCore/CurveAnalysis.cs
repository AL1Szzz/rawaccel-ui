using RaCore.Model;

namespace RaCore;

/// <summary>Where one axis's curve starts and stops rising, in the driver's own units.</summary>
public readonly record struct AxisProfile(double Threshold, double CapSpeed, double PeakGain);

/// <summary>
/// Reads a profile the way the driver would run it and reports the two numbers that actually
/// describe the shape: the speed where the multiplier leaves 1.0, and the speed where it stops
/// rising. The chart markers, the plain-language readout and the self tests all come from here,
/// so they cannot disagree with each other or with the driver.
/// </summary>
public static class CurveAnalysis
{
    /// <summary>
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

        // The reference is the curve's OWN slow-speed gain, not 1.0: an output ratio (yx/lr/ud)
        // is applied after the accel stage, so a profile with 垂直倍率 1.25 sits at gain 1.25
        // while it is still below the offset. Comparing against 1.0 reported "acceleration starts
        // at 0.00" for exactly the profiles where the multiplier is the whole point.
        double baseGain = gains[0];

        double threshold = 0, capSpeed = double.PositiveInfinity;

        int tIdx = -1;
        for (int i = 0; i <= n; i++) if (Math.Abs(gains[i] - baseGain) > 1e-3) { tIdx = i; break; }
        if (tIdx > 0)
        {
            double lo = max * (tIdx - 1) / n, hi = max * tIdx / n;
            for (int k = 0; k < 26; k++)
            {
                double mid = (lo + hi) / 2;
                if (Math.Abs(GainAt(Math.Max(mid, 1e-4)) - baseGain) > 1e-4) hi = mid; else lo = mid;
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
