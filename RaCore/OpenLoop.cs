namespace RaCore;

/// <summary>
/// The primary (ballistic) impulse of one flick: where the pointer actually stopped at the end
/// of the FIRST movement, before any correction could kick in.
///
/// Why this exists: endpoint accuracy at click time is closed-loop. Give the player extra gain
/// and they unconsciously dial back the input, so the final click looks identical (measured: a
/// 2x change in the acceleration term moved no accuracy metric at all). The initial impulse has
/// no time to be corrected, so its endpoint error scales with the gain actually applied - that
/// is the quantity that can tell an overshoot from a bias-free curve.
/// </summary>
public readonly record struct PrimaryImpulse(
    double RadialPx, double TangentialPx, double ErrorPx, double RadialPct,
    double TimeMs, double PeakCms, bool Corrected, int Samples, bool Scaled,
    // segmentation-free measures: they answer "did the gain make him fly past the target"
    // without having to decide where one submovement ends and the next begins
    double MaxExtentPct, bool Overshot, double TimeToTargetMs, double OnsetMs,
    // ---- path SHAPE. The endpoint measures above are blind to an arc: a pointer that loops
    // out and comes back lands exactly where a straight flick lands (measured: tangential
    // endpoint error is only ~20-28px on 700px flicks while the user reports drawing circles).
    // Shape survives the click-normalised rescale because the rescale is isotropic.
    double LateralPathPx, double ArcRatio, int Reversals, double PerpPeakPx, double AsymCoef,
    // The raw pieces of the fit. Averaging per-flick slopes is NOT a valid estimator: a nearly
    // axis-aligned flick divides by almost nothing, so its ratio can be anything and one such
    // sample drags the mean. The round pools these and fits once instead.
    double AsymNum, double AsymDen);

public static class OpenLoop
{
    /// <summary>Fraction of peak speed at which the primary submovement may be declared finished.</summary>
    public const double SpeedCut = 0.20;

    /// <summary>Windows (2ms each) the slowdown must last, so one sparse/coalesced window
    /// cannot cut the impulse short. Measured failure: a plain "first dip below 20% of peak"
    /// reported -21% for trials that had no correction at all and ended on the target.</summary>
    public const int HoldWindows = 8;

    /// <summary>Median filter width; delivery gaps make single windows useless.</summary>
    const int Smooth = 3;

    static double[] SmoothSpeed(double[] raw)
    {
        if (raw.Length < Smooth) return (double[])raw.Clone();
        var r = new double[raw.Length];
        int half = Smooth / 2;
        for (int i = 0; i < raw.Length; i++)
        {
            int lo = Math.Max(0, i - half), hi = Math.Min(raw.Length - 1, i + half);
            var win = new double[hi - lo + 1];
            for (int k = 0; k < win.Length; k++) win[k] = raw[lo + k];
            Array.Sort(win);
            r[i] = win[win.Length / 2];
        }
        return r;
    }

    /// <summary>
    /// samples - the recorder windows that cover this flick, in order, starting at the flick origin.
    /// aimX/aimY - unit vector from origin to target; distancePx - |target - origin|.
    /// The reconstructed path is in mouse counts; if it disagrees with the real click displacement
    /// (pointer speed, display scaling) the whole path is scaled to match, so the shape is kept.
    ///
    /// Durations are measured against the FIRST SAMPLE's own timestamp, never against a clock the
    /// caller passes in: the samples come from the raw-input monitor's process-wide stopwatch while
    /// each window belongs to the test window's own stopwatch, and the two differ by however long
    /// the app has been running. Feeding the caller's start time produced "time to target = 421141ms"
    /// in the first real rounds after the fix (measured 2026-10-21 17:55) - a 7-minute app uptime
    /// leaking into a 0.4s flick.
    /// </summary>
    public static PrimaryImpulse? Analyze(IReadOnlyList<MouseSample> samples, double originX, double originY,
                                          double targetX, double targetY, double clickX, double clickY,
                                          double reachRadius = 26)
    {
        if (samples == null || samples.Count < 4) return null;
        double startMs = samples[0].timeMs;     // the only clock these durations can be relative to

        double ax = targetX - originX, ay = targetY - originY;
        double dist = Math.Sqrt(ax * ax + ay * ay);
        if (dist < 1e-6) return null;
        ax /= dist; ay /= dist;

        // rebuild the path from the flick origin
        var px = new double[samples.Count];
        var py = new double[samples.Count];
        var sp = new double[samples.Count];
        double x = 0, y = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            x += samples[i].dx; y += samples[i].dy;
            px[i] = x; py[i] = y;
            sp[i] = samples[i].SpeedWhole;
        }
        if (Math.Abs(px[^1]) < 1e-9 && Math.Abs(py[^1]) < 1e-9) return null;

        // rescale counts -> real pointer displacement. The click IS ground truth, so the whole
        // path is always scaled to end on the real click displacement; only the shape is measured.
        // Earlier this only fired when |path|/real fell outside [0.8, 1.25] - but the path is in
        // raw counts and the baseline curve's true gain spans 1.0~1.97x, so moderate flicks sat
        // inside that band, skipped the rescale, and read 82~92% of their true extent (measured:
        // the round guard fired on all 5 real baseline rounds of 2026-10-21 morning).
        bool scaled = false;
        double recon = Math.Sqrt(px[^1] * px[^1] + py[^1] * py[^1]);
        double real = Math.Sqrt(Math.Pow(clickX - originX, 2) + Math.Pow(clickY - originY, 2));
        if (recon > 1 && real > 1)
        {
            double k = real / recon;
            for (int i = 0; i < px.Length; i++) { px[i] *= k; py[i] *= k; }
            scaled = k is < 0.8 or > 1.25;   // a rescale that is nearly a no-op isn't worth flagging
        }

