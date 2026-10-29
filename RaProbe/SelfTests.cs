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

        Console.WriteLine("\n== A. TargetLayout: multi-ball geometry and click attribution ==");
        Section("layout", Layout);

        Console.WriteLine("\n== B. ScoreHistory: the ball-test table the UI shows ==");
        Section("scoring", Scoring);

        Console.WriteLine("\n== C. Json.Validate: nothing absurd reaches the driver ==");
        Section("validate", Validate);

        Console.WriteLine("\n== D. Target attribution and respawn geometry ==");
        Section("wire", Wire);

        Console.WriteLine("\n== E. Session rules: clear N balls vs beat the clock ==");
        Section("rules", Rules);

        Console.WriteLine("\n== F. Device entries through the driver blob and settings.json ==");
        Section("devices", DeviceWire);

        Console.WriteLine("\n== G. Presets: names, hashes, and which slot the driver actually uses ==");
        Section("presets", PresetChecks);

        Console.WriteLine("\n== H. CurveAnalysis: where the curve leaves its own baseline ==");
        Section("curve", CurveChecks);

        Console.WriteLine("\n== I. CurveSampler: the points the curve page actually plots ==");
        Section("sampler", SamplerChecks);

        Console.WriteLine("\n== J. SettingsFile: the official path is found, never assumed ==");
        Section("settings", SettingsChecks);

        Console.WriteLine("\n== K. Drills: where targets go and what a point is worth ==");
        Section("drills", DrillChecks);

        Console.WriteLine("\n== L. Calibration: the turn, the conversion, and the crosshair ==");
        Section("calibration", CalibrationChecks);

        Console.WriteLine("\n== M. Curve lab: candidates, flick consistency, the blind schedule ==");
        Section("curvelab", CurveLabChecks);

        Console.WriteLine("\n== N. Session archive: a result that survives the window ==");
        Section("archive", ArchiveChecks);

        Console.WriteLine("\n== O. Sensitivity: output DPI vs the DPI on the mouse ==");
        Section("dpi", DpiChecks);

        Console.WriteLine("\n== P. Ladder: five narrowing stages instead of one wide guess ==");
        Section("ladder", LadderChecks);

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

        // the score row's only geometry claim: how far the click sat from the ball centre
        Check(TargetLayout.Error(1100, 500, 1080, 500) == 20, "20px past the centre is a 20px error");
        Check(Math.Abs(TargetLayout.Error(1040, 540, 1080, 500) - 56.5685) < 1e-3,
            $"a sideways miss is the real gap, not one axis component ({TargetLayout.Error(1040, 540, 1080, 500):0.0}px)");
        Check(TargetLayout.Error(1080, 500, 1080, 500) == 0, "an exact hit scores zero error");
    }

    // ---------- shared helpers for the JSON / validation sections ----------

    static ProfileDto BaseProfile() => new()
    {
        Name = "default",
        X = new AccelDto { Mode = "classic", Gain = true, InputOffset = 3, Acceleration = 0.06, ExponentClassic = 2, CapMode = "output", Cap = new Vec2Dto { X = 15, Y = 1.97 } },
        Y = new AccelDto { Mode = "classic", Gain = true, InputOffset = 2.5, Acceleration = 0.15, ExponentClassic = 2, CapMode = "output", Cap = new Vec2Dto { X = 0, Y = 1.83 } },
        Speed = new SpeedDto { Whole = false, LpNorm = 2 },
        OutputDpi = 1000,
    };

    /// <summary>A document with just the baseline profile - what most sections validate against.</summary>
    static RootDto BaseDoc()
    {
        var r = new RootDto();
        r.Profiles.Add(BaseProfile());
        return r;
    }

    // ---------- C ----------

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

        Console.WriteLine("   -- parity with upstream rawaccel-validate.hpp --");

        // The class of bug that actually costs a mouse: the driver takes these numbers, so a value
        // it rejects (or that multiplies the pointer by zero) must never leave user space.
        var out0 = BaseDoc(); out0.Profiles[0].OutputDpi = 0;
        Check(Json.Validate(out0).Any(p => p.Contains("输出 DPI")), "output DPI 0 is caught (it zeroed every movement)");
        var yx0 = BaseDoc(); yx0.Profiles[0].YxRatio = 0;
        Check(Json.Validate(yx0).Any(p => p.Contains("Y/X")), "Y/X ratio 0 is caught");
        var lr0 = BaseDoc(); lr0.Profiles[0].LrRatio = 0;
        Check(Json.Validate(lr0).Any(p => p.Contains("L/R")), "L/R ratio 0 is caught");
        var dom0 = BaseDoc(); dom0.Profiles[0].Domain.Y = 0;
        Check(Json.Validate(dom0).Any(p => p.Contains("域权重")), "a zero domain weight is caught");
        var lp0 = BaseDoc(); lp0.Profiles[0].Speed.LpNorm = 0;
        Check(Json.Validate(lp0).Any(p => p.Contains("lpNorm")), "lpNorm 0 is caught");
        var snap60 = BaseDoc(); snap60.Profiles[0].Snap = 60;
        Check(Json.Validate(snap60).Any(p => p.Contains("角度吸附")), "snap above the driver's 45 degrees is caught");
        var exp1 = BaseDoc(); exp1.Profiles[0].X.ExponentClassic = 1;
        Check(Json.Validate(exp1).Any(p => p.Contains("exponentClassic")), "classic exponent 1 is caught");
        var acc0 = BaseDoc(); acc0.Profiles[0].X.Acceleration = 0;
        Check(Json.Validate(acc0).Any(p => p.Contains("acceleration")), "acceleration 0 is caught (the driver requires > 0)");
        var capUnderOffset = BaseDoc();
        capUnderOffset.Profiles[0].X.InputOffset = 5;
        capUnderOffset.Profiles[0].X.Cap.X = 2;
        capUnderOffset.Profiles[0].X.CapMode = "in_out";
        Check(Json.Validate(capUnderOffset).Any(p => p.Contains("低于输入阈值")), "a cap below the offset (empty accel band) is caught");
        var lutShort = BaseDoc();
        lutShort.Profiles[0].X.Mode = "lut";
        lutShort.Profiles[0].X.Data.Clear();
        Check(Json.Validate(lutShort).Any(p => p.Contains("lut")), "a lut mode with no points is caught");
        var longName = BaseDoc(); longName.Profiles[0].Name = new string('n', 300);
        Check(Json.Validate(longName).Any(p => p.Contains("截断")), "a profile name the driver field would cut is caught");
        var zeroTime = BaseDoc();
        zeroTime.Devices.Add(new DeviceDto { Id = "HID\\VID_1234&PID_0003", Name = "m3", Profile = "default",
            Config = new DeviceConfigDto { MinimumTime = 0 } });
        Check(Json.Validate(zeroTime).Any(p => p.Contains("minimumTime")), "minimumTime 0 is caught on a device");
        var defClamp = BaseDoc();
        defClamp.DefaultDeviceConfig.MinimumTime = 5; defClamp.DefaultDeviceConfig.MaximumTime = 1;
        Check(Json.Validate(defClamp).Any(p => p.Contains("默认")), "an inverted default clamp is caught");

        // whole mode: upstream valid() does not look at the Y axis at all, so neither do we -
        // blocking it would reject a file the official GUI writes without complaint.
        var wholeIgnoresY = BaseDoc();
        wholeIgnoresY.Profiles[0].Speed.Whole = true;
        wholeIgnoresY.Profiles[0].Y.Acceleration = 0;
        wholeIgnoresY.Profiles[0].Y.ExponentClassic = 1;
        Check(!Json.Validate(wholeIgnoresY).Any(p => p.Contains(".Y.")),
            "unused Y parameters are not policed in whole mode (matches the driver)");

        // A guard is only worth having if it still says yes to the file this machine actually runs.
        var live = SettingsFile.Find();
        if (live != null)
        {
            var onDisk = Json.Deserialize(File.ReadAllText(live));
            var problems = Json.Validate(onDisk);
            Check(problems.Count == 0, $"the settings.json in use still passes: {string.Join("；", problems)}");
            var o = Modifier.EvaluateOnce(Convert4.ToStruct(onDisk.Profiles[0]), new vec2d { x = 4, y = 4 }, 0.5);
            Check(o.x != 0 || o.y != 0, "and a real movement through it still produces output");
        }
        else Check(true, "no settings.json on this machine: live-file check skipped");
    }

    // ---------- D ----------

    static void Wire()
    {
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

    // ---------- F ----------

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

    // ---------- E ----------

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

    // ---------- B ----------

    static void Scoring()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raui-scoretest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var prior = ScoreHistory.DirOverride;
        ScoreHistory.DirOverride = dir;          // never write test rows into the user's real history
        try
        {
            Check(ScoreHistory.Recent(10).Count == 0, "an empty history reads back as empty, not as an error");

            ScoreHistory.Append(Row("2026-10-20 09:00:00", 26, 30, 45_000, "aaaaaaaaaaaa"));
            ScoreHistory.Append(Row("2026-10-20 09:05:00", 30, 52, 60_000, "bbbbbbbbbbbb"));
            var rows = ScoreHistory.Recent(10);
            Check(rows.Count == 2, $"two rounds appended, both read back ({rows.Count})");
            Check(rows[0].When == "2026-10-20 09:05:00", "the newest round is first, which is what the table shows");
            Check(Math.Abs(rows[0].HitRate - 57.6923) < 0.01 && Math.Abs(rows[1].HitRate - 86.667) < 0.01,
                  $"hit rate is cleared/clicks, not cleared/goal ({rows[0].HitRate:0.0}% vs {rows[1].HitRate:0.0}%)");
            Check(rows[0].SessionMs == 60_000 && rows[0].Balls == 3 && rows[0].RadiusPx == 26,
                  $"the setup travels with the score (ms {rows[0].SessionMs:0}, balls {rows[0].Balls}, r {rows[0].RadiusPx:0})");

            var file = ScoreHistory.FilePath;
            var lines = File.ReadAllLines(file);
            Check(lines[0].StartsWith("时间;"), "the header is Chinese, so the file opens in any editor without this app");
            Check(lines.Length == 3, $"exactly one line per round plus the header ({lines.Length} lines)");

            // a hand-edited or half-written line must cost one row, not the whole table
            File.AppendAllText(file, "this line is not a record" + Environment.NewLine);
            Check(ScoreHistory.Recent(10).Count == 2, "a malformed line is skipped instead of dropping the history");

            // ';' separates fields, so a plan name carrying one must not shift the columns
            var tricky = Row("2026-10-20 09:10:00", 10, 12, 20_000, "cccccccccccc");
            tricky.PlanName = "压枪; 试验";
            tricky.Resolution = "3440x1440@180";
            ScoreHistory.Append(tricky);
            var newest = ScoreHistory.Recent(1)[0];
            Check(newest.PlanName == "压枪, 试验" && newest.Resolution == "3440x1440@180",
                  $"a separator inside a name is escaped and the columns stay put ({newest.PlanName})");
            Check(newest.Label().Contains("命中率 83%") && newest.Label().Contains("点掉 30 球")
                  && !newest.Label().Contains("cccccccccccc"),
                  $"a round reads as plain text, with no hex code in the line: {newest.Label()}");

            ScoreHistory.Clear();
            Check(!File.Exists(file), "Clear removes the file instead of leaving an orphan header");
        }
        finally
        {
            ScoreHistory.DirOverride = prior;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static RoundRow Row(string when, int cleared, int clicks, double ms, string sha) => new()
    {
        When = when, Mode = "点掉 30 球", Cleared = cleared, Clicks = clicks, SessionMs = ms,
        Balls = 3, DistancePx = 420, RadiusPx = 26, Direction = "全向",
        Resolution = "3440x1440@180", PlanName = "压枪 1.25", ConfigSha = sha,
    };

    // ---------- G ----------

    static void PresetChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raui-presets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var prior = Presets.DirOverride;
        Presets.DirOverride = dir;      // never let a test move a real preset
        try
        {
            // names double as file names, so nothing that could walk out of the library survives
            Check(Presets.SafeName("  My Curve/v1 ") == "My Curvev1", $"spaces are kept, separators dropped: '{Presets.SafeName("  My Curve/v1 ")}'");
            Check(Presets.SafeName("../../etc") == "etc", "a traversal attempt collapses to a plain word");
            Check(Presets.SafeName("###") == "", "pure punctuation yields an empty name, which Save then refuses");
            Check(Presets.SafeName(new string('x', 90)).Length == 40, "a long name is cut, not rejected mid-write");

            var p = new ProfileDto { Name = "default", YxRatio = 1.25 };
            p.Y.InputOffset = 2.5;
            var file = Presets.Save("V125", p, out string err);
            Check(file != null && err == "", "a clean profile saves as a preset");
            var list = Presets.List();
            Check(list.Count == 1 && list[0].Name == "V125", "the library lists it by name");
            Check(list[0].Sha == ConfigHash.Of(p), "the preset carries the same hash the driver reports for it");
            Check(list[0].Summary.Contains("纵向输出整体 ×1.25") && list[0].Summary.Contains("纵向：2.5 起加速"),
                  $"the summary is plain Chinese, not a hex code: {list[0].Summary}");

            var back = Presets.TryLoad(file!);
            Check(back != null && Math.Abs(back.YxRatio - 1.25) < 1e-12 && back.Name == "default",
                  "the file keeps the profile's own name, so its sha is the sha the driver will report");

            var bad = new ProfileDto { Name = "" };
            Check(Presets.Save("bad", bad, out string err2) == null && err2.Contains("名字"),
                  "an unusable profile is refused at save time, not at write-to-driver time: " + err2);

            // the wire semantics the preset bar is built on
            var doc = new RootDto();
            doc.Profiles.Add(new ProfileDto { Name = "default" });
            doc.Profiles.Add(new ProfileDto { Name = "V" });
            doc.Devices.Add(new DeviceDto { Id = @"HID\VID_1", Profile = "V" });
            Check(Presets.TargetIndex(doc, @"HID\VID_1") == 1, "a listed device follows its own profile reference");
            Check(Presets.TargetIndex(doc, @"HID\VID_2") == 0, "an unlisted device falls back to the first profile");
            doc.Devices[0].Profile = "gone";
            Check(Presets.TargetIndex(doc, @"HID\VID_1") == 0, "a dangling reference falls back instead of throwing");
            Check(Presets.TargetIndex(doc, null) == 0, "no device id at all still resolves to a writable slot");

            Check(Presets.Delete("V125", out _) && Presets.List().Count == 0, "delete takes it out of the library");
            Check(!Presets.Delete("V125", out string gone) && gone.Contains("没有"), "deleting twice reports it instead of pretending");

            // a hand-written note wins over the generated one, and delete cleans up both files
            Check(Presets.Save("压枪 1.25", p, out _, "只把纵向输出乘 1.25，横向不动") != null,
                "a Chinese preset name survives the file-name sanitizer");
            var named = Presets.List().FirstOrDefault(x => x.Name == "压枪 1.25");
            Check(named != null && named.Summary == "只把纵向输出乘 1.25，横向不动" && named.NoteFile.EndsWith(".txt"),
                $"the sidecar note is what the button shows: '{named?.Summary}'");
            Check(Presets.Delete("压枪 1.25", out _) && !File.Exists(named!.NoteFile),
                "deleting a preset also removes its note file");

            // renaming: the complaint was that changing a name only ever produced a second copy
            Check(Presets.Save("旧手感", p, out _, "最初的曲线") != null, "a preset to rename");
            string shaBefore = Presets.List().First(x => x.Name == "旧手感").Sha;

            Check(Presets.Rename("旧手感", "手腕流 0.06", out string re) && re == "", "a rename succeeds");
            var renamed = Presets.List();
            Check(renamed.Count == 1, $"and it stays ONE preset, not two ({renamed.Count})");
            Check(renamed[0].Name == "手腕流 0.06" && renamed[0].Sha == shaBefore,
                "the name changed and the curve did not - so it is still the same curve, relabelled");
            Check(renamed[0].Summary == "最初的曲线" && renamed[0].NoteFile.EndsWith("手腕流 0.06.txt"),
                "its note travels with it instead of being orphaned under the old name");
            Check(!File.Exists(Path.Combine(dir, "旧手感.json")), "the old file is gone, not left as a duplicate");

            var loaded = Presets.TryLoad(renamed[0].File);
            Check(loaded != null && Math.Abs(loaded.YxRatio - 1.25) < 1e-12 && loaded.Name == "default",
                "the profile inside is untouched, including the name the driver resolves by");

            Check(Presets.Save("另一条", new ProfileDto { Name = "default", YxRatio = 2 }, out _) != null, "a second preset to collide with");
            Check(!Presets.Rename("手腕流 0.06", "另一条", out string clash) && clash.Contains("已经有"),
                "renaming onto an existing name is refused, not silently merged");
            Check(Presets.List().Count == 2, "and nothing was lost in the attempt");
            Check(!Presets.Rename("手腕流 0.06", "///", out string empty) && empty.Contains("没留下"),
                "a name with no usable characters is refused");
            Check(!Presets.Rename("不存在的方案", "随便", out string missing) && missing.Contains("没有"),
                "renaming a preset that is not there says so");
            Check(Presets.Rename("手腕流 0.06", "手腕流 0.06 2", out _) && Presets.List().Count == 2,
                "a rename to a free name still works after the failures");
        }
        finally { Presets.DirOverride = prior; try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- H ----------

    static void CurveChecks()
    {
        var plain = BaseProfile();
        var x = CurveAnalysis.Analyze(Convert4.ToStruct(plain), true);
        var y = CurveAnalysis.Analyze(Convert4.ToStruct(plain), false);
        Check(Math.Abs(x.Threshold - 3) < 0.05, $"X starts accelerating at its own offset: {x.Threshold:0.00} counts/ms");
        Check(Math.Abs(y.Threshold - 2.5) < 0.05, $"Y starts accelerating at its own offset: {y.Threshold:0.00} counts/ms");
        Check(x.PeakGain > 1.9 && x.PeakGain < 2.05, $"X caps at the configured ratio ({x.PeakGain:0.000} against cap 1.97)");

        // The output ratio is applied AFTER the accel stage, so it lifts the whole curve without
        // moving where the acceleration begins. Comparing the threshold against a hard-coded 1.0
        // used to report "0.00" for exactly the profiles that carry a vertical multiplier.
        var boosted = BaseProfile();
        boosted.YxRatio = 1.25;
        var bx = CurveAnalysis.Analyze(Convert4.ToStruct(boosted), true);
        var by = CurveAnalysis.Analyze(Convert4.ToStruct(boosted), false);
        Check(Math.Abs(bx.Threshold - x.Threshold) < 1e-6,
            $"a y/x ratio does not move the X threshold ({x.Threshold:0.00} -> {bx.Threshold:0.00})");
        Check(Math.Abs(by.Threshold - y.Threshold) < 1e-6,
            $"nor the Y one, only its baseline ({y.Threshold:0.00} -> {by.Threshold:0.00})");
        Check(Math.Abs(by.PeakGain - 1.25 * y.PeakGain) < 1e-3,
            $"Y's peak gain carries the multiplier ({y.PeakGain:0.000} -> {by.PeakGain:0.000})");

        var flat = BaseProfile();
        flat.X.Acceleration = 0; flat.X.Cap.X = 0; flat.X.Cap.Y = 0;
        flat.Y.Acceleration = 0; flat.Y.Cap.X = 0; flat.Y.Cap.Y = 0;
        var fx = CurveAnalysis.Analyze(Convert4.ToStruct(flat), true);
        Check(fx.Threshold == 0 && double.IsInfinity(fx.CapSpeed) && Math.Abs(fx.PeakGain - 1) < 1e-6,
            "a profile with no acceleration reads as no acceleration (threshold 0, no cap, gain 1)");
    }

    // ---------- K ----------

    static void DrillChecks()
    {
        var area = DrillField.Usable(1920, 1080);
        Check(Math.Abs(area.X - 48) < 1e-9 && Math.Abs(area.Y - 96) < 1e-9
              && Math.Abs(area.W - 1824) < 1e-9 && Math.Abs(area.H - 936) < 1e-9,
            "the usable area keeps clear of the banner and the edges");

        var grid = DrillField.Grid(area, 6, 3);
        Check(grid.Length == 18, "gridshot lattice is cols x rows cells (18)");
        Check(grid.All(p => area.Fits(p.x, p.y, 22)), "every cell can hold a 22px target");
        Check(grid.Distinct().Count() == grid.Length, "no two cells are the same point");
        Check(Math.Abs(grid[0].x - (area.X + area.W / 12)) < 1e-9,
            "the first cell is inset by half a cell, not flush with the edge");
        double stepX = grid[1].x - grid[0].x, stepY = grid[6].y - grid[0].y;
        Check(grid.Take(6).All(p => Math.Abs((p.x - grid[0].x) % stepX) < 1e-6) && stepX > 22 * 2,
            "columns are evenly spaced further apart than a target is wide");
        Check(stepY > 22 * 2, "rows are far enough apart not to touch");

        var rngSix = new Random(7);
        var six = DrillField.Six(area, rngSix, 22);
        Check(six.Length == 6, "sixshot places six targets");
        Check(six.All(p => area.Fits(p.x, p.y, 22)), "all six are inside the usable area");

        double sixClosest = double.MaxValue;
        for (int i = 0; i < six.Length; i++)
            for (int j = i + 1; j < six.Length; j++)
                sixClosest = Math.Min(sixClosest,
                    Math.Sqrt(Math.Pow(six[i].x - six[j].x, 2) + Math.Pow(six[i].y - six[j].y, 2)));
        Check(sixClosest > 2 * 22 + 80,
            $"no two of the six are close enough to take with one click ({sixClosest:0} px apart)");

        // one per cell of a 3x2 lattice: spread out, but re-rolled rather than pinned to six dots
        var cellUse = new int[6];
        foreach (var p in six)
        {
            int c = Math.Clamp((int)Math.Floor((p.x - area.X) / (area.W / 3)), 0, 2);
            int r = Math.Clamp((int)Math.Floor((p.y - area.Y) / (area.H / 2)), 0, 1);
            cellUse[r * 3 + c]++;
        }
        Check(cellUse.All(n => n == 1), "one target per cell of the 3x2 lattice, every cell used");

        var sixSameSeed = DrillField.Six(area, new Random(7), 22);
        Check(sixSameSeed.SequenceEqual(six), "the same seed lays a set out identically, so a round can be replayed");
        var sixNextSeed = DrillField.Six(area, new Random(8), 22);
        bool sixMoved = false;
        for (int i = 0; i < six.Length; i++)
            if (Math.Abs(sixNextSeed[i].x - six[i].x) > 1 || Math.Abs(sixNextSeed[i].y - six[i].y) > 1) sixMoved = true;
        Check(sixMoved, "the next set is a different set - sixshot is not the same six flicks forever");

        Check(DrillInfo.MinRadiusPx(DrillKind.Tracking) > DrillInfo.MinRadiusPx(DrillKind.Gridshot),
            "the tracking target has a bigger floor than a click target: it is the thing being followed");
        Check(DrillInfo.MinRadiusPx(DrillKind.Gridshot) == 0, "and a click drill still honours whatever radius was asked for");

        // FreeCells must be a selection, never a repeat
        var rng = new Random(7);
        var picked = DrillField.FreeCells(18, new HashSet<int> { 0, 1, 2 }, 5, rng);
        Check(picked.Count == 5 && picked.Distinct().Count() == 5, "five free cells picked, all different");
        Check(!picked.Contains(0) && !picked.Contains(1) && !picked.Contains(2), "occupied cells are never offered");
        var full = DrillField.FreeCells(3, new HashSet<int> { 0, 1, 2 }, 2, rng);
        Check(full.Count == 0, "a full lattice yields nothing instead of squeezing a target in");

        // tracking motion: deterministic, and it must not leave the field
        var a2 = new DrillField.Area(0, 0, 100, 100);
        var (x1, y1, vx1, _) = DrillField.StepTracker(50, 50, 1000, 0, 1000, a2, 5);
        Check(Math.Abs(x1 - 95) < 1e-9 && vx1 < 0, "a target that leaves the right edge is put back inside");
        var (x2, y2, _, _) = DrillField.StepTracker(50, 50, 1000, 0, 1000, a2, 5);
        Check(x1 == x2 && y1 == y2, "same state and step give the same position");
        bool stayed = true;
        double px = 50, py = 50, pvx = 900, pvy = -700;
        for (int i = 0; i < 2000; i++)
        {
            (px, py, pvx, pvy) = DrillField.StepTracker(px, py, pvx, pvy, 16, a2, 5);
            if (!a2.Fits(px, py, 5)) { stayed = false; break; }
        }
        Check(stayed, "2000 steps of tracking never leaves the field");

        // scoring
        var gridRun = new DrillRun(DrillKind.Gridshot, 60);
        gridRun.Advance(30_000);
        for (int i = 0; i < 10; i++) gridRun.Click(i * 40, i * 25, i < 7);
        Check(gridRun.Shots == 10 && gridRun.Hits == 7, "clicks and hits are counted separately");
        Check(Math.Abs(gridRun.HitRatePct - 70) < 1e-9, "hit rate is hits over clicks (70%)");
        Check(Math.Abs(gridRun.HitsPerMinute - 14) < 1e-9, "7 hits in 30s is 14 per minute");
        Check(!gridRun.Finished, "30s of a 60s round is not finished");
        gridRun.Advance(30_000);
        Check(gridRun.Finished && Math.Abs(gridRun.ElapsedMs - 60_000) < 1e-9, "the round ends on the clock, exactly");
        gridRun.Click(400, 250, true);
        Check(gridRun.Hits == 7, "a click after the whistle does not count");

        var sixRun = new DrillRun(DrillKind.Sixshot, 60);
        for (int i = 0; i < 4; i++) sixRun.Click(i * 60, 120, true);
        sixRun.Click(240, 120, true, completesSet: true);
        sixRun.Click(300, 240, true, completesSet: true);
        Check(sixRun.SetsCleared == 2 && sixRun.Hits == 6, "a set is only counted when its last target falls");
        sixRun.Advance(20_000);
        Check(Math.Abs(sixRun.SecondsPerSet - 10) < 1e-9, "two sets in 20s is 10s per set");

        var trackRun = new DrillRun(DrillKind.Tracking, 30);
        trackRun.Tick(1000, true, true);
        trackRun.Tick(1000, true, false);
        trackRun.Tick(500, false, true);
        Check(Math.Abs(trackRun.HeldMs - 2000) < 1e-9, "released time is not measured");
        Check(Math.Abs(trackRun.OnTargetMs - 1000) < 1e-9, "only on-target time with the button down counts");
        Check(Math.Abs(trackRun.LongestOnTargetMs - 1000) < 1e-9, "the unbroken stretch ends when the view leaves");
        Check(Math.Abs(trackRun.OnTargetPct - 50) < 1e-9, "half the held time on target reads as 50%");
        Check(!trackRun.Score().Contains("NaN"), "the score line stays printable with a zero denominator");

        var empty = new DrillRun(DrillKind.Tracking, 30);
        Check(empty.OnTargetPct == 0 && !empty.Score().Contains("NaN"), "no clicks at all does not divide by zero");
    }

    // ---------- L ----------

    static void CalibrationChecks()
    {
        var t = new TurnMeasure { Sensitivity = 1.5, Dpi = 600 };
        t.Add(1000);
        t.Add(-400);
        t.Add(100);
        Check(t.Counts == 700 && t.Packets == 3, "counts add up and every packet is counted");
        Check(Math.Abs(t.AngleDeg - 700 * 0.022 * 1.5) < 1e-9, "angle is counts x m_yaw x sensitivity");
        Check(Math.Abs(t.Cm - 700 / 600.0 * 2.54) < 1e-9, "cm is counts / DPI x 2.54");
        Check(!t.Full, "700 counts at 1.5 is nowhere near a full turn");
        t.Reset();
        Check(t.Counts == 0 && t.Packets == 0 && t.AngleDeg == 0, "reset clears both the counts and the packet count");

        // a full turn, then one more click, must read as full and stay printable
        var full = new TurnMeasure { Sensitivity = 1.5, Dpi = 600 };
        full.Add((long)Math.Ceiling(360 / (0.022 * 1.5)));
        Check(full.Full && full.Progress >= 1.0, "360 degrees' worth of counts completes the dial");
        Check(Math.Abs(full.CmPer360Implied - SourceAim.CmPer360(1.5, 600)) < 1e-9,
            "the implied cm/360 is the same conversion the rest of the app uses");
        Check(Math.Abs(full.Cm - Math.Abs(full.CmPer360Implied)) < 0.5,
            "the distance the dial took and the implied cm/360 are the same number, as they must be");

        // the two conversions have to be exact inverses or the calibration window would lie
        double back = SourceAim.CmPer360(SourceAim.SensForCm360(30, 800), 800);
        Check(Math.Abs(back - 30) < 1e-9, "sens for 30cm/360 round-trips back to 30cm/360");
        double dpiBack = SourceAim.DpiForCm360(SourceAim.CmPer360(2.0, 1600), 2.0);
        Check(Math.Abs(dpiBack - 1600) < 1e-6, "a real cm/360 recovers the DPI it was measured at");
        Check(SourceAim.SensForCm360(30, 0) == 0 && SourceAim.DpiForCm360(0, 1.5) == 0,
            "zero DPI or zero cm/360 returns zero, not infinity");

        var c = CrosshairGeometry.Place(960, 700, 9, 3, 1.0);
        Check(Math.Abs(c.Thickness - 2) < 1e-9, "the crosshair is 2 physical pixels thick");
        Check(Math.Abs(c.Left.X - 948) < 1e-9 && Math.Abs(c.Right.X - 963) < 1e-9,
            "arms sit a gap away from the centre on both sides");
        Check(Math.Abs(c.Top.Y - 688) < 1e-9 && Math.Abs(c.Bottom.Y - 703) < 1e-9,
            "vertical arms are the same distance out as the horizontal ones");
        Check(Math.Abs(c.Dot.X - 959) < 1e-9 && Math.Abs(c.Dot.Y - 699) < 1e-9
              && Math.Abs(c.Dot.W - 2) < 1e-9, "the dot is centred on the pointer and the same size as the arms");
        Check(Math.Abs(c.Left.W - 9) < 1e-9, "arm length is the number the player typed");

        var s2 = CrosshairGeometry.Place(960, 700, 9, 3, 2.0);
        Check(Math.Abs(s2.Left.X - 474) < 1e-9 && Math.Abs(s2.Left.W - 4.5) < 1e-9
              && Math.Abs(s2.Thickness - 1) < 1e-9,
            "on a 200% display the same crosshair halves in DIPs and covers the same physical pixels");

        var noArm = CrosshairGeometry.Place(100, 100, 0, 3, 1.0);
        Check(noArm.Left.W == 0 && noArm.Dot.W == 2, "arm length 0 leaves only the dot");
    }

    // ---------- I ----------

    static void SamplerChecks()
    {
        var prof = Convert4.ToStruct(BaseProfile());
        var pts = CurveSampler.Sample(prof, chartDpi: 0, normalized: false);

        Check(pts.Length == CurveSampler.SampleCount,
            $"{CurveSampler.SampleCount} points, no more and no fewer ({pts.Length})");
        Check(Math.Abs(pts[^1].Axis - 30) < 1e-9,
            $"without a chart DPI the axis ends at 30 counts/ms ({pts[^1].Axis:0.###})");
        Check(Math.Abs(CurveSampler.Sample(prof, 800, false)[^1].Axis - 40) < 1e-9,
            "with a chart DPI it ends at 40 in/s");

        bool rising = true;
        for (int i = 1; i < pts.Length; i++) if (pts[i].Axis <= pts[i - 1].Axis) rising = false;
        Check(rising, "the axis is strictly increasing, so the polyline never doubles back");
        Check(pts.All(s => double.IsFinite(s.Sensitivity) && double.IsFinite(s.Gain)),
            "no NaN or Infinity reaches the plot: one bad field would blank the whole chart");

        // Below the 3 counts/ms offset the multiplier is exactly 1. CurveAnalysis claims the same
        // thing about the threshold; this checks it from the points that actually get drawn.
        Check(Math.Abs(pts[0].Sensitivity - 1) < 1e-6,
            $"the first point sits below the offset, so it reads 1.00 ({pts[0].Sensitivity:0.000000})");
        Check(Math.Abs(pts[0].Gain - 1) < 1e-3,
            $"and so does the slope there ({pts[0].Gain:0.0000})");

        // The cap is 1.97 on the GAIN, not on the sensitivity: sensitivity is out/in, the average
        // multiplier over the whole sweep, while gain is d(out)/d(in), the marginal one at that
        // speed. An always-rising curve is steeper at the tip than it is on average, so the two
        // series must differ - if they came out equal, the gain view would be plotting the same
        // numbers twice. (Measured: 1.742 vs 1.97.)
        double maxGain = pts.Max(s => s.Gain);
        double maxSens = pts.Max(s => s.Sensitivity);
        Check(Math.Abs(maxGain - 1.97) < 0.01, $"the output cap holds the gain at 1.97 ({maxGain:0.000})");
        Check(maxGain <= 1.97 + 1e-9, "and nothing plots above it");
        Check(maxSens > 1.1 && maxSens < maxGain - 1e-6,
            $"the sensitivity series stays under the gain series ({maxSens:0.000} vs {maxGain:0.000})");

        var flat = BaseProfile();
        flat.X.Acceleration = 0; flat.X.Cap.X = 0; flat.X.Cap.Y = 0;
        flat.Y.Acceleration = 0; flat.Y.Cap.X = 0; flat.Y.Cap.Y = 0;
        var flatPts = CurveSampler.Sample(Convert4.ToStruct(flat), 0, false);
        Check(flatPts.All(s => Math.Abs(s.Sensitivity - 1) < 1e-9),
            "a profile with no acceleration plots a flat 1.00 everywhere");

        // The display axis is the only place DPI enters, and getting it backwards shifts every
        // marker on the chart. 800 dpi with normalization off: 20 in/s is 16 counts/ms.
        Check(Math.Abs(CurveSampler.AxisToInternal(20, 800, false) - 16) < 1e-9, "20 in/s at 800 dpi is 16 counts/ms");
        Check(Math.Abs(CurveSampler.InternalToAxis(16, 800, false) - 20) < 1e-9, "and back again");
        Check(Math.Abs(CurveSampler.AxisToInternal(20, 800, true) - 20) < 1e-9,
            "with DPI normalization the driver already divided, so the axis is the internal speed");

        // The gain column costs two extra trips through the modifier per point, so the
        // sensitivity view skips it. Skipped must mean zero, not "whatever was in the array".
        var noGain = CurveSampler.Sample(prof, 0, false, withGain: false);
        Check(noGain.All(s => s.Gain == 0), "the sensitivity view does not pay for the gain column");
        Check(noGain.Select(s => s.Sensitivity).SequenceEqual(pts.Select(s => s.Sensitivity)),
            "and skipping it leaves the sensitivity values untouched");
    }

    // ---------- J ----------

    static void SettingsChecks()
    {
        var candidates = SettingsFile.Candidates();
        Check(candidates.Count >= 6, $"the official file is looked for in {candidates.Count} places, not one");
        Check(candidates.All(c => Path.IsPathRooted(c)), "every candidate is an absolute path");
        Check(candidates.Any(c => c.Contains("Raw Accel", StringComparison.OrdinalIgnoreCase)),
            "the official install layout is among them, so the two tools share one settings.json");
        Check(candidates.Any(c => c.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)),
            "a copy of the app sitting next to its own settings.json also works (portable folder)");

        // the override exists so a personal install layout can stay out of the source tree
        var withExtra = SettingsFile.Candidates(@"D:\Elsewhere\settings.json");
        Check(withExtra[0] == @"D:\Elsewhere\settings.json", "an explicit override is searched before every guess");
        Check(withExtra.Count == candidates.Count + 1 || withExtra.Count == candidates.Count,
            "the override adds a candidate instead of replacing the list");
        Check(SettingsFile.Candidates("  ").SequenceEqual(SettingsFile.Candidates(null)),
            "a blank override changes nothing");
        Check(SettingsFile.OverrideFile.EndsWith("settings-path.txt", StringComparison.OrdinalIgnoreCase)
              && SettingsFile.OverrideFile.StartsWith(AppData.Root, StringComparison.OrdinalIgnoreCase),
              $"the override is read from the data folder, not from the repo: {SettingsFile.OverrideFile}");

        var found = SettingsFile.Find();
        Check(found == null || File.Exists(found), "Find() either names a file that exists or says nothing");

        var def = SettingsFile.Default();
        Check(def.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase),
            $"the UI always has a settings path to save to: {def}");
        Check(Directory.Exists(Path.GetDirectoryName(def)),
            "and its folder really exists, so the first save cannot fail on a missing directory");
    }

    // ---------- M ----------

    static ProfileDto ProfileWith(string mode, double acceleration = 0.005)
    {
        var p = new ProfileDto();
        p.X.Mode = mode;
        p.Y.Mode = mode;
        p.X.Acceleration = acceleration;
        p.Y.Acceleration = acceleration;
        return p;
    }

    /// <summary>
    /// What the official GUI would refuse to write about this profile. A candidate that validates as
    /// a profile alone can still be broken once it is the one a device points at, so the check goes
    /// through the whole document - same gate the app uses before anything reaches the driver.
    /// </summary>
    static List<string> Problems(ProfileDto p)
    {
        var root = new RootDto
        {
            Profiles = { p },
            Devices = { new DeviceDto { Name = "mouse", Id = "id-1", Profile = p.Name } },
        };
        return Json.Validate(root);
    }

    static void CurveLabChecks()
    {
        // --- which parameter a sweep touches, per mode ---
        foreach (var (mode, knob) in new[]
                 {
                     ("classic", 0.008), ("jump", 0.9), ("natural", 0.4),
                     ("synchronous", 3.2), ("power", 1.7),
                 })
        {
            var p = ProfileWith(mode);
            AccelCandidates.Set(p, knob);
            Check(Math.Abs(AccelCandidates.Get(p) - knob) < 1e-12,
                $"{mode}: writing the swept parameter and reading it back round-trips");
            Check(AccelCandidates.Sweepable(p), $"{mode}: has something worth sweeping");
        }

        var lut = ProfileWith("lut");
        lut.X.Data = new List<float> { 1.2f, 1.5f, 1.0f };
        double before = AccelCandidates.Get(lut);
        AccelCandidates.Set(lut, before * 2);
        Check(Math.Abs(AccelCandidates.Get(lut) - before * 2) < 1e-4,
            "lut: doubling the table amplitude doubles the distance from 1.0, not the values");
        Check(Math.Abs(lut.X.Data[2] - 1.0f) < 1e-6,
            "lut: a ratio that was exactly 1.0 stays there, so the zero point does not move");

        Check(!AccelCandidates.Sweepable(ProfileWith("noaccel")), "noaccel has no parameter to sweep");
        Check(AccelCandidates.KnobFor("SYNCHRONOUS") == AccelCandidates.Knob.Amount,
            "the mode name is matched case-insensitively");

        // --- candidate list ---
        var baseProfile = ProfileWith("classic", 0.005);
        var cands = AccelCandidates.Build(baseProfile, new[] { 0.5, 1.0, 1.5, 2.0 }, includeNoAccel: true);
        Check(cands.Count == 5, "four multipliers plus the no-accel control");
        Check(cands.Count(c => c.IsBase) == 1, "exactly one candidate is the player's own curve");
        Check(cands[cands.FindIndex(c => c.IsBase)].KnobValue is > 0.0049 and < 0.0051,
            "the base candidate carries the original value, not a scaled one");
        Check(cands[1].KnobValue is > 0.0024 and < 0.0026, "a 0.5 multiplier halves the swept value");
        Check(cands[0].IsNoAccel && cands[0].Profile.X.Mode == "noaccel",
            "the control is a different mode, not a weak acceleration");
        Check(cands.All(c => Problems(c.Profile).Count == 0),
            "every candidate - including the no-accel control - is a document the driver would accept");
        Check(cands.All(c => c.BlindLabel == $"候选 {c.Index + 1}"),
            "every blind label is an index and nothing else - the value must not leak into the round banner");
        Check(cands.All(c => !c.BlindLabel.Contains(c.Secret)) && cands.All(c => c.Secret.Length > 0),
            "the secret text exists but is never the label");
        Check(baseProfile.X.Acceleration == 0.005, "building candidates does not mutate the live profile");
        Check(cands[2].Profile.Y.Mode == "classic" && Math.Abs(cands[2].Profile.Y.Acceleration - 0.005) < 1e-9,
            "both axes are copied, so a candidate cannot silently drop the vertical curve");

        // --- the sweep has to move both curves, not just the horizontal one ---
        // The first build only ever wrote X. On a whole-mode profile that is right, the driver never
        // reads Y; on a per-axis profile it meant 35 rounds where the vertical curve never moved and
        // the session measured half of what it claimed to compare.
        var both = AccelCandidates.Build(baseProfile, new[] { 1.0, 1.5 }, includeNoAccel: false);
        Check(Math.Abs(both[1].Profile.X.Acceleration - 0.0075) < 1e-9
              && Math.Abs(both[1].Profile.Y.Acceleration - 0.0075) < 1e-9,
            "a 1.5x sweep moves the vertical curve by 1.5x too, not only the horizontal one");

        var xOnly = AccelCandidates.Build(baseProfile, new[] { 1.0, 1.5 }, includeNoAccel: false,
                                          axis: AxisTarget.Horizontal);
        Check(Math.Abs(xOnly[1].Profile.Y.Acceleration - 0.005) < 1e-9,
            "asked for one axis only, the other one is left exactly as it was");

        // --- the wrist family: one strength, four consequences ---
        var wristBase = ProfileWith("classic", 0.06);
        wristBase.Speed.Whole = false;
        wristBase.X.InputOffset = 3.0;
        wristBase.Y.InputOffset = 2.5;
        wristBase.Y.Acceleration = 0.15;
        var wrist = AccelCandidates.BuildWrist(wristBase, new[] { 1.0, 2.0 }, includeNoAccel: false);
        var strong = wrist[1];
        Check(Math.Abs(strong.Profile.X.InputOffset - 6.0) < 1e-9,
            "wrist x2 pushes the horizontal threshold up: 3.0 -> 6.0, so small corrections stay 1:1");
        Check(Math.Abs(strong.Profile.Y.InputOffset - 1.25) < 1e-9,
            "and pulls the vertical threshold down: 2.5 -> 1.25, so a two-millimetre pull counts");
        Check(Math.Abs(strong.Profile.X.Acceleration - 0.12) < 1e-9
              && Math.Abs(strong.Profile.Y.Acceleration - 0.30) < 1e-9,
            "both axes get stronger, so the far end of a big flick still arrives");
        Check(wrist[0].IsBase && Math.Abs(wrist[0].Profile.X.InputOffset - 3.0) < 1e-9
              && Math.Abs(wrist[0].Profile.Y.InputOffset - 2.5) < 1e-9,
            "strength 1 is the player's own curve untouched, so 'yours was best' is reachable");
        Check(!strong.Profile.Speed.Whole,
            "the wrist family forces per-axis mode: in whole mode the driver never reads the vertical curve");
        Check(wrist.All(c => Problems(c.Profile).Count == 0),
            "every wrist candidate is still a profile the official GUI would write");

        var weak = AccelCandidates.BuildWrist(wristBase, new[] { 0.5 }, includeNoAccel: false)[0];
        Check(weak.Profile.X.InputOffset < 3.0 && weak.Profile.Y.InputOffset > 2.5,
            "half strength goes the other way: horizontal accelerates earlier, vertical later");

        // --- thresholds: where the multiplier leaves 1.0 ---
        var offP = ProfileWith("classic", 0.06);
        AccelCandidates.Set(offP, 7.0, AccelCandidates.Knob.Offset);
        Check(Math.Abs(AccelCandidates.Get(offP, AccelCandidates.Knob.Offset) - 7.0) < 1e-12,
            "the input-speed threshold round-trips through the same knob the wrist sweep turns");

        var jumpP = ProfileWith("jump", 0.9);
        AccelCandidates.Set(jumpP, 4.0, AccelCandidates.Knob.Offset);
        Check(Math.Abs(jumpP.X.Cap.X - 4.0) < 1e-12, "jump keeps its threshold in Cap.x");
        AccelCandidates.Set(jumpP, 1.4, AccelCandidates.Knob.Amount);
        Check(Math.Abs(jumpP.X.Cap.Y - 1.4) < 1e-12,
            "and its amount in Cap.y - the acceleration field is one the jump evaluator never reads");

        Check(!AccelCandidates.HasThreshold(ProfileWith("synchronous").X),
            "synchronous has no floor to move, so the wrist sweep leaves its threshold alone");

        var capped = ProfileWith("classic", 0.06);
        capped.X.CapMode = "input";
        capped.X.Cap.X = 5.0;
        AccelCandidates.Set(capped, 40.0, AccelCandidates.Knob.Offset);
        Check(capped.X.InputOffset < 5.0,
            "a threshold pushed past the input cap is stopped short of it instead of written as garbage");
        Check(Problems(capped).Count == 0, "and the clamped profile still validates");

        // --- flick consistency ---
        var even = new FlickStats();
        even.Add(150, 100); even.Add(400, 267); even.Add(800, 533); even.Add(1400, 933);
        Check(even.Consistency > 0.98, $"equal px/s in every band reads as consistent ({even.Consistency:0.000})");
        Check(even.BandSpeeds().Count(v => v > 0) >= 3, "every distance band that got a sample reports a speed");

        var lopsided = new FlickStats();
        lopsided.Add(150, 60); lopsided.Add(1400, 2000);   // 2500 px/s close in, 700 px/s far out
        Check(lopsided.Consistency < even.Consistency - 0.4,
            $"a long flick at a third of the short-flick speed scores far worse ({lopsided.Consistency:0.000} vs {even.Consistency:0.000})");
        Check(lopsided.Consistency >= 0, "consistency never goes negative");

        var single = new FlickStats();
        single.Add(300, 200);
        Check(single.Consistency == 0, "one band cannot be consistent with itself, and says so with 0");

        var emptyFlick = new FlickStats();
        Check(emptyFlick.Consistency == 0 && emptyFlick.OverallPxPerSec == 0,
            "no samples reports zero rather than NaN");
        Check(!emptyFlick.Describe().Contains("NaN"), "the description stays printable when empty");

        var ignores = new FlickStats();
        ignores.Add(-5, 100); ignores.Add(200, -1); ignores.Add(200, double.NaN);
        Check(ignores.Samples == 0, "negative distances and non-positive times are dropped, not scored");
        Check(FlickStats.BandOf(0) == 0 && FlickStats.BandOf(219) == 0 && FlickStats.BandOf(221) == 1,
            "band edges are upper bounds, so a distance lands in exactly one band");
        Check(FlickStats.BandOf(99999) == FlickStats.BandEdges.Length - 1, "the last band is open-ended");

        // --- schedule ---
        var explore = CalibPlan.Explore(5);
        Check(explore.Count == 25, "five candidates over five drills is 25 rounds");
        Check(explore.Select(r => (r.CandidateIndex, r.Item)).Distinct().Count() == 25,
            "every candidate meets every drill exactly once");
        Check(explore.Where((r, i) => i > 0 && r.CandidateIndex == explore[i - 1].CandidateIndex).Count() == 0,
            "no two consecutive rounds use the same candidate");
        Check(explore.Select(r => r.Ordinal).SequenceEqual(Enumerable.Range(1, 25)),
            "rounds are numbered without gaps");

        var exploreAgain = CalibPlan.Explore(5);
        Check(explore.SequenceEqual(exploreAgain), "the same seed gives the same order, so a bug can be reproduced");

        var final = CalibPlan.Final(1, 4);
        Check(final.Count == 10, "the head-to-head is two candidates over five drills");
        Check(final.Select(r => r.CandidateIndex).Distinct().OrderBy(x => x).SequenceEqual(new[] { 1, 4 }),
            "and it contains exactly those two");

        // --- ranking ---
        var results = new List<CalibPlan.RoundResult>();
        foreach (var r in explore)
        {
            // candidate 3 is best everywhere, candidate 0 is worst everywhere
            double score = r.CandidateIndex == 3 ? 100 : r.CandidateIndex == 0 ? 10 : 50 + r.CandidateIndex;
            results.Add(new CalibPlan.RoundResult
            {
                Round = r, Skill = score,
                Consistency = r.Item == DrillKind.Tracking ? double.NaN : 0.7,
            });
        }
        var rank = CalibPlan.Rank(results, 5);
        Check(rank.Leader == 3, "the candidate that won every drill leads");
        Check(Math.Abs(rank.MeanRank[3] - 1) < 1e-9, "winning everything is a mean rank of 1");
        Check(rank.Wins[3] == 5, "outright wins are counted per drill");
        Check(!double.IsNaN(rank.MeanConsistency[3]), "tracking's missing flick is skipped, not averaged in as zero");
        Check(rank.Order.Count == 5 && rank.Order.Distinct().Count() == 5, "every candidate appears once in the ordering");

        var thin = new List<CalibPlan.RoundResult>
        {
            new() { Round = explore.First(r => r.Item == DrillKind.Sixshot), Skill = 10 },
        };
        var thinRank = CalibPlan.Rank(thin, 5);
        Check(thinRank.Order.Count == 5, "candidates with no results still appear, they just rank last");

        // --- verdict ---
        // a head-to-head as the window would actually build it: the base and one opponent, five drills each
        List<CalibPlan.RoundResult> Head(int baseIdx, int otherIdx, double baseScore, double otherScore)
        {
            var list = new List<CalibPlan.RoundResult>();
            foreach (var r in CalibPlan.Final(baseIdx, otherIdx))
                list.Add(new CalibPlan.RoundResult
                {
                    Round = r,
                    Skill = r.CandidateIndex == baseIdx ? baseScore : otherScore,
                    Consistency = r.Item == DrillKind.Tracking ? double.NaN : 0.8,
                });
            return list;
        }

        int baseIdx = 1;                                        // candidate 1 is the player's own curve
        var win = CalibPlan.Decide(rank, Head(baseIdx, 3, 50, 60), baseIdx);
        Check(win.Recommend && win.WinnerIndex == 3 && !win.WinnerIsBase,
            "a candidate that wins the head-to-head by a clear margin is recommended");

        var tie = CalibPlan.Decide(rank, Head(baseIdx, 3, 50, 50.5), baseIdx);
        Check(!tie.Recommend && tie.WinnerIsBase && tie.WinnerIndex == baseIdx,
            "a 1% difference is not enough to tell somebody to change their curve");

        var loss = CalibPlan.Decide(rank, Head(baseIdx, 3, 50, 30), baseIdx);
        Check(!loss.Recommend && loss.Headline.Contains("先别改"),
            "losing the head-to-head keeps the player's own curve");

        // winning two drills by a mile and losing three narrowly is not a win: the margin cannot
        // outvote the count, or one lucky drill would decide the whole curve
        var few = new List<CalibPlan.RoundResult>();
        int itemNo = 0;
        foreach (var r in CalibPlan.Final(baseIdx, 3))
        {
            bool isBase = r.CandidateIndex == baseIdx;
            if (isBase) { itemNo++; continue; }
            bool bigWin = itemNo <= 2;
            few.Add(new CalibPlan.RoundResult
            {
                Round = r,
                Skill = bigWin ? 90 : 45,
                Consistency = r.Item == DrillKind.Tracking ? double.NaN : 0.8,
            });
        }
        foreach (var r in CalibPlan.Final(baseIdx, 3).Where(r => r.CandidateIndex == baseIdx))
            few.Add(new CalibPlan.RoundResult
            {
                Round = r,
                Skill = few.Any(f => f.Round.Item == r.Item && f.Skill > 80) ? 40 : 50,
                Consistency = r.Item == DrillKind.Tracking ? double.NaN : 0.8,
            });
        var fewVerdict = CalibPlan.Decide(rank, few, baseIdx);
        Check(fewVerdict.FinalWins == 2 && !fewVerdict.Recommend,
            $"two big wins and three narrow losses is still a loss ({fewVerdict.FinalWins}/5, +{fewVerdict.RelativeGain * 100:0.#}%)");

        // when the explore already says the player's own curve is best, the head-to-head must still
        // be against somebody else, or it would be ten rounds of the same curve
        var baseLeads = CalibPlan.Rank(results.Where(r => r.Round.CandidateIndex != 3).ToList(), 5);
        int ownIdx = baseLeads.Leader;
        Check(CalibPlan.Opponent(baseLeads, ownIdx) != ownIdx,
            "a head-to-head never compares a curve with itself");
        var keeps = CalibPlan.Decide(baseLeads, Head(ownIdx, CalibPlan.Opponent(baseLeads, ownIdx), 50, 40), ownIdx);
        Check(keeps.WinnerIsBase && !keeps.Recommend, "and losing that comparison leaves the player's curve alone");

        var upset = CalibPlan.Decide(baseLeads, Head(ownIdx, CalibPlan.Opponent(baseLeads, ownIdx), 40, 50), ownIdx);
        Check(upset.Recommend && upset.WinnerIndex == CalibPlan.Opponent(baseLeads, ownIdx),
            "but a runner-up that wins the re-run is allowed to overturn the explore ordering");

        var nothing = CalibPlan.Decide(rank, new List<CalibPlan.RoundResult>(), baseIdx);
        Check(!nothing.Recommend && nothing.Headline.Contains("不成立"),
            "an unfinished head-to-head produces no recommendation at all");

        // --- the new drills ---
        var area = DrillField.Usable(1920, 1080);
        var rng = new Random(7);
        var three = DrillField.Three(area, 22, rng);
        Check(three.Length == 3, "three-shot places three targets");
        Check(three.All(t => area.Fits(t.x, t.y, 22)), "all three stay inside the play area");
        double closest = double.MaxValue;
        for (int i = 0; i < three.Length; i++)
            for (int j = i + 1; j < three.Length; j++)
                closest = Math.Min(closest, Math.Sqrt(Math.Pow(three[i].x - three[j].x, 2) + Math.Pow(three[i].y - three[j].y, 2)));
        Check(closest >= 110, $"no two are close enough to take with one click ({closest:0} px apart)");

        var left = DrillField.FarSpot(area, 22, rng, goLeft: true);
        var right = DrillField.FarSpot(area, 22, rng, goLeft: false);
        Check(area.Fits(left.x, left.y, 22) && area.Fits(right.x, right.y, 22), "the flash target lands inside the area");
        Check(left.x < area.X + 100 && right.x > area.Right - 100, "and it is out at the edge, not near the middle");
        Check(Math.Abs(right.x - left.x) > area.W * 0.8, "the two sides are a full screen apart, so the return is a real flick");

        Check(DrillField.StrafeStopped(700) && !DrillField.StrafeStopped(699),
            "the strafe target is stopped exactly from moveMs onward");
        Check(DrillField.StrafeStopped(1020 + 700) && !DrillField.StrafeStopped(1020 + 699),
            "and the stop repeats every period");
        Check(Math.Abs(DrillField.StrafeNextChangeMs(0) - 700) < 1e-9
              && Math.Abs(DrillField.StrafeNextChangeMs(700) - 320) < 1e-9,
            "the countdown to the next state change lands on both edges");

        var react = new DrillRun(DrillKind.Reaction, 30);
        react.Click(10, 10, false);
        react.Click(10, 10, true);
        react.Reaction(250);
        react.Reaction(350);
        Check(react.Hits == 1 && react.Shots == 2, "a click before the target appears is a miss, not a free pass");
        Check(Math.Abs(react.MeanReactionMs - 300) < 1e-9, "reaction times average");
        Check(react.SkillScore is > 0 and < 1, "a reaction round scores on hits, discounted by the spam");

        var flash = new DrillRun(DrillKind.FlashReset, 60) { RepTarget = 8 };
        for (int i = 0; i < 8; i++) flash.Rep();
        Check(flash.Finished && flash.RepsDone == 8, "a rep-limited round ends when the reps are done, not on the clock");
        Check(flash.ElapsedMs == 0, "and it does not need the clock to have moved at all");
        Check(Math.Abs(flash.SkillScore - 8) < 1e-9, "the flash score is the completed reps");

        var tracker = new DrillRun(DrillKind.Tracking, 20);
        tracker.Tick(10_000, true, true);
        tracker.Advance(20_000);
        Check(Math.Abs(tracker.CoveragePct - 50) < 1e-9,
            "coverage is measured against the whole round, so holding for half of it is 50%, not 100%");
        Check(Math.Abs(tracker.SkillScore - 0.5) < 1e-9, "and that is what tracking is ranked on");

        foreach (DrillKind k in Enum.GetValues<DrillKind>())
        {
            Check(DrillInfo.Name(k).Length > 0, $"{k} has a name");
            Check(DrillInfo.DefaultSeconds(k) > 0, $"{k} has a default duration");
            Check(DrillInfo.DefaultAlive(k) >= 1, $"{k} keeps at least one target alive");
        }
        Check(Enum.GetValues<DrillKind>().Length == 9, "all nine drills are reachable from the picker");
    }

    // ---------- O ----------

    /// <summary>
    /// The relationship nobody can guess from the UI: the driver's base multiplier is
    /// output_dpi / device_dpi, so "Output DPI: 1000" means a different speed depending on whether
    /// the device DPI is filled in. These numbers come from upstream (modifier in
    /// common/rawaccel.hpp: output/1000 × 1000/input, and 1000/input is skipped when input is 0).
    /// </summary>
    static void DpiChecks()
    {
        Check(Math.Abs(Sensitivity.BaseRatio(1000, 0) - 1.0) < 1e-12,
            "normalisation off: output 1000 is the 1:1 reference, so nothing is scaled");
        Check(Math.Abs(Sensitivity.BaseRatio(1000, 600) - 1000.0 / 600) < 1e-12,
            "with the real DPI filled in, the same output 1000 becomes 1.67x - a 600 DPI mouse behaving like 1000");
        Check(Math.Abs(Sensitivity.BaseRatio(600, 600) - 1.0) < 1e-12,
            "output = device DPI is the only combination that is truly 1:1");
        Check(Sensitivity.IsUnity(600, 600) && !Sensitivity.IsUnity(1000, 600),
            "IsUnity agrees, so the readout can say 'no extra scaling' without doing the math itself");

        Check(Math.Abs(Sensitivity.EffectiveDpi(1000, 600) - 1000) < 1e-12,
            "the effective DPI at low speed is the output DPI when the device DPI is known");
        Check(Sensitivity.EffectiveDpi(1000, 0) == 0,
            "with normalisation off no physical DPI can be derived - reported as unknown, not as 1000");

        // the four combinations a player can actually land on
        foreach (var (output, device, expect) in new[] { (1000.0, 0.0, 1.0), (1000.0, 600.0, 1000.0 / 600),
                                                         (600.0, 600.0, 1.0), (800.0, 1600.0, 0.5) })
            Check(Math.Abs(Sensitivity.BaseRatio(output, device) - expect) < 1e-12,
                $"output {output:0} ÷ device {(device > 0 ? device.ToString("0") : "未填")} = {expect:0.00}x");
    }

    // ---------- N ----------

    /// <summary>
    /// The archive exists because a finished session was once lost: the report screen threw on
    /// "apply", the process died, and every round went with it because the rounds only lived in the
    /// window's own lists. These checks are about the file being written as the session runs and
    /// being enough, on its own, to restore a result afterwards.
    /// </summary>
    static void ArchiveChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raui-archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var prior = CalibArchive.DirOverride;
        CalibArchive.DirOverride = dir;      // never write test sessions into the user's archive
        try
        {
            Check(CalibArchive.List().Count == 0, "an empty archive reads back as empty, not as an error");
            Check(CalibArchive.SafeFile("../../etc/passwd") == "etcpasswd",
                "a traversal attempt collapses into a plain file name");

            var live = ProfileWith("classic", 0.06);
            live.Name = "default";
            live.YxRatio = 1.25;
            live.Y.InputOffset = 2.5;
            live.Y.Acceleration = 0.15;

            var cands = AccelCandidates.Build(live, new[] { 0.5, 1.0, 2.0 }, true);
            int baseIdx = cands.FindIndex(c => c.IsBase);
            int winnerIdx = cands.FindIndex(c => !c.IsBase && !c.IsNoAccel);

            var session = new CalibArchive.Session
            {
                Id = "2026-10-29_21-03-08",
                Family = "两轴同步",
                ProfileName = live.Name,
                BaseLabel = AccelCandidates.Describe(live, Plan.Uniform),
                RoundsPlanned = 35,
                BaseJson = CalibArchive.Embedded(cands[baseIdx].Profile),
                WinnerJson = CalibArchive.Embedded(cands[winnerIdx].Profile),
                WinnerIndex = winnerIdx,
                WinnerLabel = cands[winnerIdx].Secret,
                Finished = true,
                Headline = "有更合适的：候选 2。",
                Reasons = { "决选里它在 4/5 个项目上超过你现在这条。" },
                Candidates = cands.Select((c, i) => new CalibArchive.CandidateRow
                {
                    Index = i, Blind = c.BlindLabel, Secret = c.Secret, IsBase = c.IsBase, IsNoAccel = c.IsNoAccel,
                }).ToList(),
            };
            // tracking never clicks, and NaN is not a JSON number: it has to survive as "no data"
            session.Rounds.Add(new CalibArchive.RoundRow
            {
                Phase = "探索", Item = DrillInfo.Name(DrillKind.Tracking), Candidate = winnerIdx, Skill = 0.62,
            });
            session.Rounds.Add(new CalibArchive.RoundRow
            {
                Phase = "探索", Item = DrillInfo.Name(DrillKind.Sixshot), Candidate = baseIdx, Skill = 41.5,
                Consistency = 0.81, Detail = "清掉 6 组", Valid = true,
            });
            session.Rounds.Add(new CalibArchive.RoundRow
            {
                Phase = "决选", Item = DrillInfo.Name(DrillKind.Gridshot), Candidate = winnerIdx, Skill = 0,
                Valid = false, Problem = "本轮提前结束，成绩不计入",
            });

            string? file = CalibArchive.Save(session, out string err);
            Check(file != null && err == "", "a session saves");
            Check(file != null && Path.GetDirectoryName(file) == CalibArchive.Dir,
                "it lands in the archive folder, never beside it");

            var back = CalibArchive.Latest();
            Check(back != null, "the newest session comes back out of the folder");
            Check(back!.Rounds.Count == 3, $"every round is in the file ({back.Rounds.Count})");
            Check(back.Rounds[0].Consistency == null,
                "a round with no flick to measure is stored as no data, not as zero consistency");
            Check(back.Rounds[0].Skill > 0, "and its score still counts");
            Check(!back.Rounds[2].Valid && back.Rounds[2].Problem.Length > 0,
                "a discarded round is marked as such, so re-reading cannot silently rank it");
            Check(back.WinnerIndex == winnerIdx && back.WinnerLabel.Length > 0, "the verdict survives");
            Check(back.SavedAt.Length > 0, "the file is stamped, so 'last session' means something");

            var winner = CalibArchive.WinnerProfile(back);
            Check(winner != null, "the winning curve comes back out of the file as a curve");
            Check(Math.Abs(winner!.YxRatio - 1.25) < 1e-9 && Math.Abs(winner.Y.InputOffset - 2.5) < 1e-9,
                "with the vertical half intact - a result that only restored one axis would be a trap");
            Check(Math.Abs(winner.Y.Acceleration - cands[winnerIdx].Profile.Y.Acceleration) < 1e-9,
                "and with the very numbers the round was played on");
            Check(Json.Validate(new RootDto { Profiles = { winner } }).Count == 0,
                "a curve recovered from an archive is still safe to hand to the driver");

            var original = CalibArchive.BaseProfile(back);
            Check(original != null && Math.Abs(original!.X.Acceleration - 0.06) < 1e-9,
                "the pre-session curve is kept too, so a result can be undone");

            Check(CalibArchive.Summarize(back).Contains("2026-10-29"), "the one-line summary says when it ran");
            Check(CalibArchive.Summarize(back).Contains("候选 2"), "and what it decided");

            // a session interrupted before the report still has to be findable
            var partial = new CalibArchive.Session
            {
                Id = "2026-10-29_21-40-00", RoundsPlanned = 35, ProfileName = live.Name,
                BaseJson = CalibArchive.Embedded(live),
            };
            partial.Rounds.Add(new CalibArchive.RoundRow { Phase = "探索", Item = "网格速击", Candidate = 0, Skill = 12 });
            CalibArchive.Save(partial, out _);
            var newest = CalibArchive.Latest();
            Check(newest != null && newest.Id == "2026-10-29_21-40-00", "the newest file wins, newest first");
            Check(!newest!.Finished && CalibArchive.Summarize(newest).Contains("没跑完"),
                "an interrupted session says so instead of pretending to have a verdict");
            Check(CalibArchive.WinnerProfile(newest) == null && CalibArchive.BaseProfile(newest) != null,
                "it offers the original curve back but no winner, because there is none");
            Check(CalibArchive.List(2).Count == 2, "the archive lists several sessions");

            // the button that puts a result into 我的方案 must never overwrite an older one
            var presetDir = Path.Combine(Path.GetTempPath(), "raui-presets-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(presetDir);
            var priorPresets = Presets.DirOverride;
            Presets.DirOverride = presetDir;
            try
            {
                // the typed name holds a colon, which SafeName drops on the way to the file name;
                // comparing the two forms directly would call the name free forever and overwrite
                string n1 = Presets.FreeName("盲测 10-29 21:03");
                Check(n1.Length > 0 && Presets.Save(n1, live, out _) != null, "the first result saves under its own name");
                string n2 = Presets.FreeName("盲测 10-29 21:03");
                Check(n2 != n1 && Presets.Save(n2, live, out _) != null, "a second result does not overwrite the first");
                Check(Presets.List().Count(p => p.Name.StartsWith("盲测")) == 2,
                    $"both are still in the library ({string.Join(", ", Presets.List().Select(p => p.Name))})");
                string n3 = Presets.FreeName("盲测 10-29 21:03");
                Check(n3 != n1 && n3 != n2, "and a third gets the next number rather than the same slot");
            }
            finally { Presets.DirOverride = priorPresets; try { Directory.Delete(presetDir, true); } catch { } }
        }
        finally { CalibArchive.DirOverride = prior; try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- P ----------

    /// <summary>
    /// The ladder is a claim about convergence: five stages of five slots each should land nearer the
    /// right number than one sweep can, and should stop moving once the steps stop being measurable.
    /// Both halves are checked here - the geometry that narrows, and the gate that refuses to move
    /// on a difference too small to feel, because a ladder that always moves would be a random walk
    /// with extra steps.
    /// </summary>
    static void LadderChecks()
    {
        var stages = CalibLadder.Plan(5, 3);

        Check(stages.Count == 5, "five stages, one per step size");
        Check(stages.All(s => s.Multipliers.Length == CalibLadder.Slots), "each stage offers five slots");
        Check(stages.All(s => Math.Abs(s.Multipliers[s.CentreSlot] - 1.0) < 1e-12),
            "the middle slot is exactly the curve carried in, so a stage that finds nothing changes nothing");
        Check(stages.All(s => s.Multipliers[0] < 1 && s.Multipliers[^1] > 1),
            "every stage reaches both faster and slower - a one-sided stage cannot correct a wrong start");

        // the whole point is that each stage covers less ground than the one before it
        var spans = stages.Select(s => s.Span).ToList();
        Check(spans.Zip(spans.Skip(1)).All(p => p.Second < p.First),
            $"each stage spans less than the last ({string.Join(" → ", spans.Select(s => s.ToString("0.00")))}x)");
        Check(Math.Abs(spans[0] - Math.Pow(1.30, 4)) < 1e-9 && Math.Abs(spans[^1] - Math.Pow(1.04, 4)) < 1e-9,
            $"the first sweep covers {spans[0]:0.00}x, the last only {spans[^1]:0.00}x");
        Check(spans[0] > 2.5 && spans[^1] < 1.2,
            "so it starts wide enough to escape a bad neighbourhood and ends on a step too small to feel");

        Check(CalibLadder.RoundCount(stages) == 85,
            $"5 slots x 3 drills x 5 stages + the head to head = {CalibLadder.RoundCount(stages)} rounds");
        Check(CalibLadder.RoundCount(stages, false) == 75, "75 of those before the head to head");

        // drills rotate: fifteen slots across five drills has to come out even
        var used = stages.SelectMany(s => s.Items).ToList();
        Check(CalibPlan.Items.All(it => used.Count(x => x == it) == 3),
            "every drill runs on exactly three stages, so no stage is judged on one drill's mood");
        Check(stages.All(s => s.Items.Distinct().Count() == s.Items.Length),
            "and no stage runs the same drill twice, which would just be one long round");

        // the schedule inside a stage: every slot meets every drill once, never twice in a row
        var sched = CalibPlan.Schedule(Enumerable.Range(0, 5), stages[0].Items, 1000);
        Check(sched.Count == 15, "a stage of five slots on three drills is fifteen rounds");
        Check(sched.GroupBy(r => r.CandidateIndex).All(g => g.Count() == 3 && g.Select(r => r.Item).Distinct().Count() == 3),
            "every slot meets every drill of its stage exactly once");
        Check(!sched.Where((r, i) => i > 0 && r.CandidateIndex == sched[i - 1].CandidateIndex).Any(),
            "and no two rounds in a row use the same curve, so warm-up cannot look like a result");

        // the gate: a stage only moves the centre when it can actually tell the slots apart
        var st = stages[0];
        Check(CalibLadder.Improved(StageResults(0, st, st.Multipliers, v => Same(v, st.Multipliers[3]) ? 110 : 100), st.CentreSlot, 3),
            "a slot that is 10% better on every drill moves the centre");
        Check(CalibLadder.Improved(StageResults(0, st, st.Multipliers, v => Same(v, st.Multipliers[3]) ? 105 : 100), st.CentreSlot, 3),
            "5% still clears the gate");
        Check(!CalibLadder.Improved(StageResults(0, st, st.Multipliers, v => Same(v, st.Multipliers[3]) ? 101 : 100), st.CentreSlot, 3),
            "1% does not: that is noise, and moving on it would drag four stages into the wrong place");
        Check(!CalibLadder.Improved(StageResults(0, st, st.Multipliers, v => Same(v, st.Multipliers[3]) ? 110 : 100), st.CentreSlot, 2),
            "the centre cannot beat itself - a stage where it wins leaves it where it was");
        Check(!CalibLadder.Improved(new List<CalibPlan.RoundResult>(), st.CentreSlot, 3), "no data is never an improvement");
        Check(!CalibLadder.Improved(StageResults(0, st, st.Multipliers, v => Same(v, st.Multipliers[3]) ? 110 : 100), st.CentreSlot, 9),
            "neither is a slot that never ran");

        // convergence: start 2.5x away from the truth and see where five stages land
        var far = Walk(2.5);
        Check(Math.Abs(far.Centre / 2.5 - 1) < 0.05,
            $"starting 2.5x off, five stages land at x{far.Centre:0.000} - within 5% of the true best");
        Check(far.Moves >= 2, $"and it took {far.Moves} real moves to get there, not one lucky guess");
        Check(far.Distances.Zip(far.Distances.Skip(1)).All(p => p.Second <= p.First + 1e-9),
            $"the distance to the truth never grows ({string.Join(" → ", far.Distances.Select(d => d.ToString("0.000")))})");

        var home = Walk(1.0);
        Check(home.Moves == 0 && Math.Abs(home.Centre - 1.0) < 1e-12,
            "when the curve you came with already is the best, no stage moves - the honest answer is 'don't change'");
    }

    static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>One stage's worth of invented rounds, all valid, scores from <paramref name="skill"/>.</summary>
    static List<CalibPlan.RoundResult> StageResults(int stagePos, CalibLadder.Stage stage, double[] slotValues,
                                                   Func<double, double> skill)
    {
        var rounds = CalibPlan.Schedule(Enumerable.Range(0, stage.Multipliers.Length), stage.Items,
                                        seed: 1000 + stagePos * 131);
        return rounds.Select(r => new CalibPlan.RoundResult
        {
            Round = r,
            Skill = skill(slotValues[r.CandidateIndex]),
        }).ToList();
    }

    /// <summary>
    /// Replays the whole ladder offline against a made-up "truth": a curve that is
    /// <paramref name="trueBest"/> times the starting one, scored by how far a slot is from it in log
    /// space, plus 1% of jitter so the ordering is not handed to it. Returns where the centre ended
    /// up, how many stages actually moved it, and the distance after each stage.
    /// </summary>
    static (double Centre, int Moves, List<double> Distances) Walk(double trueBest)
    {
        var stages = CalibLadder.Plan(5, 3);
        double centre = 1.0;
        int moves = 0;
        var distances = new List<double>();

        for (int s = 0; s < stages.Count; s++)
        {
            var stage = stages[s];
            var vals = stage.Multipliers.Select(m => centre * m).ToArray();

            var results = StageResults(s, stage, vals, v =>
            {
                double jitter = 1 + 0.01 * Math.Sin((s + 1) * 13 + Array.IndexOf(vals, v) * 7);
                return 100 * Math.Exp(-Math.Pow(Math.Log(v / trueBest) / 0.35, 2)) * jitter;
            });

            int leader = CalibPlan.Rank(results, stage.Multipliers.Length).Leader;
            if (CalibLadder.Improved(results, stage.CentreSlot, leader))
            {
                centre = vals[leader];
                moves++;
            }
            distances.Add(Math.Abs(Math.Log(centre / trueBest)));
        }

        return (centre, moves, distances);
    }
}
