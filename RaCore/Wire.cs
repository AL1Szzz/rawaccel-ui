using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RaCore;

// Binary layout mirrors rawaccel-src/common/{rawaccel-base.hpp, rawaccel.hpp} exactly
// for MSVC x64 natural alignment (pack=8). The driver memcpy's these structs across the
// IOCTL boundary, so sizes and field offsets must match byte-for-byte.
//
// sizeof checks live in WireSizes.Validate().

public enum accel_mode : int
{
    classic = 0,
    jump = 1,
    natural = 2,
    synchronous = 3,
    power = 4,
    lookup = 5,
    noaccel = 6,
}

public enum cap_mode : int
{
    io = 0,
    @in = 1,
    @out = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct vec2d
{
    public double x;
    public double y;
}

[StructLayout(LayoutKind.Sequential)]
public struct time_clamp
{
    public double min;
    public double max;
}

[StructLayout(LayoutKind.Sequential)]
public struct device_config
{
    public byte disable;
    public byte set_extra_info;
    public byte poll_time_lock;
    // 1 byte of implicit padding before the int
    public int dpi;
    public int polling_rate;
    public time_clamp clamp;
}

/// <summary>rawaccel::accel_args</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct accel_args
{
    public const int LUT_RAW_DATA_CAPACITY = 514;

    public accel_mode mode;
    public byte gain;
    // implicit padding to 8
    public double input_offset;
    public double output_offset;
    public double acceleration;
    public double decay_rate;
    public double gamma;
    public double motivity;
    public double exponent_classic;
    public double scale;
    public double exponent_power;
    public double limit;
    public double sync_speed;
    public double smooth;
    public vec2d cap;
    public cap_mode cap_mode;
    public int length;
    public fixed float data[LUT_RAW_DATA_CAPACITY];
}

/// <summary>rawaccel::speed_args</summary>
[StructLayout(LayoutKind.Sequential)]
public struct speed_args
{
    public byte whole;
    // implicit padding to 8
    public double lp_norm;
    public double input_speed_smooth_halflife;
    public double scale_smooth_halflife;
    public double output_speed_smooth_halflife;
}

/// <summary>rawaccel::profile (one name array only; the second one lives in device_settings)</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ra_profile
{
    public const int MAX_NAME_LEN = 256;

    public fixed ushort name[MAX_NAME_LEN];
    public vec2d domain_weights;
    public vec2d range_weights;
    public accel_args accel_x;
    public accel_args accel_y;
    public speed_args speed_processor_args;
    public double output_dpi;
    public double yx_output_dpi_ratio;
    public double lr_output_dpi_ratio;
    public double ud_output_dpi_ratio;
    public double degrees_rotation;
    public double degrees_snap;
    public double speed_min;
    public double speed_max;
}

[StructLayout(LayoutKind.Sequential)]
public struct modifier_flags
{
    public byte apply_rotate;
    public byte compute_ref_angle;
    public byte apply_snap;
    public byte clamp_speed;
    public byte apply_directional_weight;
    public byte apply_dir_mul_x;
    public byte apply_dir_mul_y;
    public byte reserved;
}

// ---- accel_union members (all overlaid at offset 0) ----

[StructLayout(LayoutKind.Sequential)]
public struct classic_legacy
{
    public double accel_raised;
    public double cap;
    public double sign;
}

[StructLayout(LayoutKind.Sequential)]
public struct classic_gain
{
    public double accel_raised;
    public vec2d cap;
    public double constant;
    public double sign;
}

[StructLayout(LayoutKind.Sequential)]
public struct jump_base
{
    public vec2d step;
    public double smooth_rate;
}

public struct jump_legacy
{
    public jump_base b;
}

public struct jump_gain
{
    public jump_base b;
    public double C;
}

public struct natural_base
{
    public double offset;
    public double accel;
    public double limit;
}

public struct natural_legacy
{
    public natural_base b;
}

public struct natural_gain
{
    public natural_base b;
    public double constant;
}

public struct power_base
{
    public vec2d offset;
    public double scale;
    public double constant;
}

public struct power_legacy
{
    public power_base b;
    public double cap;
}

public struct power_gain
{
    public power_base b;
    public vec2d cap;
    public double constant_b;
}

public struct activation_legacy
{
    public double log_motivity;
    public double gamma_const;
    public double log_syncspeed;
    public double syncspeed;
    public double sharpness;
    public double sharpness_recip;
    public byte use_linear_clamp;
    // padding to 8
    public double minimum_sens;
    public double maximum_sens;
}

public struct fp_rep_range
{
    public int start;
    public int stop;
    public int num;
}

public struct activation_gain
{
    public byte velocity;
    // padding to 8
    public fp_rep_range range;
    // padding to 8
    public double x_start;
}

public struct lookup_t
{
    public int size;
    public byte velocity;
}

public struct noaccel_t
{
    public byte placeholder;
}

/// <summary>rawaccel::accel_union. Size = sizeof largest member (activation_legacy = 72).</summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
public struct accel_union
{
    [FieldOffset(0)] public noaccel_t noaccel;
    [FieldOffset(0)] public lookup_t lut;
    [FieldOffset(0)] public classic_gain classic_g;
    [FieldOffset(0)] public classic_legacy classic_l;
    [FieldOffset(0)] public jump_gain jump_g;
    [FieldOffset(0)] public jump_legacy jump_l;
    [FieldOffset(0)] public natural_gain natural_g;
    [FieldOffset(0)] public natural_legacy natural_l;
    [FieldOffset(0)] public power_gain power_g;
    [FieldOffset(0)] public power_legacy power_l;
    [FieldOffset(0)] public activation_gain loglog_sigmoid_g;
    [FieldOffset(0)] public activation_legacy loglog_sigmoid_l;
}

/// <summary>rawaccel::modifier_settings::data_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct modifier_data
{
    public modifier_flags flags;
    public vec2d rot_direction;
    public accel_union accel_x;
    public accel_union accel_y;
}

/// <summary>rawaccel::modifier_settings</summary>
[StructLayout(LayoutKind.Sequential)]
public struct modifier_settings
{
    public ra_profile prof;
    public modifier_data data;
}

/// <summary>rawaccel::device_settings</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct device_settings
{
    public const int MAX_DEV_ID_LEN = 200;

    public fixed ushort name[ra_profile.MAX_NAME_LEN];
    public fixed ushort profile[ra_profile.MAX_NAME_LEN];
    public fixed ushort id[MAX_DEV_ID_LEN];
    public device_config config;
}

/// <summary>rawaccel::io_base</summary>
[StructLayout(LayoutKind.Sequential)]
public struct io_base
{
    public device_config default_dev_cfg;
    public uint modifier_data_size;
    public uint device_data_size;
}

/// <summary>rawaccel::version_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct version_t
{
    public int major;
    public int minor;
    public int patch;
}

public static class WireSizes
{
    public static int IoBase => Unsafe.SizeOf<io_base>();
    public static int Modifier => Unsafe.SizeOf<modifier_settings>();
    public static int Device => Unsafe.SizeOf<device_settings>();
    public static int AccelArgs => Unsafe.SizeOf<accel_args>();
    public static int Profile => Unsafe.SizeOf<ra_profile>();
    public static int Union => Unsafe.SizeOf<accel_union>();
    public static int Data => Unsafe.SizeOf<modifier_data>();

    // Expected values derived from the C++ headers (x64, natural alignment).
    // speed_args is 40 (bool+pad7+4 doubles), profile has a single 512-byte name array.
    // Confirmed empirically: driver READ returns 5224 bytes = 40 + 1 * 5184.
    public const int EXP_ACCEL_ARGS = 2184;
    public const int EXP_PROFILE = 5016;
    public const int EXP_UNION = 72;
    public const int EXP_DATA = 168;
    public const int EXP_MODIFIER = 5184;
    public const int EXP_DEVICE = 1456;
    public const int EXP_IO_BASE = 40;

    public static (string name, int actual, int expected)[] Report() => new[]
    {
        ("accel_args",        AccelArgs,  EXP_ACCEL_ARGS),
        ("profile",           Profile,    EXP_PROFILE),
        ("accel_union",       Union,      EXP_UNION),
        ("modifier_data",     Data,       EXP_DATA),
        ("modifier_settings", Modifier,   EXP_MODIFIER),
        ("device_settings",   Device,     EXP_DEVICE),
        ("io_base",           IoBase,     EXP_IO_BASE),
    };

    public static bool AllMatch() => Report().All(r => r.actual == r.expected);
}
