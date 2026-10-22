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
    // path shape: curvature and the axis-asymmetry coefficient behind it
    public double ArcRatioMedian, LateralPathMedian, PerpPeakMedian, ReversalsMean;
    public int HookCount;                       // flicks that turned sideways at least once
    public double AsymMean, AsymSem; public int AsymN;
    public double PeakMedianWhenMeasured;

    /// <summary>One data-quality remark in both languages: the log file carries the English
    /// text (it is the assistant's interface), the UI shows the Chinese one. Both are produced
    /// together so neither can drift from the other.</summary>
    public sealed record QualityNote(string En, string Zh, bool Warn);
    public List<QualityNote> Notes = new();
    /// <summary>English-only view, exactly what the log file and older tests expect.</summary>
    public List<string> Quality => Notes.Select(n => n.En).ToList();

    void Note(string en, string zh, bool warn = false) => Notes.Add(new QualityNote(en, zh, warn));

    public static RoundStats Compute(IReadOnlyList<Trial> t, double radiusPx, double targetDistancePx, double radialTolPct = 5)
    {
        var s = new RoundStats();
        if (t.Count == 0) { s.Note("no trials recorded", "没有记录到任何试次"); return s; }

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
                    s.Note($"IMPLIANT: {hitPrim.Count} flicks ended on the ball yet their reconstructed path " +
                                  $"only reached {m:0}% of the distance - the reconstruction is losing pointer travel, " +
                                  "so max_extent/prim numbers this round are lower bounds, not measurements",
                           $"命中球的甩动里，重建路径只到点击距离的 {m:0}% —— 重建丢了行程，" +
                           "这轮的 max_extent/prim 数字只能当下限看，不能当测量值", warn: true);
                s.HitExtentMedian = m;
            }
            var ttt = prim.Where(x => !double.IsNaN(x.TimeToTargetMs)).Select(x => x.TimeToTargetMs).ToList();
            s.TimeToTargetMedian = Median(ttt);
            s.OnsetMedianMs = Median(prim.Select(x => x.OnsetMs).ToList());

            // ---- how much of the flick was an arc rather than a line, and which axis is to blame ----
            s.ArcRatioMedian = Median(prim.Select(x => x.ArcRatio).ToList());
            s.LateralPathMedian = Median(prim.Select(x => x.LateralPathPx).ToList());
            s.PerpPeakMedian = Median(prim.Select(x => x.PerpPeakPx).ToList());
            s.ReversalsMean = Mean(prim.Select(x => (double)x.PathReversals).ToList());
            s.HookCount = prim.Count(x => x.PathReversals >= 1);
            // only diagonal aims can carry the axis signal (its weight is (ax*ay)^2), and the
            // coefficient is meaningless when that product is near zero.
            // Pool the fit across diagonal flicks and fit ONCE. Averaging the per-flick slopes is
            // not an estimator: a near-axis flick divides by almost nothing, so its ratio is
            // arbitrary and one such sample can move the round's answer by more than the effect
            // being looked for (measured: same config, per-round e from +0.11 to -0.37).
            bool Diagonal(Trial x) => x.AsymDen > 0 && !double.IsNaN(x.AsymCoef) &&
                                      Math.Abs(Math.Cos(x.AngleRad) * Math.Sin(x.AngleRad)) >= 0.15;
            double aNum = 0, aDen = 0; int nFit = 0;
            foreach (var x in prim)
                if (Diagonal(x)) { aNum += x.AsymNum; aDen += x.AsymDen; nFit++; }
            s.AsymN = nFit;
            if (nFit >= 8 && aDen > 0)
            {
                s.AsymMean = aNum / aDen;
                // Var(per-flick slope) = sigma^2 / den_i, so the per-flick scatter weighted by its
                // own den recovers sigma, and SE(pooled slope) = sigma / sqrt(sum of den).
                double ss = 0;
                foreach (var x in prim)
                    if (Diagonal(x)) { double dv = x.AsymCoef - s.AsymMean; ss += dv * dv * x.AsymDen; }
                s.AsymSem = Math.Sqrt(ss / (nFit - 1) / aDen);
            }

            // a "duration" longer than the round itself is an absolute timestamp that escaped a
            // clock mismatch, not a measurement - refuse to publish it in the log the AI reads
            if (s.SessionMs > 0)
            {
                if (s.TimeToTargetMedian > s.SessionMs) s.TimeToTargetMedian = double.NaN;
                if (s.OnsetMedianMs > s.SessionMs) s.OnsetMedianMs = double.NaN;
            }
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
            s.Note($"{s.RetryCount} clicks were immediate retries of the ball just missed " +
                          "(<150px, same target): the distance buckets exclude them",
                   $"{s.RetryCount} 次是打偏后对同一个球的立即补枪（<150px）：距离分桶里已剔除这些");
        if (s.EmptyClickCount > 0)
            s.Note($"{s.EmptyClickCount}/{s.Count} clicks landed on no ball and were attributed to the nearest " +
                          $"one; treat those rows as misses, not as hitting the wrong ball",
                   $"{s.EmptyClickCount}/{s.Count} 次点击没落在任何球上，按最近的球计分：这些算脱靶，不是打错球");
        if (s.PrimaryCount == 0)
            s.Note("no flick had enough input windows to reconstruct its initial impulse: the open-loop " +
                          "columns are empty for this round",
                   "没有一发甩动有足够的输入窗口来重建初始冲量：这轮的开环列是空的");
        else if (s.PrimaryCount < s.Count / 2)
            s.Note($"only {s.PrimaryCount}/{s.Count} flicks could be reconstructed for the open-loop measure",
                   $"只有 {s.PrimaryCount}/{s.Count} 发甩动够数据重建开环冲量");
        if (Math.Abs(s.PrimaryRadialMedian) > 5 && s.PrimaryCount >= 12)
            s.Note($"the initial impulse itself lands {(s.PrimaryRadialMedian > 0 ? "past" : "short of")} the " +
                          $"target ({s.PrimaryRadialMedian:+0.0;-0.0}% median, {s.PrimaryOver} over / {s.PrimaryUnder} under): " +
                          "that is a gain effect, not correction noise",
                   $"第一下冲量本身{(s.PrimaryRadialMedian > 0 ? "飞过头" : "没够到")}目标（中位 {s.PrimaryRadialMedian:+0.0;-0.0}%，" +
                   $"{s.PrimaryOver} 过 / {s.PrimaryUnder} 不及）：这是增益效应，不是修正噪声");
        if (Math.Abs(s.RadialMean) < s.RadialSd && s.OvershootCount >= 2 && s.UndershootCount >= 2)
            s.Note($"signed radial mean {s.RadialMean:+0.0;-0.0}% averages {s.OvershootCount:0} overshoots with " +
                          $"{s.UndershootCount:0} undershoots (sd {s.RadialSd:0.0}%); it is NOT evidence of a bias",
                   $"径向带符号均值 {s.RadialMean:+0.0;-0.0}% 是 {s.OvershootCount:0} 次过头和 {s.UndershootCount:0} 次不及互相抵消" +
                   $"（sd {s.RadialSd:0.0}%），不能当存在偏移的证据");
        if (s.Count < 12)
            s.Note($"only {s.Count} clicks recorded ({s.BallsCleared} balls cleared): below ~12 a single parameter change is not supportable",
                   $"只记录了 {s.Count} 次点击（点掉 {s.BallsCleared} 个球）：少于 ~12 次，撑不起一个参数结论");
        if (s.MeasuredCount < s.Count)
            s.Note($"post-driver speed present for {s.MeasuredCount}/{s.Count} trials; peak 0 means no packets " +
                          "landed in that window, not a still hand",
                   $"{s.MeasuredCount}/{s.Count} 次点击采到了驱动后速度；峰值为 0 是那个窗口没收到包，不代表手没动");
        if (s.TruncatedCount > 0)
            s.Note($"{s.TruncatedCount} trial windows were trimmed out of the ring buffer",
                   $"{s.TruncatedCount} 个试次的速度窗口被环形缓冲挤掉了");
        if (s.Buckets.Count < 2)
            s.Note("this round only produced one distance bucket: it cannot say anything about " +
                          "small flicks vs large turns separately",
                   "这轮只有一个距离桶：分不出小幅定位和大幅转身各自的水平");
        if (s.DistanceMax - s.DistanceMin < 1)
            s.Note("all targets shared one distance: this round cannot separate small from large flicks",
                   "这轮所有目标距离都一样：分不出小幅和大幅甩动");
        if (radiusPx > 0 && s.TangMeanAbs > radiusPx * 1.5)
            s.Note($"mean |tangential| {s.TangMeanAbs:0}px is far larger than the {radiusPx:0}px target: " +
                          "the pointer rarely arrived where it was aimed, so amplitude conclusions are unreliable",
                   $"平均 |切向| {s.TangMeanAbs:0}px 远超 {radiusPx:0}px 的球半径：指针经常没到瞄准的地方，幅度类结论不可靠");
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
