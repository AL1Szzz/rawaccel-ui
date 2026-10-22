using System.Runtime.InteropServices;

namespace RaCore;

/// <summary>
/// Post-driver mouse telemetry. WM_INPUT here already contains whatever rawaccel.sys did to
/// the stream (the filter sits below the HID class driver), so the numbers are OUTPUT speeds,
/// never physical hand speeds. Timing is user-mode delivery time: at 1000-2000 Hz several
/// packets arrive inside one dispatcher tick, so a window's speed is an approximation.
/// </summary>
public static class RawInput
{
    public const int WM_INPUT = 0x00FF;
    const uint RIM_TYPEMOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint Type; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(IntPtr ptr, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand,
        IntPtr data, ref uint pcbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIM_TYPEMOUSE_FLAG = 0;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    /// <summary>x64 RAWINPUT: header 24 bytes (type, command, hDevice, hWindow), then RAWMOUSE.</summary>
    public const int HEADER_SIZE = 24;
    const int OFF_TYPE = 0;
    const int OFF_DEVICE = 8;
    const int OFF_MOUSE_FLAGS = 24;       // usFlags
    const int OFF_BUTTONS = 24 + 4;
    const int OFF_LAST_X = 24 + 12;
    const int OFF_LAST_Y = 24 + 16;
    const int MOUSE_DATA_MIN = 24 + 24;
    // RAWMOUSE.usFlags: MOUSE_MOVE_RELATIVE is 0x00 (no bit set), MOUSE_MOVE_ABSOLUTE is 0x01.
    // Testing bit0 for "relative" inverts the meaning and silently drops every real packet.
    const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
    const ushort MOUSE_VIRTUAL_DESKTOP = 0x0002;
    const ushort MOUSE_ATTRIBUTES_CHANGED = 0x0004;

    public static bool Register(IntPtr hwnd)
    {
        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = RIDEV_INPUTSINK,   // keep packets flowing while another window has focus
            hwndTarget = hwnd,
        };
        return RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static bool Unregister(IntPtr hwnd)
    {
        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = RIDEV_INPUTSINK | RIDEV_REMOVE,
            hwndTarget = hwnd,
        };
        try { return RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()); }
        catch { return false; }
    }

    /// <summary>Device handle -> its HID\VID... id, the same string the driver stores per device.</summary>
    public static string DeviceId(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return "";
        try
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0 || size > 4096) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                uint got = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size);
                if (got == 0 || got == 0xFFFFFFFFu) return "";
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
    }

    public static unsafe MousePacket Parse(IntPtr lParam)
    {
        // the size query returns 0 ON SUCCESS; only the out-parameter carries the length.
        // Treating that 0 as a failure silently drops every packet (seen as fed=0 while
        // WM_INPUT counts in the hundreds of thousands).
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, HEADER_SIZE);
        if (size < MOUSE_DATA_MIN || size > 1 << 16) return default;   // absurd: refuse to allocate

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, HEADER_SIZE);
            if (got == 0 || got == 0xFFFFFFFFu || got < MOUSE_DATA_MIN) return default;
            var copy = new byte[got];
            Marshal.Copy(buf, copy, 0, (int)got);
            return Decode(copy);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Decode one RAWINPUT buffer. Split out from Parse so the flag semantics are testable.</summary>
    public static unsafe MousePacket Decode(byte[] buf)
    {
        if (buf is null || buf.Length < MOUSE_DATA_MIN) return default;
        fixed (byte* b = buf)
        {
            if (*(uint*)(b + OFF_TYPE) != RIM_TYPEMOUSE) return default;

            ushort mflags = *(ushort*)(b + OFF_MOUSE_FLAGS);
            var p = new MousePacket
            {
                device = *(IntPtr*)(b + OFF_DEVICE),
                absolute = (mflags & MOUSE_MOVE_ABSOLUTE) != 0,
                virtualDesktop = (mflags & MOUSE_VIRTUAL_DESKTOP) != 0,
                attributeChange = (mflags & MOUSE_ATTRIBUTES_CHANGED) != 0,
                buttonFlags = *(uint*)(b + OFF_BUTTONS),
                bytes = (uint)buf.Length,
            };
            p.relative = !p.absolute;                       // 0x00 flags == normal relative motion
            if (!p.relative) return p;                      // caller drops absolute pointers
            p.dx = *(int*)(b + OFF_LAST_X);
            p.dy = *(int*)(b + OFF_LAST_Y);
            return p;
        }
    }
}

