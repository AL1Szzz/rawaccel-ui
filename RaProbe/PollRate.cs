using System.Runtime.InteropServices;

namespace RaProbe;

/// <summary>
/// Counts the mouse packets Windows actually delivers to applications, per device.
///
/// Why this exists: every "polling rate" reader that works through window messages
/// (browsers' pointermove, MOUSERATE, Mouse Rate Checker) sees WM_MOUSEMOVE, which the
/// system is free to coalesce. A low number there says nothing about the sensor or the
/// dongle. This counts RAW INPUT records instead - the same stream a raw-input game such
/// as CS2 consumes - and reports the packet rate together with the per-packet displacement,
/// which is what separates "the mouse really slowed down" from "Windows merged the packets".
///
/// Two things that are easy to get wrong, both found the hard way here:
///   * the HRAWINPUT for an event arrives in WM_INPUT's lParam, not wParam;
///   * and even reading lParam is not enough. At 1000Hz+ several events pile up between pump
///     iterations, and an *unfiltered* PeekMessage removes the WM_INPUT messages along with the
///     queued data behind them - the first version counted 482 notifications and only 3 readable
///     packets. So the pump below filters its message ranges to skip WM_INPUT and reads everything
///     with GetRawInputBuffer, which is Microsoft's documented buffered pattern.
///
/// Read-only: it registers for raw input on a message-only window. It never moves the pointer,
/// never writes the driver, never changes the display, and does not take focus (RIDEV_INPUTSINK
/// keeps it fed while a game is in front). The cursor poll below is a control channel: it proves
/// whether the hand moved, so "0 packets" can be told apart from "capture broken".
/// </summary>
public static class PollRate
{
    // ---------- win32 ----------

