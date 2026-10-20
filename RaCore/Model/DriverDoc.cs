using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaCore;

namespace RaCore.Model;

// DTO layer that is byte-for-byte compatible with the official Raw Accel settings.json.
// Property declaration order below == order emitted by wrapper.dll's Newtonsoft contract
// (verified against rawaccel-src/wrapper/wrapper.cpp and D:\RawAccel\settings.json).

public sealed class Vec2Dto
{
    [JsonPropertyName("x")] public double X { get; set; } = 1;
    [JsonPropertyName("y")] public double Y { get; set; } = 1;
}

public sealed class AccelDto
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "classic";

    [JsonPropertyName("Gain / Velocity")] public bool Gain { get; set; } = true;

    [JsonPropertyName("inputOffset")] public double InputOffset { get; set; }
    [JsonPropertyName("outputOffset")] public double OutputOffset { get; set; }
    [JsonPropertyName("acceleration")] public double Acceleration { get; set; } = 0.005;
    [JsonPropertyName("decayRate")] public double DecayRate { get; set; } = 0.1;
    [JsonPropertyName("gamma")] public double Gamma { get; set; } = 1;
    [JsonPropertyName("motivity")] public double Motivity { get; set; } = 1.5;
    [JsonPropertyName("exponentClassic")] public double ExponentClassic { get; set; } = 2;
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("exponentPower")] public double ExponentPower { get; set; } = 0.05;
    [JsonPropertyName("limit")] public double Limit { get; set; } = 1.5;
    [JsonPropertyName("syncSpeed")] public double SyncSpeed { get; set; } = 5;
    [JsonPropertyName("smooth")] public double Smooth { get; set; } = 0.5;

    [JsonPropertyName("Cap / Jump")] public Vec2Dto Cap { get; set; } = new() { X = 15, Y = 1.5 };
    [JsonPropertyName("Cap mode")] public string CapMode { get; set; } = "output";

    [JsonPropertyName("data")] public List<float> Data { get; set; } = new();

    public AccelDto Clone() => new()
    {
        Mode = Mode, Gain = Gain, InputOffset = InputOffset, OutputOffset = OutputOffset,
        Acceleration = Acceleration, DecayRate = DecayRate, Gamma = Gamma, Motivity = Motivity,
        ExponentClassic = ExponentClassic, Scale = Scale, ExponentPower = ExponentPower,
        Limit = Limit, SyncSpeed = SyncSpeed, Smooth = Smooth,
        Cap = new Vec2Dto { X = Cap.X, Y = Cap.Y }, CapMode = CapMode,
        Data = new List<float>(Data),
    };
}

public sealed class SpeedDto
{
    [JsonPropertyName("Whole/combined accel (set false for 'by component' mode)")]
    public bool Whole { get; set; } = true;

    [JsonPropertyName("lpNorm")] public double LpNorm { get; set; } = 2;

    [JsonPropertyName("Time in ms after which an input is weighted at half its original value.")]
    public double InputHalflife { get; set; }

    [JsonPropertyName("Time in ms after which scale is weighted at half its original value.")]
    public double ScaleHalflife { get; set; }

    [JsonPropertyName("Time in ms after which an output is weighted at half its original value.")]
    public double OutputHalflife { get; set; }

    public SpeedDto Clone() => new()
    {
        Whole = Whole, LpNorm = LpNorm,
        InputHalflife = InputHalflife, ScaleHalflife = ScaleHalflife, OutputHalflife = OutputHalflife
    };
}