public struct MousePacket
{
    public int dx;
    public int dy;
    public uint buttonFlags;
    public uint bytes;
    public IntPtr device;
    public bool relative;
    public bool absolute;
    public bool virtualDesktop;
    public bool attributeChange;
}

/// <summary>
/// One measurement window. dx/dy are the signed net counts (what the pointer actually moved);
/// pathX/pathY are unsigned sums, so a wiggle inside one window cannot cancel itself out.
/// Seq is monotonic and survives buffer trimming, which is why list indices are unusable.
/// </summary>
public readonly struct MouseSample
{
    public MouseSample(int dx, int dy, double dtMs, double timeMs, long seq = 0,
                       double? pathX = null, double? pathY = null)
    {
        this.dx = dx; this.dy = dy; this.dtMs = dtMs; this.timeMs = timeMs; this.seq = seq;
        this.pathX = pathX ?? Math.Abs((double)dx);
        this.pathY = pathY ?? Math.Abs((double)dy);
    }

    public readonly int dx, dy;
    public readonly double pathX, pathY;
    public readonly double dtMs, timeMs;
    public readonly long seq;

    /// <summary>Approximate post-driver output speed, counts/ms, per axis and combined path.</summary>
    public double SpeedX => dtMs > 0 ? pathX / dtMs : 0;
    public double SpeedY => dtMs > 0 ? pathY / dtMs : 0;
    public double SpeedWhole => dtMs > 0 ? Math.Sqrt(pathX * pathX + pathY * pathY) / dtMs : 0;
}

/// <summary>
/// Rolling window recorder with explicit, boundary-correct marks.
/// Mark() flushes whatever partial window is pending and returns the sequence number the next
/// emitted sample will get, so Since(mark) contains exactly the samples emitted after the mark.
/// IsTruncated(mark) reports when the ring already dropped part of that window.
/// </summary>
public sealed class SpeedRecorder
{
    readonly Queue<MouseSample> recent = new();
    readonly object gate = new();
    readonly Func<double> nowMs;

    double lastEmitMs = -1, lastPacketMs = -1;
    double accX, accY, pathAccX, pathAccY;
    long nextSeq = 1;
    long droppedSeq = 0;          // highest seq removed from the queue
    bool pending;

    public SpeedRecorder(Func<double>? nowMs = null) => this.nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>Minimum time per emitted sample; 2ms keeps 2000Hz delivery bursts honest.</summary>
    public double WindowMs { get; set; } = 2.0;

    /// <summary>A gap longer than this starts a fresh window instead of inflating dt.</summary>
    public double IdleGapMs { get; set; } = 80.0;

    public int Capacity { get; set; } = 60_000;      // ~2 min at a 2ms window

    public long Mark()
    {
        lock (gate)
        {
            FlushNoLock();
            return nextSeq;
        }
    }

    /// <summary>Emits the partial window so no counts are lost at a trial boundary.</summary>
    public void Flush()
    {
        lock (gate) FlushNoLock();
    }

    void FlushNoLock()
    {
        if (!pending) return;
        double t = nowMs();
        double dt = Math.Max(t - lastEmitMs, WindowMs);
        EmitNoLock((int)accX, (int)accY, pathAccX, pathAccY, dt, t);
        accX = accY = pathAccX = pathAccY = 0;
        lastEmitMs = t;
        pending = false;
    }

    void EmitNoLock(int dx, int dy, double px, double py, double dt, double t)
    {
        recent.Enqueue(new MouseSample(dx, dy, dt, t, nextSeq++, px, py));
        while (recent.Count > Capacity)
        {
            var lost = recent.Dequeue();
            droppedSeq = lost.seq;
        }
    }

    /// <summary>Feed one relative mouse packet (already processed by the driver).</summary>
    public void Feed(int dx, int dy)
    {
        double t = nowMs();
        lock (gate)
        {
            if (lastEmitMs < 0 || (t - lastPacketMs) > IdleGapMs)
            {
                FlushNoLock();                     // close the old window rather than stretch it
                lastEmitMs = t;
            }
            lastPacketMs = t;

            accX += dx; accY += dy;
            pathAccX += Math.Abs((double)dx);
            pathAccY += Math.Abs((double)dy);
            pending = true;

            double dt = t - lastEmitMs;
            if (dt >= WindowMs)
            {
                EmitNoLock((int)accX, (int)accY, pathAccX, pathAccY, dt, t);
                accX = accY = pathAccX = pathAccY = 0;
                lastEmitMs = t;
                pending = false;
            }
        }
    }