    const int WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x00000003;
    const uint RIM_TYPEMOUSE = 0;
    const uint QS_ALLINPUT = 0x04FF;
    const uint QS_RAWINPUT = 0x0400;
    const int MOUSE_MOVE_ABSOLUTE = 1;
    static readonly IntPtr HWND_MESSAGE = new(-3);      // message-only window parent
    const int RAWHEADER = 24;                           // sizeof(RAWINPUTHEADER), x64

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

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

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    /// <summary>Returns the number of records read, (uint)-1 when the buffer was too small
    /// (size then holds what is needed), 0 when the queue is empty or on error.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputBuffer(IntPtr data, ref uint size, uint headerSize);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll")]
    static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint ms, uint wakeMask);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    const int WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
    const int SW_SHOWNORMAL = 1;

    const uint PM_REMOVE = 1;
    const int WM_APP_QUIT = 0x8000 + 1;

    // ---------- captured state ----------

    sealed class Dev
    {
        public string Id = "";
        public string Name = "";
        public readonly List<int> dx = new();
        public readonly List<int> dy = new();
        public readonly List<long> at = new();        // QPC ticks at delivery
        public long MovePackets, ButtonPackets, AbsolutePackets;
        public long SumAbs;
        public long Packets => dx.Count;
    }

    static readonly Dictionary<IntPtr, Dev> devs = new();
    static readonly Dictionary<IntPtr, (string id, string name)> known = new();
    static readonly List<string> log = new();
    static WndProcDelegate? kept;                     // must survive GC while the window lives
    static long freq, t0;
    static IntPtr buf = IntPtr.Zero;
    static uint bufSize;
    static int blockSzSeen;
    static long drainCalls, drainRecords, drainShrunk;
    static readonly List<string> thrown = new();
    static POINT lastCursor;
    static long cursorSteps, cursorTravel;

    public static int Run(string[] args)
    {
        double seconds = 15;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--hz" && double.TryParse(args[i + 1], out var s) && s > 0) seconds = Math.Min(s, 300);

        freq = System.Diagnostics.Stopwatch.Frequency;
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bufSize = 256 * 64;
        buf = Marshal.AllocHGlobal((int)bufSize);

        try
        {
            foreach (var (h, e) in RaCore.DeviceEnum.Handles()) known[h] = e;
            foreach (var m in RaCore.DeviceEnum.ListMice())
                log.Add($"device {m.Id}  '{m.Name}'  handles={m.Handles}");
        }
        catch (Exception e) { log.Add("设备枚举失败：" + e.Message); }

        kept = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(kept),
            lpszClassName = "RaProbePollRate",
        };
        if (RegisterClassExW(ref wc) == 0) { Console.WriteLine("注册窗口类失败：" + Marshal.GetLastWin32Error()); return 1; }

        // --hz-fg counts on a REAL, VISIBLE, foreground window instead of a message-only
        // background sink. Every number this tool has ever printed came from the background path,
        // and a game in focus uses the foreground one - if Windows coalesces differently between
        // the two, then 125 was an artifact of how we listen, not of the mouse. (GetConsoleWindow
        // is no help: that HWND belongs to conhost, so raw input would go to that process.)
        bool fg = args.Contains("--hz-fg");
        IntPtr hwnd;
        if (fg)
        {
            hwnd = CreateWindowExW(0, "RaProbePollRate", "RaProbe 回报率采样 - 请保持本窗口在最前面",
                WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, 120, 120, 620, 190,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("建前台窗口失败：" + Marshal.GetLastWin32Error()); return 1; }
            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetForegroundWindow(hwnd);
        }
        else
        {
            hwnd = CreateWindowExW(0, "RaProbePollRate", null, 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("建消息窗口失败：" + Marshal.GetLastWin32Error()); return 1; }
        }

        var rid = new RAWINPUTDEVICE[]
        {
            new() { usUsagePage = 1, usUsage = 2, dwFlags = fg ? 0u : RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Console.WriteLine("RegisterRawInputDevices 失败：" + Marshal.GetLastWin32Error());
            DestroyWindow(hwnd);
            return 1;
        }

        GetCursorPos(out lastCursor);

        Console.WriteLine($"== 原始输入包计数，{seconds:0} 秒 ==");
        Console.WriteLine(fg
            ? "投递路径 = 前台可见窗口（游戏拿到的是这一路）。*** 屏幕上会弹一个小窗口，这期间别点别的窗口、别切走 ***"
            : "投递路径 = 消息专用窗口 + RIDEV_INPUTSINK（后台接收，不抢焦点）。");
        Console.WriteLine("只注册 raw input：不动指针、不写驱动。现在请持续移动鼠标，画 8 字或来回扫都可以，别停。");

        var quitThread = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
            PostMessageW(hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true };
        quitThread.Start();

        bool done = false;
        while (!done)
        {
            // Deliberately two ranges that SKIP WM_INPUT. Removing the WM_INPUT message with an
            // unfiltered PeekMessage consumes the raw input queue behind it: the first version of
            // this tool did that, and 482 notifications produced only 3 readable packets (the
            // HRAWINPUT handles were already stale). Leaving the messages in place keeps every
            // packet in the queue, where GetRawInputBuffer below reads them all.
            while (true)
            {
                bool got = PeekMessageW(out MSG m, IntPtr.Zero, 0, (uint)WM_INPUT - 1, PM_REMOVE)
                        || PeekMessageW(out m, IntPtr.Zero, (uint)WM_INPUT + 1, 0xFFFF, PM_REMOVE);
                if (!got) break;
                if (m.message == WM_APP_QUIT) { done = true; break; }
                DispatchMessageW(ref m);
            }
            Drain();
            PollCursor();
            // QS_ALLINPUT includes QS_RAWINPUT, and because we deliberately leave WM_INPUT in the
            // queue that flag is always set - waiting on it spun at ~560k iterations/s and burned a
            // whole core, which is exactly the kind of disturbance that would invalidate a timing
            // measurement. Exclude it and let a 1ms timeout bound the loop instead.
            if (!done) MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 1, QS_ALLINPUT & ~QS_RAWINPUT);
        }

        DestroyWindow(hwnd);
        Report((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1.0 / freq, fg);
        return 0;
    }

    /// <summary>Control channel: did the hand actually move? Counted from the system cursor
    /// position, which is downstream of everything being measured - it can only prove motion
    /// happened, never prove a polling rate.</summary>
    static void PollCursor()
    {
        if (!GetCursorPos(out var p)) return;
        int d = Math.Abs(p.x - lastCursor.x) + Math.Abs(p.y - lastCursor.y);
        if (d != 0) { cursorSteps++; cursorTravel += d; }
        lastCursor = p;
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    static void Drain()
    {
        drainCalls++;
        for (int guard = 0; guard < 64; guard++)
        {
            uint size = bufSize;
            uint n = GetRawInputBuffer(buf, ref size, RAWHEADER);
            if (n == 0) return;
            if (n == uint.MaxValue)                                  // too small: grow, capped, then retry
            {
                drainShrunk++;
                int want = (int)Math.Min(Math.Max(size, bufSize * 2), 8u * 1024 * 1024);
                if ((uint)want <= bufSize) return;                   // already at the cap: stop, do not spin
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(want);
                bufSize = (uint)want;
                continue;
            }

            // walk the packed blocks: each header's dwSize is the whole block length
            long pos = 0;
            for (uint i = 0; i < n; i++)
            {
                IntPtr block = buf + (int)pos;
                uint blockSz = (uint)Marshal.ReadInt32(block, 4);
                if (blockSz <= RAWHEADER) return;
                HandleBlock(block, blockSz);
                drainRecords++;
                pos += blockSz;
                if ((ulong)pos >= bufSize) return;
            }
        }
    }

    static void HandleBlock(IntPtr b, uint blockSz)
    {
        uint type = (uint)Marshal.ReadInt32(b);
        if (type != RIM_TYPEMOUSE) return;
        IntPtr device = Marshal.ReadIntPtr(b, 8);
        blockSzSeen = (int)blockSz;

        // RAWMOUSE follows the 24-byte header. Its union has shipped both packed and aligned in
        // the headers, so the offsets come from the block length the API reports: 48 = natural
        // x64 alignment (lLastX at +12), 46 = pack(1) (lLastX at +10).
        bool packed = blockSz == 46;
        int offX = RAWHEADER + (packed ? 10 : 12);
        int offY = RAWHEADER + (packed ? 14 : 16);
        ushort flags = (ushort)Marshal.ReadInt16(b, RAWHEADER);

        var d = Get(device);
        d.at.Add(System.Diagnostics.Stopwatch.GetTimestamp());
        int dx = Marshal.ReadInt32(b, offX);
        int dy = Marshal.ReadInt32(b, offY);
        d.dx.Add(dx);
        d.dy.Add(dy);
        if ((flags & MOUSE_MOVE_ABSOLUTE) != 0) d.AbsolutePackets++;
        else if (dx != 0 || dy != 0) { d.MovePackets++; d.SumAbs += Math.Abs(dx) + Math.Abs(dy); }
        else d.ButtonPackets++;
    }

    static Dev Get(IntPtr handle)
    {
        if (devs.TryGetValue(handle, out var d)) return d;
        d = new Dev();
        if (known.TryGetValue(handle, out var k)) { d.Id = k.id; d.Name = k.name; }
        devs[handle] = d;
        return d;
    }

    static void Report(double elapsed, bool fg)
    {
        double want = Math.Round(elapsed);
        Console.WriteLine();
        Console.WriteLine("   投递路径：" + (fg ? "前台可见窗口（游戏用的这一路）" : "消息专用窗口 + INPUTSINK（后台）"));
        foreach (var line in log) Console.WriteLine("   " + line);
        Console.WriteLine();
        Console.WriteLine($"   采样 {elapsed:0.00}s · GetRawInputBuffer 调用 {drainCalls} 次 / 解出 {drainRecords} 块 / 缓冲不足 {drainShrunk} 次（当前缓冲 {bufSize}B）· 对照：指针位置变化 {cursorSteps} 次 / 共 {cursorTravel} px");
        foreach (var t in thrown) Console.WriteLine("   *** 采集异常：" + t);

        var all = devs.Values.OrderByDescending(d => d.Packets).ToList();
        if (all.Count == 0 || all.Sum(d => d.Packets) == 0)
        {
            Console.WriteLine(cursorSteps > 50
                ? "   *** 指针确实在动，但一个 raw input 包都没抓到：这是采集的问题，不是鼠标的问题。把这段发给助手。"
                : $"   没抓到包，而且指针也没怎么动（对照只记到 {cursorSteps} 次位置变化）：再跑一次，这 {want:0} 秒里手别停。");
            return;
        }

        foreach (var d in all.Where(d => d.Packets > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"   --- {(string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name)}  [{d.Id}] ---");
            double hz = d.Packets / elapsed;
            var (b50, b90, activeBuckets, totalBuckets) = BucketRate(d.at, elapsed);
            var gaps = new List<double>();
            for (int i = 1; i < d.at.Count; i++) gaps.Add((d.at[i] - d.at[i - 1]) * 1000.0 / freq);
            var mag = d.dx.Zip(d.dy, (a, b) => Math.Abs(a) + Math.Abs(b)).Where(x => x > 0).OrderBy(x => x).ToList();

            Console.WriteLine($"   包数 {d.Packets}（移动 {d.MovePackets} · 按键/零位移 {d.ButtonPackets} · 绝对坐标 {d.AbsolutePackets}）");
            Console.WriteLine($"   活跃期包/秒（100ms 桶）p50={b50:0}  p90={b90:0}  —— 有包的桶 {activeBuckets}/{totalBuckets}；全程平均 {hz:0.0}/秒（被手停下的时间稀释，只作参考）");
            Console.WriteLine($"   合计位移 {d.SumAbs} counts = {d.SumAbs / elapsed:0} counts/秒");
            if (gaps.Count > 2)
                Console.WriteLine($"   投递间隔 ms  p50={Pct(gaps, .50):0.000}  p90={Pct(gaps, .90):0.000}  p99={Pct(gaps, .99):0.000}  min={gaps.Min():0.000}  max={gaps.Max():0.000}（1ms 轮询会量化这个数，比较时优先看上面的包/秒）");
            if (mag.Count > 2)
                Console.WriteLine($"   每个移动包 |dx|+|dy| counts  p50={Pct(mag, .50):0}  p90={Pct(mag, .90):0}  max={mag.Max()}");
            Console.WriteLine($"   块长自检 RAWINPUT={blockSzSeen}（48=x64 自然对齐 / 46=pack1，位移数值按这个解）");

            Console.WriteLine("   判读：" + (b50 >= 3000 ? "活跃期就有 3000+ 包/秒，4000Hz 这一档确实到了应用层"
                          : b50 >= 1500 ? "活跃期 ~2000 包/秒：2000Hz 是实的，4000 没吃到"
                          : b50 >= 700 ? $"活跃期 ~{b50:0} 包/秒：只到 1000Hz 级"
                          : $"活跃期 ~{b50:0} 包/秒、每包位移中位 {Pct(mag, .50):0} counts —— 这是合包后的投递节律，不是传感器报告率；但这只是后台接收路径，游戏用的是前台窗口那一路，要 --hz-fg 才算数"));
            Console.WriteLine($"   MACHINE\tpath={(fg ? "fg" : "mo")}\t{d.Id}\tpackets={d.Packets}\tactive_hz_p50={b50:0}\tactive_hz_p90={b90:0}\tmean_hz={hz:0.0}\tgap_p50_ms={(gaps.Count > 2 ? Pct(gaps, .50) : 0):0.000}\tmag_p50={(mag.Count > 2 ? Pct(mag, .50) : 0):0}\tcounts_per_s={d.SumAbs / elapsed:0}\tcursor_steps={cursorSteps}");
        }

        Console.WriteLine();
        Console.WriteLine("   怎么定案（只有 A/B 说得清）：HUB 里 4000Hz 跑一次，2000Hz 再跑一次，手速差不多。");
        Console.WriteLine("   看三个数：包/秒 翻倍 = 真的多收到一倍；每包位移 减半 = 同样的手速被切成更细的包；");
        Console.WriteLine("   counts/秒 基本不变 = 位移没丢。三条同时成立 = 4K 是实的，网页那一千多是网页的天花板。");
        Console.WriteLine("   先分投递路径再下结论：后台 sink 低而前台窗口高 = 管道差别，不是鼠标（今天就栽在这）");
        Console.WriteLine("   两条路都低 = 这一层真的没收到，再查 USB/固件。");
    }

    /// <summary>Packet rate over the 100ms buckets where packets actually arrive. The whole-run
    /// average is diluted by every second the hand was idle, and the inter-arrival gaps are
    /// quantised by the 1ms poll, so this bucketed number is the one to compare between two
    /// polling-rate settings.</summary>
    static (double p50, double p90, int active, int total) BucketRate(List<long> at, double elapsed)
    {
        const double bucketMs = 100;
        if (at.Count == 0 || elapsed <= 0) return (0, 0, 0, 0);
        int n = Math.Max(1, (int)Math.Ceiling(elapsed * 1000 / bucketMs));
        var counts = new int[n];
        foreach (var a in at)
        {
            int b = (int)(Math.Max(0, a - t0) * 1000.0 / freq / bucketMs);
            if (b < n) counts[b]++;
        }
        var active = counts.Where(c => c > 0).Select(c => c * (1000.0 / bucketMs)).OrderBy(x => x).ToList();
        if (active.Count == 0) return (0, 0, 0, n);
        double P(double p) => active[Math.Clamp((int)Math.Round(p * (active.Count - 1)), 0, active.Count - 1)];
        return (P(.50), P(.90), active.Count, n);
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1)];
    }

    static double Pct(List<int> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1)];
    }
}
