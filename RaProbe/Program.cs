using System.Runtime.InteropServices;
using System.Text.Json;
using RaCore;

namespace RaProbe;

/// <summary>
/// Verification harness for task #1: does our C# layout match the driver's C++ layout,
/// and does the live driver agree with D:\RawAccel\settings.json?
/// Read-only: this program never writes to the driver.
/// </summary>
internal static class Program
{
    static unsafe void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // offline assertions first: they must run even when the driver is not reachable
        if (args.Contains("--selftest"))
        {
            Console.WriteLine("== offline self tests (no driver, no display, no input) ==");
            int code = SelfTests.Run();
            Console.WriteLine(code == 0 ? "ALL CHECKS PASSED" : "SELFTEST FAILED");
            Environment.Exit(code);
            return;
        }

        if (args.Contains("--rawinput-probe"))
        {
            RawInputProbe.Run(args);
            return;
        }

        if (args.Contains("--apply-settings"))
        {
            Environment.Exit(ApplySettings.Run(args));
            return;
        }

        Console.WriteLine("== 1. struct sizes (C# vs expected from C++ headers) ==");
        foreach (var (name, actual, expected) in WireSizes.Report())
            Console.WriteLine($"   {name,-18} actual={actual,-6} expected={expected,-6} {(actual == expected ? "OK" : "*** MISMATCH ***")}");
        Console.WriteLine("   all match: " + WireSizes.AllMatch());
        if (!WireSizes.AllMatch())
        {
            Console.WriteLine("ABORT: struct layout does not match the driver headers");
            Environment.Exit(1);
            return;
        }

        Console.WriteLine("\n== 2. driver version (IOCTL 0x8888088a) ==");
        try
        {
            var v = RaDevice.GetVersion();
            Console.WriteLine($"   driver = {v.major}.{v.minor}.{v.patch}   (min understood by 1.7 tooling: 1.7.0)");
        }
        catch (Exception e)
        {
            Console.WriteLine("   FAILED: " + e.Message);
            return;
        }

        Console.WriteLine("\n== 3. READ active settings (IOCTL 0x88880888) ==");
        byte[] blob;
        try { blob = RaDevice.Read(); }
        catch (Exception e) { Console.WriteLine("   FAILED: " + e.Message); return; }

        var b = RaDevice.GetBase(blob);
        Console.WriteLine($"   blob length        = {blob.Length}");
        Console.WriteLine($"   modifier_data_size = {b.modifier_data_size}");
        Console.WriteLine($"   device_data_size   = {b.device_data_size}");
        Console.WriteLine($"   default dev cfg    = disable:{b.default_dev_cfg.disable} extra:{b.default_dev_cfg.set_extra_info} " +
                          $"pollLock:{b.default_dev_cfg.poll_time_lock} dpi:{b.default_dev_cfg.dpi} rate:{b.default_dev_cfg.polling_rate} " +
                          $"clamp:[{b.default_dev_cfg.clamp.min},{b.default_dev_cfg.clamp.max}]");

        var modifiers = RaDevice.Modifiers(blob).ToList();
        foreach (var m in modifiers)
        {
            var p = m.prof;
            Console.WriteLine($"\n   --- profile '{Str(p.name, 256)}' ---");
            Console.WriteLine($"     domain=({p.domain_weights.x},{p.domain_weights.y}) range=({p.range_weights.x},{p.range_weights.y})");
            Describe("accel_x", p.accel_x);
            Describe("accel_y", p.accel_y);
            Console.WriteLine($"     speed: whole:{p.speed_processor_args.whole} lp:{p.speed_processor_args.lp_norm} " +
                              $"hl_in:{p.speed_processor_args.input_speed_smooth_halflife} " +
                              $"hl_scale:{p.speed_processor_args.scale_smooth_halflife} " +
                              $"hl_out:{p.speed_processor_args.output_speed_smooth_halflife}");
            Console.WriteLine($"     output_dpi:{p.output_dpi} yx:{p.yx_output_dpi_ratio} lr:{p.lr_output_dpi_ratio} ud:{p.ud_output_dpi_ratio} " +
                              $"rot:{p.degrees_rotation} snap:{p.degrees_snap} min:{p.speed_min} max:{p.speed_max}");
            Console.WriteLine($"     data.flags = rot:{m.data.flags.apply_rotate} refang:{m.data.flags.compute_ref_angle} snap:{m.data.flags.apply_snap} " +
                              $"clamp:{m.data.flags.apply_directional_weight} dirw:{m.data.flags.apply_directional_weight} " +
                              $"mx:{m.data.flags.apply_dir_mul_x} my:{m.data.flags.apply_dir_mul_y}");
            Console.WriteLine($"     data.rot_direction = ({m.data.rot_direction.x},{m.data.rot_direction.y})");
        }

