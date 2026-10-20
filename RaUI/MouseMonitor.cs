using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using RaCore;

namespace RaUI;

/// <summary>
/// Subscribes to WM_INPUT on the main window and feeds a SpeedRecorder.
/// RIDEV_INPUTSINK keeps packets flowing while the window is not focused.
/// Packets here are POST-driver output and delivery-timed, so speeds are approximate;
/// when SelectedDeviceId is set, only that device's packets are recorded.
/// </summary>
public sealed class MouseMonitor : IDisposable
{
    readonly HwndSource source;
    readonly HwndSourceHook hook;
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly bool registered;
    bool disposed;

    /// <summary>Uses the stopwatch clock: Environment.TickCount64 granularity (~15ms) is too coarse for a 2ms window.</summary>
    public SpeedRecorder Recorder { get; }

    public event Action<MouseSample>? Moved;

    /// <summary>Device name as reported by Raw Input (\?\HID#VID...#...). Null = record every mouse.</summary>
    public string? SelectedDeviceName { get; set; }

    string lastHandleProbe = "";
    IntPtr lastHandleDevice = IntPtr.Zero;

    public MouseMonitor(Window window)
    {
        Recorder = new SpeedRecorder(() => watch.Elapsed.TotalMilliseconds);
        source = (HwndSource)PresentationSource.FromVisual(window)!;
        hook = WndProc;
        source.AddHook(hook);
        registered = RawInput.Register(source.Handle);
    }

    public bool Active => registered && !disposed;

    /// <summary>Diagnostics, split by where a packet dies: a round with fed=0 must say whether
    /// nothing arrived, the decode failed, the deltas were zero, or the device filter rejected it.</summary>
    public long WmInputSeen { get; private set; }
    public long Undecoded { get; private set; }
    public long AbsoluteSeen { get; private set; }
    public long ZeroDeltaSeen { get; private set; }
    public long DeviceMismatch { get; private set; }
    public long PacketsFed { get; private set; }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawInput.WM_INPUT)
        {
            WmInputSeen++;
            var p = RawInput.Parse(lParam);
            if (p.bytes == 0) { Undecoded++; return IntPtr.Zero; }
            if (p.absolute) { AbsoluteSeen++; return IntPtr.Zero; }
            if (p.dx == 0 && p.dy == 0) { ZeroDeltaSeen++; return IntPtr.Zero; }
            if (!MatchesDevice(p.device)) { DeviceMismatch++; return IntPtr.Zero; }

            PacketsFed++;
            Recorder.Feed(p.dx, p.dy);
            Moved?.Invoke(new MouseSample(p.dx, p.dy, 0, NowMs, pathX: Math.Abs((double)p.dx), pathY: Math.Abs((double)p.dy)));
        }
        return IntPtr.Zero;
    }

    bool MatchesDevice(IntPtr device)
    {
        if (string.IsNullOrEmpty(SelectedDeviceName)) return true;
        if (device == IntPtr.Zero) return false;

        // cheap identity: the same handle repeats thousands of times per second
        if (device == lastHandleDevice) return string.Equals(lastHandleProbe, SelectedDeviceName, StringComparison.OrdinalIgnoreCase);

        string id = DeviceEnum.CanonicalId(RawInput.DeviceId(device));
        lastHandleDevice = device;
        lastHandleProbe = id;
        return string.Equals(id, SelectedDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Monotonic milliseconds, same clock the recorder uses.</summary>
    public double NowMs => watch.Elapsed.TotalMilliseconds;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Recorder.Flush(); } catch { }
        try { if (registered) RawInput.Unregister(source.Handle); } catch { }
        try { source.RemoveHook(hook); } catch { }
    }
}
