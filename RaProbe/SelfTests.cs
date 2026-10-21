using System.Text.Json;
using RaCore;
using RaCore.Model;

namespace RaProbe;

/// <summary>
/// Offline assertions. These never touch the driver, the display or the mouse, so they can run
/// while the user is in a game. Every check must FAIL LOUDLY: the process exit code is 1 when
/// any assertion breaks, so a stale build or a swallowed error cannot look like a pass.
/// </summary>
public static class SelfTests
{
    static readonly List<string> failures = new();
    static int checks;

    static void Check(bool ok, string what)
    {
        checks++;
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures.Add(what);
    }

    public static int Run()
    {
        failures.Clear();
        checks = 0;

        Console.WriteLine("\n== A. SpeedRecorder: window boundaries, trimming, timing ==");
        Section("recorder", Recorder);

        Console.WriteLine("\n== B. TargetLayout: multi-ball geometry and scoring ==");
        Section("layout", Layout);

        Console.WriteLine("\n== C. RoundStats: spread instead of a cancelling mean ==");
        Section("stats", Stats);

        Console.WriteLine("\n== D. AiPatch: whitelist, ranges, staleness ==");
        Section("patch", Patch);

        Console.WriteLine("\n== E. TestLog: frozen snapshot round trip and comparison ==");
        Section("log", Logging);

        Console.WriteLine("\n== F. Json.Validate: nothing absurd reaches the driver ==");
        Section("validate", Validate);

        Console.WriteLine("\n== G. Raw input decoding, target attribution, respawn geometry ==");
        Section("wire", Wire);

        Console.WriteLine("\n== H. Session rules: clear N balls vs beat the clock ==");
        Section("rules", Rules);

        Console.WriteLine("\n== I. Open loop: the endpoint of the FIRST movement ==");
        Section("openloop", OpenLoopChecks);

        Console.WriteLine("\n== J. Simulated flicks: metrics must recover the injected truth ==");
        Section("simulator", SimulatorChecks);

        Console.WriteLine("\n== K. Device entries through the driver blob and settings.json ==");
        Section("devices", DeviceWire);

        Console.WriteLine("\n== L. History groups, noise verdicts, archive round trip ==");
        Section("history", HistoryChecks);

        Console.WriteLine();
        Console.WriteLine($"   {checks} checks, {failures.Count} failed");
        foreach (var f in failures) Console.WriteLine("   FAILED: " + f);
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>A crash is a failed check, never a silent early exit that hides the other sections.</summary>
    static void Section(string name, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            Check(false, $"{name} threw {e.GetType().Name}: {e.Message}");
            var trace = (e.StackTrace ?? "").Split('\n').Take(4);
            foreach (var line in trace) Console.WriteLine("       " + line.Trim());
        }
    }

    // ---------- A ----------

    static void Recorder()
    {
        double clock = 0;
        var rec = new SpeedRecorder(() => clock) { WindowMs = 2.0, Capacity = 60, IdleGapMs = 80 };

        // the sample emitted right after Mark() must be inside the window, not skipped
        long mark = rec.Mark();
        clock += 3; rec.Feed(10, 0);
        var first = rec.Since(mark);
        Check(first.Count == 1 && first[0].dx == 10, "Mark() then one window: first sample included (was off by one)");

        // counts must survive a trial boundary: pending partials are flushed, not dropped
        clock += 1; rec.Feed(4, 0);                       // partial window (<2ms after last emit)
        long mark2 = rec.Mark();                          // flushing here
        var before = rec.Since(mark);
        clock += 3; rec.Feed(7, 0);                       // first sample after the mark belongs to it
        var after = rec.Since(mark2);
        int totalCounts = before.Sum(s => Math.Abs(s.dx)) + after.Sum(s => Math.Abs(s.dx));
        Check(totalCounts == 21 && after.Count == 1 && after[0].dx == 7,
            $"flush at the boundary keeps every count (fed 10+4+7, saw {totalCounts}; after-window starts clean)");

        // a back-and-forth wiggle inside one window must not look like "no movement"
        rec.Clear();
        clock += 200;                                      // idle: new window
        long m3 = rec.Mark();
        clock += 2; rec.Feed(5, 0); clock += 0.5; rec.Feed(-5, 0);
        var wig = rec.Since(m3);
        Check(wig.Sum(s => s.pathX) == 10, "net displacement 0 but path length 10 is still recorded");

        // bursts: 200 packets delivered as 10 groups of 20 inside one tick must not divide by ~0
        rec.Clear();
        clock = 1000;
        long m4 = rec.Mark();
        for (int burst = 0; burst < 10; burst++)
        {
            for (int i = 0; i < 20; i++) rec.Feed(2, 1);   // same instant: coalesced delivery
            clock += 10;                                   // ...then the next group arrives
        }
        rec.Flush();
        var burstWin = rec.Since(m4);
        double peak = burstWin.Count == 0 ? double.MaxValue : burstWin.Max(s => s.SpeedWhole);
        Check(burstWin.Count > 0 && burstWin.All(s => s.dtMs >= 2.0 - 1e-9),
            $"every emitted window is at least WindowMs long ({burstWin.Count} windows)");
        Check(peak < 30 && double.IsFinite(peak), $"peak {peak:0.0} c/ms is plausible, not a dt~0 blowup");
        double pathX = burstWin.Sum(s => s.pathX), pathY = burstWin.Sum(s => s.pathY);
        Check(pathX == 400 && pathY == 200,
            $"all 200 packets' path is accounted for ({pathX:0} x-counts, {pathY:0} y-counts)");

        // trimming: an old mark must be reported as truncated, never silently shortened
        rec.Clear();
        rec.Capacity = 10;
        clock += 500;
        long old = rec.Mark();
        for (int i = 0; i < 60; i++) { clock += 3; rec.Feed(1, 0); }
        var win = rec.Since(old);
        Check(rec.IsTruncated(old) && win.Count is > 0 and <= 10,
            $"ring overflow is detected (truncated={rec.IsTruncated(old)}, kept {win.Count}/60)");

        // Clear() must not make an old mark suddenly mean "everything"
        rec.Clear();
        Check(rec.IsTruncated(old), "after Clear() an old mark still reads as truncated");

        // idle gap: a 5s pause must not turn one small movement into a tiny speed over a huge dt
        rec.Clear();
        clock = 10_000;
        long m5 = rec.Mark();
        clock = 10_000 + 5_000; rec.Feed(3, 0);
        clock += 2; rec.Feed(0, 0);
        var idle = rec.Since(m5);
        Check(idle.Any(s => s.dtMs <= 2.5), "a long idle starts a fresh window instead of stretching dt to 5s");

        // percentiles are time weighted and ordered
        rec.Clear();
        clock = 0;
        for (int i = 0; i < 20; i++) { clock += 10; rec.Feed(i, 0); }
        var pr = rec.Percentiles(byComponent: true);
        Check(pr.p25 <= pr.p50 && pr.p50 <= pr.p75 && pr.p75 <= pr.p95 && pr.p95 <= pr.peak,
            $"percentiles ordered (p50 {pr.p50:0.00} p95 {pr.p95:0.00} peak {pr.peak:0.00})");
    }

    // ---------- B ----------

