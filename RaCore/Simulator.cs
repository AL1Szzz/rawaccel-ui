namespace RaCore;

/// <summary>
/// Synthetic flicks with KNOWN ground truth, so the measurement pipeline can be tested without
/// a human at the mouse. The velocity shape is a minimum-jerk profile (what human pointing
/// movements look like), sampled into the same 2ms windows the real recorder emits, and it can
/// include coalesced/empty windows because that is exactly what broke the first open-loop code.
/// </summary>
public static class Simulator
{
    public readonly record struct Flick(
        List<MouseSample> Path, double StartMs, double PeakCms, double TrueMaxExtentPx,
        double TrueReachMs, double TruePrimaryExtentPx, bool TrueOvershot, bool TrueCorrected);

    /// <summary>
    /// distancePx      - origin to target centre
    /// primaryFraction - how far along the aim line the FIRST movement gets (1.0 = straight in,
    ///                   1.15 = flies past, 0.76 = stops short and then creeps)
    /// peakCms         - peak output speed in counts/ms
    /// emptyWindowRate - share of windows that report nothing because their packets were merged
    ///                   into the next window (0..0.5). The travel they owed is CARRIED, because
    ///                   that is what a coalesced delivery does - dropping it would invent a
    ///                   shortfall and then "measure" that shortfall back out of the analysis.
    /// </summary>
    public static Flick Flick1(double distancePx, double peakCms, double primaryFraction,
                               int seed, double windowMs = 2.0, double emptyWindowRate = 0.0)
    {
        var rng = new Random(seed);
        double primaryTarget = Math.Clamp(primaryFraction, 0.05, 2.0);
        // minimum-jerk: v(u) = A*30*u^2*(1-u)^2 integrates to A over the movement, and its peak
        // value is 1.875*A. So to hit a desired peak speed, A = peak/1.875, and the duration
        // follows from distance = A * duration.
        double amp = Math.Max(peakCms, 0.1) / 1.875;
        double totalMs = distancePx * Math.Max(primaryTarget, 1.0) / amp;

        var path = new List<MouseSample>();
        double t = 1000, peakSeen = 0;
        double exactPos = 0;      // float position along the aim line
        int emittedPos = 0;       // what the integer windows add up to
        double pending = 0;       // travel owed by a window that reported nothing

        void AddWindow(double wantPx, double dur, bool empty = false)
        {
            if (empty)
            {
                // a coalesced window: nothing reported here, its distance joins the next one
                path.Add(new MouseSample(0, 0, dur, t + dur, path.Count + 1));
                pending += wantPx;
                t += dur;
                return;
            }
            exactPos += wantPx + pending;
            pending = 0;
            int target = (int)Math.Round(exactPos);
            int delta = target - emittedPos;
            emittedPos = target;
            var s = new MouseSample(delta, 0, dur, t + dur, path.Count + 1);
            path.Add(s);
            peakSeen = Math.Max(peakSeen, Math.Abs(delta) / dur);
            t += dur;
        }

        // a real second submovement is separated from the first by a short still phase; without a
        // pause, velocity alone cannot tell "creeping in" from "still the same movement"
        void Pause(int windows)
        {
            for (int i = 0; i < windows; i++) AddWindow(0, windowMs);
        }

        // phase 1: the primary impulse, integrated in 2ms slices until the planned extent
        double travelledTarget = distancePx * primaryTarget;
        double u = 0;
        while (emittedPos < travelledTarget - 0.5 && u < 1)
        {
            double dur = Math.Min(windowMs, totalMs * (1 - u));
            if (dur <= 0) break;
            double v = amp * 30 * Math.Pow(u, 2) * Math.Pow(1 - u, 2);
            bool empty = emptyWindowRate > 0 && rng.NextDouble() < emptyWindowRate && path.Count > 0;
            AddWindow(v * dur, dur, empty);
            u += dur / totalMs;
        }
        // land the first movement exactly on the planned extent, owed travel and rounding included
        if (Math.Abs(travelledTarget - exactPos - pending) > 0.01)
            AddWindow(travelledTarget - exactPos - pending, windowMs);

        double truePrimaryExtent = emittedPos;
        bool corrected = Math.Abs(emittedPos - distancePx) > Math.Max(distancePx * 0.08, 26);

        // phase 2: a slower second submovement that lands the pointer on the target
        if (Math.Abs(emittedPos - distancePx) > 0.5)
        {
            double remaining = distancePx - emittedPos;
            double amp2 = Math.Max(amp * 0.25, 0.02);
            double dur2 = Math.Max(windowMs * 6, Math.Abs(remaining) / amp2);
            int n = Math.Max(3, (int)(dur2 / windowMs));
            Pause(6);
            double u2 = 0;
            for (int i = 0; i < n; i++)
            {
                double v = amp2 * 30 * Math.Pow(u2, 2) * Math.Pow(1 - u2, 2) * Math.Sign(remaining);
                bool empty = emptyWindowRate > 0 && rng.NextDouble() < emptyWindowRate;
                AddWindow(v * windowMs, windowMs, empty);
                u2 = Math.Min(0.98, u2 + windowMs / dur2);
            }
            // the click is recorded on the target, so the series has to end on the target too
            AddWindow(distancePx - exactPos - pending, windowMs);
        }

        // injected truth, taken from the plan rather than read back off the emitted series
        double trueMax = Math.Max(travelledTarget, distancePx);
        double reachMs = double.NaN, run = 0;
        foreach (var s in path)
        {
            run += s.dx;
            if (double.IsNaN(reachMs) && Math.Abs(run - distancePx) <= 26) reachMs = s.timeMs - 1000;
        }
        return new Flick(path, 1000, peakSeen, trueMax, reachMs, truePrimaryExtent,
                         trueMax > distancePx + 26, corrected);
    }

