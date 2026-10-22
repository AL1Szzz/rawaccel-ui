using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RaCore;
using RaCore.Model;

namespace RaProbe;

/// <summary>
/// Is the mouse packet stream regular, and does its irregularity move the curve?
///
/// Why this exists: rawaccel does NOT assume a fixed polling rate unless the user locks it.
/// driver.cpp measures the elapsed QPC time per packet and derives speed = counts / that time,
/// clamped only to [DEFAULT_TIME_MIN, 100ms]. With "Use constant time interval based on polling
/// rate" = false and "Polling rate Hz" = 0 (both are the defaults, and both are what this
/// machine's settings.json has), a late packet is therefore read as a SLOWER hand, and the gain
/// drops for that packet. That is a real, load-dependent wobble in the curve, and it is what the
/// user reports feeling.
///
/// This probe records the arrival rhythm of unmerged WM_INPUT packets and prices it in gain.
/// Read-only: nothing is written to the driver, and no settings are touched.
///
/// Caveat stated up front so nobody over-reads it: these are USER-MODE timestamps, so they also
/// contain the scheduling jitter of this process, which the kernel driver does not see. Treat the
/// numbers as an upper bound on the driver's distortion - and as a fair A/B between two conditions,
/// because the same bias applies to both.
/// </summary>
internal static class PollJitter
{
    const int WM_INPUT = 0x00FF;

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Field order IS the Win32 layout: cbSize, style, lpfnWndProc, cbClsExtra, cbWndExtra,
    // hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm. Grouping the
    // IntPtrs together reads nicer but marshals garbage and RegisterClassEx fails with 87.
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    // Field order is the Win32 layout and it is NOT cosmetic: PeekMessageW writes the whole 48-byte
    // MSG into this struct. Writing it as "message, time" together (which reads tidier) shrank the
    // struct to 40 bytes, the extra 8 landed on the stack, and the tool died at random points - one
    // run a NullReferenceException blamed on GetCursorPos, the next an AccessViolationException.
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
                 public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h,
                                         IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr hWnd, uint min, uint max, uint rm);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string? name);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    internal readonly record struct Pkt(long us, int dx, int dy);

    static readonly List<Pkt> pkts = new();
    static WndProc? kept;
    static long t0;
    static double freqPerUs;

    /// <summary>
    /// A diagnostic tool must never die silently. Anything that escapes gets written out in full
    /// (with line numbers, once the .pdb sits next to the exe) both to the console and to the
    /// jitter folder, so one run is enough to locate it.
    /// </summary>
    static string? firstThrow;

    public static int Run(string[] args)
    {
        // The stack that reaches the catch below has already crossed a native transition
        // (DispatchMessageW -> our WndProc) and comes back truncated to the call site - it blamed
        // GetCursorPos/Pump for two rounds. FirstChanceException fires at the throw point, while
        // the real frames are still on the stack.
        AppDomain.CurrentDomain.FirstChanceException += (_, ev) => firstThrow ??= ev.Exception.ToString();
        try { return RunInner(args); }
        catch (Exception e)
        {
            Console.WriteLine("探针自己出错了（测量数据没有丢，是代码问题）：");
            if (firstThrow != null && firstThrow != e.ToString())
                Console.WriteLine("真实现场（首次抛出时记录）：\n" + firstThrow);
            Console.WriteLine(e);
            try
            {
                var dir = Path.Combine(AppData.Root, "jitter");
                Directory.CreateDirectory(dir);
                var txt = Path.Combine(dir, "CRASH-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
                File.WriteAllText(txt, string.Join(Environment.NewLine, report) + Environment.NewLine +
                                  "首次抛出：" + Environment.NewLine + firstThrow + Environment.NewLine +
                                  "冒泡到此：" + Environment.NewLine + e);
                Console.WriteLine("已写: " + txt);
            }
            catch (Exception e2) { Console.WriteLine("连崩溃日志都没写成: " + e2.Message); }
            return 1;
        }
    }

    static int RunInner(string[] args)
    {
        int seconds = 20;
        int si = Array.FindIndex(args, a => a == "--seconds");
        if (si >= 0 && si + 1 < args.Length) int.TryParse(args[si + 1], out seconds);
        else
        {
            // both spellings work: "--poll-jitter --seconds 30" and the shorter "--poll-jitter 30"
            int self = Array.FindIndex(args, a => a == "--poll-jitter");
            if (self >= 0 && self + 1 < args.Length) int.TryParse(args[self + 1], out seconds);
        }
        if (seconds <= 0 || seconds > 300) seconds = 20;

        string settingsPath = @"D:\RawAccel\settings.json";
        int pi = Array.FindIndex(args, a => a == "--settings");
        if (pi >= 0 && pi + 1 < args.Length) settingsPath = args[pi + 1];

        // --synthetic runs the whole analysis on a made-up packet stream: no hand, no console
        // quirks. It is what makes this tool testable, and it is how a crash that only appears
        // once there is data can be reproduced at all.
        if (args.Contains("--synthetic"))
        {
            Say($"合成数据自检（settings = {settingsPath}）");
            long t = 0;
            var rnd = new Random(7);
            for (int i = 0; i < 4000; i++)
            {
                t += i % 5 == 0 ? 1500 : 500;                       // 20% of packets 3x late
                if (i % 200 == 0) t += 30_000;                        // plus real pauses: must not be priced
                int d = 3 + rnd.Next(0, 9);
                pkts.Add(new Pkt(t, d, d / 2));
            }
            Analyze(2.0, settingsPath);
            return 0;
        }

        var sw0 = Stopwatch.StartNew();
        t0 = sw0.ElapsedTicks;
        freqPerUs = Stopwatch.Frequency / 1_000_000.0;

        var inst = GetModuleHandleW(null);
        kept = Wnd;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(kept),
            hInstance = inst,
            lpszClassName = "RaProbePollJitter",
        };
        if (RegisterClassExW(ref wc) == 0) { Console.WriteLine("RegisterClassEx 失败: " + Marshal.GetLastWin32Error()); return 1; }
        IntPtr hwnd = CreateWindowExW(0, "RaProbePollJitter", "raprobe", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("CreateWindowEx 失败: " + Marshal.GetLastWin32Error()); return 1; }
        if (!RawInput.Register(hwnd)) { Console.WriteLine("RegisterRawInputDevices 失败"); return 1; }

        // Progress has to be printed. A tool that says nothing for up to 140 seconds gets read as
        // "it hung", and the user cannot tell that apart from a real freeze.
        Console.WriteLine($"采集 {seconds}s。请像平时甩枪那样反复移动鼠标。");
        Console.WriteLine("（先等光标动起来，最多等 120 秒）");
        double travel = 0;
        GetCursorPos(out var prev);
        var wait = Stopwatch.StartNew();
        double lastBeat = -1;
        while (travel < 200 && wait.Elapsed.TotalSeconds < 120)
        {
            Pump();
            var now = CursorPos();                      // named helper: if the reported line is a
                                                        // lie, the frame name tells us where it moved
            travel += Math.Abs(now.X - prev.X) + Math.Abs(now.Y - prev.Y);
            prev = now;
            if (wait.Elapsed.TotalSeconds - lastBeat >= 3)
            {
                lastBeat = wait.Elapsed.TotalSeconds;
                Console.WriteLine($"  还在等光标动… 已等 {wait.Elapsed.TotalSeconds:0} 秒，" +
                                  $"累计移动 {travel:0} px（要 200 才开始计数）");
            }
        }
        if (travel < 200) Console.WriteLine("没等到移动，直接开始 —— 现在起请持续甩动鼠标。");

        pkts.Clear();
        Console.WriteLine($"开始计数 {seconds} 秒，请持续甩动鼠标…");
        var run = Stopwatch.StartNew();
        lastBeat = 0;
        while (run.Elapsed.TotalSeconds < seconds)
        {
            Pump();
            if (run.Elapsed.TotalSeconds >= lastBeat + 1)
            {
                lastBeat = Math.Floor(run.Elapsed.TotalSeconds);
                Console.WriteLine($"  {run.Elapsed.TotalSeconds:0}s / {seconds}s  已收到 {pkts.Count} 个包");
            }
        }
        DestroyWindow(hwnd);

        Analyze(run.Elapsed.TotalSeconds, settingsPath);
        return 0;
    }

    /// <summary>Struct sizes the Win32 ABI expects, exposed so the selftest can pin them. A P/Invoke
    /// struct that is smaller than the API writes back is stack corruption, not a type error.</summary>
    internal static (int Msg, int ClsEx, int Pt) MarshaledSizes =>
        (System.Runtime.InteropServices.Marshal.SizeOf<MSG>(),
         System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
         System.Runtime.InteropServices.Marshal.SizeOf<POINT>());

    static POINT CursorPos() { GetCursorPos(out POINT p); return p; }

    static void Pump()
    {
        while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
        {
            if (m.message == 0x12) return;
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
        Thread.SpinWait(200);      // no Sleep(1): a 1ms quantum would be pure measurement bias
    }

    static int wndErrors;
    static Exception? firstWndError;

    static unsafe IntPtr Wnd(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_INPUT) return DefWindowProcW(hWnd, msg, wParam, lParam);

        // One bad packet must never take down the whole measurement, and an exception that
        // escapes through the native DispatchMessageW transition gets a collapsed, misleading
        // managed stack (it blamed line 166, GetCursorPos, for weeks). Catch it here, where the
        // frame is still ours, and report it once.
        try
        {
            long us = (long)((Stopwatch.GetTimestamp() - t0) / freqPerUs);

            uint size = 0;
            RawInput.GetRawInputData(lParam, RawInput.RID_INPUT, IntPtr.Zero, ref size, RawInput.HEADER_SIZE);
            if (size == 0 || size > 1 << 16) return DefWindowProcW(hWnd, msg, wParam, lParam);
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = RawInput.GetRawInputData(lParam, RawInput.RID_INPUT, buf, ref size, RawInput.HEADER_SIZE);
                if (got >= 48 && got != 0xFFFFFFFFu)
                {
                    byte* b = (byte*)buf;
                    if (*(uint*)b == 0 && (*(ushort*)(b + 24) & 0x0001) == 0)      // mouse, relative
                        pkts.Add(new Pkt(us, *(int*)(b + 36), *(int*)(b + 40)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception e)
        {
            wndErrors++;
            firstWndError ??= e;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    internal readonly record struct Cell(double Speed, double Ideal, double Avg, double LossPct, double BadPct);

    internal sealed record JitterReport(int Packets, int Gaps, double MedUs, double P90Us, double P99Us, double MaxUs,
                                        double Hz, double Slow15, double Slow20, double Slow30, double Slow50,
                                        Cell[] Gain, string Note);

    /// <summary>Pure computation, so the selftest can feed it synthetic rhythms and check the pricing.</summary>
    internal static JitterReport? Measure(IReadOnlyList<Pkt> list, string settingsPath)
    {
        if (list.Count < 50) return null;

        var dt = new List<double>();
        for (int i = 1; i < list.Count; i++)
        {
            double d = list[i].us - list[i - 1].us;
            if (d > 0 && d < 50_000) dt.Add(d);            // longer pauses are not a polling gap
        }
        if (dt.Count < 20) return null;
        dt.Sort();
        double Q(double f) => dt[Math.Min(dt.Count - 1, (int)(dt.Count * f))];
        double med = Q(0.5);

        ra_profile prof;
        try
        {
            var doc = Json.Deserialize(File.ReadAllText(settingsPath));
            prof = Convert4.ToStruct(doc.Profiles[0]);
        }
        catch (Exception e) { throw new InvalidOperationException("读不到曲线配置: " + e.Message, e); }

        // Steady-state gain, not one-shot. The speed processor blends recent packets, so a single
        // Modify call still smells like the pointer was standing still and reports a lower speed
        // than the flick is really at (measured: one call at 8 counts/ms returned 1.188x while the
        // curve's own cap says 1.97x from 7.03 counts/ms). Feed the same rate repeatedly and take
        // the last output - that is what the driver applies in the middle of a moving flick.
        double GainX(double speed)
        {
            if (speed < 1e-3) return 1;      // a stationary pointer has no curve to consult
            var mod = new Modifier(new ModifierSettings(prof));
            var sp = new SpeedProcessor();
            sp.Init(prof.speed_processor_args);
            double last = 1;
            for (int i = 0; i < 24; i++)
                last = mod.Modify(new vec2d { x = speed, y = 0 }, sp, 1.0, 1.0).x / speed;
            return last;
        }

        // Only plausible polling gaps may price the curve. A 30ms gap means the hand stopped, not
        // that a packet was late; feeding it through gain() asks the model about a near-stationary
        // pointer, which is meaningless for the statistics and a path that can throw.
        var price = dt.FindAll(d => d <= 5_000);
        if (price.Count < 20) price = dt;

        // The driver derives speed = counts / measured time, so a packet that arrives k times late
        // is read as a hand moving k times slower, and the gain is whatever the curve says at that
        // (wrong) speed. This prices exactly that, per representative true input speed.
        var cells = new List<Cell>();
        foreach (double s in new[] { 2.0, 4.0, 6.0, 8.0, 12.0, 16.0 })   // 2 is below the X offset: must stay 1.000x
        {
            double ideal = GainX(s), sum = 0; int bad = 0;
            foreach (double d in price)
            {
                double g = GainX(s * med / d);
                sum += g;
                if (g < ideal * 0.95) bad++;
            }
            double avg = sum / price.Count;
            cells.Add(new Cell(s, ideal, avg, 100 * (avg / ideal - 1), 100.0 * bad / price.Count));
        }

        return new JitterReport(list.Count, dt.Count, med, Q(0.9), Q(0.99), dt[^1], 1_000_000.0 / med,
            100.0 * dt.Count(d => d > med * 1.5) / dt.Count, 100.0 * dt.Count(d => d > med * 2) / dt.Count,
            100.0 * dt.Count(d => d > med * 3) / dt.Count, 100.0 * dt.Count(d => d > med * 5) / dt.Count,
            cells.ToArray(), "");
    }

    static readonly List<string> report = new();

    /// <summary>Print and remember: the same text goes to a file, so reading the result never
    /// depends on the console staying un-paused, un-scrolled and un-closed.</summary>
    static void Say(string s) { Console.WriteLine(s); report.Add(s); }
    static void Say() => Say("");

    static void Analyze(double seconds, string settingsPath)
    {
        Say();
        JitterReport? r;
        try { r = Measure(pkts, settingsPath); }
        catch (Exception e) { Say("只报抖动，不报增益：" + e.Message); return; }
        if (r == null) { Say($"包数 {pkts.Count}，太少或移动太断续，结论不成立"); return; }

        Say($"包数 {r.Packets} / {seconds:0.0}s   间隔样本 {r.Gaps}" +
            (wndErrors > 0 ? $"   （处理中有 {wndErrors} 个包出错：{firstWndError}）" : ""));
        Say($"包间隔 ms: p50 {r.MedUs / 1000:0.000}  p90 {r.P90Us / 1000:0.000}  " +
                          $"p99 {r.P99Us / 1000:0.000}  max {r.MaxUs / 1000:0.000}");
        Say($"等效轮询率(按 p50): {r.Hz:0} Hz");
        Say($"   比 p50 慢 1.5/2/3/5 倍以上的包: {r.Slow15:0.0}% / {r.Slow20:0.0}% / {r.Slow30:0.0}% / {r.Slow50:0.0}%");
        int still = pkts.Count(p => p.dx == 0 && p.dy == 0);
        Say($"   没有位移的包 {100.0 * still / pkts.Count:0.0}%  ← Windows 会滤掉无变化的报告，所以慢慢挪的时候" +
            "上面的 p50 不等于轮询率；要测真实节律，全程保持明显甩动");

        Say();
        Say("把「包迟到」折算成增益损失（速度被低估 k 倍 → 查表查到 k 倍更慢的地方）：");
        Say("  真实输入速度   理想增益    抖动下平均增益    平均损失   损失>5%的包");
        foreach (var c in r.Gain)
            Say($"   {c.Speed,4:0} c/ms     {c.Ideal,7:0.000}      {c.Avg,7:0.000}      " +
                              $" {c.LossPct,6:+0.0;-0.0}%   {c.BadPct,6:0.0}%");
        Say();
        Say("注：这是用户态时间戳，含本进程调度抖动，驱动看不到那部分 —— 当作上界看；");
        Say("    但「空载 vs 开游戏」两次对比是公平的，偏差两边一样。");

        var csv = Path.Combine(Path.GetTempPath(), $"poll-jitter-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
        using (var w = new StreamWriter(csv))
        {
            w.WriteLine("us,dx,dy");
            foreach (var p in pkts) w.WriteLine(p.us.ToString(CultureInfo.InvariantCulture) + "," + p.dx + "," + p.dy);
        }
        Say("原始逐包记录已写: " + csv);

        // Same text to a file under the app's data folder. The user should never have to select,
        // copy and paste console output to get an answer - the window can be closed, scrolled or
        // paused by QuickEdit, and the assistant can read the file directly.
        try
        {
            var dir = Path.Combine(AppData.Root, "jitter");
            Directory.CreateDirectory(dir);
            var txt = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            File.WriteAllText(txt, string.Join(Environment.NewLine, report) + Environment.NewLine);
            Say("文字报告已写: " + txt);
        }
        catch (Exception e) { Say("文字报告没能保存: " + e.Message); }
    }
}
