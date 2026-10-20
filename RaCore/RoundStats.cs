namespace RaCore;

/// <summary>
/// Honest summary statistics for one round. Every number here describes the sample; none of it
/// is a diagnosis. Signed means are reported next to their spread because an equal number of
/// overshoots and undershoots averages to "no problem", and the recorded speeds are
/// post-driver output, so they cannot be compared to the profile's input thresholds.
/// </summary>
public sealed class RoundStats
{
    public int Count;
    public int Clicks;
    public int BallsCleared;
    public double HitRate;                                     // % of clicks that cleared a ball
    public double KillsPerMinute;
    public double SessionMs;
    public double RadialMean, RadialMedian, RadialSd, RadialMae;
    public double OvershootCount, UndershootCount, OnTargetCount;
    public double TangMeanAbs, TangMedianAbs, TangSd, SignedTangMean;
    public double ErrorMean, ErrorMedian;
    public double TimeMedian, TimeP90;
    public double DistanceMin, DistanceMax, DistanceMedian;
    public int DistanceClampedCount;      // unused: kept so older logs still parse
    public int MeasuredCount;             // trials where the recorder saw packets
    public int TruncatedCount;
    public int EmptyClickCount;           // clicks that landed on no ball, attributed to the nearest one
    public int RetryCount;                // immediate re-clicks on the ball just missed
    /// <summary>Flick-length buckets: the whole point of continuous mode is that one round
    /// covers small corrections AND big turns, so the small-distance bucket is where
    /// "fine aiming must not accelerate" shows up.</summary>
    public List<(string label, int n, double hitPct, double radialMedian, double tangMedian, double timeMedian,
                double primaryRadialMedian, int primaryN)> Buckets = new();

    /// <summary>Open-loop view: the endpoint of the FIRST movement. Unlike the click-time error
    /// this cannot be corrected away, so it is the number that responds to a gain change.</summary>
    public int PrimaryCount;
    public int CorrectedCount;
    public double PrimaryRadialMedian, PrimaryRadialMean, PrimaryRadialSd, PrimaryMae;
    public double PrimaryTangMedian;
    public int PrimaryOver, PrimaryUnder, PrimaryOn;
    public double PrimaryPeakMedian;
    /// <summary>Self-check on the detector: among trials with NO second surge, the initial impulse
    /// ended where the click ended, so their primary radial must be ~0. If it is not, the
    /// reconstruction is cutting the movement short and every prim_* number is untrustworthy.</summary>
    public double NoFixPrimMedian;
    public int NoFixCount;
    /// <summary>Segmentation-free headline numbers: how far the pointer actually travels along the
    /// aim line before the click, and how often it passes the target. These do not depend on
    /// where one submovement is declared to end, so they survive the coalescing noise.</summary>
    public double MaxExtentMedian;
    public double HitExtentMedian;                 // guard: hits must have reached ~100%
    public int OvershootCount2;
    public double TimeToTargetMedian, OnsetMedianMs;
    public double PeakMedianWhenMeasured;
    public List<string> Quality = new();

