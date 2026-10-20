using System.Diagnostics;
using System.Runtime.InteropServices;
using RaCore;

namespace RaProbe;

/// <summary>
/// Read-only ground truth for the WM_INPUT path: registers raw input on a message-only window,
/// pumps messages for a few seconds and reports what actually arrives - packet count, the raw
/// usFlags values, decoded deltas and the originating device names. Nothing is written to the
/// driver or the display. Use this when the in-app telemetry shows zero samples: it separates
/// "no WM_INPUT at all" from "packets arrive but my filter drops them".
/// </summary>
internal static class RawInputProbe
{
    const int WM_INPUT = 0x00FF;
    const uint HWND_MESSAGE = 0xFFFFFFFD;      // (HWND)-3; 0xFFFFFFFF is HWND_BROADCAST and fails with 1400

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
                 public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string? name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const uint PM_REMOVE = 1;

    static readonly Dictionary<ushort, int> flagHist = new();
    static readonly Dictionary<string, int> deviceHist = new();
    static int packets, mouse, parsed, relative, absolute, moved, droppedByGate, decodeFailed;
    static int appPathOk, appPathBad;
    static long sumX, sumY;
    static readonly List<string> firstHex = new();

    static WndProc? kept;           // the delegate must outlive the window

    public static void Run(string[] args)
    {
        int seconds = 8;
        int si = Array.FindIndex(args, a => a == "--seconds");
        if (si >= 0 && si + 1 < args.Length) int.TryParse(args[si + 1], out seconds);
        if (seconds <= 0 || seconds > 120) seconds = 8;

        var instance = GetModuleHandleW(null);
        kept = Wnd;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(kept),
            hInstance = instance,
            lpszClassName = "RaProbeRawInput",
        };
        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0) { Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error()); return; }

        // a real but invisible overlapped window, like the WPF main window: a message-only
        // window is not the same delivery path and would hide an app-specific problem
        IntPtr hwnd = CreateWindowExW(0, "RaProbeRawInput", "raprobe", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("CreateWindowEx failed: " + Marshal.GetLastWin32Error()); return; }

        bool registered = RawInput.Register(hwnd);
        Console.WriteLine($"hidden window hwnd = 0x{hwnd.ToInt64():X}, RegisterRawInputDevices = {registered}");
        Console.WriteLine($"等光标开始移动（最多 120 秒），然后计数 {seconds} 秒 —— 请动一下鼠标");

        double cursorTravel = 0;
        GetCursorPos(out var prev);
        var wait = Stopwatch.StartNew();
        while (cursorTravel < 40 && wait.Elapsed.TotalSeconds < 120)
        {
            PumpOnce();
            GetCursorPos(out var now);
            cursorTravel += Math.Abs(now.X - prev.X) + Math.Abs(now.Y - prev.Y);
            prev = now;
        }
        Console.WriteLine($"光标已移动 {cursorTravel:0} px，开始计数…");

        packets = parsed = mouse = relative = absolute = moved = droppedByGate = decodeFailed = 0;
        double travelBeforeCounting = cursorTravel;
        var run = Stopwatch.StartNew();
        prev = new POINT(); GetCursorPos(out prev);
        while (run.Elapsed.TotalSeconds < seconds)
        {
            PumpOnce();
            GetCursorPos(out var now);
            cursorTravel += Math.Abs(now.X - prev.X) + Math.Abs(now.Y - prev.Y);
            prev = now;
        }

        DestroyWindow(hwnd);
        Report(cursorTravel - travelBeforeCounting, run.Elapsed.TotalSeconds);
    }

    static void PumpOnce()
    {
        while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (m.message == 0x12 /*WM_QUIT*/) return;
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
        Thread.Sleep(1);
    }

    static void Report(double cursorTravel, double measuredSeconds)
    {
        Console.WriteLine();
        Console.WriteLine($"measured window    : {measuredSeconds:0.0}s, cursor travelled {cursorTravel:0} px");
        Console.WriteLine($"WM_INPUT seen      : {packets}  ({packets / Math.Max(measuredSeconds, 0.001):0} per second)");
        Console.WriteLine($"  type==mouse        : {mouse}");
        Console.WriteLine($"  decoded ok         : {parsed}   (decode failures {decodeFailed})");
        Console.WriteLine($"  relative / absolute: {relative} / {absolute}");
        Console.WriteLine($"  with nonzero delta : {moved}   dropped by the current gate: {droppedByGate}");
        Console.WriteLine($"  summed dx/dy       : {sumX} / {sumY}");
        Console.WriteLine($"  RawInput.Parse ok  : {appPathOk}   rejected: {appPathBad}" +
                          (appPathOk == 0 && packets > 0 ? "   <<< App 里 fed=0 的原因就在这" : ""));
        Console.WriteLine();
        Console.WriteLine("usFlags histogram (what RAWMOUSE.usFlags really contains on this machine):");
        if (flagHist.Count == 0) Console.WriteLine("   (none - no mouse packet arrived at all)");
        foreach (var kv in flagHist.OrderByDescending(k => k.Value))
            Console.WriteLine($"   0x{kv.Key:X4}  x{kv.Value}   " + Describe(kv.Key));
        Console.WriteLine();
        Console.WriteLine("devices:");
        foreach (var kv in deviceHist.OrderByDescending(k => k.Value))
            Console.WriteLine($"   x{kv.Value,-6} {kv.Key}");
        if (firstHex.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("first packets (hex, x64 RAWINPUT: type@0 hDevice@8 usFlags@24 buttons@28 dx@36 dy@40):");
            foreach (var h in firstHex) Console.WriteLine("   " + h);
        }
        Console.WriteLine();
        if (packets == 0) Console.WriteLine("=> 结论：这个窗口根本没收到 WM_INPUT（注册或 sink 的问题，不是过滤的问题）");
        else if (moved > 0 && relative == 0) Console.WriteLine("=> 结论：包进来了，但我对 usFlags 的相对/绝对判断是错的");
        else if (relative > 0 && droppedByGate == 0) Console.WriteLine("=> 结论：包能正常解码，App 里 0 采样的原因在别处（记录器或窗口句柄）");
    }

    static string Describe(ushort f) =>
        f == 0x0000 ? "MOUSE_MOVE_RELATIVE(0x00)"
        : f == 0x0001 ? "MOUSE_MOVE_ABSOLUTE(0x01)"
        : f == 0x0002 ? "0x0002 (VIRTUAL_DESKTOP bit only)"
        : f == 0x0003 ? "0x0003 absolute|virtual"
        : f == 0x0009 ? "0x0009 absolute|nocoalesce"
        : $"0x{f:X4} (unusual - my flag table is wrong)";

    static unsafe IntPtr Wnd(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_INPUT) return DefWndProc(hWnd, msg, wParam, lParam);
        packets++;

        uint size = 0;
        RawInput.GetRawInputData(lParam, RawInput.RID_INPUT, IntPtr.Zero, ref size, RawInput.HEADER_SIZE);
        if (size == 0 || size > 1 << 16) { decodeFailed++; return DefWndProc(hWnd, msg, wParam, lParam); }

        // the app's own decode path, so a mistake in RawInput.Parse shows up here too
        var viaApp = RawInput.Parse(lParam);
        if (viaApp.bytes != 0 && viaApp.relative) appPathOk++; else appPathBad++;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint got = RawInput.GetRawInputData(lParam, RawInput.RID_INPUT, buf, ref size, RawInput.HEADER_SIZE);
            if (got == 0 || got == 0xFFFFFFFFu || got < 48) { decodeFailed++; return DefWndProc(hWnd, msg, wParam, lParam); }
            parsed++;
            byte* b = (byte*)buf;
            if (*(uint*)b != 0) return DefWndProc(hWnd, msg, wParam, lParam);   // not a mouse packet
            mouse++;

            ushort flags = *(ushort*)(b + 24);
            flagHist[flags] = flagHist.TryGetValue(flags, out int n) ? n + 1 : 1;
            bool abs = (flags & 0x0001) != 0;
            if (abs) absolute++; else relative++;

            int dx = *(int*)(b + 36), dy = *(int*)(b + 40);
            if (dx != 0 || dy != 0) { moved++; sumX += dx; sumY += dy; }
            else droppedByGate++;

            var dev = new IntPtr(*(long*)(b + 8));
            string name = RawInput.DeviceId(dev);
            deviceHist[name] = deviceHist.TryGetValue(name, out int c) ? c + 1 : 1;

            if (firstHex.Count < 4)
            {
                var bytes = new byte[Math.Min(48, got)];
                Marshal.Copy(buf, bytes, 0, bytes.Length);
                firstHex.Add(Convert.ToHexString(bytes) + $"   dx={dx} dy={dy} flags=0x{flags:X4}");
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return DefWndProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    static IntPtr DefWndProc(IntPtr h, uint m, IntPtr w, IntPtr l) => DefWindowProcW(h, m, w, l);
}