public sealed class ProfileDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "default";

    [JsonPropertyName("Stretches domain for horizontal vs vertical inputs")]
    public Vec2Dto Domain { get; set; } = new();

    [JsonPropertyName("Stretches accel range for horizontal vs vertical inputs")]
    public Vec2Dto Range { get; set; } = new();

    [JsonPropertyName("Whole or horizontal accel parameters")] public AccelDto X { get; set; } = new();
    [JsonPropertyName("Vertical accel parameters")] public AccelDto Y { get; set; } = new();
    [JsonPropertyName("Input speed calculation parameters")] public SpeedDto Speed { get; set; } = new();

    [JsonPropertyName("Output DPI")] public double OutputDpi { get; set; } = 1000;
    [JsonPropertyName("Y/X output DPI ratio (vertical sens multiplier)")] public double YxRatio { get; set; } = 1;
    [JsonPropertyName("L/R output DPI ratio (left sens multiplier)")] public double LrRatio { get; set; } = 1;
    [JsonPropertyName("U/D output DPI ratio (up sens multiplier)")] public double UdRatio { get; set; } = 1;
    [JsonPropertyName("Degrees of rotation")] public double Rotation { get; set; }
    [JsonPropertyName("Degrees of angle snapping")] public double Snap { get; set; }

    /// <summary>maps to ra::profile::speed_max; speed_min stays 0 like the official GUI does.</summary>
    [JsonPropertyName("Input Speed Cap")] public double InputSpeedCap { get; set; }

    public ProfileDto Clone() => new()
    {
        Name = Name,
        Domain = new Vec2Dto { X = Domain.X, Y = Domain.Y },
        Range = new Vec2Dto { X = Range.X, Y = Range.Y },
        X = X.Clone(), Y = Y.Clone(), Speed = Speed.Clone(),
        OutputDpi = OutputDpi, YxRatio = YxRatio, LrRatio = LrRatio, UdRatio = UdRatio,
        Rotation = Rotation, Snap = Snap, InputSpeedCap = InputSpeedCap,
    };
}

public sealed class DeviceConfigDto
{
    [JsonPropertyName("disable")] public bool Disable { get; set; }

