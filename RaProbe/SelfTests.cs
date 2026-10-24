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

        Console.WriteLine("\n== I. SettingsFile: the official path is found, never assumed ==");
        Section("settings", SettingsChecks);

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

    // ---------- I ----------

    static void SettingsChecks()
    {
        var candidates = SettingsFile.Candidates();
        Check(candidates.Count >= 6, $"the official file is looked for in {candidates.Count} places, not one");
        Check(candidates.All(c => Path.IsPathRooted(c)), "every candidate is an absolute path");
        Check(candidates.Any(c => c.Contains("Raw Accel", StringComparison.OrdinalIgnoreCase)),
            "the official install layout is among them, so the two tools share one settings.json");
        Check(candidates.Any(c => c.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)),
            "a copy of the app sitting next to its own settings.json also works (portable folder)");

        var found = SettingsFile.Find();
        Check(found == null || File.Exists(found), "Find() either names a file that exists or says nothing");

        var def = SettingsFile.Default();
        Check(def.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase),
            $"the UI always has a settings path to save to: {def}");
        Check(Directory.Exists(Path.GetDirectoryName(def)),
            "and its folder really exists, so the first save cannot fail on a missing directory");
    }
}