    static void Layout()
    {
        var balls = TargetLayout.Place(960, 720, 3440, 1440, 420, 26, 5, seed: 1234);
        Check(balls.Count == 5, $"5 balls placed ({balls.Count})");
        Check(balls.All(b => b.X > 26 && b.X < 3440 - 26 && b.Y > 90 + 26 && b.Y < 1440 - 26),
            "every ball is fully on screen and clear of the banner");
        Check(balls.All(b => Math.Abs(b.DistancePx - 420) < 1e-6), "every ball sits at the requested distance");
        bool overlap = false;
        for (int i = 0; i < balls.Count; i++)
            for (int j = i + 1; j < balls.Count; j++)
                if (Math.Sqrt(Math.Pow(balls[i].X - balls[j].X, 2) + Math.Pow(balls[i].Y - balls[j].Y, 2)) < 26 * 5 - 1e-6)
                    overlap = true;
        Check(!overlap, "no two balls are closer than 5 radii (3.5 still looked touching)");

        var wide = TargetLayout.Place(960, 720, 3440, 1440, 700, 26, 3, seed: 77);
        bool tooClose = false;
        for (int i = 0; i < wide.Count; i++)
            for (int j = i + 1; j < wide.Count; j++)
                if (Math.Sqrt(Math.Pow(wide[i].X - wide[j].X, 2) + Math.Pow(wide[i].Y - wide[j].Y, 2)) < 175 - 1e-6)
                    tooClose = true;
        Check(wide.Count >= 2 && !tooClose,
            $"at 700px placement the gap scales to 175px, not 5r ({wide.Count} balls, tooClose={tooClose})");

        var again = TargetLayout.Place(960, 720, 3440, 1440, 420, 26, 5, seed: 1234);
        Check(again.SequenceEqual(balls), "same seed reproduces the same layout (rounds stay comparable)");
        var other = TargetLayout.Place(960, 720, 3440, 1440, 420, 26, 5, seed: 9999);
        Check(!other.SequenceEqual(balls), "a different seed gives a different layout");

        var crowded = TargetLayout.Place(1720, 720, 3440, 1440, 420, 60, 30, seed: 5);
        Check(crowded.Count is > 0 and < 30 && crowded.All(b => b.X > 60 && b.X < 3440 - 60 && b.Y > 150 && b.Y < 1440 - 60),
            $"a crowded request reports a shortfall instead of squeezing balls in ({crowded.Count}/30)");

        var none = TargetLayout.Place(1720, 720, 300, 300, 420, 26, 3, seed: 7);
        Check(none.Count == 0, "no room at all returns zero balls instead of placing them off screen");

        // scoring: radial is signed along the aim direction, tangential perpendicular to it
        var (over, tOver, eOver, pctOver) = TargetLayout.Score(1100, 500, 1000, 500, 1080, 500);
        Check(over == 20 && tOver == 0 && eOver == 20 && Math.Abs(pctOver - 25) < 1e-9,
            $"aiming right and stopping beyond the ball = +{over:0}px / +{pctOver:0}% overshoot");
        var (under, t2, e2, pct2) = TargetLayout.Score(1040, 540, 1000, 500, 1080, 500);
        Check(under == -40 && t2 == 40 && pct2 == -50,
            $"stopping short and sideways = {under:0}px radial, {t2:0}px tangential, {pct2:0}% (not ±5%)");
        var (_, _, e3, _) = TargetLayout.Score(1080, 500, 1000, 500, 1080, 500);
        Check(e3 == 0, "an exact hit scores zero error");
    }

    // ---------- C ----------

    static void Stats()
    {
        // 50% overshoot and 50% undershoot: the signed mean says "nothing wrong"
        var t = new List<Trial>();
        for (int i = 0; i < 10; i++) t.Add(T(20));
        for (int i = 0; i < 10; i++) t.Add(T(-20));
        var s = RoundStats.Compute(t, radiusPx: 26, targetDistancePx: 420);
        Check(Math.Abs(s.RadialMean) < 0.001, $"signed mean cancels to {s.RadialMean:0.###}");
        Check(s.RadialMae == 20 && Math.Abs(s.RadialSd - 20.54) < 0.1, $"MAE {s.RadialMae:0.0}% sd {s.RadialSd:0.0}% still show the spread");
        Check(s.OvershootCount == 10 && s.UndershootCount == 10,
            $"over/under split counts both groups ({s.OvershootCount:0}/{s.UndershootCount:0})");
        Check(s.Quality.Any(q => q.Contains("NOT evidence of a bias")),
            "the log warns that the cancelling mean is not a bias");

        var few = RoundStats.Compute(new List<Trial> { T(3), T(-2) }, 26, 420);
        Check(few.Count == 2 && few.Quality.Any(q => q.Contains("only 2 clicks")), "small samples are flagged, not hidden");

        var noSpeed = RoundStats.Compute(new List<Trial> { new Trial { RadialPct = 1, DistancePx = 420, SampleCount = 0 } }, 26, 420);
        Check(noSpeed.Quality.Any(q => q.Contains("no packets")), "peak 0 is reported as missing telemetry, not a still hand");

        var seq = new List<Trial>
        {
            new() { DistancePx = 300, TargetId = 4, Hit = false, RadialPct = -20, TangentialPx = 5, ErrorPx = 60, StartMs = 0, EndMs = 250, SampleCount = 5 },
            new() { DistancePx = 40, TargetId = 4, Hit = true, RadialPct = 5, TangentialPx = 1, ErrorPx = 3, StartMs = 250, EndMs = 350, SampleCount = 5 },
            new() { DistancePx = 60, TargetId = 9, Hit = true, RadialPct = -3, TangentialPx = 2, ErrorPx = 4, StartMs = 350, EndMs = 500, SampleCount = 5 },
        };
        var rs = RoundStats.Compute(seq, 26, 420);
        Check(rs.RetryCount == 1, $"an immediate re-click on the ball just missed is flagged as a retry ({rs.RetryCount})");
        Check(rs.Buckets.All(b => b.label != "<100px" || b.n == 1),
            $"the retry is kept out of the small-flick bucket ({string.Join(",", rs.Buckets.Select(b => b.label + ":" + b.n))})");
        Check(rs.Quality.Any(q => q.Contains("retries")), "the log says the buckets exclude retries");

        var same = RoundStats.Compute(t, 26, 420);
        Check(same.Quality.Any(q => q.Contains("one distance bucket")),
            $"a single-distance round cannot separate small from large flicks ({same.Buckets.Count} bucket)");

        var mixed = new List<Trial>();
        for (int i = 0; i < 8; i++) mixed.Add(T(6, 60));
        for (int i = 0; i < 8; i++) mixed.Add(T(-6, 300));
        for (int i = 0; i < 8; i++) mixed.Add(T(20, 700));
        var ms = RoundStats.Compute(mixed, 26, 420);
        Check(ms.Buckets.Count == 3 && ms.Buckets[0].label == "<100px" && ms.Buckets[2].label == ">=500px",
            $"flicks get split by length ({string.Join(",", ms.Buckets.Select(b => b.label + ":" + b.n))})");
        Check(Math.Abs(ms.Buckets[0].radialMedian - 6) < 1e-9 && Math.Abs(ms.Buckets[2].radialMedian - 20) < 1e-9,
            "each bucket keeps its own radial median, so a small-flick bias cannot hide in the overall mean");
    }

    static Trial T(double radialPct, double distancePx = 420) => new()
    {
        RadialPct = radialPct, RadialPx = radialPct * distancePx / 100.0, TangentialPx = 3,
        ErrorPx = Math.Abs(radialPct * distancePx / 100.0),
        DistancePx = distancePx, Hit = Math.Abs(radialPct) < 7, StartMs = 0, EndMs = 300,
        SampleCount = 5, TargetId = 1, VisibleTargets = 3,
    };