    public static RoundStats Compute(IReadOnlyList<Trial> t, double radiusPx, double targetDistancePx, double radialTolPct = 5)
    {
        var s = new RoundStats();
        if (t.Count == 0) { s.Quality.Add("no trials recorded"); return s; }

        s.Count = t.Count;
        s.Clicks = t.Count;
        s.BallsCleared = t.Count(x => x.Hit);
        s.HitRate = t.Count(x => x.Hit) * 100.0 / t.Count;
        s.SessionMs = t.Max(x => x.EndMs) - t.Min(x => x.StartMs);
        s.KillsPerMinute = s.SessionMs > 0 ? s.BallsCleared * 60_000.0 / s.SessionMs : 0;

        var rad = t.Select(x => x.RadialPct).ToList();
        var tan = t.Select(x => Math.Abs(x.TangentialPx)).ToList();
        var tanSigned = t.Select(x => x.TangentialPx).ToList();
        var err = t.Select(x => x.ErrorPx).ToList();
        var times = t.Select(x => x.EndMs - x.StartMs).ToList();
        var dist = t.Select(x => x.DistancePx).ToList();

        s.RadialMean = Mean(rad);
        s.RadialMedian = Median(rad);
        s.RadialSd = StdDev(rad);
        s.RadialMae = Mean(rad.Select(Math.Abs).ToList());
        s.OvershootCount = t.Count(x => x.RadialPct > radialTolPct);
        s.UndershootCount = t.Count(x => x.RadialPct < -radialTolPct);
        s.OnTargetCount = t.Count(x => Math.Abs(x.RadialPct) <= radialTolPct);
        s.TangMeanAbs = Mean(tan);
        s.TangMedianAbs = Median(tan);
        s.TangSd = StdDev(tan);
        s.SignedTangMean = Mean(tanSigned);
        s.ErrorMean = Mean(err);
        s.ErrorMedian = Median(err);
        s.TimeMedian = Median(times);
        s.TimeP90 = Pct(times, 0.90);
        s.DistanceMin = dist.Min();
        s.DistanceMax = dist.Max();
        s.DistanceMedian = Median(dist);
        // in continuous mode a click often lands on a DIFFERENT, closer ball than the
        // placement distance, so "shorter than requested" is normal, not a clamp
        s.DistanceClampedCount = 0;

        // A click that missed leaves the pointer next to the same ball, so the very next
        // "flick" is a short retry on that same ball. Counting those as small-distance aiming
        // inflates the <100px bucket and makes fine-aim accuracy look better than it is.
        var retry = new bool[t.Count];
        for (int i = 1; i < t.Count; i++)
            retry[i] = !t[i - 1].Hit && t[i].DistancePx < 150 && t[i].TargetId == t[i - 1].TargetId;
        s.RetryCount = t.Where((_, i) => retry[i]).Count();
        var aiming = t.Where((_, i) => !retry[i]).ToList();
        if (aiming.Count == 0) aiming = t.ToList();

        foreach (var (label, lo, hi) in new[] { ("<100px", 0.0, 100.0), ("100-250px", 100.0, 250.0),
                                                ("250-500px", 250.0, 500.0), (">=500px", 500.0, double.MaxValue) })
        {
            var g = aiming.Where(x => x.DistancePx >= lo && x.DistancePx < hi).ToList();
            if (g.Count == 0) continue;
            var gp = g.Where(x => x.HasPrimary).ToList();
            s.Buckets.Add((label, g.Count, g.Count(x => x.Hit) * 100.0 / g.Count,
                           Median(g.Select(x => x.RadialPct).ToList()),
                           Median(g.Select(x => Math.Abs(x.TangentialPx)).ToList()),
                           Median(g.Select(x => x.EndMs - x.StartMs).ToList()),
                           gp.Count > 0 ? Median(gp.Select(x => x.PrimaryRadialPct).ToList()) : 0, gp.Count));
        }

        var prim = t.Where(x => x.HasPrimary).ToList();
        s.PrimaryCount = prim.Count;
        s.CorrectedCount = prim.Count(x => x.Corrected);
        if (prim.Count > 0)
        {
            var pr = prim.Select(x => x.PrimaryRadialPct).ToList();
            s.PrimaryRadialMedian = Median(pr);
            s.PrimaryRadialMean = Mean(pr);
            s.PrimaryRadialSd = StdDev(pr);
            s.PrimaryMae = Mean(pr.Select(Math.Abs).ToList());
            s.PrimaryTangMedian = Median(prim.Select(x => Math.Abs(x.PrimaryTangentialPx)).ToList());
            s.PrimaryPeakMedian = Median(prim.Select(x => x.PrimaryPeakCms).ToList());
            s.PrimaryOver = prim.Count(x => x.PrimaryRadialPct > 5);
            s.PrimaryUnder = prim.Count(x => x.PrimaryRadialPct < -5);
            s.PrimaryOn = prim.Count(x => Math.Abs(x.PrimaryRadialPct) <= 5);

            var noFix = prim.Where(x => !x.Corrected).ToList();
            s.NoFixCount = noFix.Count;
            s.MaxExtentMedian = Median(prim.Select(x => x.MaxExtentPct).ToList());
            s.OvershootCount2 = prim.Count(x => x.Overshot);
            // independent guard on the reconstruction: a click that LANDED on the ball means the
            // pointer reached it, so the furthest along-axis point cannot be much short of 100%.
            // (The old "no correction" check went tautological once 'corrected' was defined from
            // the same series - this one is not derived from the segmentation.)
            var hitPrim = prim.Where(x => x.Hit).ToList();
            if (hitPrim.Count >= 8)
            {
                double m = Median(hitPrim.Select(x => x.MaxExtentPct).ToList());
                if (m < 95)
                    s.Quality.Add($"IMPLIANT: {hitPrim.Count} flicks ended on the ball yet their reconstructed path " +
                                  $"only reached {m:0}% of the distance - the reconstruction is losing pointer travel, " +
                                  "so max_extent/prim numbers this round are lower bounds, not measurements");
                s.HitExtentMedian = m;
            }
            var ttt = prim.Where(x => !double.IsNaN(x.TimeToTargetMs)).Select(x => x.TimeToTargetMs).ToList();
            s.TimeToTargetMedian = Median(ttt);
            s.OnsetMedianMs = Median(prim.Select(x => x.OnsetMs).ToList());
            if (noFix.Count >= 8)
            {
                s.NoFixPrimMedian = Median(noFix.Select(x => x.PrimaryRadialPct).ToList());
            }
        }

        var measured = t.Where(x => x.SampleCount > 0).ToList();
        s.MeasuredCount = measured.Count;
        s.TruncatedCount = t.Count(x => x.TelemetryTruncated);
        s.EmptyClickCount = t.Count(x => !x.BallUnderCursor);
        if (measured.Count > 0) s.PeakMedianWhenMeasured = Median(measured.Select(x => x.PeakSpeed).ToList());

        // ---- what the reader must not assume ----
        if (s.RetryCount > 0)
            s.Quality.Add($"{s.RetryCount} clicks were immediate retries of the ball just missed " +
                          "(<150px, same target): the distance buckets exclude them");
        if (s.EmptyClickCount > 0)
            s.Quality.Add($"{s.EmptyClickCount}/{s.Count} clicks landed on no ball and were attributed to the nearest " +
                          $"one; treat those rows as misses, not as hitting the wrong ball");
        if (s.PrimaryCount == 0)
            s.Quality.Add("no flick had enough input windows to reconstruct its initial impulse: the open-loop " +
                          "columns are empty for this round");
        else if (s.PrimaryCount < s.Count / 2)
            s.Quality.Add($"only {s.PrimaryCount}/{s.Count} flicks could be reconstructed for the open-loop measure");
        if (Math.Abs(s.PrimaryRadialMedian) > 5 && s.PrimaryCount >= 12)
            s.Quality.Add($"the initial impulse itself lands {(s.PrimaryRadialMedian > 0 ? "past" : "short of")} the " +
                          $"target ({s.PrimaryRadialMedian:+0.0;-0.0}% median, {s.PrimaryOver} over / {s.PrimaryUnder} under): " +
                          "that is a gain effect, not correction noise");
        if (Math.Abs(s.RadialMean) < s.RadialSd && s.OvershootCount >= 2 && s.UndershootCount >= 2)
            s.Quality.Add($"signed radial mean {s.RadialMean:+0.0;-0.0}% averages {s.OvershootCount:0} overshoots with " +
                          $"{s.UndershootCount:0} undershoots (sd {s.RadialSd:0.0}%); it is NOT evidence of a bias");
        if (s.Count < 12) s.Quality.Add($"only {s.Count} clicks recorded ({s.BallsCleared} balls cleared): below ~12 a single parameter change is not supportable");
        if (s.MeasuredCount < s.Count)
            s.Quality.Add($"post-driver speed present for {s.MeasuredCount}/{s.Count} trials; peak 0 means no packets " +
                          "landed in that window, not a still hand");
        if (s.TruncatedCount > 0) s.Quality.Add($"{s.TruncatedCount} trial windows were trimmed out of the ring buffer");
        if (s.Buckets.Count < 2)
            s.Quality.Add("this round only produced one distance bucket: it cannot say anything about " +
                          "small flicks vs large turns separately");
        if (s.DistanceMax - s.DistanceMin < 1)
            s.Quality.Add("all targets shared one distance: this round cannot separate small from large flicks");
        if (radiusPx > 0 && s.TangMeanAbs > radiusPx * 1.5)
            s.Quality.Add($"mean |tangential| {s.TangMeanAbs:0}px is far larger than the {radiusPx:0}px target: " +
                          "the pointer rarely arrived where it was aimed, so amplitude conclusions are unreliable");
        return s;
    }

    static double Mean(List<double> v) => v.Count == 0 ? 0 : v.Average();
    static double Median(List<double> v) => Pct(v, 0.5);
    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var a = v.OrderBy(x => x).ToList();
        double idx = p * (a.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return a[lo] + (a[hi] - a[lo]) * (idx - lo);
    }
    static double StdDev(List<double> v)
    {
        if (v.Count < 2) return 0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }
}
