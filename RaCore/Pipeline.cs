using System.Runtime.InteropServices;

namespace RaCore;

public enum DistanceMode { euclidean, separate, max, Lp }

/// <summary>rawaccel::speed_processor</summary>
public sealed class SpeedProcessor
{
    public speed_args args;
    public DistanceMode distMode;
    public bool smoothInput, smoothScale, smoothOutput;
    public SmootherSet sx = new(), sy = new();

    public const double InputTrendHalflife = 1.25;
    public const double OutputTrendHalflife = 0.7;

    public void Init(speed_args in_args)
    {
        args = in_args;

        if (in_args.whole == 0) distMode = DistanceMode.separate;
        else if (in_args.lp_norm >= 16 || in_args.lp_norm <= 0) distMode = DistanceMode.max;
        else if (in_args.lp_norm != 2) distMode = DistanceMode.Lp;
        else distMode = DistanceMode.euclidean;

        smoothInput = in_args.input_speed_smooth_halflife > 0;
        smoothScale = in_args.scale_smooth_halflife > 0;
        smoothOutput = in_args.output_speed_smooth_halflife > 0;

        if (smoothInput)
        {
            sx.input.Init(in_args.input_speed_smooth_halflife, InputTrendHalflife);
            sy.input.Init(in_args.input_speed_smooth_halflife, InputTrendHalflife);
        }
        if (smoothScale)
        {
            sx.scale.Init(in_args.scale_smooth_halflife);
            sy.scale.Init(in_args.scale_smooth_halflife);
        }
        if (smoothOutput)
        {
            sx.output.Init(in_args.output_speed_smooth_halflife, OutputTrendHalflife);
            sy.output.Init(in_args.output_speed_smooth_halflife, OutputTrendHalflife);
        }
    }

    public double CalcSpeedWhole(vec2d input, double time)
    {
        double speed = distMode switch
        {
            DistanceMode.max => Math.Max(Math.Abs(input.x), Math.Abs(input.y)),
            DistanceMode.Lp => M.Lp(input, args.lp_norm),
            _ => M.Magnitude(input),
        };
        if (smoothInput) speed = sx.input.Smooth(speed, time);
        return speed;
    }

    public vec2d CalcSpeedSeparate(vec2d input, double time)
    {
        double x = input.x, y = input.y;
        if (smoothInput)
        {
            x = sx.input.Smooth(x, time);
            y = sy.input.Smooth(y, time);
        }
        return new vec2d { x = x, y = y };
    }
}

/// <summary>
/// rawaccel::modifier_settings, mutable so a synchronous/gain profile can rebuild its LUT
/// into accel_args.data the way the C++ constructors do.
/// </summary>
public sealed class ModifierSettings
{
    public ra_profile prof;
    public modifier_data data;

    public ModifierSettings() { }
    public ModifierSettings(ra_profile p) { prof = p; Rebuild(); }

    /// <summary>init_data(): fills data_t (flags, rot_direction, both unions) from prof.</summary>
    public unsafe void Rebuild()
    {
        data.accel_x = Accel.BuildUnion(ref prof.accel_x);
        data.accel_y = Accel.BuildUnion(ref prof.accel_y);
        data.rot_direction = M.Direction(prof.degrees_rotation);

        var p = prof;
        data.flags = new modifier_flags
        {
            clamp_speed = (byte)((p.speed_max > 0 && p.speed_min <= p.speed_max) ? 1 : 0),
            apply_rotate = (byte)(p.degrees_rotation != 0 ? 1 : 0),
            apply_snap = (byte)(p.degrees_snap != 0 ? 1 : 0),
            apply_directional_weight = (byte)((p.speed_processor_args.whole != 0 && p.range_weights.x != p.range_weights.y) ? 1 : 0),
            apply_dir_mul_x = (byte)(p.lr_output_dpi_ratio != 1 ? 1 : 0),
            apply_dir_mul_y = (byte)(p.ud_output_dpi_ratio != 1 ? 1 : 0),
        };
        data.flags.compute_ref_angle = (byte)((data.flags.apply_snap != 0 || data.flags.apply_directional_weight != 0) ? 1 : 0);
    }

    public modifier_settings ToStruct()
    {
        var s = new modifier_settings { prof = prof, data = data };
        return s;
    }

    public static ModifierSettings From(modifier_settings s) => new() { prof = s.prof, data = s.data };
}

/// <summary>
/// rawaccel::modifier::modify() - the exact per-packet path the kernel driver runs.
/// stateful because of the smoothers; use one instance per device.
/// </summary>
public sealed class Modifier
{
    readonly ModifierSettings settings;
    readonly double outputDpiFactor;

