using System.Runtime.InteropServices;

namespace RaCore;

/// <summary>
/// Display mode switching for the fullscreen aim test. Only modes the monitor actually
/// reports are offered, and the original mode is always restored when the caller is done.
/// </summary>
public static class Display
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        public const int CCHDEVICENAME = 32;
        public const int CCHFORMFORMAT = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMFORMAT)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    const int ENUM_CURRENT_SETTINGS = -1;
    const int ENUM_REGISTRY_SETTINGS = -2;
    const uint CDS_FULLSCREEN = 0x00000004;
    const uint DM_PELSWIDTH = 0x00080000;
    const uint DM_PELSHEIGHT = 0x00100000;
    const uint DM_DISPLAYFREQUENCY = 0x00400000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsExW(string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    public readonly record struct Mode(int Width, int Height, int Refresh, int Bits);

    const int MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    delegate bool MonitorEnum(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsExW(string? deviceName, int modeNum, ref DEVMODE devMode, uint dwFlags);

    /// <summary>
    /// Device name of the primary monitor (\.\DISPLAY1 style). Passing NULL to the
    /// EnumDisplay* APIs is not safe: it can resolve to a device that is not the one the
    /// user is looking at, which is how a 3440x1440 desktop reported 1920x1440 as "current".
    /// </summary>
    public static string? PrimaryDevice()
    {
        string? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfoW(hmon, ref mi) && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
            {
                found = mi.szDevice;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Physical pixel geometry of the primary monitor.</summary>
    public static (int x, int y, int w, int h) PrimaryGeometry()
    {
        int x = 0, y = 0, w = 0, h = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfoW(hmon, ref mi) && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
            {
                x = mi.rcMonitor.Left; y = mi.rcMonitor.Top;
                w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return (x, y, w, h);
    }

    public static Mode Current() => Current(PrimaryDevice());

    public static Mode Current(string? device)
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, ref dm, 0)) return new Mode(0, 0, 0, 0);
        return new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel);
    }

    public static List<Mode> AvailableModes() => AvailableModes(PrimaryDevice());

    public static List<Mode> AvailableModes(string? device)
    {
        var list = new List<Mode>();
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();

        for (int i = 0; i < 4096 && EnumDisplaySettingsExW(device, i, ref dm, 0); i++)
        {
            var m = new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel);
            if (!list.Contains(m)) list.Add(m);
        }
        return list.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Refresh).ToList();
    }

    static string? usedDevice;
    static Mode? original;

    /// <summary>True when this process currently holds a CDS_FULLSCREEN mode change that still needs restoring.</summary>
    public static bool HasPendingChange => original is not null;

    /// <summary>
    /// Requests a mode with CDS_FULLSCREEN only (registry untouched). Windows reverts such a
    /// change when the process exits normally, but that is not a guarantee against a crash,
    /// so callers must still call Restore() on every exit path and surface a failure.
    /// </summary>
    public static bool TrySet(Mode m)
    {
        var device = PrimaryDevice();
        if (string.IsNullOrEmpty(device)) return false;

        var cur = Current(device);
        if (cur.Width == 0) return false;
        if (m.Width <= 0 || m.Height <= 0) return false;
        if (cur.Width == m.Width && cur.Height == m.Height && (m.Refresh <= 0 || cur.Refresh == m.Refresh))
        {
            original ??= cur;
            usedDevice = device;
            return true;
        }

        // only offer modes the driver actually enumerates for this device
        bool offered = AvailableModes(device).Any(o => o.Width == m.Width && o.Height == m.Height
                                                   && (m.Refresh <= 0 || o.Refresh == m.Refresh));
        if (!offered) return false;

        var dm = new DEVMODE
        {
            dmSize = (short)Marshal.SizeOf<DEVMODE>(),
            dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY,
            dmPelsWidth = m.Width,
            dmPelsHeight = m.Height,
            dmDisplayFrequency = m.Refresh,
            dmBitsPerPel = m.Bits > 0 ? m.Bits : 32,
        };

        int rc = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        if (rc != 0 /* DISP_CHANGE_SUCCESSFUL */) return false;

        var after = Current(device);
        if (after.Width != m.Width || after.Height != m.Height) return false;   // reported success but nothing changed

        original ??= cur;
        usedDevice = device;
        return true;
    }

    /// <summary>Restores the mode remembered before TrySet. False when the request was refused.</summary>
    public static bool Restore()
    {
        if (original is not { } o) return true;         // nothing to undo
        var device = usedDevice ?? PrimaryDevice();
        var dm = new DEVMODE
        {
            dmSize = (short)Marshal.SizeOf<DEVMODE>(),
            dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY,
            dmPelsWidth = o.Width,
            dmPelsHeight = o.Height,
            dmDisplayFrequency = o.Refresh,
            dmBitsPerPel = o.Bits,
        };
        int rc = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        var after = Current(device);
        bool ok = rc == 0 && after.Width == o.Width && after.Height == o.Height;
        if (ok) { original = null; usedDevice = null; }
        return ok;     // keep the backup so a caller can retry
    }

    /// <summary>Physical pixel bounds of the primary monitor.</summary>
    public static (int w, int h) ScreenPixels()
    {
        var (_, _, w, h) = PrimaryGeometry();
        if (w > 0) return (w, h);
        var c = Current();
        return (c.Width, c.Height);
    }
}