    /// <summary>
    /// A whole round of synthetic trials at a fixed distance, written through the real
    /// TestLog path so the log format itself is exercised without a human.
    /// </summary>
    public static List<Trial> Round(int trials, double distancePx, double peakCms, double primaryFraction,
                                    int seed, double originX = 960, double originY = 720)
    {
        var list = new List<Trial>();
        var rng = new Random(seed);
        double ox = originX, oy = originY;
        for (int i = 0; i < trials; i++)
        {
            double angle = rng.NextDouble() * 2 * Math.PI;
            double tx = ox + Math.Cos(angle) * distancePx, ty = oy + Math.Sin(angle) * distancePx;
            if (tx < 60 || tx > 3380 || ty < 140 || ty > 1380) { tx = ox + distancePx; ty = oy; }

            // vary the primary extent around the requested value so the metrics have a spread
            double f = primaryFraction * (0.85 + rng.NextDouble() * 0.3);
            var f1 = Flick1(distancePx, peakCms * (0.85 + rng.NextDouble() * 0.3), f, seed * 31 + i,
                             emptyWindowRate: 0.18);
            double ax = (tx - ox) / distancePx, ay = (ty - oy) / distancePx;
            double clickAlong = distancePx;                      // the correction lands on the target
            var (radial, tang, err, pct) = TargetLayout.Score(ox + ax * clickAlong, oy + ay * clickAlong, ox, oy, tx, ty);

            var prim = OpenLoop.Analyze(f1.Path, ox, oy, tx, ty, ox + ax * clickAlong, oy + ay * clickAlong, 26);
            list.Add(new Trial
            {
                DistancePx = distancePx,
                AngleRad = Math.Atan2(ay, ax),
                StartMs = f1.StartMs, EndMs = f1.Path[^1].timeMs,
                OriginX = ox, OriginY = oy, TargetX = tx, TargetY = ty,
                ClickX = ox + ax * clickAlong, ClickY = oy + ay * clickAlong,
                RadialPx = radial, TangentialPx = tang, RadialPct = pct, ErrorPx = err,
                Hit = err <= 26, PeakSpeed = f1.PeakCms, SampleCount = f1.Path.Count,
                TargetId = 1, VisibleTargets = 1, BallUnderCursor = err <= 26,
                HasPrimary = prim != null,
                PrimaryRadialPx = prim?.RadialPx ?? 0, PrimaryTangentialPx = prim?.TangentialPx ?? 0,
                PrimaryRadialPct = prim?.RadialPct ?? 0, PrimaryErrorPx = prim?.ErrorPx ?? 0,
                PrimaryTimeMs = prim?.TimeMs ?? 0, PrimaryPeakCms = prim?.PeakCms ?? 0,
                Corrected = prim?.Corrected ?? false, PrimaryScaled = prim?.Scaled ?? false,
                PrimaryScaleK = prim?.ScaleK ?? double.NaN,
                MaxExtentPct = prim?.MaxExtentPct ?? 0, Overshot = prim?.Overshot ?? false,
                TimeToTargetMs = prim?.TimeToTargetMs ?? double.NaN, OnsetMs = prim?.OnsetMs ?? 0,
                LateralPathPx = prim?.LateralPathPx ?? 0, ArcRatio = prim?.ArcRatio ?? 0,
                PathReversals = prim?.Reversals ?? 0, PerpPeakPx = prim?.PerpPeakPx ?? 0,
                AsymCoef = prim?.AsymCoef ?? double.NaN,
                AsymNum = prim?.AsymNum ?? 0, AsymDen = prim?.AsymDen ?? 0,
            });
            ox = tx; oy = ty;
        }
        return list;
    }
}
