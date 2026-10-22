using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RaCore;

/// <summary>
/// Direct user-mode client for the signed rawaccel.sys filter driver.
/// Mirrors rawaccel-src/common/rawaccel-io.hpp:
///   \\.\rawaccel  +  IOCTL READ(0x888) / WRITE(0x889) / GET_VERSION(0x88a)
///   device type 0x8888, METHOD_BUFFERED, FILE_ANY_ACCESS, opened with 0 access rights.
/// The driver's DAC decides whether the current token may open the device. What has actually been
/// observed on this machine: writes succeed from a double-clicked RaProbe and from RaUI's patch
/// path, but neither run has had its integrity level recorded, so "non-admin works" is NOT a
/// proven claim - and the assistant's own shell here runs elevated, so its successes prove nothing
/// either. If a future driver tightens the DAC, the failure shows up as Win32 error 5, which both
/// RaProbe and the UI name explicitly instead of calling it a missing driver.
/// </summary>
public static unsafe class RaDevice
{
    const uint DEVICE_TYPE = 0x8888;

    // CTL_CODE(0x8888, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    // = (type<<16) | (access<<14) | (function<<2) | method
    public const uint IOCTL_READ = (DEVICE_TYPE << 16) | (0x888u << 2);        // 0x88882220
    public const uint IOCTL_WRITE = (DEVICE_TYPE << 16) | (0x889u << 2);       // 0x88882224
    public const uint IOCTL_GET_VERSION = (DEVICE_TYPE << 16) | (0x88au << 2); // 0x88882228