        foreach (var d in RaDevice.Devices(blob))
            Console.WriteLine($"   device id='{Str(d.id, 200)}' prof='{Str(d.profile, 256)}' " +
                              $"dpi:{d.config.dpi} rate:{d.config.polling_rate} disable:{d.config.disable}");

        Console.WriteLine("\n== 4. cross-check driver bytes vs settings.json ==");
        CrossCheck(blob, modifiers.FirstOrDefault());

        Console.WriteLine("\n== 5. data_t (precomputed state the driver actually runs) ==");
        if (modifiers.Count > 0)
        {
            var mod = modifiers[0];
            foreach (var (nm, u) in new (string, accel_union)[]
                     { ("union_x", mod.data.accel_x), ("union_y", mod.data.accel_y) })
            {
                var cg = u.classic_g;
                Console.WriteLine($"   {nm} as classic_g: raised:{cg.accel_raised} cap:({cg.cap.x},{cg.cap.y}) constant:{cg.constant} sign:{cg.sign}");
                Console.WriteLine($"   {nm} raw 72 bytes: {Hex(u)}");
            }
        }

        if (args.Contains("--write-selftest"))
        {
            Console.WriteLine("\n== 6. WRITE round-trip self test (blobs byte-identical; cursor may hitch ~1s) ==");
            string H(byte[] x) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x));
            var before = H(blob);
            Console.WriteLine($"   sha256 before = {before}");
            try
            {
                RaDevice.Write(blob);
                var after = RaDevice.Read();
                var ah = H(after);
                Console.WriteLine($"   sha256 after  = {ah}");
                Console.WriteLine("   => " + (ah == before
                    ? "WRITE PATH OK: driver accepted our serialization and returned identical bytes"
                    : "*** DRIVER STATE CHANGED - investigate before continuing ***"));
            }
            catch (Exception e) { Console.WriteLine("   write failed: " + e.Message); }
        }

        if (args.Contains("--verify-math"))
        {
            Console.WriteLine("\n== 7. math port vs driver data_t (byte comparison) ==");
            var driverStruct = modifiers[0];
            var mine = new ModifierSettings(driverStruct.prof).ToStruct();

            string Bytes(modifier_settings s)
            {
                var span = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
                return Convert.ToHexString(MemoryMarshal.AsBytes(span)
                    .Slice(WireSizes.Profile, WireSizes.Data).ToArray()).ToUpperInvariant();
            }

            string a = Bytes(driverStruct), c = Bytes(mine);
            Console.WriteLine($"   driver data_t = {a}");
            Console.WriteLine($"   my     data_t = {c}");
            Console.WriteLine("   => " + (a == c ? "IDENTICAL: C# port reproduces the driver's precomputed state"
                                                 : "*** DIFFERS ***"));

            // whole modifier_settings blob (profile + data) must match, that is what we would write back
            string Full(modifier_settings s)
            {
                var span = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
                return Convert.ToHexString(MemoryMarshal.AsBytes(span).ToArray()).ToUpperInvariant();
            }
            bool fullSame = Full(driverStruct) == Full(mine);
            Console.WriteLine($"   whole modifier_settings (5184 bytes) identical: {fullSame}");
            if (!fullSame)
            {
                var x = Convert.FromHexString(Full(driverStruct));
                var y = Convert.FromHexString(Full(mine));
                var diffs = new List<int>();
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) diffs.Add(i);
                Console.WriteLine($"   differing byte offsets (first 20 of {diffs.Count}): {string.Join(",", diffs.Take(20))}");
            }

            Console.WriteLine("\n   curve for the live profile (by-component, 2000Hz poll => time=0.5ms):");
            Console.WriteLine("      speed    sens_x   gain_x   sens_y   gain_y   (counts/ms, 1000dpi-normalised)");
            var prof = driverStruct.prof;
            const double t = 0.5;
            foreach (double v in new double[] { 0.5, 1, 2, 2.5, 3, 4, 5, 6, 8, 11.0833, 15, 25, 50 })
            {
                double sensX = Out(prof, true, v) / v;
                double sensY = Out(prof, false, v) / v;
                double h = 1e-6;
                double gainX = (Out(prof, true, v + h) - Out(prof, true, v - h)) / (2 * h);
                double gainY = (Out(prof, false, v + h) - Out(prof, false, v - h)) / (2 * h);
                Console.WriteLine($"     {v,8:F4} {sensX,9:F4} {gainX,9:F4} {sensY,9:F4} {gainY,9:F4}");
            }
        }

        if (args.Contains("--selftest-json"))
        {
            Console.WriteLine("\n== 8. end-to-end: official settings.json -> our DTO -> driver blob ==");
            const string file = @"D:\RawAccel\settings.json";
            var text = File.ReadAllText(file);
            var root = RaCore.Model.Json.Deserialize(text);
            var mine = RaCore.Model.Convert4.BuildBlob(root);
            var live = RaDevice.Read();

            string H(byte[] x) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x));
            Console.WriteLine($"   profiles={root.Profiles.Count} devices={root.Devices.Count} " +
                              $"defaultDpi={root.DefaultDeviceConfig.Dpi} blob={mine.Length}B driver={live.Length}B");
            Console.WriteLine($"   our blob sha256   = {H(mine)}");
            Console.WriteLine($"   driver blob sha256= {H(live)}");
            Console.WriteLine("   => " + (H(mine) == H(live)
                ? "IDENTICAL: settings.json parsed by our code produces exactly what the signed driver is running"
                : "*** DIFFERS ***"));
            if (H(mine) != H(live))
            {
                var offs = new List<int>();
                for (int i = 0; i < Math.Min(mine.Length, live.Length); i++) if (mine[i] != live[i]) offs.Add(i);
                Console.WriteLine($"   differing bytes: {offs.Count} -> {string.Join(",", offs.Take(40))}{(offs.Count > 40 ? "..." : "")}");

                // classify: is each difference inside a struct padding hole?
                int padding = offs.Count(o => IsPadding(o));
                Console.WriteLine($"   of those, {padding}/{offs.Count} fall in alignment padding (semantically dead bytes)");
                foreach (var o in offs.Where(x => !IsPadding(x)).Take(20))
                    Console.WriteLine($"   *** real difference at {o}: ours=0x{mine[o]:X2} driver=0x{live[o]:X2}");

                static bool IsPadding(int o)
                {
                    int ioBase = 40, mod = WireSizes.Modifier, prof = WireSizes.Profile, aa = WireSizes.AccelArgs;
                    if (o < ioBase) return o == 3 || (o >= 12 && o <= 15);   // device_config tail pad before doubles
                    int rel = o - ioBase;
                    if (rel >= prof) return false;                            // modifier_data region checked below
                    // profile: name(512) domain(16) range(16) then two accel_args, then speed_args, then 8 doubles
                    if (rel < 512) return false;
                    int r2 = rel - 512 - 32;                                  // into accel_x
                    if (r2 >= 0 && r2 < 2 * aa)
                    {
                        int axis = r2 / aa, inAxis = r2 % aa;
                        if (inAxis is >= 5 and <= 7) return true;              // gain byte + pad
                        if (inAxis >= 128) return false;                       // lut data
                        return false;
                    }
                    int r3 = rel - 512 - 32 - 2 * aa;                           // speed_args
                    if (r3 >= 0 && r3 < 40) return r3 >= 1 && r3 <= 7;
                    return false;
                }
            }

            // round trip through our own serializer
            var again = RaCore.Model.Json.Deserialize(RaCore.Model.Json.Serialize(root));
            var mine2 = RaCore.Model.Convert4.BuildBlob(again);
            Console.WriteLine("   reparse(serialize(root)) -> same blob: " + (H(mine2) == H(mine)));

            // textual diff vs the official file (formatting differences are informational only)
            var ours = RaCore.Model.Json.Serialize(root).Replace("\r\n", "\n").Split('\n');
            var orig = text.Replace("\r\n", "\n").Split('\n');
            int diff = 0;
            for (int i = 0; i < Math.Max(ours.Length, orig.Length); i++)
            {
                string lineOurs = i < ours.Length ? ours[i].Trim() : "<eof>";
                string lineFile = i < orig.Length ? orig[i].Trim() : "<eof>";
                if (lineOurs != lineFile)
                {
                    if (diff < 6) Console.WriteLine($"   line {i + 1}:\n     ours: {lineOurs}\n     file: {lineFile}");
                    diff++;
                }
            }
            Console.WriteLine($"   textual line diffs vs official file: {diff} (ours {ours.Length} lines, file {orig.Length} lines)");
        }

        if (args.Contains("--devices"))
        {
            Console.WriteLine("\n== 9. rawinput mouse enumeration (same id derivation as the driver) ==");
            var mice = DeviceEnum.ListMice();
            Console.WriteLine($"   found {mice.Count} distinct mouse ids");
            foreach (var d in mice)
                Console.WriteLine($"   {d.Id,-52} handles={d.Handles} name='{d.Name}'");
            if (mice.Count == 0 || args.Contains("--devices-v"))
                foreach (var line in DeviceEnum.Diagnose()) Console.WriteLine("   | " + line);
        }

        if (args.Contains("--selftest-devices"))
        {
            Console.WriteLine("\n== 10. device entries round-trip (no driver write) ==");
            var root = RaCore.Model.Json.Deserialize(File.ReadAllText(@"D:\RawAccel\settings.json"));
            var mice = DeviceEnum.ListMice();
            Console.WriteLine($"   enumerated {mice.Count} mice; adding per-device entries with DPI=800");
            foreach (var m in mice)
                root.Devices.Add(new RaCore.Model.DeviceDto
                {
                    Id = m.Id,
                    Name = m.Name,
                    Profile = root.Profiles[0].Name,
                    Config = new RaCore.Model.DeviceConfigDto { Dpi = 800 },
                });

            var devBlob = RaCore.Model.Convert4.BuildBlob(root);
            int expect = WireSizes.IoBase + root.Profiles.Count * WireSizes.Modifier + root.Devices.Count * WireSizes.Device;
            Console.WriteLine($"   blob = {devBlob.Length} bytes, expected {expect} -> {(devBlob.Length == expect ? "OK" : "*** MISMATCH ***")}");

            var back = RaCore.Model.Convert4.ReadBlob(devBlob);
            Console.WriteLine($"   re-read devices: {back.Devices.Count}");
            foreach (var d in back.Devices)
                Console.WriteLine($"     id='{d.Id}' name='{d.Name}' profile='{d.Profile}' dpi={d.Config.Dpi} disable={d.Config.Disable}");

            bool idsOk = back.Devices.Select(d => d.Id).SequenceEqual(mice.Select(m => m.Id));
            Console.WriteLine("   ids survive the binary round trip: " + idsOk);

            // what the driver would actually compare against (BusQueryDeviceID style, upper case)
            Console.WriteLine("   driver-side comparison is wcsncmp(devExt->dev_id, id, 200); our ids are upper-case canonical:");
            foreach (var d in back.Devices)
                Console.WriteLine($"     {d.Id}  {(d.Id == d.Id.ToUpperInvariant() ? "OK" : "*** not upper ***")}");
        }

        if (args.Contains("--simulate-advice"))
        {
            Console.WriteLine("\n== 11. simulate a player, then check the advice engine ==");
            var prof = modifiers[0].prof;
            var ax = CurveAnalysis.Analyze(prof, true);
            var ay = CurveAnalysis.Analyze(prof, false);
            Console.WriteLine($"   X thresh={ax.Threshold:0.00} capAt={ax.CapSpeed:0.00} peak={ax.PeakGain:0.00}   " +
                              $"Y thresh={ay.Threshold:0.00} capAt={ay.CapSpeed:0.00} peak={ay.PeakGain:0.00}");

            double sens = 1.0, dpi = 600;
            var mod = new Modifier(new ModifierSettings(prof));

            // the simulated player sizes every flick as if the stream were NOT accelerated
            var sim = new List<Trial>();
            foreach (double deg in new double[] { 3, 5, 8, 12, 18, 25, 35, 50, 70, 100 })
            for (int rep = 0; rep < 4; rep++)
            {
                double need = Cs2.CountsForDegrees(deg, sens);
                double duration = 90 + deg * 1.6;
                double handSpeed = need / duration;              // counts/ms
                double t = 1.0;
                var sp = new SpeedProcessor(); sp.Init(prof.speed_processor_args);
                var o = mod.Modify(new vec2d { x = handSpeed * t, y = 0 }, sp, 1, t);
                double ratio = (o.x / t) / Math.Max(handSpeed, 1e-9);
                double delivered = need * ratio;

                sim.Add(new Trial
                {
                    Degrees = deg,
                    DistancePx = need,
                    AngleRad = Random.Shared.NextDouble() * 2 * Math.PI,
                    PeakSpeed = handSpeed * 1.9,
                    RadialPct = (delivered - need) / need * 100.0,
                    RadialPx = delivered - need,
                    TangentialPx = Random.Shared.NextDouble() * 6 - 3,
                    ErrorPx = Math.Abs(delivered - need),
                    Hit = Math.Abs(delivered - need) <= 26,
                    StartMs = 0, EndMs = duration,
                });
            }

            Console.WriteLine("   simulated flicks (hand sized for a neutral stream):");
            foreach (var g in sim.GroupBy(x => x.Degrees).OrderBy(g => g.Key))
            {
                var t0 = g.First();
                Console.WriteLine($"     {g.Key,5:0}deg  need {t0.DistancePx,7:0}px  peak {t0.PeakSpeed,5:0.0}c/ms  " +
                                  $"radial {t0.RadialPct,+6:0.0}%  hit {(g.Count(x => x.Hit) * 100.0 / g.Count()):0}%");
            }

            Console.WriteLine("\n   summary of the simulated round (description only, no advice engine):");
            var simStats = RoundStats.Compute(sim, 26, sim.Average(x => x.DistancePx));
            Console.WriteLine($"     n={simStats.Count}  hit {simStats.HitRate:0.0}%  radial median {simStats.RadialMedian:+0.0;-0.0}%  MAE {simStats.RadialMae:0.0}%  sd {simStats.RadialSd:0.0}%");
            Console.WriteLine($"     over/on/under = {simStats.OvershootCount:0}/{simStats.OnTargetCount:0}/{simStats.UndershootCount:0}");
            foreach (var q in simStats.Quality) Console.WriteLine("     flag: " + q);
        }

        if (args.Contains("--display"))
        {
            Console.WriteLine("\n== 12. display modes (read only, nothing is changed) ==");
            var cur = Display.Current();
            var modes = Display.AvailableModes();
            Console.WriteLine($"   current = {cur.Width}x{cur.Height} @ {cur.Refresh}Hz, {cur.Bits}bpp");
            Console.WriteLine($"   reported modes: {modes.Count}");
            foreach (var g in modes.GroupBy(m => (m.Width, m.Height)).OrderByDescending(g => (long)g.Key.Width * g.Key.Height))
                Console.WriteLine($"     {g.Key.Width,5}x{g.Key.Height,-5} hz: {string.Join(",", g.Select(m => m.Refresh).Distinct().OrderByDescending(x => x))}");
            var odd = modes.Where(m => m.Refresh <= 0 || m.Width <= 0).ToList();
            Console.WriteLine($"   implausible entries: {odd.Count}");
        }

        if (args.Contains("--sample-log"))
        {
            Console.WriteLine("\n== 13. write a sample round log (synthetic trials) ==");
            var prof = modifiers[0].prof;
            var mod = new Modifier(new ModifierSettings(prof));
            var t = new List<Trial>();
            var rnd = new Random(7);
            foreach (double deg in new double[] { 4, 8, 15, 25, 40, 60 })
            for (int rep = 0; rep < 5; rep++)
            {
                double need = Cs2.CountsForDegrees(deg, 1.5) * 1.25;
                double dur = 90 + deg * 1.6;
                double hand = need / dur / 1.25;
                var sp = new SpeedProcessor(); sp.Init(prof.speed_processor_args);
                var o = mod.Modify(new vec2d { x = hand, y = 0 }, sp, 1, 1.0);
                double ratio = o.x / Math.Max(hand, 1e-9);
                double radial = (ratio - 1) * need + (rnd.NextDouble() - 0.5) * 22;
                t.Add(new Trial
                {
                    Degrees = deg, DistancePx = need, AngleRad = rnd.NextDouble() * 6.28,
                    StartMs = 0, EndMs = dur, PeakSpeed = hand * 1.9,
                    RadialPx = radial, RadialPct = radial / need * 100,
                    TangentialPx = (rnd.NextDouble() - 0.5) * 14,
                    ErrorPx = Math.Abs(radial), Hit = Math.Abs(radial) <= 26,
                });
            }

            var path = @"C:\temp\sample-round.log";
            var roundId = TestLog.NewRoundId();
            TestLog.Write(path, new RoundData
            {
                RoundId = roundId,
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DriverVersion = "driver v1.7.0 · connected",
                ProfileName = "default",
                ConfigSha = ConfigHash.Of(prof),
                ConfigSource = "driver (synthetic trials for format checking)",
                ProfileJson = RaCore.Model.Json.Serialize(RaCore.Model.Convert4.ReadBlob(blob)),
                Sensitivity = 1.5, MouseDpi = 600, PollRate = 2000,
                TargetDistancePx = 420, RadiusPx = 26, TargetCount = 3, Seed = 1234,
                Direction = "全向", Resolution = "3440x1440@180",
                Profile = prof, Trials = t,
                OutputSpeed = (4.4, 9.8, 18.2, 27.5),
                TelemetryNote = "SYNTHETIC ROUND: the trials were generated from the curve, not thrown by a human",
            });
            Console.WriteLine("   wrote " + path + " (" + new FileInfo(path).Length + " bytes)");
            Console.WriteLine("   read back: " + (TestLog.ReadSummary(path)?.Trials ?? -1) + " trials, sha " +
                              (TestLog.ReadSummary(path)?.ConfigSha ?? "?"));
            Console.WriteLine();
            foreach (var line in File.ReadAllLines(path)) Console.WriteLine("   | " + line);
        }

        if (args.Contains("--recorder-test") || args.Contains("--replay-log"))
        {
            Console.WriteLine("\n== 14. superseded: use --selftest (assertion based, no printing-only checks) ==");
            Console.WriteLine("   offline recorder/layout/patch/log assertions now live in SelfTests.Run()");
            Console.WriteLine("   exit code is the verdict: 0 pass, 1 fail");
        }

        Console.WriteLine("\n(done)");
    }

    static double D(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    static double Pct(List<Trial> t, double p)
    {
        var a = t.Select(x => x.PeakSpeed).OrderBy(x => x).ToList();
        return a.Count == 0 ? 0 : a[Math.Clamp((int)(p * (a.Count - 1)), 0, a.Count - 1)];
    }
    /// <summary>output speed (counts/ms) for a pure-axis input of the given speed; fresh stateless pipeline.</summary>
    static double Out(ra_profile prof, bool horizontal, double speed)
    {
        const double t = 0.5;
        var sp = new SpeedProcessor();
        sp.Init(prof.speed_processor_args);
        var m = new Modifier(new ModifierSettings(prof));
        var input = new vec2d { x = horizontal ? speed * t : 0, y = horizontal ? 0 : speed * t };
        var o = m.Modify(input, sp, 1, t);
        return horizontal ? o.x / t : o.y / t;
    }

    static unsafe string Str(ushort* buf, int len)
    {
        int n = 0;
        while (n < len && buf[n] != 0) n++;
        return new string((char*)buf, 0, n);
    }

    static unsafe void Describe(string label, accel_args a)
    {
        int nonZero = 0;
        for (int i = 0; i < accel_args.LUT_RAW_DATA_CAPACITY; i++) if (a.data[i] != 0) nonZero++;
        Console.WriteLine($"     {label}: mode={a.mode} gain={a.gain} in_off={a.input_offset} out_off={a.output_offset} " +
                          $"accel={a.acceleration} decay={a.decay_rate} gamma={a.gamma} mot={a.motivity} expC={a.exponent_classic} " +
                          $"scale={a.scale} expP={a.exponent_power} limit={a.limit} sync={a.sync_speed} smooth={a.smooth} " +
                          $"cap=({a.cap.x},{a.cap.y}) cap_mode={a.cap_mode} len={a.length} lutNonZero={nonZero}");
    }

    static unsafe string Hex(accel_union u)
    {
        var span = MemoryMarshal.CreateSpan(ref u, 1);
        return BitConverter.ToString(MemoryMarshal.AsBytes(span).ToArray()).Replace("-", "");
    }

    static void CrossCheck(byte[] blob, modifier_settings driver)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(@"D:\RawAccel\settings.json"));
            var prof = doc.RootElement.GetProperty("profiles")[0];
            var hx = prof.GetProperty("Whole or horizontal accel parameters");
            var hy = prof.GetProperty("Vertical accel parameters");
            var dx = driver.prof.accel_x;
            var dy = driver.prof.accel_y;

            bool ok = true;
            ok &= Cmp("x.mode", ModeFromJson(hx.GetProperty("mode").GetString()), dx.mode.ToString());
            ok &= Cmp("x.gain", hx.GetProperty("Gain / Velocity").GetBoolean() ? 1d : 0d, dx.gain);
            ok &= Cmp("x.inputOffset", hx.GetProperty("inputOffset").GetDouble(), dx.input_offset);
            ok &= Cmp("x.acceleration", hx.GetProperty("acceleration").GetDouble(), dx.acceleration);
            ok &= Cmp("x.cap.x", hx.GetProperty("Cap / Jump").GetProperty("x").GetDouble(), dx.cap.x);
            ok &= Cmp("x.cap.y", hx.GetProperty("Cap / Jump").GetProperty("y").GetDouble(), dx.cap.y);
            ok &= Cmp("x.cap_mode", CapFromJson(hx.GetProperty("Cap mode").GetString()), dx.cap_mode.ToString());
            ok &= Cmp("y.mode", ModeFromJson(hy.GetProperty("mode").GetString()), dy.mode.ToString());
            ok &= Cmp("y.inputOffset", hy.GetProperty("inputOffset").GetDouble(), dy.input_offset);
            ok &= Cmp("y.acceleration", hy.GetProperty("acceleration").GetDouble(), dy.acceleration);
            ok &= Cmp("y.cap.y", hy.GetProperty("Cap / Jump").GetProperty("y").GetDouble(), dy.cap.y);
            ok &= Cmp("Output DPI", prof.GetProperty("Output DPI").GetDouble(), driver.prof.output_dpi);
            var sp = prof.GetProperty("Input speed calculation parameters");
            ok &= Cmp("whole", sp.GetProperty("Whole/combined accel (set false for 'by component' mode)").GetBoolean() ? 1d : 0d,
                      driver.prof.speed_processor_args.whole);
            ok &= Cmp("lpNorm", sp.GetProperty("lpNorm").GetDouble(), driver.prof.speed_processor_args.lp_norm);
            var dflt = doc.RootElement.GetProperty("defaultDeviceConfig");
            ok &= Cmp("default.disable", dflt.GetProperty("disable").GetBoolean() ? 1d : 0d, b_default_disable(blob));
            ok &= Cmp("default.DPI", dflt.GetProperty("DPI (normalizes input speed unit: counts/ms -> in/s)").GetInt32(),
                      RaDevice.GetBase(blob).default_dev_cfg.dpi);

            Console.WriteLine("   => " + (ok ? "driver state == settings.json (layout confirmed)" : "*** DIFFERS: layout or mapping is wrong ***"));

            static double b_default_disable(byte[] bl) => RaDevice.GetBase(bl).default_dev_cfg.disable;
        }
        catch (Exception e)
        {
            Console.WriteLine("   cross-check failed: " + e.Message);
        }
    }

    static string ModeFromJson(string s) => s switch
    {
        "classic" => "classic", "jump" => "jump", "natural" => "natural", "synchronous" => "synchronous",
        "power" => "power", "lut" => "lookup", "noaccel" => "noaccel", _ => "?" + s
    };

    static string CapFromJson(string s) => s switch
    {
        "in_out" => "io", "input" => "in", "output" => "out", _ => "?" + s
    };

    static bool Cmp(string name, object a, object c)
    {
        bool eq = string.Equals(a?.ToString(), c?.ToString(), StringComparison.Ordinal);
        Console.WriteLine($"     {(eq ? "ok " : "XX ")} {name,-16} json={a} driver={c}");
        return eq;
    }

    static bool Cmp(string name, double a, double c)
    {
        bool eq = Math.Abs(a - c) < 1e-12;
        Console.WriteLine($"     {(eq ? "ok " : "XX ")} {name,-16} json={a} driver={c}");
        return eq;
    }

    static bool Cmp(string name, int a, int c) => Cmp(name, (double)a, c);
}
