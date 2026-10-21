using System.Globalization;
using System.Text;
using System.Text.Json;
using RaCore.Model;

namespace RaCore;

/// <summary>
/// Everything that must be frozen for one round. The caller hands this to Write() at the
/// END of the round, and every field inside it was captured at the START (config, device,
/// setup), so the log cannot describe a curve that was never in effect.
/// </summary>
public sealed class RoundData
{
    public string RoundId = "";
    public string When = "";
    public string AppVersion = "1.0.0";
    public string DriverVersion = "";

    public string ProfileName = "";
    public string ConfigSha = "";
    public string ConfigSource = "";      // driver | settings.json | draft
    public string ProfileJson = "";       // whole document, exactly as it was at round start
    public ra_profile Profile;            // struct of the selected profile at round start

    public string DeviceId = "";
    public string DeviceName = "";

    public double Sensitivity, MouseDpi, PollRate;
    public double TargetDistancePx, RadiusPx;
    public int TargetCount;
    public int Seed;
    public string Mode = "";              // "点掉 30 球" or "60 秒限时"
    public string Score = "";             // what the round achieved, per the mode
    public double SessionMs;
    public string Direction = "";
    public string Resolution = "";

    public List<Trial> Trials = new();
    public (double p50, double p75, double p95, double peak) OutputSpeed;
    public string TelemetryNote = "";
    public string AppliedPatch = "";      // set only when this round was run with a patch already applied
}

public sealed class RoundSummary
{
    public string File = "";
    public string When = "";
    public string ConfigSha = "";
    public string Mode = "";
    public string Score = "";
    public double DistancePx, RadiusPx;
    public int TargetCount, Trials, Clicks, BallsCleared;
    public double SessionMs, KillsPerMinute;
    public string Resolution = "";
    public string Direction = "";
    public double HitRate, RadialMean, RadialMedian, RadialSd, RadialMae;
    public double TangMedian;
    public double TimeMedian;
    public List<string> Quality = new();

    /// <summary>Segmentation-free open-loop medians; 0 when the file predates them.</summary>
    public double MaxExtentMedian, HitExtentMedian;
    public int Overshoot2;
    /// <summary>Path curvature and the axis-asymmetry fit; see the meaning block of the log.</summary>
    public double ArcRatio, ReversalsMean, AsymMean, AsymSem; public int HookCount, AsymN;
    public double TimeToTargetMedian;
    /// <summary>Same remarks as Quality, in the language the UI shows. Empty in pre-v3.1 files.</summary>
    public List<(string Text, bool Warn)> NotesZh = new();

    /// <summary>False for rounds written by builds whose open-loop reconstruction was broken
    /// (the eras are recognisable by their guard texts, new builds mark it with the guard itself).
    /// The tuning protocol must not read max_extent/prim from such rounds.</summary>
    public bool OpenLoopUsable =>
        !Quality.Any(q => q.Contains("no second surge") || q.Contains("losing pointer travel"));