    [JsonPropertyName("setExtraInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SetExtraInfo { get; set; }

    [JsonPropertyName("Use constant time interval based on polling rate")]
    public bool PollTimeLock { get; set; }

    [JsonPropertyName("DPI (normalizes input speed unit: counts/ms -> in/s)")]
    public int Dpi { get; set; }

    [JsonPropertyName("Polling rate Hz (keep at 0 for automatic adjustment)")]
    public int PollingRate { get; set; }

    [JsonPropertyName("minimumTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumTime { get; set; }

    [JsonPropertyName("maximumTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumTime { get; set; }

    public DeviceConfigDto Clone() => new()
    {
        Disable = Disable, SetExtraInfo = SetExtraInfo, PollTimeLock = PollTimeLock,
        Dpi = Dpi, PollingRate = PollingRate, MinimumTime = MinimumTime, MaximumTime = MaximumTime
    };
}

public sealed class DeviceDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("config")] public DeviceConfigDto Config { get; set; } = new();

    public DeviceDto Clone() => new()
    {
        Name = Name, Profile = Profile, Id = Id, Config = Config.Clone()
    };
}

public sealed class RootDto
{
    [JsonPropertyName("### Accel modes ###")] public string AccelModesNote { get; set; } =
        "classic | jump | natural | synchronous | power | lut | noaccel";

    [JsonPropertyName("### Cap modes ###")] public string CapModesNote { get; set; } = "in_out | input | output";

    [JsonPropertyName("version")] public string Version { get; set; } = "1.7.0";

    [JsonPropertyName("defaultDeviceConfig")] public DeviceConfigDto DefaultDeviceConfig { get; set; } = new();

    [JsonPropertyName("profiles")] public List<ProfileDto> Profiles { get; set; } = new();

    [JsonPropertyName("devices")] public List<DeviceDto> Devices { get; set; } = new();

    public RootDto DeepCopy() => new()
    {
        Version = Version,
        DefaultDeviceConfig = DefaultDeviceConfig.Clone(),
        Profiles = Profiles.Select(p => p.Clone()).ToList(),
        Devices = Devices.Select(d => d.Clone()).ToList(),
    };
}

/// <summary>DTO <-> ra:: structs, and driver blob assembly.</summary>
public static class Convert4
{
    static readonly Dictionary<string, accel_mode> ModeIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classic"] = accel_mode.classic, ["jump"] = accel_mode.jump, ["natural"] = accel_mode.natural,
        ["synchronous"] = accel_mode.synchronous, ["power"] = accel_mode.power, ["lut"] = accel_mode.lookup,
        ["noaccel"] = accel_mode.noaccel,
    };
    static readonly Dictionary<accel_mode, string> ModeOut = new()
    {
        [accel_mode.classic] = "classic", [accel_mode.jump] = "jump", [accel_mode.natural] = "natural",
        [accel_mode.synchronous] = "synchronous", [accel_mode.power] = "power", [accel_mode.lookup] = "lut",
        [accel_mode.noaccel] = "noaccel",
    };
    static readonly Dictionary<string, cap_mode> CapIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in_out"] = cap_mode.io, ["input"] = cap_mode.@in, ["output"] = cap_mode.@out,
    };
    static readonly Dictionary<cap_mode, string> CapOut = new()
    {
        [cap_mode.io] = "in_out", [cap_mode.@in] = "input", [cap_mode.@out] = "output",
    };

    public static unsafe accel_args ToStruct(AccelDto d)
    {
        var a = new accel_args
        {
            mode = ModeIn.TryGetValue(d.Mode, out var m) ? m : accel_mode.classic,
            gain = (byte)(d.Gain ? 1 : 0),
            input_offset = d.InputOffset,
            output_offset = d.OutputOffset,
            acceleration = d.Acceleration,
            decay_rate = d.DecayRate,
            gamma = d.Gamma,
            motivity = d.Motivity,
            exponent_classic = d.ExponentClassic,
            scale = d.Scale,
            exponent_power = d.ExponentPower,
            limit = d.Limit,
            sync_speed = d.SyncSpeed,
            smooth = d.Smooth,
            cap = new vec2d { x = d.Cap.X, y = d.Cap.Y },
            cap_mode = CapIn.TryGetValue(d.CapMode, out var c) ? c : cap_mode.@out,
            length = d.Data.Count,
        };
        for (int i = 0; i < Math.Min(d.Data.Count, accel_args.LUT_RAW_DATA_CAPACITY); i++) a.data[i] = d.Data[i];
        return a;
    }

    public static unsafe AccelDto FromStruct(accel_args a)
    {
        var d = new AccelDto
        {
            Mode = ModeOut[a.mode],
            Gain = a.gain != 0,
            InputOffset = a.input_offset,
            OutputOffset = a.output_offset,
            Acceleration = a.acceleration,
            DecayRate = a.decay_rate,
            Gamma = a.gamma,
            Motivity = a.motivity,
            ExponentClassic = a.exponent_classic,
            Scale = a.scale,
            ExponentPower = a.exponent_power,
            Limit = a.limit,
            SyncSpeed = a.sync_speed,
            Smooth = a.smooth,
            Cap = new Vec2Dto { X = a.cap.x, Y = a.cap.y },
            CapMode = CapOut[a.cap_mode],
            Data = new List<float>(),
        };
        int n = Math.Clamp(a.length, 0, accel_args.LUT_RAW_DATA_CAPACITY);
        for (int i = 0; i < n; i++) d.Data.Add(a.data[i]);
        return d;
    }

    static vec2d V(Vec2Dto v) => new() { x = v.X, y = v.Y };
    static Vec2Dto V(vec2d v) => new() { X = v.x, Y = v.y };

    public static unsafe ra_profile ToStruct(ProfileDto p)
    {
        var r = new ra_profile
        {
            domain_weights = V(p.Domain),
            range_weights = V(p.Range),
            accel_x = ToStruct(p.X),
            accel_y = ToStruct(p.Y),
            speed_processor_args = new speed_args
            {
                whole = (byte)(p.Speed.Whole ? 1 : 0),
                lp_norm = p.Speed.LpNorm,
                input_speed_smooth_halflife = p.Speed.InputHalflife,
                scale_smooth_halflife = p.Speed.ScaleHalflife,
                output_speed_smooth_halflife = p.Speed.OutputHalflife,
            },
            output_dpi = p.OutputDpi,
            yx_output_dpi_ratio = p.YxRatio,
            lr_output_dpi_ratio = p.LrRatio,
            ud_output_dpi_ratio = p.UdRatio,
            degrees_rotation = p.Rotation,
            degrees_snap = p.Snap,
            speed_min = 0,
            speed_max = p.InputSpeedCap,
        };
        WriteWz(r.name, ra_profile.MAX_NAME_LEN, string.IsNullOrEmpty(p.Name) ? "default" : p.Name);
        return r;
    }

    public static unsafe ProfileDto FromStruct(ra_profile p) => new()
    {
        Name = ReadWz(p.name, ra_profile.MAX_NAME_LEN),
        Domain = V(p.domain_weights),
        Range = V(p.range_weights),
        X = FromStruct(p.accel_x),
        Y = FromStruct(p.accel_y),
        Speed = new SpeedDto
        {
            Whole = p.speed_processor_args.whole != 0,
            LpNorm = p.speed_processor_args.lp_norm,
            InputHalflife = p.speed_processor_args.input_speed_smooth_halflife,
            ScaleHalflife = p.speed_processor_args.scale_smooth_halflife,
            OutputHalflife = p.speed_processor_args.output_speed_smooth_halflife,
        },
        OutputDpi = p.output_dpi,
        YxRatio = p.yx_output_dpi_ratio,
        LrRatio = p.lr_output_dpi_ratio,
        UdRatio = p.ud_output_dpi_ratio,
        Rotation = p.degrees_rotation,
        Snap = p.degrees_snap,
        InputSpeedCap = p.speed_max,
    };

    public static unsafe void WriteWz(ushort* dst, int capacity, string s)
    {
        for (int i = 0; i < capacity; i++) dst[i] = 0;
        int n = Math.Min(s.Length, capacity - 1);
        for (int i = 0; i < n; i++) dst[i] = (ushort)s[i];
    }

    public static unsafe string ReadWz(ushort* src, int capacity)
    {
        int n = 0;
        while (n < capacity && src[n] != 0) n++;
        var chars = new char[n];
        for (int i = 0; i < n; i++) chars[i] = (char)src[i];
        return new string(chars);
    }

    public static device_config ToStruct(DeviceConfigDto d) => new device_config
    {
        disable = (byte)(d.Disable ? 1 : 0),
        set_extra_info = (byte)(d.SetExtraInfo.GetValueOrDefault() ? 1 : 0),
        poll_time_lock = (byte)(d.PollTimeLock ? 1 : 0),
        dpi = d.Dpi,
        polling_rate = d.PollingRate,
        clamp = new time_clamp
        {
            min = d.MinimumTime ?? 0.0625,   // ra::DEFAULT_TIME_MIN = 1000/8000/2
            max = d.MaximumTime ?? 100,      // ra::DEFAULT_TIME_MAX
        },
    };

    public static DeviceConfigDto FromStruct(device_config c) => new DeviceConfigDto
    {
        Disable = c.disable != 0,
        SetExtraInfo = c.set_extra_info != 0 ? true : (bool?)null,
        PollTimeLock = c.poll_time_lock != 0,
        Dpi = c.dpi,
        PollingRate = c.polling_rate,
        MinimumTime = Math.Abs(c.clamp.min - 0.0625) < 1e-12 ? null : c.clamp.min,
        MaximumTime = Math.Abs(c.clamp.max - 100) < 1e-12 ? null : c.clamp.max,
    };

    /// <summary>Full blob for IOCTL_WRITE: io_base + modifier_settings[n] + device_settings[m].</summary>
    public static unsafe byte[] BuildBlob(RootDto root)
    {
        int n = root.Profiles.Count, m = root.Devices.Count;
        var blob = new byte[WireSizes.IoBase + n * WireSizes.Modifier + m * WireSizes.Device];

        var baseData = new io_base
        {
            default_dev_cfg = ToStruct(root.DefaultDeviceConfig),
            modifier_data_size = (uint)n,
            device_data_size = (uint)m,
        };
        MemoryMarshal.Write(blob.AsSpan(0, WireSizes.IoBase), in baseData);

        int off = WireSizes.IoBase;
        foreach (var p in root.Profiles)
        {
            var ms = new ModifierSettings(ToStruct(p)).ToStruct();   // Rebuild() computes data_t
            MemoryMarshal.Write(blob.AsSpan(off, WireSizes.Modifier), in ms);
            off += WireSizes.Modifier;
        }
        foreach (var d in root.Devices)
        {
            var ds = new device_settings { config = ToStruct(d.Config) };
            WriteWz(ds.name, ra_profile.MAX_NAME_LEN, d.Name);
            WriteWz(ds.profile, ra_profile.MAX_NAME_LEN, d.Profile);
            WriteWz(ds.id, device_settings.MAX_DEV_ID_LEN, d.Id);
            MemoryMarshal.Write(blob.AsSpan(off, WireSizes.Device), in ds);
            off += WireSizes.Device;
        }
        return blob;
    }

    public static unsafe RootDto ReadBlob(byte[] blob)
    {
        var root = new RootDto();
        var b = RaDevice.GetBase(blob);
        root.DefaultDeviceConfig = FromStruct(b.default_dev_cfg);
        foreach (var mod in RaDevice.Modifiers(blob)) root.Profiles.Add(FromStruct(mod.prof));
        foreach (var dev in RaDevice.Devices(blob))
            root.Devices.Add(new DeviceDto
            {
                Name = ReadWzPtr(dev.name, ra_profile.MAX_NAME_LEN),
                Profile = ReadWzPtr(dev.profile, ra_profile.MAX_NAME_LEN),
                Id = ReadWzPtr(dev.id, device_settings.MAX_DEV_ID_LEN),
                Config = FromStruct(dev.config),
            });
        return root;
    }

    static unsafe string ReadWzPtr(ushort* p, int cap) => ReadWz(p, cap);
}

public static class Json
{
    /// <summary>
    /// Guard for anything heading to the driver: the kernel precomputes logs and roots from
    /// these numbers, so NaN/Infinity/absurd values must be stopped in user space.
    /// Returns a list of problems; empty means the document is safe to serialize.
    /// </summary>
    public static List<string> Validate(RootDto r)
    {
        var bad = new List<string>();
        void Num(string where, double v, double max)
        {
            if (double.IsNaN(v)) bad.Add($"{where} 是 NaN");
            else if (double.IsInfinity(v)) bad.Add($"{where} 是无穷大");
            else if (Math.Abs(v) > max) bad.Add($"{where} = {v} 超出合理范围（|值| ≤ {max}）");
        }
        void Accel(string axis, AccelDto a)
        {
            if (!new[] { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" }.Contains(a.Mode))
                bad.Add($"{axis}.mode “{a.Mode}” 不是已知模式");
            if (!new[] { "in_out", "input", "output" }.Contains(a.CapMode))
                bad.Add($"{axis}.Cap mode “{a.CapMode}” 不是已知封顶方式");
            Num($"{axis}.inputOffset", a.InputOffset, 1000);
            Num($"{axis}.outputOffset", a.OutputOffset, 1000);
            Num($"{axis}.acceleration", a.Acceleration, 1000);
            Num($"{axis}.decayRate", a.DecayRate, 100);
            Num($"{axis}.gamma", a.Gamma, 100);
            Num($"{axis}.motivity", a.Motivity, 100);
            Num($"{axis}.exponentClassic", a.ExponentClassic, 100);
            Num($"{axis}.scale", a.Scale, 1000);
            Num($"{axis}.exponentPower", a.ExponentPower, 100);
            Num($"{axis}.limit", a.Limit, 1000);
            Num($"{axis}.syncSpeed", a.SyncSpeed, 1000);
            Num($"{axis}.smooth", a.Smooth, 1);
            Num($"{axis}.Cap.x", a.Cap.X, 10000);
            Num($"{axis}.Cap.y", a.Cap.Y, 1000);
            if (a.Data.Count > accel_args.LUT_RAW_DATA_CAPACITY)
                bad.Add($"{axis}.data 有 {a.Data.Count} 个点，超过 {accel_args.LUT_RAW_DATA_CAPACITY} 的上限");
            for (int i = 0; i < a.Data.Count; i++)
                if (!float.IsFinite(a.Data[i])) bad.Add($"{axis}.data[{i}] 不是有限数字");
        }
        for (int i = 0; i < r.Profiles.Count; i++)
        {
            var p = r.Profiles[i];
            var tag = $"profile[{i}]“{p.Name}”";
            if (string.IsNullOrWhiteSpace(p.Name)) bad.Add($"{tag} 名字是空的");
            Accel(tag + ".X", p.X);
            Accel(tag + ".Y", p.Y);
            Num(tag + ".Output DPI", p.OutputDpi, 8000);
            Num(tag + ".Y/X ratio", p.YxRatio, 100);
            Num(tag + ".L/R ratio", p.LrRatio, 100);
            Num(tag + ".U/D ratio", p.UdRatio, 100);
            Num(tag + ".rotation", p.Rotation, 360);
            Num(tag + ".snap", p.Snap, 180);
            Num(tag + ".Input Speed Cap", p.InputSpeedCap, 10000);
            Num(tag + ".lpNorm", p.Speed.LpNorm, 100);
            Num(tag + ".domain x", p.Domain.X, 1000);
            Num(tag + ".domain y", p.Domain.Y, 1000);
            Num(tag + ".range x", p.Range.X, 1000);
            Num(tag + ".range y", p.Range.Y, 1000);
            Num(tag + ".halflife input", p.Speed.InputHalflife, 10000);
            Num(tag + ".halflife scale", p.Speed.ScaleHalflife, 10000);
            Num(tag + ".halflife output", p.Speed.OutputHalflife, 10000);
            if (p.Speed.InputHalflife < 0 || p.Speed.ScaleHalflife < 0 || p.Speed.OutputHalflife < 0)
                bad.Add($"{tag} 有负的平滑半衰期");
        }
        for (int i = 0; i < r.Devices.Count; i++)
        {
            var d = r.Devices[i];
            var tag = $"device[{i}]“{d.Name}”";
            if (string.IsNullOrWhiteSpace(d.Id)) bad.Add($"{tag} 没有设备 id");
            // the wire field is WCHAR[200]; WriteWz truncates silently, and two ids that share their
            // first 199 characters would become the same device inside the driver
            if (d.Id.Length >= device_settings.MAX_DEV_ID_LEN)
                bad.Add($"{tag} 的设备 id 有 {d.Id.Length} 字符，超过驱动字段上限 {device_settings.MAX_DEV_ID_LEN - 1}，写进驱动会被截断");
            if (!r.Profiles.Any(p => p.Name == d.Profile)) bad.Add($"{tag} 引用了不存在的 profile “{d.Profile}”");
            if (d.Config.Dpi < 0 || d.Config.Dpi > 64000) bad.Add($"{tag} DPI={d.Config.Dpi} 不合理");
            if (d.Config.PollingRate < 0 || d.Config.PollingRate > 16000) bad.Add($"{tag} 轮询率={d.Config.PollingRate} 不合理");
            if (d.Config.MinimumTime is { } mn && (mn <= 0 || mn > 1000)) bad.Add($"{tag} minimumTime={mn} 不合理");
            if (d.Config.MaximumTime is { } mx && (mx <= 0 || mx > 1000)) bad.Add($"{tag} maximumTime={mx} 不合理");
            if (d.Config.MinimumTime is { } a && d.Config.MaximumTime is { } b && a > b)
                bad.Add($"{tag} 时间上下限反了");
        }
        var def = r.DefaultDeviceConfig;
        if (def.Dpi < 0 || def.Dpi > 64000) bad.Add($"默认 DPI={def.Dpi} 不合理");
        if (def.PollingRate < 0 || def.PollingRate > 16000) bad.Add($"默认轮询率={def.PollingRate} 不合理");
        if (r.Profiles.Count == 0) bad.Add("文档里一个 profile 都没有");
        if (r.Profiles.Count > 256) bad.Add($"profile 有 {r.Profiles.Count} 个，驱动不接受这么多");
        if (r.Devices.Count > 1024) bad.Add($"设备有 {r.Devices.Count} 个，驱动不接受这么多");
        var dup = r.Profiles.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) bad.Add($"profile 名字重复：“{dup.Key}” 出现 {dup.Count()} 次，设备会不知道跟哪个");
        return bad;
    }

    /// <summary>
    /// Newtonsoft (what wrapper.dll uses) writes doubles as 1.0 / 3.0, not 1 / 3.
    /// Matching that keeps the official file diff-free when our app rewrites it.
    /// </summary>
    sealed class DoubleStyle : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double v, JsonSerializerOptions o)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { writer.WriteNumberValue(0.0); return; }
            string s = v == Math.Floor(v) && Math.Abs(v) < 1e15
                ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            writer.WriteRawValue(s, skipInputValidation: true);
        }
    }

    sealed class FloatStyle : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.GetSingle();
        public override void Write(Utf8JsonWriter writer, float v, JsonSerializerOptions o)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { writer.WriteNumberValue(0f); return; }
            string s = v == MathF.Floor(v) && Math.Abs(v) < 1e15f
                ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            writer.WriteRawValue(s, skipInputValidation: true);
        }
    }

    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new DoubleStyle(), new FloatStyle() },
    };

    public static string Serialize(RootDto r) => JsonSerializer.Serialize(r, Opts);
    public static RootDto Deserialize(string text) =>
        JsonSerializer.Deserialize<RootDto>(text) ?? throw new InvalidDataException("settings.json is empty");
}