    public Modifier(ModifierSettings settings)
    {
        this.settings = settings;
        outputDpiFactor = settings.prof.output_dpi / M.NORMALIZED_DPI;
    }

    public unsafe vec2d Modify(vec2d input, SpeedProcessor sp, double dpiFactor, double time)
    {
        var p = settings.prof;
        ref var data = ref settings.data;
        var flags = data.flags;

        double referenceAngle = 0;
        double ipsFactor = dpiFactor / time;
        var vin = input;

        if (flags.apply_rotate != 0) vin = M.Rotate(vin, data.rot_direction);

        if (flags.compute_ref_angle != 0 && vin.y != 0)
        {
            if (vin.x == 0) referenceAngle = M.PI / 2;
            else
            {
                referenceAngle = Math.Atan(Math.Abs(vin.y / vin.x));

                if (flags.apply_snap != 0)
                {
                    double snap = p.degrees_snap * M.PI / 180;
                    if (referenceAngle > M.PI / 2 - snap)
                    {
                        referenceAngle = M.PI / 2;
                        vin = new vec2d { x = 0, y = M.CopySign(M.Magnitude(vin), vin.y) };
                    }
                    else if (referenceAngle < snap)
                    {
                        referenceAngle = 0;
                        vin = new vec2d { x = M.CopySign(M.Magnitude(vin), vin.x), y = 0 };
                    }
                }
            }
        }

        if (flags.clamp_speed != 0)
        {
            double speed = M.Magnitude(vin) * ipsFactor;
            double ratio = M.ClampD(speed, p.speed_min, p.speed_max) / speed;
            vin.x *= ratio;
            vin.y *= ratio;
        }

        var absWeighted = new vec2d
        {
            x = Math.Abs(vin.x * ipsFactor * p.domain_weights.x),
            y = Math.Abs(vin.y * ipsFactor * p.domain_weights.y),
        };

        if (sp.distMode == DistanceMode.separate)
        {
            var v = sp.CalcSpeedSeparate(absWeighted, time);

            double scale_x = Accel.Scale(data.accel_x, ref p.accel_x, v.x, p.range_weights.x);
            double scale_y = Accel.Scale(data.accel_y, ref p.accel_y, v.y, p.range_weights.y);

            if (sp.smoothScale)
            {
                scale_x = sp.sx.scale.Smooth(scale_x, time);
                scale_y = sp.sy.scale.Smooth(scale_y, time);
            }

            vin.x *= scale_x;
            vin.y *= scale_y;

            if (sp.smoothOutput)
            {
                vin.x = M.CopySign(sp.sx.output.Smooth(Math.Abs(vin.x), time), vin.x);
                vin.y = M.CopySign(sp.sy.output.Smooth(Math.Abs(vin.y), time), vin.y);
            }
        }
        else
        {
            double speed = sp.CalcSpeedWhole(absWeighted, time);
            double weight = p.range_weights.x;

            if (flags.apply_directional_weight != 0)
            {
                double diff = p.range_weights.y - p.range_weights.x;
                weight += 2 / M.PI * referenceAngle * diff;
            }

            double scale = Accel.Scale(data.accel_x, ref p.accel_x, speed, weight);

            if (sp.smoothScale) scale = sp.sx.scale.Smooth(scale, time);

            vin.x *= scale;
            vin.y *= scale;

            if (sp.smoothOutput)
            {
                double mag = M.Magnitude(vin);
                if (mag > 0)
                {
                    double sm = sp.sx.output.Smooth(mag, time);
                    vin.x *= sm / mag;
                    vin.y *= sm / mag;
                }
            }
        }

        double dpiAdj = outputDpiFactor * dpiFactor;
        vin.x *= dpiAdj;
        vin.y *= dpiAdj * p.yx_output_dpi_ratio;

        if (flags.apply_dir_mul_x != 0 && vin.x < 0) vin.x *= p.lr_output_dpi_ratio;
        if (flags.apply_dir_mul_y != 0 && vin.y < 0) vin.y *= p.ud_output_dpi_ratio;

        return vin;
    }

    /// <summary>Convenience: one-shot, stateless (all smoothers off) evaluation of a pure-x input.</summary>
    public static vec2d EvaluateOnce(ra_profile prof, vec2d input, double time, double dpiFactor = 1)
    {
        var ms = new ModifierSettings(prof);
        var sp = new SpeedProcessor();
        sp.Init(prof.speed_processor_args);
        return new Modifier(ms).Modify(input, sp, dpiFactor, time);
    }
}