    /// <summary>One readable line for the round picker. Uses the file name (round START, to the
    /// second) because two rounds finished in the same minute and looked identical.</summary>
    public string Label()
    {
        var name = Path.GetFileNameWithoutExtension(File);       // round-2026-10-20_20-44-10-631
        string stamp = name.Length >= 25 ? name[(^12)..(^4)].Replace('-', ':') : name;
        var bits = new List<string> { stamp, Mode };
        if (Clicks > 0) bits.Add($"中 {HitRate:0}%（{BallsCleared}/{Clicks}）");
        if (KillsPerMinute > 0) bits.Add($"{KillsPerMinute:0.0} 球/分");
        bits.Add("cfg " + (ConfigSha.Length > 6 ? ConfigSha[..6] : ConfigSha));
        return string.Join(" · ", bits.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}

public static class TestLog
{
    public const int FormatVersion = 3;

    public static string LogDir => AppData.LogDir;
    public static string AuditDir => AppData.AuditDir;

    /// <summary>Redirect index.csv (used by the offline tests so they never pollute the real history).</summary>
    public static string? IndexOverride;

    /// <summary>Millisecond resolution: two rounds started back to back must not share a file.</summary>
    public static string NewRoundId(DateTime? at = null) =>
        "round-" + (at ?? DateTime.Now).ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);

    public static string RoundPath(string roundId) => Path.Combine(LogDir, roundId + ".log");

    public static void Write(string path, RoundData d)
    {
        Directory.CreateDirectory(LogDir);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var stats = RoundStats.Compute(d.Trials, d.RadiusPx, d.TargetDistancePx);
        var x = CurveAnalysis.Analyze(d.Profile, true);
        var y = CurveAnalysis.Analyze(d.Profile, false);

        var sb = new StringBuilder();
        sb.AppendLine("Raw Accel Modern UI - round log (format v" + FormatVersion + ")");
        sb.AppendLine("PASTE THIS WHOLE FILE TO THE ASSISTANT. It contains the frozen config, the setup,");
        sb.AppendLine("per-trial data and a machine-readable JSON block at the end.");
        sb.AppendLine();
        sb.AppendLine($"round            : {d.RoundId}");
        sb.AppendLine($"measured at      : {d.When}          (config below was captured at round START)");
        sb.AppendLine($"config source    : {d.ConfigSource}");
        sb.AppendLine($"app / driver     : {d.AppVersion} / {d.DriverVersion}");
        sb.AppendLine($"profile          : {d.ProfileName}   config sha {d.ConfigSha}");
        if (!string.IsNullOrWhiteSpace(d.DeviceId))
            sb.AppendLine($"device           : {d.DeviceName}  [{d.DeviceId}]");
        sb.AppendLine($"game sens / dpi / poll : {Num(d.Sensitivity)} / {Num(d.MouseDpi)} / {Num(d.PollRate)} Hz");
        sb.AppendLine($"test resolution  : {d.Resolution}");
        sb.AppendLine($"setup            : {d.Mode}, distance {Num(d.TargetDistancePx)} px, ball radius {Num(d.RadiusPx)} px, " +
                      $"{d.TargetCount} balls, direction {d.Direction}, seed {d.Seed}");
        sb.AppendLine($"result           : {(string.IsNullOrWhiteSpace(d.Score) ? $"{d.Trials.Count} clicks" : d.Score)}");
        sb.AppendLine();
        sb.AppendLine("--- what the numbers are ---");
        sb.AppendLine("mode             : many balls stay on screen, clicked one after another; a clicked ball");
        sb.AppendLine("                   reappears elsewhere, so the run is continuous.");
        sb.AppendLine("origin           : where the pointer actually was after the PREVIOUS click (round start for");
        sb.AppendLine("                   trial 1). distance/radial/tangential are all relative to that.");
        sb.AppendLine("radial/tangential: px, relative to that trial's own aim direction.");
        sb.AppendLine("                 radial > 0 = pointer ended BEYOND the ball (overshoot).");
        sb.AppendLine("                 tangential is perpendicular to that direction, not 'vertical'.");
        sb.AppendLine("distance         : physical desktop px from that origin to the ball centre. It varies per");
        sb.AppendLine("                   click, so one round already covers small and large flicks.");
        sb.AppendLine("                 NOT a game angle and NOT mouse counts.");
        sb.AppendLine("target           : the ball that was clicked. direct=0 means the click landed on no ball and");
        sb.AppendLine("                 was attributed to the nearest one (a miss, not a wrong-ball hit).");
        sb.AppendLine("hit              : the cursor was inside that ball's radius when the button went down.");
        sb.AppendLine("peak_cms         : approximate POST-driver output speed (WM_INPUT after rawaccel.sys),");
        sb.AppendLine("                 windowed and delivery-timed. It is not hand speed and must not be");
        sb.AppendLine("                 compared to the profile's input thresholds.");
        sb.AppendLine("angle/deg fields : legacy column, this test does not simulate a camera.");
        sb.AppendLine("prim_radial_pct  : where the FIRST movement stopped along the aim line (speed dropping below");
        sb.AppendLine("                 20% of its peak and staying there). Theory-dependent - the sanity line");
        sb.AppendLine("                 below says whether it can be trusted for this round.");
        sb.AppendLine("max_extent_pct   : SEGMENTATION-FREE, use this one. How far along the aim line the pointer");
        sb.AppendLine("                 ever got before the click, as % of the distance. >100% means it flew past.");
        sb.AppendLine("overshot=1       : it passed the far edge of the ball at some point during the flick.");
        sb.AppendLine("reach_ms         : time from the flick start until the pointer was first inside the ball.");
        sb.AppendLine("corrected=1      : after the first movement stopped, the pointer still moved by more than");
        sb.AppendLine("                 8% of the distance (or the ball radius) - forwards or back. corrected=0");
        sb.AppendLine("                 means the impulse endpoint IS where the click landed.");
        sb.AppendLine("na in prim_*     : too few input windows in that flick to reconstruct a path.");
        if (!string.IsNullOrWhiteSpace(d.TelemetryNote)) sb.AppendLine("telemetry        : " + d.TelemetryNote);
        sb.AppendLine();
        sb.AppendLine("--- curve that was active (driver units, static single-packet evaluation) ---");
        Line(sb, "X", x);
        Line(sb, "Y", y);
        sb.AppendLine($"mode X/Y       : {d.Profile.accel_x.mode} gain={d.Profile.accel_x.gain} / {d.Profile.accel_y.mode} gain={d.Profile.accel_y.gain}");
        sb.AppendLine($"offset X/Y     : {Num(d.Profile.accel_x.input_offset)} / {Num(d.Profile.accel_y.input_offset)}");
        sb.AppendLine($"accel X/Y      : {Num(d.Profile.accel_x.acceleration)} / {Num(d.Profile.accel_y.acceleration)}");
        sb.AppendLine($"cap X/Y        : ({Num(d.Profile.accel_x.cap.x)},{Num(d.Profile.accel_x.cap.y)}) / ({Num(d.Profile.accel_y.cap.x)},{Num(d.Profile.accel_y.cap.y)})  cap_mode X {d.Profile.accel_x.cap_mode} / Y {d.Profile.accel_y.cap_mode}");
        sb.AppendLine($"output dpi     : {Num(d.Profile.output_dpi)}   yx {Num(d.Profile.yx_output_dpi_ratio)}   lr {Num(d.Profile.lr_output_dpi_ratio)}   ud {Num(d.Profile.ud_output_dpi_ratio)}");
        sb.AppendLine($"whole={d.Profile.speed_processor_args.whole} lpNorm={Num(d.Profile.speed_processor_args.lp_norm)} " +
                      $"halflife in/scale/out = {Num(d.Profile.speed_processor_args.input_speed_smooth_halflife)}/" +
                      $"{Num(d.Profile.speed_processor_args.scale_smooth_halflife)}/{Num(d.Profile.speed_processor_args.output_speed_smooth_halflife)} " +
                      $"rot={Num(d.Profile.degrees_rotation)} snap={Num(d.Profile.degrees_snap)} speed_max={Num(d.Profile.speed_max)}");
        sb.AppendLine();
        sb.AppendLine("--- post-driver output speed during the round (approximate, counts/ms) ---");
        var s = d.OutputSpeed;
        sb.AppendLine($"p50 {Num(s.p50)}   p75 {Num(s.p75)}   p95 {Num(s.p95)}   peak {Num(s.peak)}");
        if (d.MouseDpi > 0)
            sb.AppendLine($"at {Num(d.MouseDpi)} DPI that is p50 {Num(Cms(s.p50, d.MouseDpi))} cm/s, p95 {Num(Cms(s.p95, d.MouseDpi))} cm/s (still output, not hand motion)");
        sb.AppendLine();
        sb.AppendLine("--- trials ---");
        sb.AppendLine("idx;target;balls;distance_px;peak_cms;samples;radial_px;radial_pct;tangential_px;error_px;hit;ms;truncated;direct;prim_radial_pct;max_extent_pct;overshot;reach_ms;onset_ms;corrected");
        int i = 0;
        foreach (var t in d.Trials)
        {
            i++;
            sb.AppendLine(string.Join(";", i, t.TargetId, t.VisibleTargets, Num(t.DistancePx), Num(t.PeakSpeed),
                t.SampleCount, Num(t.RadialPx), Num(t.RadialPct), Num(t.TangentialPx), Num(t.ErrorPx),
                t.Hit ? "1" : "0", Num(t.EndMs - t.StartMs), t.TelemetryTruncated ? "1" : "0",
                t.BallUnderCursor ? "1" : "0",
                t.HasPrimary ? Num(t.PrimaryRadialPct) : "na",
                t.HasPrimary ? Num(t.MaxExtentPct) : "na",
                t.HasPrimary ? (t.Overshot ? "1" : "0") : "na",
                t.HasPrimary ? (double.IsNaN(t.TimeToTargetMs) ? "never" : Num(t.TimeToTargetMs)) : "na",
                t.HasPrimary ? Num(t.OnsetMs) : "na",
                t.HasPrimary ? (t.Corrected ? "1" : "0") : "na"));
        }

        sb.AppendLine();
        sb.AppendLine("--- statistics (description of THIS sample, not a diagnosis) ---");
        if (d.Trials.Count == 0) sb.AppendLine("(no trials)");
        else
        {
            sb.AppendLine($"count          : {stats.Count} clicks   cleared {stats.BallsCleared} balls   hit {stats.HitRate:0.0}%   radius {Num(d.RadiusPx)} px");
            sb.AppendLine($"pace           : {stats.KillsPerMinute:0.0} balls/min over {stats.SessionMs / 1000.0:0.0}s");
            sb.AppendLine($"radial %       : mean {stats.RadialMean:+0.0;-0.0}   median {stats.RadialMedian:+0.0;-0.0}   sd {stats.RadialSd:0.0}   MAE|.| {stats.RadialMae:0.0}");
            sb.AppendLine($"radial split   : overshoot >+5% = {stats.OvershootCount:0}   within +-5% = {stats.OnTargetCount:0}   undershoot <-5% = {stats.UndershootCount:0}");
            sb.AppendLine($"tangential px  : mean|.| {stats.TangMeanAbs:0.0}   median|.| {stats.TangMedianAbs:0.0}   sd {stats.TangSd:0.0}   signed mean {stats.SignedTangMean:+0.0;-0.0}");
            sb.AppendLine($"total error px : mean {stats.ErrorMean:0.0}   median {stats.ErrorMedian:0.0}");
            sb.AppendLine($"time ms        : median {stats.TimeMedian:0}   p90 {stats.TimeP90:0}");
            sb.AppendLine($"distance px    : min {Num(stats.DistanceMin)}   median {Num(stats.DistanceMedian)}   max {Num(stats.DistanceMax)}");
            if (stats.Buckets.Count > 0)
            {
                sb.AppendLine("by flick length:");
                foreach (var b in stats.Buckets)
                    sb.AppendLine($"  {b.label,-10} n={b.n,3}  hit {b.hitPct,5:0.0}%  radial median {b.radialMedian,+6:0.0;-0.0}%  " +
                                  $"|tangential| {b.tangMedian,5:0.0}px  time {b.timeMedian,3:0}ms" +
                                  (b.primaryN > 0 ? $"   initial impulse {b.primaryRadialMedian,+6:0.0;-0.0}% (n={b.primaryN})" : ""));
            }
            sb.AppendLine($"open loop      : {stats.PrimaryCount}/{stats.Count} flicks reconstructed; initial-impulse radial " +
                          $"median {stats.PrimaryRadialMedian:+0.0;-0.0}% (mean {stats.PrimaryRadialMean:+0.0;-0.0}, sd {stats.PrimaryRadialSd:0.0}, MAE {stats.PrimaryMae:0.0})");
            sb.AppendLine($"                 over >+5% = {stats.PrimaryOver}   within +-5% = {stats.PrimaryOn}   under <-5% = {stats.PrimaryUnder}" +
                          $"   |tangential| median {stats.PrimaryTangMedian:0.0}px   peak {stats.PrimaryPeakMedian:0.0} c/ms   corrected before click: {stats.CorrectedCount}");
            sb.AppendLine($"                 {stats.NoFixCount} flicks needed no second submovement (impulse ended {stats.NoFixPrimMedian:+0.0;-0.0}% off the target)");
            sb.AppendLine($"segment-free     : furthest point along the aim line = {stats.MaxExtentMedian:0}% of the distance, " +
                          $"passed the ball entirely in {stats.OvershootCount2}/{stats.PrimaryCount} flicks, " +
                          $"reached the ball in {stats.TimeToTargetMedian:0}ms (movement started at {stats.OnsetMedianMs:0}ms)");
            if (stats.HitExtentMedian > 0)
                sb.AppendLine($"                 guard: flicks that hit the ball reached {stats.HitExtentMedian:0}% of the distance " +
                              "(must be >=95, otherwise the path reconstruction is losing travel)");
            sb.AppendLine($"path shape     : sideways travel {stats.LateralPathMedian:0}px on a {stats.DistanceMedian:0}px flick " +
                          $"= arc ratio {stats.ArcRatioMedian:0.00}x the aim (0 = straight)   direction changes {stats.ReversalsMean:0.0}/flick   " +
                          $"hooks (>=1 turn) {stats.HookCount}/{stats.PrimaryCount}   furthest off the line {stats.PerpPeakMedian:+0;-0}px");
            if (stats.AsymN >= 8)
                sb.AppendLine($"axis asymmetry : e = {stats.AsymMean:+0.000;-0.000} +- {stats.AsymSem:0.000} (n={stats.AsymN} diagonal flicks)   " +
                              "fit perp = e * along * ax * ay:  e>0 bends towards Y (Y gains more), e<0 bends towards X, 0 = unaligned noise");
            sb.AppendLine($"speed captured : {stats.MeasuredCount}/{stats.Count} trials" +
                          (stats.MeasuredCount > 0 ? $"   median peak where measured {stats.PeakMedianWhenMeasured:0.0} c/ms" : ""));
            sb.AppendLine($"empty clicks   : {stats.EmptyClickCount} (click hit no ball; scored against the nearest one)");
            sb.AppendLine($"retries        : {stats.RetryCount} (immediate re-click of the ball just missed, <150px; excluded from the buckets)");
        }

        if (stats.Quality.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- data quality flags (read before concluding anything) ---");
            foreach (var q in stats.Quality) sb.AppendLine("* " + q);
        }
        if (!string.IsNullOrWhiteSpace(d.AppliedPatch))
        {
            sb.AppendLine();
            sb.AppendLine("--- patch applied before this round ---");
            sb.AppendLine(d.AppliedPatch);
        }

        sb.AppendLine();
        sb.AppendLine("--- machine readable ---");
        sb.AppendLine("```json");
        sb.AppendLine(BuildJson(d, stats, x, y));
        sb.AppendLine("```");

        sb.AppendLine();
        sb.AppendLine("--- profile json (frozen copy for exact reproduction) ---");
        sb.AppendLine(d.ProfileJson);

        File.WriteAllText(path, sb.ToString());
        AppendIndex(path, d, stats);
    }

    static string BuildJson(RoundData d, RoundStats st, AxisProfile x, AxisProfile y)
    {
        var o = new Dictionary<string, object?>
        {
            ["formatVersion"] = FormatVersion,
            ["round"] = d.RoundId,
            ["when"] = d.When,
            ["configSha"] = d.ConfigSha,
            ["configSource"] = d.ConfigSource,
            ["app"] = d.AppVersion,
            ["driver"] = d.DriverVersion,
            ["profile"] = d.ProfileName,
            ["device"] = new Dictionary<string, object?> { ["id"] = d.DeviceId, ["name"] = d.DeviceName },
            ["setup"] = new Dictionary<string, object?>
            {
                ["mode"] = d.Mode,
                ["score"] = d.Score,
                ["distancePx"] = d.TargetDistancePx,
                ["radiusPx"] = d.RadiusPx,
                ["balls"] = d.TargetCount,
                ["direction"] = d.Direction,
                ["seed"] = d.Seed,
                ["clicks"] = d.Trials.Count,
                ["sessionMs"] = Math.Round(d.SessionMs, 1),
                ["resolution"] = d.Resolution,
                ["gameSensitivity"] = d.Sensitivity,
                ["mouseDpi"] = d.MouseDpi,
                ["pollHz"] = d.PollRate,
            },
            ["curve"] = new Dictionary<string, object?>
            {
                ["x"] = new Dictionary<string, object?> { ["mode"] = d.Profile.accel_x.mode.ToString(), ["gain"] = d.Profile.accel_x.gain != 0,
                    ["inputOffset"] = d.Profile.accel_x.input_offset, ["acceleration"] = d.Profile.accel_x.acceleration,
                    ["exponent"] = d.Profile.accel_x.exponent_classic, ["capX"] = d.Profile.accel_x.cap.x, ["capY"] = d.Profile.accel_x.cap.y,
                    ["capMode"] = d.Profile.accel_x.cap_mode.ToString(), ["threshold"] = x.Threshold, ["capSpeed"] = JNum(x.CapSpeed), ["peakGain"] = x.PeakGain },
                ["y"] = new Dictionary<string, object?> { ["mode"] = d.Profile.accel_y.mode.ToString(), ["gain"] = d.Profile.accel_y.gain != 0,
                    ["inputOffset"] = d.Profile.accel_y.input_offset, ["acceleration"] = d.Profile.accel_y.acceleration,
                    ["exponent"] = d.Profile.accel_y.exponent_classic, ["capX"] = d.Profile.accel_y.cap.x, ["capY"] = d.Profile.accel_y.cap.y,
                    ["capMode"] = d.Profile.accel_y.cap_mode.ToString(), ["threshold"] = y.Threshold, ["capSpeed"] = JNum(y.CapSpeed), ["peakGain"] = y.PeakGain },
                ["outputDpi"] = d.Profile.output_dpi,
                ["yxRatio"] = d.Profile.yx_output_dpi_ratio,
                ["whole"] = d.Profile.speed_processor_args.whole != 0,
                ["halflife"] = new Dictionary<string, object?>
                {
                    ["input"] = d.Profile.speed_processor_args.input_speed_smooth_halflife,
                    ["scale"] = d.Profile.speed_processor_args.scale_smooth_halflife,
                    ["output"] = d.Profile.speed_processor_args.output_speed_smooth_halflife,
                },
            },
            ["meaning"] = new Dictionary<string, object?>
            {
                ["mode"] = "continuous: many balls on screen, each click targets the ball under the cursor " +
                           "(or the nearest one) and a hit ball respawns elsewhere",
                ["units"] = "physical desktop px; peak_cms = approximate POST-driver output counts/ms",
                ["origin"] = "pointer position after the previous click; distance is the real flick length, so it varies",
                ["radialPositive"] = "overshoot (pointer stopped beyond the ball along that trial's aim direction)",
                ["tangential"] = "perpendicular to that trial's own direction; not screen-vertical",
                ["ballUnderCursorFalse"] = "the click hit no ball; scored against the nearest one (a miss)",
                ["speedIsHandSpeed"] = false,
                ["angleIsGameAngle"] = false,
                ["arcRatio"] = "perpendicular path length / aim distance. Endpoint measures cannot see an arc " +
                               "(a pointer that loops out and comes back lands exactly where a straight one does), " +
                               "so this is the curvature the eye sees and the endpoint error does not",
                ["asymCoef"] = "least-squares fit of the signed off-line offset against along * ax * ay over the " +
                               "primary impulse. A relative over-drive e of one axis versus the other predicts exactly " +
                               "that shape: e>0 bends towards the Y axis, e<0 towards X, and motor noise has no such " +
                               "alignment and averages towards 0. Only diagonal aims carry it, hence asymN",
            },
            ["statistics"] = new Dictionary<string, object?>
            {
                ["count"] = st.Count,
                ["clicks"] = st.Clicks,
                ["ballsCleared"] = st.BallsCleared,
                ["killsPerMinute"] = Round(st.KillsPerMinute),
                ["sessionMs"] = Round(st.SessionMs),
                ["hitRatePct"] = Round(st.HitRate),
                ["radialMeanPct"] = Round(st.RadialMean),
                ["radialMedianPct"] = Round(st.RadialMedian),
                ["radialSdPct"] = Round(st.RadialSd),
                ["radialMaePct"] = Round(st.RadialMae),
                ["overshoot"] = (int)st.OvershootCount,
                ["onTarget"] = (int)st.OnTargetCount,
                ["undershoot"] = (int)st.UndershootCount,
                ["tangAbsMeanPx"] = Round(st.TangMeanAbs),
                ["tangAbsMedianPx"] = Round(st.TangMedianAbs),
                ["tangSignedMeanPx"] = Round(st.SignedTangMean),
                ["errorMedianPx"] = Round(st.ErrorMedian),
                ["timeMedianMs"] = Round(st.TimeMedian),
                ["timeP90Ms"] = Round(st.TimeP90),
                ["distanceMinPx"] = Round(st.DistanceMin),
                ["distanceMaxPx"] = Round(st.DistanceMax),
                ["distanceBuckets"] = st.Buckets.Select(b => new Dictionary<string, object?>
                {
                    ["label"] = b.label, ["n"] = b.n, ["hitRatePct"] = Round(b.hitPct),
                    ["radialMedianPct"] = Round(b.radialMedian), ["tangAbsMedianPx"] = Round(b.tangMedian),
                    ["timeMedianMs"] = Round(b.timeMedian),
                }).ToList(),
                ["emptyClicks"] = st.EmptyClickCount,
                ["retries"] = st.RetryCount,
                ["bucketedClicks"] = st.Count - st.RetryCount,
                ["speedMeasuredTrials"] = st.MeasuredCount,
                ["truncatedTrials"] = st.TruncatedCount,
                ["qualityFlags"] = st.Quality,
                ["qualityZh"] = st.Notes.Select(n => new Dictionary<string, object?> { ["t"] = n.Zh, ["warn"] = n.Warn }).ToList(),
                ["maxExtentMedianPct"] = Num3(st.MaxExtentMedian),
                ["hitExtentMedianPct"] = Num3(st.HitExtentMedian),
                ["overshoot2"] = st.OvershootCount2,
                ["timeToTargetMedianMs"] = Num3(st.TimeToTargetMedian),
                ["arcRatioMedian"] = Num3(st.ArcRatioMedian),
                ["lateralPathMedianPx"] = Num3(st.LateralPathMedian),
                ["perpPeakMedianPx"] = Num3(st.PerpPeakMedian),
                ["reversalsMean"] = Num3(st.ReversalsMean),
                ["hookCount"] = st.HookCount,
                ["asymCoef"] = Num3(st.AsymMean), ["asymSem"] = Num3(st.AsymSem), ["asymN"] = st.AsymN,
            },
            ["openLoop"] = new Dictionary<string, object?>
            {
                ["reconstructed"] = st.PrimaryCount,
                ["of"] = st.Count,
                ["radialMedianPct"] = Round(st.PrimaryRadialMedian),
                ["radialMeanPct"] = Round(st.PrimaryRadialMean),
                ["radialSdPct"] = Round(st.PrimaryRadialSd),
                ["radialMaePct"] = Round(st.PrimaryMae),
                ["tangAbsMedianPx"] = Round(st.PrimaryTangMedian),
                ["over"] = st.PrimaryOver, ["onTarget"] = st.PrimaryOn, ["under"] = st.PrimaryUnder,
                ["peakMedianCms"] = Round(st.PrimaryPeakMedian),
                ["correctedBeforeClick"] = st.CorrectedCount,
                ["meaning"] = "endpoint of the FIRST movement (speed < 20% of peak); unlike the click error " +
                              "this cannot be corrected away, so it is the measure that responds to gain",
            },
            ["outputSpeedApprox"] = new Dictionary<string, object?>
            {
                ["p50"] = Round(d.OutputSpeed.p50), ["p75"] = Round(d.OutputSpeed.p75),
                ["p95"] = Round(d.OutputSpeed.p95), ["peak"] = Round(d.OutputSpeed.peak),
                ["note"] = d.TelemetryNote,
            },
            ["trials"] = d.Trials.Select((t, n) => new Dictionary<string, object?>
            {
                ["i"] = n + 1, ["target"] = t.TargetId, ["balls"] = t.VisibleTargets,
                ["distancePx"] = Round(t.DistancePx), ["radialPx"] = Round(t.RadialPx),
                ["radialPct"] = Round(t.RadialPct), ["tangentialPx"] = Round(t.TangentialPx),
                ["errorPx"] = Round(t.ErrorPx), ["hit"] = t.Hit, ["timeMs"] = Round(t.EndMs - t.StartMs),
                ["peakCmsApprox"] = Round(t.PeakSpeed), ["samples"] = t.SampleCount,
                ["truncated"] = t.TelemetryTruncated, ["ballUnderCursor"] = t.BallUnderCursor,
                ["primary"] = t.HasPrimary ? new Dictionary<string, object?>
                {
                    ["radialPx"] = Round(t.PrimaryRadialPx), ["radialPct"] = Round(t.PrimaryRadialPct),
                    ["tangentialPx"] = Round(t.PrimaryTangentialPx), ["errorPx"] = Round(t.PrimaryErrorPx),
                    ["timeMs"] = Round(t.PrimaryTimeMs), ["peakCms"] = Round(t.PrimaryPeakCms),
                    ["corrected"] = t.Corrected, ["scaled"] = t.PrimaryScaled,
                    ["maxExtentPct"] = Round(t.MaxExtentPct), ["overshot"] = t.Overshot,
                    ["timeToTargetMs"] = double.IsNaN(t.TimeToTargetMs) ? null : Round(t.TimeToTargetMs),
                    ["onsetMs"] = Round(t.OnsetMs),
                    ["arcRatio"] = Round(t.ArcRatio), ["lateralPathPx"] = Round(t.LateralPathPx),
                    ["reversals"] = t.PathReversals, ["perpPeakPx"] = Round(t.PerpPeakPx),
                    ["asymCoef"] = double.IsNaN(t.AsymCoef) ? null : Round(t.AsymCoef),
                } : null,
            }).ToList(),
            ["patchApplied"] = string.IsNullOrWhiteSpace(d.AppliedPatch) ? null : d.AppliedPatch,
        };
        return JsonSerializer.Serialize(o, JsonOpts);
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static object? JNum(double v) => double.IsInfinity(v) ? "inf" : Round(v);
    static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    /// <summary>JSON has no NaN: a measure that could not be computed has to leave the machine
    /// block as null, or the whole embedded block stops parsing.</summary>
    static object? Num3(double v) => double.IsFinite(v) ? Math.Round(v, 3, MidpointRounding.AwayFromZero) : null;

    static void Line(StringBuilder sb, string tag, AxisProfile a) =>
        sb.AppendLine($"{tag}: accel starts {Num(a.Threshold)} counts/ms, " +
                      (double.IsInfinity(a.CapSpeed) ? "no cap below 30 counts/ms" : $"caps at {Num(a.CapSpeed)} counts/ms") +
                      $", peak gain {Num(a.PeakGain)}");

    static void AppendIndex(string path, RoundData d, RoundStats st)
    {
        var idx = Path.Combine(IndexOverride ?? LogDir, "index.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(idx)!);
        const string header = "file;when;configSha;profile;mode;distancePx;radiusPx;balls;clicks;cleared;hit%;kpm;radialMean%;radialMedian%;radialSd%;radialMAE%;over/in/under;tangMedianPx;timeMedianPx;speedMeasured;flags";
        var line = string.Join(";",
            Path.GetFileName(path), Safe(d.When), d.ConfigSha, Safe(d.ProfileName), Safe(d.Mode), Num(d.TargetDistancePx),
            Num(d.RadiusPx), d.TargetCount, st.Clicks, st.BallsCleared, $"{st.HitRate:0.0}", $"{st.KillsPerMinute:0.0}",
            $"{st.RadialMean:+0.0;-0.0}", $"{st.RadialMedian:+0.0;-0.0}",
            $"{st.RadialSd:0.0}", $"{st.RadialMae:0.0}",
            $"{st.OvershootCount:0}/{st.OnTargetCount:0}/{st.UndershootCount:0}",
            $"{st.TangMedianAbs:0.0}", $"{st.TimeMedian:0}", $"{st.MeasuredCount}/{st.Count}",
            Safe(string.Join(" | ", st.Quality)));

        var existing = File.Exists(idx) ? File.ReadAllLines(idx).ToList() : new List<string> { header };
        if (existing.Count == 0 || !existing[0].StartsWith("file;")) existing.Insert(0, header);
        var name = Path.GetFileName(path);
        // one row per round; a round is written once and never rewritten (the log is immutable)
        existing = existing.Where(l => !l.StartsWith(name + ";")).ToList();
        existing.Add(line);
        var tmp = idx + ".tmp";
        File.WriteAllLines(tmp, existing);
        File.Move(tmp, idx, true);
    }

    /// <summary>Read the machine block back out of a saved round (used for round-to-round comparison).</summary>
    public static RoundSummary? ReadSummary(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            int a = text.IndexOf("```json", StringComparison.Ordinal);
            int b = a < 0 ? -1 : text.IndexOf("```", a + 7, StringComparison.Ordinal);
            if (b < 0) return null;
            string json = text[(a + 7)..b].Trim();
            using var dom = JsonDocument.Parse(json);
            var r = dom.RootElement;
            var s = new RoundSummary
            {
                File = Path.GetFileName(file),
                When = r.TryGetProperty("when", out var w) ? w.GetString() ?? "" : "",
                ConfigSha = r.TryGetProperty("configSha", out var c) ? c.GetString() ?? "" : "",
            };
            if (r.TryGetProperty("setup", out var su))
            {
                s.DistancePx = D(su, "distancePx"); s.RadiusPx = D(su, "radiusPx");
                s.TargetCount = (int)D(su, "balls");
                s.Clicks = (int)D(su, "clicks");
                s.SessionMs = D(su, "sessionMs");
                s.Mode = Str(su, "mode"); s.Score = Str(su, "score");
                s.Resolution = Str(su, "resolution");
                s.Direction = Str(su, "direction");
                s.Trials = s.Clicks > 0 ? s.Clicks : (int)D(su, "trials");   // v3 called it trials
            }
            if (r.TryGetProperty("statistics", out var st))
            {
                s.HitRate = D(st, "hitRatePct"); s.RadialMean = D(st, "radialMeanPct");
                s.RadialMedian = D(st, "radialMedianPct"); s.RadialSd = D(st, "radialSdPct");
                s.RadialMae = D(st, "radialMaePct"); s.TangMedian = D(st, "tangAbsMedianPx");
                s.TimeMedian = D(st, "timeMedianMs");
                s.BallsCleared = (int)D(st, "ballsCleared");
                s.KillsPerMinute = D(st, "killsPerMinute");
                if (s.Trials == 0) s.Trials = (int)D(st, "count");
                if (st.TryGetProperty("qualityFlags", out var q) && q.ValueKind == JsonValueKind.Array)
                    s.Quality = q.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList();
                if (st.TryGetProperty("qualityZh", out var qz) && qz.ValueKind == JsonValueKind.Array)
                    s.NotesZh = qz.EnumerateArray()
                        .Select(e => (Str(e, "t"), e.TryGetProperty("warn", out var wv) && wv.ValueKind == JsonValueKind.True))
                        .Where(x => x.Item1.Length > 0).ToList();
                s.MaxExtentMedian = D(st, "maxExtentMedianPct");
                s.HitExtentMedian = D(st, "hitExtentMedianPct");
                s.Overshoot2 = (int)D(st, "overshoot2");
                s.TimeToTargetMedian = D(st, "timeToTargetMedianMs");
                s.ArcRatio = D(st, "arcRatioMedian");
                s.ReversalsMean = D(st, "reversalsMean");
                s.HookCount = (int)D(st, "hookCount");
                s.AsymMean = D(st, "asymCoef"); s.AsymSem = D(st, "asymSem"); s.AsymN = (int)D(st, "asymN");
            }
            return s;
        }
        catch { return null; }
    }

    static double D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Two-round comparison. A different setup is reported instead of being silently averaged
    /// away; a different config sha is EXPECTED when this is the before/after of a patch, and
    /// is stated as such so the reader knows which side changed what.
    /// </summary>
    public static List<string> Compare(RoundSummary a, RoundSummary b)
    {
        var lines = new List<string>();
        var diffs = new List<string>();
        if (Math.Abs(a.DistancePx - b.DistancePx) > 1) diffs.Add($"距离 {Num(a.DistancePx)} → {Num(b.DistancePx)} px");
        if (Math.Abs(a.RadiusPx - b.RadiusPx) > 0.5) diffs.Add($"球半径 {Num(a.RadiusPx)} → {Num(b.RadiusPx)} px");
        if (a.TargetCount != b.TargetCount) diffs.Add($"同屏球数 {a.TargetCount} → {b.TargetCount}");
        if (!string.Equals(a.Mode, b.Mode, StringComparison.OrdinalIgnoreCase) && a.Mode.Length > 0 && b.Mode.Length > 0)
            diffs.Add($"模式 {a.Mode} → {b.Mode}");
        if (!string.Equals(a.Resolution, b.Resolution, StringComparison.OrdinalIgnoreCase)) diffs.Add($"分辨率 {a.Resolution} → {b.Resolution}");
        if (!string.Equals(a.Direction, b.Direction, StringComparison.OrdinalIgnoreCase)) diffs.Add($"方向 {a.Direction} → {b.Direction}");

        lines.Add($"{a.File}");
        lines.Add($"    vs  {b.File}");
        if (!string.Equals(a.ConfigSha, b.ConfigSha, StringComparison.OrdinalIgnoreCase))
            lines.Add($"配置变了：{Short(a.ConfigSha)} → {Short(b.ConfigSha)}" +
                      "（这就是改设置前/后；只有这一项不同时，差值才归功于那次改动）");
        else
            lines.Add($"配置相同：{Short(a.ConfigSha)}（两轮可比，差异来自你手感波动或别的条件）");

        lines.Add($"成绩：{Or(a.Score, $"{a.BallsCleared}/{a.Clicks} 点击")}   →   {Or(b.Score, $"{b.BallsCleared}/{b.Clicks} 点击")}");
        if (a.KillsPerMinute > 0 || b.KillsPerMinute > 0)
            lines.Add($"节奏：{a.KillsPerMinute:0.0} → {b.KillsPerMinute:0.0} 球/分钟");
        lines.Add($"命中率 {a.HitRate:0.0}% → {b.HitRate:0.0}%");
        lines.Add($"径向中位 {a.RadialMedian:+0.0;-0.0}% → {b.RadialMedian:+0.0;-0.0}%   MAE {a.RadialMae:0.0}% → {b.RadialMae:0.0}%   标准差 {a.RadialSd:0.0}% → {b.RadialSd:0.0}%");
        lines.Add($"切向中位 {a.TangMedian:0.0}px → {b.TangMedian:0.0}px   单发用时中位 {a.TimeMedian:0}ms → {b.TimeMedian:0}ms");
        if (diffs.Count > 0)
            lines.Add("注意：其他条件不一致，不能全归因于参数 —— " + string.Join("；", diffs));
        var flags = a.Quality.Concat(b.Quality).Distinct().ToList();
        if (flags.Count > 0) lines.Add("质量警告：" + string.Join(" / ", flags.Take(3)));
        return lines;
    }

    static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : (sha.Length > 6 ? sha[..6] : sha);
    static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    public static string LatestRound()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return "";
            var marker = "format v" + FormatVersion;
            return Directory.GetFiles(LogDir, "round-*.log")
                           .Where(f => File.ReadAllText(f).Contains(marker))
                           .OrderByDescending(f => f, StringComparer.Ordinal)
                           .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    public static List<string> RecentRounds(int n)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return new List<string>();
            return Directory.GetFiles(LogDir, "round-*.log").OrderByDescending(f => f, StringComparer.Ordinal)
                           .Take(n).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Recent rounds with their readable summary, newest first. Feeds the log picker.</summary>
    public static List<RoundSummary> Rounds(int n = 40)
    {
        var list = new List<RoundSummary>();
        foreach (var f in RecentRounds(n))
        {
            var s = ReadSummary(f);
            if (s == null) continue;
            s.File = f;
            list.Add(s);
        }
        return list;
    }

    /// <summary>Immutable audit trail for every AI patch (parsed / confirmed / written / verified / restored).</summary>
    public static string WriteAudit(string roundId, string patchText, string status, string beforeJson, string afterJson)
    {
        Directory.CreateDirectory(AuditDir);
        var file = Path.Combine(AuditDir, roundId + "-" + DateTime.Now.ToString("HH-mm-ss-fff") + ".json");
        var o = new Dictionary<string, object?>
        {
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["round"] = roundId,
            ["status"] = status,
            ["patch"] = patchText,
            ["configBefore"] = beforeJson,
            ["configAfter"] = afterJson,
        };
        File.WriteAllText(file, JsonSerializer.Serialize(o, JsonOpts));
        return file;
    }

    static string Safe(string s) => (s ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    static double Cms(double countsPerMs, double dpi) => countsPerMs * 1000.0 / dpi * 2.54;
    static string Num(double v) => double.IsInfinity(v) ? "inf" : v.ToString("0.###", CultureInfo.InvariantCulture);
}