    static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr templ);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr input, uint inLen, IntPtr output, uint outLen, out uint ret, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr h);

    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>Upper bound accepted from the driver header; anything larger means a size mismatch or garbage.</summary>
    public const uint MAX_MODIFIERS = 256;
    public const uint MAX_DEVICES = 1024;

    public static bool IsInstalled()
    {
        var h = Open();
        if (h == IntPtr.Zero) return false;      // absent driver: CreateFile failed with ERROR_FILE_NOT_FOUND
        try { CloseHandle(h); } catch { }
        return h != INVALID_HANDLE_VALUE;
    }

    static IntPtr Open()
    {
        // io.hpp: CreateFileW(L"\\\\.\\rawaccel", 0, 0, 0, OPEN_EXISTING, 0, 0)
        var h = CreateFileW(@"\\.\rawaccel", 0, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3) return IntPtr.Zero;    // not present / no path
            throw new Win32Exception(err, $"CreateFile(\\\\.\\rawaccel) failed with code {err}");
        }
        return h;
    }

    static void Control(uint code, ReadOnlySpan<byte> input, Span<byte> output, out uint bytesReturned)
    {
        var h = Open();
        if (h == IntPtr.Zero)
            throw new RaDriverAbsentException();

        try
        {
            fixed (byte* pi = input)
            fixed (byte* po = output)
            {
                bool ok = DeviceIoControl(
                    h, code,
                    (IntPtr)pi, (uint)input.Length,
                    (IntPtr)po, (uint)output.Length,
                    out bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"DeviceIoControl(0x{code:X8}) failed ({err})");
                }
            }
        }
        finally { CloseHandle(h); }
    }

    public static version_t GetVersion()
    {
        var buf = new byte[sizeof(version_t)];
        Control(IOCTL_GET_VERSION, ReadOnlySpan<byte>.Empty, buf, out uint ret);
        if (ret != buf.Length)
            throw new RaProtocolException($"GET_VERSION returned {ret} bytes, expected {buf.Length}");
        return MemoryMarshal.Read<version_t>(buf);
    }

    /// <summary>Two-step read, exactly like io.hpp::read(). Returns the full settings blob.</summary>
    public static byte[] Read()
    {
        var head = new byte[WireSizes.IoBase];
        Control(IOCTL_READ, ReadOnlySpan<byte>.Empty, head, out uint headRet);
        if (headRet != head.Length)
            throw new RaProtocolException($"READ header returned {headRet} bytes, expected {head.Length}");
        var baseData = MemoryMarshal.Read<io_base>(head);

        if (baseData.modifier_data_size > MAX_MODIFIERS || baseData.device_data_size > MAX_DEVICES)
            throw new RaProtocolException($"implausible counts in driver header: " +
                                          $"{baseData.modifier_data_size} modifiers / {baseData.device_data_size} devices " +
                                          $"(max {MAX_MODIFIERS}/{MAX_DEVICES}) - struct layout mismatch?");

        long total = WireSizes.IoBase
                   + (long)baseData.modifier_data_size * WireSizes.Modifier
                   + (long)baseData.device_data_size * WireSizes.Device;
        if (total > 1L << 26) throw new RaProtocolException($"driver claims a {total} byte state, refusing to allocate");

        if (baseData.modifier_data_size == 0 && baseData.device_data_size == 0)
            return head;    // the driver really holds nothing; do not pretend a curve is active

        // the counts can change between the two calls (another writer process); retry once
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var full = new byte[total];
            Control(IOCTL_READ, ReadOnlySpan<byte>.Empty, full, out uint ret);
            if (ret == total) return full;

            // length moved under us: re-read the header and try again with the new size
            Control(IOCTL_READ, ReadOnlySpan<byte>.Empty, head, out headRet);
            if (headRet != head.Length) throw new RaProtocolException($"READ header returned {headRet} on retry");
            baseData = MemoryMarshal.Read<io_base>(head);
            if (baseData.modifier_data_size > MAX_MODIFIERS || baseData.device_data_size > MAX_DEVICES)
                throw new RaProtocolException("counts changed to an implausible value while reading");
            total = WireSizes.IoBase
                  + (long)baseData.modifier_data_size * WireSizes.Modifier
                  + (long)baseData.device_data_size * WireSizes.Device;
        }
        throw new RaProtocolException($"READ length kept changing (last {total} bytes) - another process is writing the driver");
    }

    /// <summary>Writes a full blob. Driver enforces buffer_length == sizeof(io_base) + n*modifier + m*device.</summary>
    public static void Write(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < WireSizes.IoBase)
            throw new RaProtocolException($"write blob is only {blob.Length} bytes, smaller than io_base ({WireSizes.IoBase})");

        var b = MemoryMarshal.Read<io_base>(blob);
        if (b.modifier_data_size > MAX_MODIFIERS || b.device_data_size > MAX_DEVICES)
            throw new RaProtocolException($"refusing to write implausible counts ({b.modifier_data_size}/{b.device_data_size})");

        long expected = WireSizes.IoBase
                      + (long)b.modifier_data_size * WireSizes.Modifier
                      + (long)b.device_data_size * WireSizes.Device;
        if (blob.Length != expected)
            throw new RaProtocolException($"write blob is {blob.Length} bytes, driver expects {expected}");

        Control(IOCTL_WRITE, blob, Span<byte>.Empty, out _);
    }

    /// <summary>Clears all settings (driver disables every device). Same as io.hpp::reset().</summary>
    public static void Reset()
    {
        var empty = new byte[WireSizes.IoBase];
        Control(IOCTL_WRITE, empty, Span<byte>.Empty, out _);
    }

    // ---- typed views over a blob ----

    public static io_base GetBase(byte[] blob)
    {
        if (blob is null || blob.Length < WireSizes.IoBase)
            throw new RaProtocolException($"blob is {blob?.Length ?? 0} bytes, shorter than io_base ({WireSizes.IoBase})");
        return MemoryMarshal.Read<io_base>(blob);
    }

    public static IEnumerable<modifier_settings> Modifiers(byte[] blob)
    {
        var b = GetBase(blob);
        if (b.modifier_data_size > MAX_MODIFIERS) throw new RaProtocolException("implausible modifier count");
        long need = WireSizes.IoBase + (long)b.modifier_data_size * WireSizes.Modifier;
        if (blob.Length < need)
            throw new RaProtocolException($"blob holds {blob.Length} bytes but {b.modifier_data_size} modifiers need {need}");
        for (int i = 0; i < b.modifier_data_size; i++)
            yield return MemoryMarshal.Read<modifier_settings>(
                blob.AsSpan(WireSizes.IoBase + i * WireSizes.Modifier, WireSizes.Modifier));
    }

    public static IEnumerable<device_settings> Devices(byte[] blob)
    {
        var b = GetBase(blob);
        if (b.device_data_size > MAX_DEVICES) throw new RaProtocolException("implausible device count");
        int off = WireSizes.IoBase + (int)b.modifier_data_size * WireSizes.Modifier;
        long need = off + (long)b.device_data_size * WireSizes.Device;
        if (blob.Length < need)
            throw new RaProtocolException($"blob holds {blob.Length} bytes but {b.device_data_size} devices need {need}");
        for (int i = 0; i < b.device_data_size; i++)
            yield return MemoryMarshal.Read<device_settings>(blob.AsSpan(off + i * WireSizes.Device, WireSizes.Device));
    }

    public static string ToCsString(ReadOnlySpan<ushort> utf16)
    {
        int len = 0;
        while (len < utf16.Length && utf16[len] != 0) len++;
        return new string(System.Text.Encoding.Unicode.GetString(
            MemoryMarshal.AsBytes(utf16.Slice(0, len))));
    }
}

public class RaDriverAbsentException : Exception
{
    public RaDriverAbsentException() : base(
        @"rawaccel.sys is not reachable (CreateFile(\\.\rawaccel) failed). Install the driver with installer.exe and reboot.") { }
}

public class RaProtocolException : Exception
{
    public RaProtocolException(string m) : base(m) { }
}