        int peak = 0;
        var sm = SmoothSpeed(sp);
        for (int i = 1; i < sm.Length; i++) if (sm[i] > sm[peak]) peak = i;
        if (sm[peak] <= 0) return null;

        // end of the primary submovement: the speed must drop below the cut AND stay down.
        // Without this, one coalesced window in the middle of the movement ends the impulse early.
        int end = sm.Length - 1;
        for (int i = peak; i < sm.Length; i++)
        {
            if (sm[i] > SpeedCut * sm[peak]) continue;
            bool holds = true;
            for (int j = i; j < Math.Min(i + HoldWindows, sm.Length); j++)
                if (sm[j] > SpeedCut * sm[peak] * 1.75) { holds = false; break; }
            if (holds) { end = i; break; }
        }

        double radial = (px[end] * ax + py[end] * ay) - dist;
        double tang = -px[end] * ay + py[end] * ax;
        double err = Math.Sqrt(Math.Pow(originX + px[end] - targetX, 2) + Math.Pow(originY + py[end] - targetY, 2));

        // along-axis progress over time: the segmentation-free view
        var along = new double[px.Length];
        for (int i = 0; i < px.Length; i++) along[i] = px[i] * ax + py[i] * ay;
        double maxAlong = along.Max();
        int firstIn = -1;
        for (int i = 0; i < px.Length; i++)
        {
            double dx2 = originX + px[i] - targetX, dy2 = originY + py[i] - targetY;
            if (Math.Sqrt(dx2 * dx2 + dy2 * dy2) <= reachRadius) { firstIn = i; break; }
        }
        int onset = 0;
        for (int i = 0; i < sp.Length; i++) if (sp[i] > 0.1 * sm[peak]) { onset = i; break; }

        // consistent definition: whatever happened after the first movement stopped counts as a
        // second submovement if it moved the pointer meaningfully - forwards (a creep that fixes
        // an undershoot) or backwards (a pull-back after flying past)
        double afterMove = Math.Sqrt(Math.Pow(px[^1] - px[end], 2) + Math.Pow(py[^1] - py[end], 2));
        bool corrected = afterMove > Math.Max(dist * 0.08, reachRadius);

        // ---- shape of the path, in the same click-normalised px as everything above ----
        // perp[i] is the signed distance from the aim line; the screen deviation vector is
        // perp * (-ay, ax).
        var perp = new double[px.Length];
        for (int i = 0; i < px.Length; i++) perp[i] = -px[i] * ay + py[i] * ax;

        double lateralPath = 0;
        for (int i = 1; i < perp.Length; i++) lateralPath += Math.Abs(perp[i] - perp[i - 1]);

        // Direction changes have to be counted as SWINGS, not per window: one 2ms window of a
        // smooth 35px arc moves the offset by half a pixel, so any per-step threshold either
        // kills the real turn or counts jitter as one. Zigzag: a leg ends when the series pulls
        // minLeg away from its running extreme.
        int reversals = 0;
        double minLeg = Math.Max(6.0, dist * 0.015);
        double extreme = perp[0];
        int trend = 0;                                   // 0 = no leg established yet
        for (int i = 1; i < perp.Length; i++)
        {
            double v = perp[i];
            if (trend == 0)
            {
                if (Math.Abs(v - extreme) < minLeg) continue;
                trend = v > extreme ? 1 : -1;
                extreme = v;
                continue;
            }
            if (trend > 0)
            {
                if (v > extreme) extreme = v;
                else if (extreme - v >= minLeg) { reversals++; trend = -1; extreme = v; }
            }
            else
            {
                if (v < extreme) extreme = v;
                else if (v - extreme >= minLeg) { reversals++; trend = 1; extreme = v; }
            }
        }

        double perpPeak = 0;
        for (int i = 0; i <= end; i++)
            if (Math.Abs(perp[i]) > Math.Abs(perpPeak)) perpPeak = perp[i];

        // The axis-asymmetry estimator. If one axis is over-driven relative to the other by a
        // relative amount e, the pointer is displaced by e*(0, y_travel) for a Y surplus, whose
        // component off the aim line works out to perp ~= e * along * ax * ay. So the least
        // squares slope of perp on (along * ax * ay) IS e: positive = Y gains more than X,
        // negative = X gains more than Y, and pure motor noise has no such alignment and
        // averages towards zero. Diagonal aims carry the signal; straight horizontal or vertical
        // flicks have ax*ay ~ 0 and contribute nothing (their weight is (ax*ay)^2).
        double axay = ax * ay, num = 0, den = 0;
        for (int i = 0; i <= end; i++)
        {
            double basis = along[i] * axay;
            num += perp[i] * basis;
            den += basis * basis;
        }
        double asym = den > 1e-9 ? num / den : double.NaN;

        return new PrimaryImpulse(radial, tang, err, radial / dist * 100.0,
                                  samples[end].timeMs - startMs, sp.Max(), corrected, samples.Count, scaled,
                                  maxAlong / dist * 100.0, maxAlong > dist + reachRadius,
                                  firstIn >= 0 ? samples[firstIn].timeMs - startMs : double.NaN,
                                  samples[onset].timeMs - startMs,
                                  lateralPath, lateralPath / dist, reversals, perpPeak, asym, num, den);
    }
}