    public int Count { get { lock (gate) return recent.Count; } }

    /// <summary>
    /// Windows ever emitted, monotonic, never trimmed. Count is the wrong thing to poll for
    /// "did anything arrive": the ring buffer pins at Capacity after ~2 minutes of movement and
    /// then stops changing, which froze the live speed overlay on the curve page.
    /// </summary>
    public long Windows { get { lock (gate) return nextSeq - 1; } }

    public List<MouseSample> Snapshot() { lock (gate) { FlushNoLock(); return recent.ToList(); } }

    /// <summary>Every sample emitted since the given mark (inclusive of the mark's first sample).</summary>
    public List<MouseSample> Since(long mark)
    {
        lock (gate)
        {
            FlushNoLock();
            if (recent.Count == 0) return new List<MouseSample>();
            if (mark <= droppedSeq) mark = recent.Peek().seq;   // best effort, flagged by TruncatedAfter
            return recent.Where(s => s.seq >= mark).ToList();
        }
    }

    /// <summary>True when samples belonging to this mark were already dropped by the ring.</summary>
    public bool IsTruncated(long mark)
    {
        lock (gate)
        {
            if (recent.Count == 0) return mark < nextSeq;
            return mark < recent.Peek().seq && mark <= droppedSeq;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            recent.Clear();
            accX = accY = pathAccX = pathAccY = 0;
            pending = false;
            lastEmitMs = lastPacketMs = -1;
            droppedSeq = 0;
            // sequence numbers never go backwards: an old mark must read as truncated, not as "all samples"
        }
    }

    /// <summary>Binned approximate post-driver output speed over the retained windows.</summary>
    public List<(double from, double to, int count)> Histogram(double max, int bins, bool byComponent, double dpi = 0)
    {
        var snap = Snapshot();
        var outBins = new List<(double, double, int)>(bins);
        if (snap.Count == 0 || max <= 0 || bins <= 0) return outBins;

        var counts = new int[bins];
        foreach (var s in snap)
        {
            double v = byComponent ? Math.Max(s.SpeedX, s.SpeedY) : s.SpeedWhole;
            if (dpi > 0) v = v * 1000.0 / dpi;   // -> in/s at that DPI
            int i = Math.Clamp((int)(v / max * bins), 0, bins - 1);
            counts[i]++;
        }

        double width = max / bins;
        for (int i = 0; i < bins; i++) if (counts[i] > 0) outBins.Add((i * width, (i + 1) * width, counts[i]));
        return outBins;
    }

    /// <summary>Time-weighted percentiles of the approximate post-driver output speed.</summary>
    public (double p25, double p50, double p75, double p95, double peak, double weightedMedian) Percentiles(bool byComponent)
    {
        var snap = Snapshot();
        var pts = snap.Select(s => (v: byComponent ? Math.Max(s.SpeedX, s.SpeedY) : s.SpeedWhole,
                                    w: Math.Max(s.dtMs, 0.0)))
                      .Where(p => p.v > 0)
                      .OrderBy(p => p.v)
                      .ToList();
        if (pts.Count == 0) return (0, 0, 0, 0, 0, 0);

        double total = pts.Sum(p => p.w);
        double At(double q)
        {
            double want = q * total, run = 0;
            foreach (var p in pts) { run += p.w; if (run >= want) return p.v; }
            return pts[^1].v;
        }
        var plain = pts.Select(p => p.v).ToList();
        return (plain[plain.Count / 4], plain[plain.Count / 2],
                plain[Math.Min(plain.Count * 3 / 4, plain.Count - 1)],
                plain[Math.Min(plain.Count * 19 / 20, plain.Count - 1)],
                plain[^1], At(0.5));
    }

    /// <summary>Time-weighted median only; the plain percentile list is biased by window length.</summary>
    public double WeightedMedian(bool byComponent) => Percentiles(byComponent).weightedMedian;
}