    // ---------- D ----------

    static ProfileDto BaseProfile() => new()
    {
        Name = "default",
        X = new AccelDto { Mode = "classic", Gain = true, InputOffset = 3, Acceleration = 0.06, ExponentClassic = 2, CapMode = "output", Cap = new Vec2Dto { X = 15, Y = 1.97 } },
        Y = new AccelDto { Mode = "classic", Gain = true, InputOffset = 2.5, Acceleration = 0.15, ExponentClassic = 2, CapMode = "output", Cap = new Vec2Dto { X = 0, Y = 1.83 } },
        Speed = new SpeedDto { Whole = false, LpNorm = 2 },
        OutputDpi = 1000,
    };

    static RootDto BaseDoc()
    {
        var r = new RootDto();
        r.Profiles.Add(BaseProfile());
        return r;
    }

    static string PatchJson(string round, string sha, string body) =>
        "{\"kind\":\"raui.tune-patch\",\"schema\":1,\"round\":\"" + round + "\",\"baseConfig\":\"" + sha +
        "\",\"profile\":\"default\"," + body + "}";

    static void Patch()
    {
        var doc = BaseDoc();
        string sha = ConfigHash.Of(doc.Profiles[0]);
        const string round = "round-2026-10-20_09-00-00-000";

        var ok = AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":3.6,\"capY\":1.85}}"), doc, "default", round);
        Check(ok.Ok && ok.Changes.Count == 2 && Math.Abs(ok.PreviewProfile!.X.InputOffset - 3.6) < 1e-9,
            "a valid patch parses and previews without touching the document");
        Check(Math.Abs(doc.Profiles[0].X.InputOffset - 3) < 1e-9, "Parse() left the live document alone");

        var fenced = AiPatch.Parse("```json\n" + PatchJson(round, sha, "\"changes\":{\"x\":{\"acceleration\":0.05}}") + "\n```", doc, "default", round);
        Check(fenced.Ok, "a fenced block from a chat reply is accepted");

        var prose = AiPatch.Parse("好的，这是方案：\n" + PatchJson(round, sha, "\"changes\":{\"y\":{\"inputOffset\":2.8}}") + "\n祝顺利", doc, "default", round);
        Check(prose.Ok, "surrounding prose is tolerated as long as one object is present");

        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":1e999}}"), doc, "default", round).Ok,
            "an infinite/absurd number is refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":-3}}"), doc, "default", round).Ok,
            "out-of-range values are refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"bounce\":2}}"), doc, "default", round).Ok,
            "unknown field names are refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"devices\":{}}"), doc, "default", round).Ok,
            "unknown sections are refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"data\":[1,2]}}"), doc, "default", round).Ok,
            "raw LUT data arrays are refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"mode\":\"banana\"}}"), doc, "default", round).Ok,
            "an unknown accel mode is refused");
        var modeOk = AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"mode\":\"natural\"}}"), doc, "default", round);
        Check(modeOk.Ok, "a known mode string is accepted" + (modeOk.Ok ? "" : " :: " + string.Join(" | ", modeOk.Errors)));
        Check(!AiPatch.Parse(PatchJson("round-other", sha, "\"changes\":{\"x\":{\"acceleration\":0.05}}"), doc, "default", round).Ok,
            "a patch for another round is refused");
        Check(!AiPatch.Parse(PatchJson(round, "deadbeef0000", "\"changes\":{\"x\":{\"acceleration\":0.05}}"), doc, "default", round).Ok,
            "a stale baseConfig is refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"acceleration\":0.05}}"), doc, "other", round).Ok,
            "a patch aimed at a different profile is refused");
        Check(!AiPatch.Parse("{\"kind\":\"something-else\"}", doc, "default", round).Ok, "a foreign kind is refused");
        Check(!AiPatch.Parse("{\"kind\":\"raui.tune-patch\",\"schema\":2,\"round\":\"" + round + "\",\"baseConfig\":\"" + sha + "\",\"changes\":{}}", doc, "default", round).Ok,
            "an unknown schema is refused");
        Check(!AiPatch.Parse("", doc, "default", round).Ok, "empty input is refused");
        Check(!AiPatch.Parse("{\"changes\":{}}", doc, "default", round).Ok, "a missing envelope is refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":3.4}}") + " {\"a\":1}", doc, "default", round).Ok,
            "two top-level objects are refused");
        Check(!AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":\"3.4\"}}"), doc, "default", round).Ok,
            "a number quoted as a string is refused");

        // the effect preview must describe the real curve, not just echo the number
        var eff = AiPatch.Parse(PatchJson(round, sha, "\"changes\":{\"x\":{\"inputOffset\":6}}"), doc, "default", round);
        Check(eff.Ok && eff.Warnings.Any(w => w.Contains("起加速")), "preview states the new activation point");

        // round trip: serialize our own suggestion, paste it back
        var text = AiPatch.SerializeSuggestion(round, sha, "default",
            new List<PatchChange> { new("x.inputOffset 横向输入阈值", "3", "3.6", "") });
        var rt = AiPatch.Parse(text, doc, "default", round);
        Check(rt.Ok && rt.Changes.Count == 1, "a suggestion emitted by the app itself parses back cleanly" +
              (rt.Ok ? $" (changes={rt.Changes.Count})" : " :: " + string.Join(" | ", rt.Errors)) + " :: " + text.Replace("\n", "\\n"));

        // applying twice in a row must not drift the document
        var doc2 = BaseDoc();
        var p1 = AiPatch.Parse(PatchJson(round, ConfigHash.Of(doc2.Profiles[0]), "\"changes\":{\"x\":{\"capY\":1.5}}"), doc2, "default", round);
        doc2.Profiles[0] = p1.PreviewProfile!;
        var p2 = AiPatch.Parse(PatchJson(round, ConfigHash.Of(doc2.Profiles[0]), "\"changes\":{\"x\":{\"capY\":1.5}}"), doc2, "default", round);
        Check(p2.Ok && p2.Changes.Count == 0, "a no-op patch is accepted but reports nothing to change");

        // the doc hash changes when a value changes (stale patch protection really works)
        Check(ConfigHash.Of(BaseProfile()) != ConfigHash.Of(p1.PreviewProfile!), "config hash tracks a parameter change");
    }

    // ---------- F ----------

    static void Validate()
    {
        var good = BaseDoc();
        Check(Json.Validate(good).Count == 0, "the normal document passes validation");

        var nan = BaseDoc();
        nan.Profiles[0].X.Acceleration = double.NaN;
        Check(Json.Validate(nan).Any(p => p.Contains("NaN")), "NaN in a curve field is caught");

        var inf = BaseDoc();
        inf.Profiles[0].Y.Cap.Y = double.PositiveInfinity;
        Check(Json.Validate(inf).Any(p => p.Contains("无穷")), "Infinity is caught");

        var huge = BaseDoc();
        huge.Profiles[0].OutputDpi = 9_999_999;
        Check(Json.Validate(huge).Any(p => p.Contains("Output DPI")), "an absurd output DPI is caught");

        var mode = BaseDoc();
        mode.Profiles[0].X.Mode = "clasci";
        Check(Json.Validate(mode).Any(p => p.Contains("已知模式")), "a typo'd accel mode is caught");

        var cap = BaseDoc();
        cap.Profiles[0].Y.CapMode = "outpu";
        Check(Json.Validate(cap).Any(p => p.Contains("封顶方式")), "a typo'd cap mode is caught");

        var dangling = BaseDoc();
        dangling.Devices.Add(new DeviceDto { Id = "HID\\VID_1234&PID_0001", Name = "m", Profile = "ghost" });
        Check(Json.Validate(dangling).Any(p => p.Contains("不存在的 profile")), "a device pointing at a missing profile is caught");

        var dup = BaseDoc();
        dup.Profiles.Add(new ProfileDto { Name = "default" });
        Check(Json.Validate(dup).Any(p => p.Contains("重复")), "duplicate profile names are caught");

        var empty = new RootDto();
        Check(Json.Validate(empty).Any(p => p.Contains("一个 profile 都没有")), "an empty document is caught");

        var lut = BaseDoc();
        for (int i = 0; i < 600; i++) lut.Profiles[0].X.Data.Add(1f);
        Check(Json.Validate(lut).Any(p => p.Contains("data")), "an over-long LUT is caught");

        var time = BaseDoc();
        time.Devices.Add(new DeviceDto { Id = "HID\\VID_1234&PID_0002", Name = "m2", Profile = "default",
            Config = new DeviceConfigDto { Dpi = 600, MinimumTime = 5, MaximumTime = 1 } });
        Check(Json.Validate(time).Any(p => p.Contains("反了")), "inverted time clamp is caught");
    }

    // ---------- G ----------

    static byte[] RawInputMouse(ushort usFlags, int dx, int dy, long device)
    {
        var b = new byte[48];
        BitConverter.GetBytes(0u).CopyTo(b, 0);            // dwType = RID_TYPEMOUSE
        BitConverter.GetBytes(1u).CopyTo(b, 4);            // dwCommand
        BitConverter.GetBytes(device).CopyTo(b, 8);        // hDevice
        BitConverter.GetBytes(0L).CopyTo(b, 16);           // hWnd
        BitConverter.GetBytes(usFlags).CopyTo(b, 24);      // RAWMOUSE.usFlags
        BitConverter.GetBytes(0u).CopyTo(b, 28);           // ulButtons
        BitConverter.GetBytes(0u).CopyTo(b, 32);           // ulRawButtons
        BitConverter.GetBytes(dx).CopyTo(b, 36);           // lLastX
        BitConverter.GetBytes(dy).CopyTo(b, 40);           // lLastY
        BitConverter.GetBytes(0u).CopyTo(b, 44);           // ulExtraInformation
        return b;
    }

    static void Wire()
    {
        // the flags that decide whether a packet is kept: MOUSE_MOVE_RELATIVE is 0, ABSOLUTE is 1.
        // Reading bit0 as "relative" once silently dropped every packet (a round logged 0/30 speeds).
        var rel = RawInput.Decode(RawInputMouse(0x0000, 7, -3, 0x1234));
        Check(rel.relative && !rel.absolute && rel.dx == 7 && rel.dy == -3,
            $"a normal relative packet is decoded (dx {rel.dx}, dy {rel.dy})");
        Check(rel.device == new IntPtr(0x1234), "the originating device handle is read");

        var abs = RawInput.Decode(RawInputMouse(0x0001, 99, 99, 0x1));
        Check(!abs.relative && abs.absolute && abs.dx == 0, "an absolute pointer is not treated as movement");

        var virt = RawInput.Decode(RawInputMouse(0x0002, 4, 4, 0x1));
        Check(virt.relative && virt.virtualDesktop && virt.dx == 4, "virtual-desktop relative motion is still kept");

        var kb = RawInputMouse(0, 1, 1, 0x1);
        BitConverter.GetBytes(1u).CopyTo(kb, 0);           // dwType = keyboard
        var notMouse = RawInput.Decode(kb);
        Check(notMouse.bytes == 0 && notMouse.dx == 0, "a non-mouse packet decodes to nothing");
        Check(RawInput.Decode(new byte[10]).bytes == 0, "a short buffer is refused instead of read out of bounds");

        // target attribution: the ball under the cursor wins, otherwise the nearest one is flagged
        var list = new List<BallTarget> { new(1, 100, 100, 0, 0), new(2, 800, 400, 0, 0), new(3, 900, 900, 0, 0) };
        Check(TargetLayout.Pick(list, 26, 110, 95) == (0, true), "a click inside a ball is attributed to that ball");
        var near = TargetLayout.Pick(list, 26, 700, 400);
        Check(near == (1, false), $"a click on nothing falls back to the nearest ball and is flagged (index {near.index}, underCursor {near.underCursor})");
        Check(TargetLayout.Pick(new List<BallTarget>(), 26, 0, 0) == (-1, false), "no balls returns no target");

        // respawning must not land on a ball that is still alive
        var blocker = new BallTarget(1, 1320, 500, 0, 0);          // sits exactly where a 0-degree respawn would go
        int hits = 0;
        for (int seed = 1; seed <= 60; seed++)
        {
            var fresh = TargetLayout.Place(900, 500, 3440, 1440, 420, 26, 1, seed: seed, avoid: new[] { blocker });
            if (fresh.Count == 0) continue;
            if (Math.Sqrt(Math.Pow(fresh[0].X - blocker.X, 2) + Math.Pow(fresh[0].Y - blocker.Y, 2)) < 26 * 2 + 10) hits++;
        }
        Check(hits == 0, $"none of 60 respawns overlapped the ball still on screen ({hits} would have)");

        var withRoom = TargetLayout.Place(900, 500, 3440, 1440, 420, 26, 1, seed: 7, avoid: new[] { blocker });
        Check(withRoom.Count == 1 && Math.Abs(Math.Sqrt(Math.Pow(withRoom[0].X - 900, 2) + Math.Pow(withRoom[0].Y - 500, 2)) - 420) < 1,
            "a respawn still keeps the configured distance from the click point");
    }

    // ---------- K ----------

    static void DeviceWire()
    {
        var doc = BaseDoc();
        doc.Profiles.Add(new ProfileDto { Name = "sniper" });
        doc.Devices.Add(new DeviceDto
        {
            Id = @"HID\VID_258A&PID_0049&MI_01&COL07", Name = "主鼠", Profile = "default",
            Config = new DeviceConfigDto { Dpi = 800, PollingRate = 2000 },
        });
        doc.Devices.Add(new DeviceDto
        {
            Id = @"HID\VID_046D&PID_C08B", Name = "mouse B", Profile = "sniper",
            Config = new DeviceConfigDto { Dpi = 1600, Disable = true },
        });

        Check(Json.Validate(doc).Count == 0, "a two-device, two-profile document passes validation");

        var blob = Convert4.BuildBlob(doc);
        int expect = WireSizes.IoBase + doc.Profiles.Count * WireSizes.Modifier + doc.Devices.Count * WireSizes.Device;
        Check(blob.Length == expect, $"blob = io_base + {doc.Profiles.Count} modifier + {doc.Devices.Count} device = {expect} bytes (got {blob.Length})");

        var back = Convert4.ReadBlob(blob);
        Check(back.Devices.Count == 2, "both device entries come back out of the blob");
        Check(back.Devices.Select(d => d.Id).SequenceEqual(doc.Devices.Select(d => d.Id)),
            "device ids survive the binary round trip byte for byte, in order");
        Check(back.Devices[0].Name == "主鼠", "a non-ASCII device name survives (the wire field is UTF-16)");
        Check(back.Devices[1].Profile == "sniper", "the per-device profile binding survives");
        Check(back.Devices[0].Config.Dpi == 800 && back.Devices[1].Config.Dpi == 1600, "per-device DPI survives");
        Check(back.Devices[1].Config.Disable && !back.Devices[0].Config.Disable, "the per-device disable flag survives");
        Check(back.Devices[0].Config.PollingRate == 2000, "the per-device polling rate survives");

        var viaJson = Json.Deserialize(Json.Serialize(doc));
        Check(viaJson.Devices.Count == 2 && viaJson.Devices[1].Profile == "sniper",
            "the same document also survives our settings.json round trip (official-file compatibility)");

        // the wire id field is WCHAR[200] and WriteWz truncates without complaining: two ids that
        // share their first 199 characters would become one device inside the driver, so the
        // validator has to refuse it AND the truncation must be the documented behaviour
        var longId = BaseDoc();
        longId.Devices.Add(new DeviceDto { Id = new string('X', device_settings.MAX_DEV_ID_LEN), Name = "long", Profile = "default" });
        Check(Json.Validate(longId).Any(p => p.Contains("截断")),
            "an id the driver field would cut down is refused before it can silently collide");
        var cut = Convert4.ReadBlob(Convert4.BuildBlob(longId)).Devices.First(d => d.Name == "long");
        Check(cut.Id.Length == device_settings.MAX_DEV_ID_LEN - 1,
            $"and the wire really does cut it at {device_settings.MAX_DEV_ID_LEN - 1} chars (got {cut.Id.Length})");
    }

    // ---------- J ----------

    static void SimulatorChecks()
    {
        const double dist = 700;

        // 1) a clean flick that lands on the first movement
        var a = Simulator.Flick1(dist, 6.0, 1.00, seed: 11, emptyWindowRate: 0.18);
        var pa = OpenLoop.Analyze(a.Path, 0, 0, dist, 0, dist, 0, a.StartMs, a.Path[^1].timeMs, 26);
        Check(pa != null, "clean flick reconstructed");
        if (pa == null) return;
        double truthA = a.TrueMaxExtentPx / dist * 100.0;
        Check(Math.Abs(pa.Value.MaxExtentPct - truthA) < 3 && !pa.Value.Overshot,
            $"clean flick: max_extent {pa.Value.MaxExtentPct:0.0}% against injected {truthA:0.0}%, overshot={pa.Value.Overshot}");
        Check(!pa.Value.Corrected, $"clean flick needs no second submovement (corrected={pa.Value.Corrected})");
        Check(Math.Abs(pa.Value.RadialPct) < 3,
            $"and the first impulse is measured on the target ({pa.Value.RadialPct:0.0}%, truth 0)");
        Check(!double.IsNaN(pa.Value.TimeToTargetMs) && Math.Abs(pa.Value.TimeToTargetMs - a.TrueReachMs) < 6,
            $"entry time {pa.Value.TimeToTargetMs:0}ms against injected {a.TrueReachMs:0}ms");
        Check(a.Path.Count(s => s.dx == 0) > 3, $"the injected series really does contain coalesced windows ({a.Path.Count(s => s.dx == 0)} of {a.Path.Count})");

        // 2) a flick that flies 15% past the target and is pulled back
        var b = Simulator.Flick1(dist, 6.0, 1.15, seed: 22, emptyWindowRate: 0.18);
        var pb = OpenLoop.Analyze(b.Path, 0, 0, dist, 0, dist, 0, b.StartMs, b.Path[^1].timeMs, 26);
        double truthB = b.TrueMaxExtentPx / dist * 100.0;
        Check(pb != null && Math.Abs(pb.Value.MaxExtentPct - truthB) < 3 && pb.Value.Overshot,
            $"overshooting flick: max_extent {pb?.MaxExtentPct:0.0}% against injected {truthB:0.0}%, overshot={pb?.Overshot}");
        Check(pb != null && pb.Value.Corrected, "and it is counted as corrected (it had to come back)");
        double primB = (b.TruePrimaryExtentPx - dist) / dist * 100.0;
        Check(pb != null && pb.Value.RadialPct > primB - 4 && pb.Value.RadialPct <= primB + 1,
            $"the impulse is measured flying past ({pb?.RadialPct:0.0}% against injected {primB:0.0}%)");

        // 3) a flick that stops at 76% and creeps in - the case the old code truncated wrongly
        var c = Simulator.Flick1(dist, 6.0, 0.76, seed: 33, emptyWindowRate: 0.18);
        var pc = OpenLoop.Analyze(c.Path, 0, 0, dist, 0, dist, 0, c.StartMs, c.Path[^1].timeMs, 26);
        double truthC = c.TrueMaxExtentPx / dist * 100.0;
        Check(pc != null && Math.Abs(pc.Value.MaxExtentPct - truthC) < 3,
            $"short-then-creep flick: max_extent {pc?.MaxExtentPct:0.0}% against injected {truthC:0.0}%");
        Check(pc != null && pc.Value.Corrected, "the creep is counted as a second submovement");
        double primC = (c.TruePrimaryExtentPx - dist) / dist * 100.0;
        Check(pc != null && Math.Abs(pc.Value.RadialPct - primC) < 4,
            $"and the segmentation view still reports the first impulse short ({pc?.RadialPct:0.0}% against injected {primC:0.0}%)");

        // 4) a whole simulated round through the real statistics + guard
        var round = Simulator.Round(40, dist, 6.0, 1.0, seed: 77);
        var st = RoundStats.Compute(round, 26, dist);
        Check(st.PrimaryCount == round.Count, $"every simulated flick was reconstructed ({st.PrimaryCount}/{round.Count})");
        Check(st.HitExtentMedian >= 95 && !st.Quality.Any(q => q.Contains("losing pointer travel")),
            $"the travel guard stays quiet on clean data (hits reached {st.HitExtentMedian:0}%)");
        Check(st.MaxExtentMedian > 96 && st.MaxExtentMedian < 106,
            $"round-level max_extent median {st.MaxExtentMedian:0.0}%");
        Check(st.TimeToTargetMedian > 20 && st.TimeToTargetMedian < 1200,
            $"round-level entry time median {st.TimeToTargetMedian:0}ms");
        Check(st.Count == 40 && st.BallsCleared >= 30, $"simulated round scored {st.BallsCleared}/{st.Count} clicks");

        // 5) the metric that the whole tuning protocol rests on must be blind to nothing:
        //    a systematically-overshooting round has to show it, a clean one must not
        var hot = Simulator.Round(40, dist, 6.0, 1.12, seed: 91);
        var sh = RoundStats.Compute(hot, 26, dist);
        Check(sh.MaxExtentMedian > st.MaxExtentMedian + 5,
            $"a round that flies past reads {sh.MaxExtentMedian:0.0}% vs {st.MaxExtentMedian:0.0}% for a neutral one");
        Check(sh.OvershootCount2 > st.OvershootCount2,
            $"and the overshoot count separates them ({sh.OvershootCount2} vs {st.OvershootCount2})");
        Check(Math.Abs(sh.RadialMedian - st.RadialMedian) < 1,
            "closed-loop accuracy is blind to it: both rounds click on the ball because he corrects, " +
            $"radial median {st.RadialMedian:0.0}% vs {sh.RadialMedian:0.0}%");
    }

    // ---------- I ----------

    static void OpenLoopChecks()
    {
        // a flick that flies past the target and is then pulled back before the click:
        // the closed-loop error says "fine", only the initial impulse reveals the overshoot
        int[] dx = { 100, 180, 220, 160, 90, 40, 20, -30, -40 };
        var path = new List<MouseSample>();
        double t = 1000;
        foreach (var d in dx) { path.Add(new MouseSample(d, 0, 2.0, t, path.Count + 1)); t += 2; }

        var p = OpenLoop.Analyze(path, 0, 0, 700, 0, 740, 0, 1000, t + 400);
        Check(p != null, "a path with enough windows is reconstructed");
        if (p == null) return;
        Check(p.Value.RadialPx > 60 && p.Value.RadialPct > 8,
            $"the primary impulse is caught past the target ({p.Value.RadialPx:0}px, {p.Value.RadialPct:0.0}%)");
        Check(p.Value.Corrected, "the pull-back before clicking is flagged as a correction");
        Check(!p.Value.Scaled, "no rescale when the reconstructed path already matches the click displacement");
        Check(p.Value.TimeMs > 0 && p.Value.PeakCms > 50, $"impulse time {p.Value.TimeMs:0}ms, peak {p.Value.PeakCms:0} c/ms");

        // the failure that made the first five baseline rounds useless: one smooth movement with
        // sparse/coalesced windows inside it must not be truncated at the first dip
        int[] spiky = { 60, 140, 200, 0, 200, 50, 30, 20, 0, 10, -10 };
        var sp2 = new List<MouseSample>();
        double t2 = 1000;
        foreach (var d in spiky) { t2 += 2; sp2.Add(new MouseSample(d, 0, 2.0, t2, sp2.Count + 1)); }
        var q = OpenLoop.Analyze(sp2, 0, 0, 700, 0, 700, 0, 1002, t2 + 20);
        Check(q != null && Math.Abs(q.Value.RadialPct) < 6,
            $"a monotone ramp with two empty windows is not truncated ({q?.RadialPct:0.0}%)");
        Check(q != null && !q.Value.Corrected, "and that ramp is not counted as corrected");
        Check(q != null && q.Value.MaxExtentPct > 98 && q.Value.MaxExtentPct < 103 && !q.Value.Overshot,
            $"a clean flick that never passes the ball reads max_extent {q?.MaxExtentPct:0}% / overshot={q?.Overshot}");
        Check(q != null && !double.IsNaN(q.Value.TimeToTargetMs),
            $"the moment it first entered the ball is measurable ({q?.TimeToTargetMs:0}ms)");
        Check(p.Value.MaxExtentPct > 110 && p.Value.Overshot,
            $"a flick that flies past reads max_extent {p.Value.MaxExtentPct:0}% / overshot={p.Value.Overshot}");

        var rs = RoundStats.Compute(new List<Trial>
        {
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -30, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -28, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -31, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -29, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -30, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -33, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -27, Corrected = false, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, PrimaryRadialPct = -35, Corrected = false, SampleCount = 9 },
        }, 26, 700);
        Check(rs.NoFixCount == 8 && Math.Abs(rs.NoFixPrimMedian + 30) < 3,
            "the no-correction impulse number is reported");

        List<Trial> Short(int n, double extent) => Enumerable.Range(0, n)
            .Select(_ => new Trial { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = extent, PrimaryRadialPct = 0, SampleCount = 9 })
            .ToList();
        Check(RoundStats.Compute(Short(10, 70), 26, 700).Quality.Any(q => q.Contains("losing pointer travel")),
            "a hit whose reconstructed path only reached 70% is flagged: the reconstruction lost travel");
        Check(!RoundStats.Compute(Short(10, 101), 26, 700).Quality.Any(q => q.Contains("losing pointer travel")),
            "and the same check stays quiet when hits really reached the ball");

        Check(OpenLoop.Analyze(path[..2], 0, 0, 700, 0, 740, 0, 1000, 1010) == null,
            "two windows are not enough to claim an initial impulse");
        Check(OpenLoop.Analyze(path, 0, 0, 0, 0, 1, 1, 1000, 1100) == null, "a zero-length aim is refused");

        // counts disagree with the pointer's real travel (scaling/DPI): the shape is kept, rescaled
        var p2 = OpenLoop.Analyze(path, 0, 0, 700, 0, 1480, 0, 1000, 2000);
        Check(p2 != null && p2.Value.Scaled && p2.Value.RadialPx > 0,
            $"path rescaled to the real displacement ({p2?.RadialPx:0.0}px past, scaled={p2?.Scaled})");

        // the exact failure of the 2026-10-21 morning rounds: the path is in raw counts while the
        // click is in screen px, and the true gain (1.176x here) sat inside the old [0.8,1.25]
        // "small mismatch" band - so the rescale was skipped and every hit read ~86% of its
        // extent, tripping the travel guard on all 5 real rounds. The click is ground truth.
        double g = 1.176;
        var cnt = new List<MouseSample>();
        double t3 = 1002, acc = 0; int emitted = 0;
        foreach (double d in spiky)
        {
            acc += d / g;                       // the driver emits integer counts: carry the remainder
            int whole = (int)Math.Round(acc);
            t3 += 2;
            cnt.Add(new MouseSample(whole - emitted, 0, 2.0, t3, cnt.Count + 1));
            emitted = whole;
        }
        var r = OpenLoop.Analyze(cnt, 0, 0, 700, 0, 700, 0, 1002, t3 + 20);
        Check(r != null && q != null && Math.Abs(r.Value.MaxExtentPct - q.Value.MaxExtentPct) < 0.5,
            $"a counts-unit flick reads the same shape as the px flick ({r?.MaxExtentPct:0.0}% vs {q?.MaxExtentPct:0.0}%; the old band read ~86%)");
        Check(r != null && q != null && Math.Abs(r.Value.RadialPct - q.Value.RadialPct) < 0.5,
            $"and its impulse is not reported short by the unit gap ({r?.RadialPct:0.0}% vs {q?.RadialPct:0.0}%)");
    }

    // ---------- H ----------

    static void Rules()
    {
        var balls = new SessionRule(TestMode.Balls, 30, 60);
        for (int i = 0; i < 10; i++) { balls.Clicks++; if (i < 5) balls.Hits++; }
        Check(!balls.Finished && balls.RemainingBalls == 25, $"misses do not consume the goal (cleared {balls.Hits}, remaining {balls.RemainingBalls})");
        Check(Math.Abs(balls.HitRatePct - 50) < 1e-9, $"hit rate counts clicks, not balls ({balls.HitRatePct:0}%)");

        for (int i = 0; i < 25; i++) { balls.Clicks++; balls.Hits++; }
        Check(balls.Finished && balls.EndReason == "goal_reached" && balls.RemainingBalls == 0,
            $"the round ends only after {balls.Hits} cleared balls, whatever the click count");

        var timed = new SessionRule(TestMode.Timed, 30, 60);
        timed.Clicks = 40; timed.Hits = 33; timed.ElapsedMs = 20_000;
        Check(!timed.Finished && timed.RemainingMs > 0, "timed mode ignores the ball goal and keeps running");
        timed.ElapsedMs = 61_000;
        Check(timed.Finished && timed.EndReason == "time_up", "timed mode ends on the clock");
        var t2 = new SessionRule(TestMode.Timed, 30, 60) { Clicks = 40, Hits = 30, ElapsedMs = 60_000 };
        Check(Math.Abs(t2.KillsPerMinute - 30) < 1e-9, $"pace is balls cleared per minute ({t2.KillsPerMinute:0.0})");
        Check(t2.Hud().Contains("已点掉 30") && t2.Hud().Contains("命中率 75"), "HUD shows the score and the live hit rate: " + t2.Hud());
        Check(new SessionRule(TestMode.Balls, 5, 60) { Hits = 9, Clicks = 9 }.RemainingBalls == 0, "remaining never goes negative");
        Check(t2.Score().Contains("30 球"), "score line states what the round achieved");
    }

    // ---------- E ----------

    static void Logging()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raui-logtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        TestLog.IndexOverride = dir;          // never write test rows into the user's real index.csv
        try
        {
            // TestLog writes beside the running executable; use a copy in temp for the read-back
            var doc = BaseDoc();
            var data = new RoundData
            {
                RoundId = "round-test-1",
                When = "2026-10-20 09:00:00",
                DriverVersion = "driver v1.7.0",
                ProfileName = "default",
                ConfigSha = ConfigHash.Of(doc.Profiles[0]),
                ConfigSource = "driver (frozen before the round)",
                ProfileJson = Json.Serialize(doc),
                Profile = Convert4.ToStruct(doc.Profiles[0]),
                Sensitivity = 1.5, MouseDpi = 600, PollRate = 2000,
                TargetDistancePx = 420, RadiusPx = 26, TargetCount = 3, Seed = 1234,
                Mode = "点掉 12 球", Score = "点掉 3/12 球，用了 12 次点击（9.2s）", SessionMs = 9200,
                Direction = "全向", Resolution = "3440x1440@180",
                Trials = new List<Trial> { T(20), T(-20), T(5), T(-8), T(30), T(-30), T(2), T(-2), T(12), T(-15),
                                            T(18), T(-11) },
                OutputSpeed = (4, 9, 20, 40),
                TelemetryNote = "test note",
            };

            var file = Path.Combine(dir, data.RoundId + ".log");
            TestLog.Write(file, data);
            var text = File.ReadAllText(file);

            Check(File.Exists(file), "round file written");
            Check(text.Contains("format v" + TestLog.FormatVersion), "log carries a format version");
            Check(text.Contains("config sha " + data.ConfigSha), "log records the frozen config hash");
            Check(text.Contains("driver (frozen before the round)"), "log states where the config came from");
            Check(text.Contains("POST-driver"), "log labels speeds as post-driver output");
            Check(!text.Contains("甩到位了吗") && !text.Contains("压枪"), "log has no subjective questionnaire section");
            Check(text.Contains("machine readable") && text.Contains("```json"), "log embeds a machine-readable block");
            Check(text.Contains("quality flags"), "log prints data-quality flags");

            var sum = TestLog.ReadSummary(file);
            Check(sum != null && sum.Trials == 12, "machine block reads back with the click count");
            Check(sum!.Clicks == 12 && sum.BallsCleared == 3,
                $"cleared vs clicked survive the round trip ({sum.BallsCleared} cleared / {sum.Clicks} clicks)");
            Check(sum.Mode.Contains("12 球") && sum.Score.Contains("点掉"), "the mode and score line are stored machine-readably");
            Check(sum.KillsPerMinute > 0, $"pace is stored ({sum.KillsPerMinute:0.0} balls/min)");
            Check(text.Contains("cleared 3 balls"), "the human summary separates cleared balls from clicks");
            Check(text.Contains("点掉 12 球"), "the setup line states the goal mode");
            Check(text.Contains("result           :"), "the result line states what the round achieved");
            Check(sum.ConfigSha == data.ConfigSha, "machine block carries the same config hash");
            Check(Math.Abs(sum.DistancePx - 420) < 1e-6 && sum.TargetCount == 3, "machine block keeps the setup for comparison");
            Check(sum.Quality.Count > 0, "machine block keeps the quality flags");

            // JSON inside the fence must be valid on its own
            int a = text.IndexOf("```json", StringComparison.Ordinal);
            int b = text.IndexOf("```", a + 7, StringComparison.Ordinal);
            try { using var _d = JsonDocument.Parse(text[(a + 7)..b].Trim()); Check(true, "embedded json parses"); }
            catch (Exception e) { Check(false, "embedded json parses: " + e.Message); }

            var same = new RoundData
            {
                RoundId = "round-test-2", When = data.When, DriverVersion = data.DriverVersion,
                ProfileName = data.ProfileName, ConfigSha = data.ConfigSha, ConfigSource = data.ConfigSource,
                ProfileJson = data.ProfileJson, Profile = data.Profile, Sensitivity = 1.5, MouseDpi = 600,
                PollRate = 2000, TargetDistancePx = 420, RadiusPx = 26, TargetCount = 3, Seed = 1234,
                Direction = "全向", Resolution = "3440x1440@180", Trials = new List<Trial>(data.Trials),
                OutputSpeed = data.OutputSpeed, TelemetryNote = "",
            };
            var f2 = Path.Combine(dir, "round-test-2.log");
            TestLog.Write(f2, same);
            var cmp = TestLog.Compare(sum, TestLog.ReadSummary(f2)!);
            Check(cmp.Any(l => l.Contains("命中率")) && cmp.Any(l => l.Contains("配置相同")),
                "identical setups compare, and the comparison says so");

            same.ConfigSha = "ffff0000aaaa";          // pretend a patch changed the curve in between
            var f4 = Path.Combine(dir, "round-test-4.log");
            TestLog.Write(f4, same);
            var cmpCfg = TestLog.Compare(sum, TestLog.ReadSummary(f4)!);
            Check(cmpCfg.Any(l => l.Contains("配置变了")) && cmpCfg.Any(l => l.Contains("ffff00")),
                "a different config sha is reported as the before/after of a settings change");

            same.TargetDistancePx = 300;
            var f3 = Path.Combine(dir, "round-test-3.log");
            TestLog.Write(f3, same);
            var cmp2 = TestLog.Compare(sum, TestLog.ReadSummary(f3)!);
            Check(cmp2.Any(l => l.Contains("不能全归因")), "a different distance is still flagged as a confound");
            var idx = Path.Combine(dir, "index.csv");
            Check(File.Exists(idx) && File.ReadAllLines(idx).Length >= 4,
                "index.csv gets one row per round, in the redirected folder");

            var lbl = new RoundSummary
            {
                File = @"C:\logs\round-2026-10-20_20-44-10-631.log", ConfigSha = "abcdef123456",
                Mode = "点掉 30 球", Clicks = 12, BallsCleared = 9, HitRate = 75,
            }.Label();
            Check(lbl.Contains("20:44:10") && lbl.Contains("点掉 30 球") && lbl.Contains("9/12") && lbl.Contains("abcdef"),
                "the picker label shows start time, mode, score and config: " + lbl);
            var realIndex = Path.Combine(TestLog.LogDir, "index.csv");
            Check(!File.Exists(realIndex) || !File.ReadAllText(realIndex).Contains("round-test-"),
                "no test rows leaked into the real index.csv");
        }
        catch (Exception e)
        {
            Check(false, "unexpected exception in the log test: " + e.Message);
        }
        finally
        {
            TestLog.IndexOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------- L ----------

    static RoundSummary RS(string file, string sha, string mode, double dist, int balls, string when,
                           double hit, double pace, double rad, double maxExtent = 0, int clicks = 60) =>
        new()
        {
            File = file, ConfigSha = sha, Mode = mode, DistancePx = dist, TargetCount = balls,
            When = when, HitRate = hit, KillsPerMinute = pace, RadialMedian = rad,
            MaxExtentMedian = maxExtent, Clicks = clicks, BallsCleared = (int)Math.Round(clicks * hit / 100.0),
        };

    static void HistoryChecks()
    {
        // grouping: newest group first, medians are true medians
        var rounds = new List<RoundSummary>
        {
            RS("a1", "aaaaaaaa11", "30 秒限时", 700, 26, "2026-10-21 16:56:48", 69.8, 88.8, +0.4),
            RS("a2", "aaaaaaaa11", "30 秒限时", 700, 26, "2026-10-21 16:57:28", 70.0, 97.9, +0.0),
            RS("a3", "aaaaaaaa11", "30 秒限时", 700, 26, "2026-10-21 16:58:05", 76.1, 102.0, +0.3),
            RS("b1", "bbbbbbbb22", "30 秒限时", 700, 26, "2026-10-20 23:31:00", 60.0, 84.0, +0.1),
            RS("b2", "bbbbbbbb22", "30 秒限时", 700, 26, "2026-10-20 23:32:00", 64.0, 86.0, +0.2),
            RS("b3", "bbbbbbbb22", "30 秒限时", 700, 26, "2026-10-20 23:33:00", 68.0, 88.0, +0.9),
        };
        var groups = History.Group(rounds);
        Check(groups.Count == 2 && groups[0].Sha == "aaaaaaaa11" && groups[0].Rounds.Count == 3,
            "rounds group by config+setup, newest group first");
        Check(Math.Abs(groups[0].MedianHit - 70.0) < 0.01 && Math.Abs(groups[1].MedianHit - 64.0) < 0.01,
            $"group medians are true medians ({groups[0].MedianHit:0.0} / {groups[1].MedianHit:0.0})");

        // config actually changed, difference beyond the noise floor -> say so, ask for confirmation
        var v = History.CompareNewestTwo(rounds);
        Check(v.Any(l => l.Contains("aaaaaa")) && v.Any(l => l.Contains("bbbbbb")), "both groups are described");
        Check(v.Any(l => l.Contains("超过了噪声底")), $"a +6pp hit jump is called beyond the noise floor: {string.Join(" | ", v)}");
        Check(v.Any(l => l.Contains("差值来自这次改动")), "same-conditions groups attribute the difference to the change");

        // difference inside the noise floor -> no claim
        rounds[0].HitRate = 65.0; rounds[1].HitRate = 65.5; rounds[2].HitRate = 66.0;    // median 65.5
        var v2 = History.CompareNewestTwo(rounds);
        Check(v2.Any(l => l.Contains("测不出差别")), "a +1.5pp move stays inside the noise floor and claims nothing");

        // same config on both sides -> drift, not effect
        var drift = rounds.Select(r => { r.ConfigSha = "aaaaaaaa11"; return r; }).ToList();
        var v3 = History.CompareNewestTwo(drift);
        Check(v3.Any(l => l.Contains("状态波动")) && !v3.Any(l => l.Contains("超过了噪声底")),
            "same-sha groups are labelled as state drift, never as a parameter effect");

        // confounded conditions -> not attributable
        var conf = new List<RoundSummary>(rounds) { [2] = RS("a3", "cccccccc33", "30 秒限时", 420, 26, "2026-10-21 16:58:05", 76, 102, 0.3) };
        var v4 = History.CompareNewestTwo(conf);
        Check(v4.Any(l => l.Contains("不能全归因")), "a setup change between groups kills the attribution");

        // open-loop availability marker
        var stale = new RoundSummary { File = "x", ConfigSha = "a", Quality = { "IMPLIANT: 49 flicks ended on the ball yet their reconstructed path only reached 82% of the distance - the reconstruction is losing pointer travel, so max_extent/prim numbers this round are lower bounds" } };
        var stale2 = new RoundSummary { File = "y", ConfigSha = "a", Quality = { "IMPLIANT: 59 flicks had no second surge yet their initial impulse is reported -26.0% off the target" } };
        var fresh = new RoundSummary { File = "z", ConfigSha = "a" };
        Check(!stale.OpenLoopUsable && !stale2.OpenLoopUsable && fresh.OpenLoopUsable,
            "both broken-build eras are recognised by their guard texts; a clean round is usable");

        // ---- archive round trip: files move, index stays consistent, nothing is deleted ----
        var dir = Path.Combine(Path.GetTempPath(), "raui-archtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        TestLog.IndexOverride = dir;
        try
        {
            var f1 = Path.Combine(dir, "round-arch-1.log");
            var f2 = Path.Combine(dir, "round-arch-2.log");
            File.WriteAllText(f1, "log one");
            File.WriteAllText(f2, "log two");
            var idx = Path.Combine(dir, "index.csv");
            File.WriteAllLines(idx, new[]
            {
                "file;when;configSha",
                "round-arch-1.log;2026-10-20 23:31:00;aaaaaaaa11",
                "round-arch-2.log;2026-10-21 16:56:48;aaaaaaaa11",
                "round-gone.log;2026-10-19 10:00:00;aaaaaaaa11",   // dangling from an older manual delete
            });

            var res = LogArchive.Archive(new[] { f1, Path.Combine(dir, "missing.log") });
            Check(res.Moved == 1 && res.Failed.Count == 1, $"one file moved, the missing one is reported: {res.Moved} moved / {res.Failed.Count} failed");
            Check(!File.Exists(f1), "the archived file is gone from logs\\");
            var archived = Path.Combine(res.ArchiveDir, "round-arch-1.log");
            Check(File.Exists(archived) && File.ReadAllText(archived) == "log one",
                "and it is recoverable in the archive folder");
            Check(File.Exists(f2), "the file that was not selected stays put");
            var lines = File.ReadAllLines(idx);
            Check(lines.Length == 2 && lines[1].StartsWith("round-arch-2.log"),
                $"index.csv kept only the row whose file still exists ({string.Join(" / ", lines.Skip(1))}) - the dangling row went too");
        }
        finally
        {
            TestLog.IndexOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- bilingual notes: generated together, so neither language can drift ----
        var st = RoundStats.Compute(new List<Trial>
        {
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 70, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 71, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 69, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 70, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 70, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 71, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 70, PrimaryRadialPct = 0, SampleCount = 9 },
            new() { DistancePx = 700, Hit = true, HasPrimary = true, MaxExtentPct = 70, PrimaryRadialPct = 0, SampleCount = 9 },
        }, 26, 700);
        Check(st.Notes.Count == st.Quality.Count && st.Notes.All(n => n.Zh.Length > 0),
            $"every quality note has both languages ({st.Notes.Count} notes)");
        var guard = st.Notes.FirstOrDefault(n => n.Warn);
        Check(guard != null && guard.Zh.Contains("行程") && guard.En.Contains("losing pointer travel"),
            "the travel guard speaks both languages");
        Check(st.Notes.Any(n => !n.Warn && n.Zh.Contains("距离桶")),
            $"the one-bucket note is present in Chinese: {string.Join(" | ", st.Notes.Select(n => n.Zh))}");
        Check(st.Notes.Any(n => n.Zh.Contains("8 次点击")),
            "numbers are interpolated into the Chinese text, not just the English one");
    }
}
