using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RaUI;

/// <summary>
/// Raw mouse deltas for one window, straight off WM_INPUT.
///
/// Why not WPF's MouseMove: those are pointer positions after Windows has applied pointer speed and
/// the "enhance pointer precision" curve, and they are coalesced - both of which destroy exactly the
/// thing the calibration window is trying to count. WM_INPUT carries the per-packet displacement the
/// device reported (after Raw Accel, since the driver sits under the device stack), which is what a
/// game sees.
///
/// Registered with RIDEV_INPUTSINK: the dial must keep turning even if the window is not the active
/// one. Foreground-only delivery was the first version and it failed the one test that matters - a
/// window that never got the foreground could not be told apart from a mouse that never moved. The
/// window is topmost and fullscreen while the dial is up, so "another program has focus" means the
/// player alt-tabbed away, and a dial that quietly kept counting is a smaller problem than a dial
/// that looks broken.
/// </summary>
sealed class RawMouse : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RIM_TYPEMOUSE = 0;
    const uint RID_INPUT = 0x10000003;
    const int RAWINPUTHEADER_SIZE = 24;      // x64: dwType, dwSize, hDevice, wParam
    const int RAWINPUT_SIZE = 48;            // header + RAWMOUSE
    const int MOUSE_MOVE_ABSOLUTE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    HwndSource? source;
    IntPtr buffer = Marshal.AllocHGlobal(RAWINPUT_SIZE);

    public long Dx { get; private set; }
    public long Dy { get; private set; }
    public long Packets { get; private set; }
    /// <summary>Non-null when raw input never started, so the window can say so instead of showing a dead dial.</summary>
    public string? Error { get; private set; }

    /// <summary>Attach after the window handle exists (SourceInitialized or later).</summary>
    public static RawMouse Attach(Window w)
    {
        var rm = new RawMouse();
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) { rm.Error = "窗口还没创建"; return rm; }

        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            rm.Error = "原始输入注册失败（" + Marshal.GetLastWin32Error() + "）";
            return rm;
        }

        rm.source = HwndSource.FromHwnd(hwnd);
        if (rm.source == null) { rm.Error = "拿不到窗口的消息源"; return rm; }
        rm.source.AddHook(rm.WndProc);
        return rm;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;

        uint size = RAWINPUT_SIZE;
        uint got = GetRawInputData(lParam, RID_INPUT, buffer, ref size, RAWINPUTHEADER_SIZE);
        if (got == uint.MaxValue || got < RAWINPUTHEADER_SIZE) return IntPtr.Zero;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
        if (header.dwType != RIM_TYPEMOUSE) return IntPtr.Zero;

        var mouse = Marshal.PtrToStructure<RAWMOUSE>(IntPtr.Add(buffer, RAWINPUTHEADER_SIZE));
        if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0) return IntPtr.Zero;   // tablets/remote desktop

        // left unhandled on purpose: WM_INPUT has no default processing worth blocking, and
        // swallowing it would hide the packet from anything else in this window that wants it
        Dx += mouse.lLastX;
        Dy += mouse.lLastY;
        Packets++;
        return IntPtr.Zero;
    }

    public void Reset()
    {
        Dx = 0;
        Dy = 0;
        Packets = 0;
    }

    public void Dispose()
    {
        if (source != null) source.RemoveHook(WndProc);
        source = null;
        if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
    }
}
