using System.Runtime.InteropServices;

namespace RaCore;

public sealed class MouseDevice
{
    public string Id { get; init; } = "";         // what the driver matches: HID\VID_xxxx&PID_xxxx&MI_01&COL01
    public string Name { get; init; } = "";       // manufacturer + product from the HID strings
    public int Handles { get; init; }             // >1 when one physical device exposes several HID collections
    public string InstanceId { get; init; } = ""; // full instance id, for display only

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name}  ({Id})";
}

/// <summary>
/// C# port of wrapper/input.h::rawinput_foreach: same calls, same id derivation
/// (DEVPKEY_Device_InstanceId, then drop everything after the last backslash),
/// so the ids we write into device_settings are the ones the driver compares against.
/// </summary>
public static class DeviceEnum
{
    const uint RIM_TYPEMOUSE = 0;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RI_ERROR = 0xFFFFFFFFu;
    const uint FILE_SHARE_READ = 1;
    const uint OPEN_EXISTING = 3;

    // DEVPKEY_Device_InstanceId {78C34FC8-104A-4ACA-B7EA-5EED4D529973}, pid 512
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new()
    {
        fmtid = new Guid("78C34FC8-104A-4ACA-B7EA-5EED4D529973"),
        pid = 512,
    };

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE_LIST { public IntPtr hDevice; public uint dwType; }

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList(IntPtr pDeviceList, ref uint pnNumDevices, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool HidD_GetProductString(IntPtr h, IntPtr buffer, int len);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool HidD_GetManufacturerString(IntPtr h, IntPtr buffer, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr templ);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(string interfaceId, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);

    const int HID_STR_MAX = 127;

    public static List<MouseDevice> ListMice()
    {
        var byId = new SortedDictionary<string, (string name, int handles, string instanceId)>(StringComparer.Ordinal);

        foreach (var (handle, id, instanceId) in EnumerateHandles())
        {
            var name = ReadHidName(handle);
            if (string.IsNullOrEmpty(id)) continue;

            if (byId.TryGetValue(id, out var old))
                byId[id] = (string.IsNullOrWhiteSpace(old.name) ? name : old.name, old.handles + 1, old.instanceId);
            else
                byId[id] = (name, 1, instanceId);
        }

        return byId.Select(kv => new MouseDevice
        {
            Id = kv.Key,
            Name = kv.Value.name,
            Handles = kv.Value.handles,
            InstanceId = kv.Value.instanceId,
        }).ToList();
    }

    /// <summary>Step-by-step trace, for diagnosing an empty list.</summary>
    public static List<string> Diagnose()
    {
        var log = new List<string>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICE_LIST>();
        uint rc0 = GetRawInputDeviceList(IntPtr.Zero, ref count, size);
        log.Add($"GetRawInputDeviceList(null): rc={rc0} count={count} structSize={size}");
        if (count == 0) return log;

        IntPtr buf = Marshal.AllocHGlobal((int)(count * size));
        try
        {
            uint rc = GetRawInputDeviceList(buf, ref count, size);
            log.Add($"GetRawInputDeviceList(buf): rc={rc} count={count}");

            int shown = 0;
            for (int i = 0; i < count && shown < 5; i++)
            {
                var entry = Marshal.PtrToStructure<RAWINPUTDEVICE_LIST>(buf + i * (int)size);
                if (entry.dwType != RIM_TYPEMOUSE) continue;
                shown++;

                string iface = ReadDeviceName(entry.hDevice);
                log.Add($"[{i}] handle=0x{entry.hDevice.ToInt64():X} type={entry.dwType} iface='{iface}'");
                if (string.IsNullOrEmpty(iface)) continue;

                uint sz = 0; var key = DEVPKEY_Device_InstanceId;
                uint r1 = CM_Get_Device_Interface_PropertyW(iface, ref key, out _, null, ref sz, 0);
                log.Add($"     CM(size) res={r1} size={sz}");
                if (r1 != 0 && r1 != 30) { log.Add("     -> CR error, skipped"); continue; }
                var bytes = new byte[sz];
                uint r2 = CM_Get_Device_Interface_PropertyW(iface, ref key, out _, bytes, ref sz, 0);
                log.Add($"     CM(get) res={r2} instance='{System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0')}'");
                log.Add($"     hidName='{ReadHidName(entry.hDevice)}'");
            }
            log.Add($"mouse entries examined: {shown}");
        }
        finally { Marshal.FreeHGlobal(buf); }
        return log;
    }

    /// <summary>
    /// Device interface paths are  \\?\&lt;device id with '#' separators&gt;#&lt;instance&gt;#{interface-guid}.
    /// The driver compares BusQueryDeviceID, i.e. the device id part, canonical upper case.
    /// </summary>
    public static (string deviceId, string instance)? SplitInterfacePath(string iface)
    {
        if (!iface.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        string body = iface[4..];
        int h1 = body.IndexOf('#');
        if (h1 <= 0) return null;
        int h2 = body.IndexOf('#', h1 + 1);
        if (h2 <= h1) return null;

        string deviceId = body[..h2].Replace('#', '\\').ToUpperInvariant();
        string rest = body[(h2 + 1)..];
        int g = rest.IndexOf('#');
        string instance = (g >= 0 ? rest[..g] : rest);
        return (deviceId, deviceId + "\\" + instance.ToUpperInvariant());
    }

    /// <summary>Interface path (\?\HID#...#{guid}) -> the device id the driver stores, canonical upper case.</summary>
    public static string CanonicalId(string iface)
    {
        if (string.IsNullOrEmpty(iface)) return "";
        var split = SplitInterfacePath(iface);
        return split?.deviceId ?? iface.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>All Win32 raw-input handles whose device id equals <paramref name="deviceId"/> (a device can expose several collections).</summary>
    public static List<IntPtr> HandlesForId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return new List<IntPtr>();
        string want = deviceId.Replace('#', '\\').ToUpperInvariant();
        return EnumerateHandles().Where(e => string.Equals(e.id, want, StringComparison.OrdinalIgnoreCase))
                                 .Select(e => e.handle).ToList();
    }

    static List<(IntPtr handle, string id, string instance)> EnumerateHandles()
    {
        var result = new List<(IntPtr, string, string)>();

        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICE_LIST>();
        GetRawInputDeviceList(IntPtr.Zero, ref count, size);
        if (count == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)(count * size));
        try
        {
            uint rc = GetRawInputDeviceList(buf, ref count, size);
            if (rc == RI_ERROR || rc == 0) return result;

            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<RAWINPUTDEVICE_LIST>(buf + i * (int)size);
                if (entry.dwType != RIM_TYPEMOUSE) continue;

                string iface = ReadDeviceName(entry.hDevice);
                if (string.IsNullOrEmpty(iface)) continue;

                // authoritative when available, path derivation otherwise
                string? cm = ReadInstanceId(iface);
                var split = SplitInterfacePath(iface);
                string? id;
                if (cm is not null)
                {
                    int cut = cm.LastIndexOf('\\');
                    id = cut > 0 ? cm[..cut] : cm;
                }
                else id = split?.deviceId;
                string? instance = cm ?? split?.instance;
                if (string.IsNullOrEmpty(id)) continue;

                result.Add((entry.hDevice, id!, instance!));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return result;
    }

    static string ReadDeviceName(IntPtr handle)
    {
        uint len = 0;
        if (GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, IntPtr.Zero, ref len) == RI_ERROR) return "";
        if (len == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)len * 2);
        try
        {
            if (GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, buf, ref len) == RI_ERROR) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string? ReadInstanceId(string interfaceName)
    {
        uint size = 0;
        var key = DEVPKEY_Device_InstanceId;
        uint res = CM_Get_Device_Interface_PropertyW(interfaceName, ref key, out _, null, ref size, 0);
        if (res != 0 && res != 30) return null; // 30 = CR_BUFFER_SMALL
        if (size == 0) return null;

        var buffer = new byte[size];
        res = CM_Get_Device_Interface_PropertyW(interfaceName, ref key, out _, buffer, ref size, 0);
        if (res != 0) return null;

        int chars = (int)size / 2;
        while (chars > 0 && buffer[(chars - 1) * 2] == 0 && buffer[(chars - 1) * 2 + 1] == 0) chars--;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, chars * 2);
    }

    static string ReadHidName(IntPtr handle)
    {
        string iface = ReadDeviceName(handle);
        if (string.IsNullOrEmpty(iface)) return "";

        // share-only open, same as input.h (access = 0)
        var h = CreateFileW(iface, 0, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return "";

        try
        {
            IntPtr prod = Marshal.AllocHGlobal(HID_STR_MAX * 2);
            IntPtr manu = Marshal.AllocHGlobal(HID_STR_MAX * 2);
            try
            {
                bool haveProd = HidD_GetProductString(h, prod, HID_STR_MAX * 2);
                string product = haveProd ? (Marshal.PtrToStringUni(prod) ?? "") : "";
                bool haveManu = HidD_GetManufacturerString(h, manu, HID_STR_MAX * 2);
                string manufacturer = haveManu ? (Marshal.PtrToStringUni(manu) ?? "") : "";

                if (string.IsNullOrEmpty(product)) return manufacturer;
                if (string.IsNullOrEmpty(manufacturer)) return product;
                if (product.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)) return product;
                return manufacturer.Trim() + " " + product;
            }
            finally { Marshal.FreeHGlobal(prod); Marshal.FreeHGlobal(manu); }
        }
        finally { CloseHandle(h); }
    }
}
