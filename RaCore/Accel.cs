using System.Runtime.InteropServices;

namespace RaCore;

/// <summary>
/// Faithful C# port of rawaccel-src/common/accel-*.hpp + rawaccel.hpp.
/// The same headers compile into rawaccel.sys, so this is the driver's own math,
/// not a re-derivation. Accel.BuildUnion() must produce byte-identical data_t.
/// </summary>
public static class M
{
    public const double NORMALIZED_DPI = 1000;
    public const double PI = Math.PI;

    public static double MinD(double a, double b) => a < b ? a : b;
    public static double MaxD(double a, double b) => b < a ? a : b;
    public static double ClampD(double v, double lo, double hi) => MinD(MaxD(v, lo), hi);

    public static double Magnitude(vec2d v) => Math.Sqrt(v.x * v.x + v.y * v.y);
    public static double Lp(vec2d v, double p) => Math.Pow(Math.Pow(Math.Abs(v.x), p) + Math.Pow(Math.Abs(v.y), p), 1 / p);
    public static vec2d Direction(double degrees) { double r = degrees * PI / 180; return new vec2d { x = Math.Cos(r), y = Math.Sin(r) }; }
    public static vec2d Rotate(vec2d v, vec2d d) => new() { x = v.x * d.x - v.y * d.y, y = v.x * d.y + v.y * d.x };

    /// <summary>C++ lerp with the same monotonic clamp trick.</summary>
    public static double Lerp(double a, double b, double t)
    {
        double x = a + t * (b - a);
        if ((t > 1) == (a < b)) return MaxD(x, b);
        return MinD(x, b);
    }

    /// <summary>bit-twiddling ilogb from utility.hpp (unbiased exponent of a normal double).</summary>
    public static int Ilogb(double x)
    {
        ulong i = (ulong)BitConverter.DoubleToInt64Bits(x);
        return (int)((i >> 52) & 0x7ff) - 0x3ff;
    }

    public static double Scalbn(double x, int n) => x * Math.Pow(2, n);

    public static double CopySign(double magnitude, double sign) => Math.CopySign(magnitude, sign);
}

/// <summary>simple_ema_smoother / linear_ema_smoother from rawaccel.hpp.</summary>
public sealed class SimpleEma
{
    public double windowCoefficient, cutoffCoefficient, windowTotal, cutoffTotal;

    public void Init(double halfLife)
    {
        windowTotal = 0; cutoffTotal = 0;
        windowCoefficient = halfLife > 0 ? Math.Pow(0.5, 1 / halfLife) : 0;
        cutoffCoefficient = 1.0 - Math.Sqrt(1.0 - windowCoefficient);
    }

    public double Smooth(double speed, double time)
    {
        double tw = 1 - Math.Pow(windowCoefficient, time);
        double tc = 1 - Math.Pow(cutoffCoefficient, time);
        windowTotal += tw * (speed - windowTotal);
        cutoffTotal += tc * (speed - cutoffTotal);
        return Math.Min(windowTotal, cutoffTotal);
    }
}

public sealed class LinearEma
{
    public const double TrendDampening = 0.75; // found by experimentation upstream
    public double windowCoefficient, cutoffCoefficient, windowTrendCoefficient, cutoffTrendCoefficient;
    public double windowTotal, cutoffTotal, windowTrendTotal, cutoffTrendTotal;
    public bool is_init;

    public void Init(double halfLife, double trendHalfLife)
    {
        windowTotal = cutoffTotal = windowTrendTotal = cutoffTrendTotal = 0;
        windowCoefficient = halfLife > 0 ? Math.Pow(0.5, 1 / halfLife) : 0;
        windowTrendCoefficient = trendHalfLife > 0 ? Math.Pow(0.5, 1 / trendHalfLife) : 0;
        cutoffCoefficient = 1.0 - Math.Sqrt(1.0 - windowCoefficient);
        cutoffTrendCoefficient = 1.0 - Math.Sqrt(1.0 - windowTrendCoefficient);
    }

    public double Smooth(double speed, double time)
    {
        double tw = 1 - Math.Pow(windowCoefficient, time);
        double tc = 1 - Math.Pow(cutoffCoefficient, time);
        double twt = 1 - Math.Pow(windowTrendCoefficient, time);
        double tct = 1 - Math.Pow(cutoffTrendCoefficient, time);

        double oldWindow = windowTotal, oldCutoff = cutoffTotal;

        windowTrendTotal *= TrendDampening;
        cutoffTrendTotal *= TrendDampening;
        windowTotal += windowTrendTotal * time;
        cutoffTotal += cutoffTrendTotal * time;

        windowTotal += tw * (speed - windowTotal);
        cutoffTotal += tc * (speed - cutoffTotal);

        windowTotal = Math.Max(windowTotal, 0.0);
        cutoffTotal = Math.Max(cutoffTotal, 0.0);

        double newWindowTrend = time > 0 ? (windowTotal - oldWindow) / time : 0;
        double newCutoffTrend = time > 0 ? (cutoffTotal - oldCutoff) / time : 0;
        windowTrendTotal += twt * (newWindowTrend - windowTrendTotal);
        cutoffTrendTotal += tct * (newCutoffTrend - cutoffTrendTotal);

        return Math.Min(windowTotal, cutoffTotal);
    }
}

public sealed class SmootherSet
{
    public LinearEma input = new();
    public SimpleEma scale = new();
    public LinearEma output = new();
}

/// <summary>
/// Precomputed per-axis accel state. BuildUnion() fills rawaccel::accel_union;
/// Eval() is the operator() of the matching C++ struct.
/// </summary>
public static class Accel
{
    const double DBL_MAX = double.MaxValue;

    // ---------- fp_rep_range (used by synchronous/gain LUT) ----------
    public readonly struct FpRange
    {
        public readonly int start, stop, num;
        public FpRange(int s, int e, int n) { start = s; stop = e; num = n; }
        public int Size() => (stop - start) * num + 1;
        public void ForEach(Action<double> fn)
        {
            for (int e = 0; e < stop - start; e++)
            {
                double expScale = M.Scalbn(1, e + start) / num;
                for (int i = 0; i < num; i++) fn((i + num) * expScale);
            }
            fn(M.Scalbn(1, stop));
        }
    }

    // ---------- classic ----------
    static double ClassicBaseFn(double x, double accelRaised, in accel_args a)
        => accelRaised * Math.Pow(x - a.input_offset, a.exponent_classic) / x;

    static double ClassicBaseAccel(double x, double y, in accel_args a)
        => Math.Pow(x * y * Math.Pow(x - a.input_offset, -a.exponent_classic), 1 / (a.exponent_classic - 1));

    static double CGain(double x, double accel, double power, double offset)
        => power * Math.Pow(accel * (x - offset), power - 1);
    static double CGainInverse(double y, double accel, double power, double offset)
        => (accel * offset + Math.Pow(y / power, 1 / (power - 1))) / accel;
    static double CGainAccel(double x, double y, double power, double offset)
        => -Math.Pow(y / power, 1 / (power - 1)) / (offset - x);

    static classic_gain BuildClassicGain(in accel_args a)
    {
        var s = new classic_gain { cap = new vec2d { x = DBL_MAX, y = DBL_MAX }, constant = 0, sign = 1 };
        switch (a.cap_mode)
        {
            case cap_mode.io:
                s.cap.x = a.cap.x;
                s.cap.y = a.cap.y - 1;
                if (s.cap.y < 0) { s.cap.y = -s.cap.y; s.sign = -s.sign; }
                {
                    double aa = CGainAccel(s.cap.x, s.cap.y, a.exponent_classic, a.input_offset);
                    s.accel_raised = Math.Pow(aa, a.exponent_classic - 1);
                }
                s.constant = (ClassicBaseFn(s.cap.x, s.accel_raised, a) - s.cap.y) * s.cap.x;
                break;
            case cap_mode.@in:
                s.accel_raised = Math.Pow(a.acceleration, a.exponent_classic - 1);
                if (a.cap.x > 0)
                {
                    s.cap.x = a.cap.x;
                    s.cap.y = CGain(s.cap.x, a.acceleration, a.exponent_classic, a.input_offset);
                    s.constant = (ClassicBaseFn(s.cap.x, s.accel_raised, a) - s.cap.y) * s.cap.x;
                }
                break;
            default: // out
                s.accel_raised = Math.Pow(a.acceleration, a.exponent_classic - 1);
                if (a.cap.y > 0)
                {
                    s.cap.y = a.cap.y - 1;
                    if (s.cap.y == 0) s.cap.x = 0;
                    else
                    {
                        if (s.cap.y < 0) { s.cap.y = -s.cap.y; s.sign = -s.sign; }
                        s.cap.x = CGainInverse(s.cap.y, a.acceleration, a.exponent_classic, a.input_offset);
                        s.constant = (ClassicBaseFn(s.cap.x, s.accel_raised, a) - s.cap.y) * s.cap.x;
                    }
                }
                break;
        }
        return s;
    }

    static double EvalClassicGain(classic_gain s, double x, in accel_args a)
    {
        if (x <= a.input_offset) return 1;
        double output = x < s.cap.x ? ClassicBaseFn(x, s.accel_raised, a) : s.constant / x + s.cap.y;
        return s.sign * output + 1;
    }

    static classic_legacy BuildClassicLegacy(in accel_args a)
    {
        var s = new classic_legacy { cap = DBL_MAX, sign = 1 };
        switch (a.cap_mode)
        {
            case cap_mode.io:
                s.cap = a.cap.y - 1;
                if (s.cap < 0) { s.cap = -s.cap; s.sign = -s.sign; }
                s.accel_raised = Math.Pow(ClassicBaseAccel(a.cap.x, s.cap, a), a.exponent_classic - 1);
                break;
            case cap_mode.@in:
                s.accel_raised = Math.Pow(a.acceleration, a.exponent_classic - 1);
                if (a.cap.x > 0) s.cap = ClassicBaseFn(a.cap.x, s.accel_raised, a);
                break;
            default: // out
                s.accel_raised = Math.Pow(a.acceleration, a.exponent_classic - 1);
                if (a.cap.y > 0)
                {
                    s.cap = a.cap.y - 1;
                    if (s.cap < 0) { s.cap = -s.cap; s.sign = -s.sign; }
                }
                break;
        }
        return s;
    }

    static double EvalClassicLegacy(classic_legacy s, double x, in accel_args a)
    {
        if (x <= a.input_offset) return 1;
        return s.sign * Math.Min(ClassicBaseFn(x, s.accel_raised, a), s.cap) + 1;
    }

    // ---------- jump ----------
    static jump_base BuildJumpBase(in accel_args a)
    {
        var jb = new jump_base { step = new vec2d { x = a.cap.x, y = a.cap.y - 1 } };
        double rateInverse = a.smooth * jb.step.x;
        jb.smooth_rate = rateInverse < 1 ? 0 : 2 * M.PI / rateInverse;
        return jb;
    }
    static double JumpDecay(vec2d step, double rate, double x) => Math.Exp(rate * (step.x - x));
    static double JumpSmooth(vec2d step, double rate, double x) => step.y / (1 + JumpDecay(step, rate, x));
    static double JumpSmoothAntideriv(vec2d step, double rate, double x)
        => step.y * (x + Math.Log(1 + JumpDecay(step, rate, x)) / rate);

    static double EvalJumpGain(jump_gain s, double x, in accel_args a)
    {
        if (x <= 0) return 1;
        bool smooth = s.b.smooth_rate != 0;
        if (smooth) return 1 + (JumpSmoothAntideriv(s.b.step, s.b.smooth_rate, x) + s.C) / x;
        return x < s.b.step.x ? 1 : 1 + s.b.step.y * (x - s.b.step.x) / x;
    }
    static double EvalJumpLegacy(jump_legacy s, double x, in accel_args a)
    {
        bool smooth = s.b.smooth_rate != 0;
        if (smooth) return JumpSmooth(s.b.step, s.b.smooth_rate, x) + 1;
        return x < s.b.step.x ? 1 : 1 + s.b.step.y;
    }

    // ---------- natural ----------
    static natural_base BuildNatural(in accel_args a)
    {
        double limit = a.limit - 1;
        return new natural_base { offset = a.input_offset, limit = limit, accel = a.decay_rate / Math.Abs(limit) };
    }
    static double EvalNaturalLegacy(natural_legacy s, double x, in accel_args a)
    {
        if (x <= s.b.offset) return 1;
        double offset_x = s.b.offset - x;
        double decay = Math.Exp(s.b.accel * offset_x);
        return s.b.limit * (1 - (s.b.offset - decay * offset_x) / x) + 1;
    }
    static double EvalNaturalGain(natural_gain s, double x, in accel_args a)
    {
        if (x <= s.b.offset) return 1;
        double offset_x = s.b.offset - x;
        double decay = Math.Exp(s.b.accel * offset_x);
        double output = s.b.limit * (decay / s.b.accel - offset_x) + s.constant;
        return output / x + 1;
    }

    // ---------- power ----------
    static power_base BuildPowerBase(in accel_args a)
    {
        var b = new power_base();
        double n = a.exponent_power;
        if (a.cap_mode != cap_mode.io) b.scale = a.scale;
        else if (a.gain != 0) b.scale = ScaleFromGainPoint(a.cap.x, a.cap.y, n);
        else
        {
            b.offset = default; b.constant = 0;
            b.scale = ScaleFromOutputPoint(a.cap.x, a.cap.y, n, 0);
            return b; // legacy+io ignores output_offset (circular dependency upstream)
        }
        b.offset.x = PowerGainInverse(a.output_offset, n, b.scale);
        b.offset.y = a.output_offset;
        b.constant = b.offset.x * b.offset.y * n / (n + 1);
        return b;
    }
    static double PowerBaseFn(in power_base b, double x, in accel_args a)
        => x <= b.offset.x ? b.offset.y : Math.Pow(b.scale * x, a.exponent_power) + b.constant / x;
    static double PowerGain(double input, double power, double scale) => (power + 1) * Math.Pow(input * scale, power);
    static double PowerGainInverse(double gain, double power, double scale) => Math.Pow(gain / (power + 1), 1 / power) / scale;
    static double ScaleFromGainPoint(double input, double gain, double power) => Math.Pow(gain / (power + 1), 1 / power) / input;
    static double ScaleFromOutputPoint(double input, double output, double power, double C) => Math.Pow(output - C / input, 1 / power) / input;

    static power_legacy BuildPowerLegacy(in accel_args a)
    {
        var b = BuildPowerBase(a);
        var s = new power_legacy { b = b, cap = DBL_MAX };
        switch (a.cap_mode)
        {
            case cap_mode.io: s.cap = a.cap.y; break;
            case cap_mode.@in: if (a.cap.x > 0) s.cap = PowerBaseFn(b, a.cap.x, a); break;
            default: if (a.cap.y > 0) s.cap = a.cap.y; break;
        }
        return s;
    }
    static double EvalPowerLegacy(power_legacy s, double x, in accel_args a) => Math.Min(PowerBaseFn(s.b, x, a), s.cap);

    static power_gain BuildPowerGain(in accel_args a)
    {
        var b = BuildPowerBase(a);
        var s = new power_gain { b = b, cap = new vec2d { x = DBL_MAX, y = DBL_MAX } };
        switch (a.cap_mode)
        {
            case cap_mode.io: s.cap = a.cap; break;
            case cap_mode.@in:
                if (a.cap.x > 0)
                {
                    if (a.cap.x <= b.offset.x)
                    {
                        s.cap.x = 0; s.cap.y = b.offset.y;
                        s.constant_b = 0;
                        return s;
                    }
                    s.cap.x = a.cap.x;
                    s.cap.y = PowerGain(a.cap.x, a.exponent_power, b.scale);
                }
                break;
            default:
                if (a.cap.y > 0)
                {
                    s.cap.x = PowerGainInverse(a.cap.y, a.exponent_power, b.scale);
                    s.cap.y = a.cap.y;
                }
                break;
        }
        s.constant_b = (PowerBaseFn(b, s.cap.x, a) - s.cap.y) * s.cap.x;
        return s;
    }
    static double EvalPowerGain(power_gain s, double x, in accel_args a)
        => x < s.cap.x ? PowerBaseFn(s.b, x, a) : s.cap.y + s.constant_b / x;

    // ---------- synchronous (activation_framework) ----------
    static activation_legacy BuildActivationLegacy(in accel_args a)
    {
        double sharpness = a.smooth == 0 ? 16 : 0.5 / a.smooth;
        return new activation_legacy
        {
            log_motivity = Math.Log(a.motivity),
            gamma_const = a.gamma / Math.Log(a.motivity),
            log_syncspeed = Math.Log(a.sync_speed),
            syncspeed = a.sync_speed,
            sharpness = sharpness,
            sharpness_recip = 1 / sharpness,
            use_linear_clamp = (byte)(sharpness >= 16 ? 1 : 0),
            minimum_sens = 1 / a.motivity,
            maximum_sens = a.motivity,
        };
    }
    static double EvalActivationLegacy(activation_legacy s, double x, in accel_args a)
    {
        if (s.use_linear_clamp != 0)
        {
            double logSpace = s.gamma_const * (Math.Log(x) - s.log_syncspeed);
            if (logSpace < -1) return s.minimum_sens;
            if (logSpace > 1) return s.maximum_sens;
            return Math.Exp(logSpace * s.log_motivity);
        }
        if (x == s.syncspeed) return 1.0;
        double logDiff = Math.Log(x) - s.log_syncspeed;
        if (logDiff > 0)
        {
            double logSpace = s.gamma_const * logDiff;
            double exponent = Math.Pow(Math.Tanh(Math.Pow(logSpace, s.sharpness)), s.sharpness_recip);
            return Math.Exp(exponent * s.log_motivity);
        }
        else
        {
            double logSpace = -s.gamma_const * logDiff;
            double exponent = -Math.Pow(Math.Tanh(Math.Pow(logSpace, s.sharpness)), s.sharpness_recip);
            return Math.Exp(exponent * s.log_motivity);
        }
    }

    /// <summary>
    /// synchronous + gain builds a LUT and writes it into args.data (mutable float[514]),
    /// exactly like the C++ ctor. Returns the descriptor that goes into the union.
    /// </summary>
    public static unsafe activation_gain BuildActivationGain(ref accel_args a)
    {
        var s = new activation_gain { velocity = 1, range = new fp_rep_range { start = -3, stop = 9, num = 8 } };
        s.x_start = M.Scalbn(1, s.range.start);
        var range = new FpRange(-3, 9, 8);

        var sig = BuildActivationLegacy(a);
        double sum = 0, left = 0;

        static IEnumerable<double> RangePoints(FpRange r)
        {
            for (int e = 0; e < r.stop - r.start; e++)
            {
                double expScale = M.Scalbn(1, e + r.start) / r.num;
                for (int i = 0; i < r.num; i++) yield return (i + r.num) * expScale;
            }
            yield return M.Scalbn(1, r.stop);
        }

        int idx = 0;
        foreach (double x in RangePoints(range))
        {
            const int partitions = 2;
            double interval = (x - left) / partitions;
            for (int i = 1; i <= partitions; i++)
                sum += EvalActivationLegacy(sig, left + i * interval, a) * interval;
            left = x;

            double y = sum;
            if (s.velocity == 0) y /= x;
            a.data[idx++] = (float)y;
        }
        return s;
    }

    public static unsafe double EvalActivationGain(activation_gain s, ref accel_args a, double x)
    {
        int e = Math.Min(M.Ilogb(x), s.range.stop - 1);
        if (e >= s.range.start)
        {
            int idxIntLogPart = e - s.range.start;
            double idxFracLinPart = M.Scalbn(x, -e) - 1;
            double idxF = s.range.num * (idxIntLogPart + idxFracLinPart);
            int idx = (int)Math.Min(idxF, new FpRange(s.range.start, s.range.stop, s.range.num).Size() - 2);
            if (idx < accel_args.LUT_RAW_DATA_CAPACITY - 1)
            {
                double y = M.Lerp(a.data[idx], a.data[idx + 1], idxF - idx);
                if (s.velocity != 0) y /= x;
                return y;
            }
        }
        double y0 = a.data[0];
        if (s.velocity != 0) y0 /= s.x_start;
        return y0;
    }

    // ---------- lookup ----------
    public static unsafe double EvalLookup(lookup_t s, ref accel_args a, double x)
    {
        if (x <= 0) return 0;
        int capacity = accel_args.LUT_RAW_DATA_CAPACITY / 2;
        int lo = 0, hi = s.size - 2;
        if (hi < capacity - 1)
        {
            fixed (float* p = a.data)
            {
                vec2f* points = (vec2f*)p;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var pt = points[mid];
                    if (x < pt.x) hi = mid - 1;
                    else if (x > pt.x) lo = mid + 1;
                    else { double y = pt.y; if (s.velocity != 0) y /= x; return y; }
                }
                if (lo > 0)
                {
                    var pa = points[lo - 1];
                    var pb = points[lo];
                    double t = (x - pa.x) / (pb.x - pa.x);
                    double yy = M.Lerp(pa.y, pb.y, t);
                    if (s.velocity != 0) yy /= x;
                    return yy;
                }
            }
        }
        {
            fixed (float* p = a.data)
            {
                vec2f* points = (vec2f*)p;
                double y = points[0].y;
                if (s.velocity != 0) y /= points[0].x;
                return y;
            }
        }
    }

    // ---------- dispatch ----------

    /// <summary>Builds the 72-byte accel_union the driver stores in data_t.</summary>
    public static unsafe accel_union BuildUnion(ref accel_args a)
    {
        var u = new accel_union();
        switch (a.mode)
        {
            case accel_mode.classic:
                if (a.gain != 0) u.classic_g = BuildClassicGain(a); else u.classic_l = BuildClassicLegacy(a);
                break;
            case accel_mode.jump:
            {
                var b = BuildJumpBase(a);
                if (a.gain != 0)
                {
                    var j = new jump_gain { b = b };
                    j.C = -JumpSmoothAntideriv(b.step, b.smooth_rate, 0);
                    u.jump_g = j;
                }
                else u.jump_l = new jump_legacy { b = b };
                break;
            }
            case accel_mode.natural:
            {
                var b = BuildNatural(a);
                if (a.gain != 0) u.natural_g = new natural_gain { b = b, constant = -b.limit / b.accel };
                else u.natural_l = new natural_legacy { b = b };
                break;
            }
            case accel_mode.synchronous:
                if (a.gain != 0) u.loglog_sigmoid_g = BuildActivationGain(ref a);
                else u.loglog_sigmoid_l = BuildActivationLegacy(a);
                break;
            case accel_mode.power:
                if (a.gain != 0) u.power_g = BuildPowerGain(a);
                else u.power_l = BuildPowerLegacy(a);
                break;
            case accel_mode.lookup:
                u.lut = new lookup_t { size = a.length / 2, velocity = a.gain };
                break;
            default:
                break; // noaccel
        }
        return u;
    }

    /// <summary>accel_fn(x) - the raw multiplier before the callback_template blends in range_weight.</summary>
    public static unsafe double Eval(accel_union u, ref accel_args a, double x) => a.mode switch
    {
        accel_mode.classic => a.gain != 0 ? EvalClassicGain(u.classic_g, x, a) : EvalClassicLegacy(u.classic_l, x, a),
        accel_mode.jump => a.gain != 0 ? EvalJumpGain(u.jump_g, x, a) : EvalJumpLegacy(u.jump_l, x, a),
        accel_mode.natural => a.gain != 0 ? EvalNaturalGain(u.natural_g, x, a) : EvalNaturalLegacy(u.natural_l, x, a),
        accel_mode.synchronous => a.gain != 0 ? EvalActivationGain(u.loglog_sigmoid_g, ref a, x) : EvalActivationLegacy(u.loglog_sigmoid_l, x, a),
        accel_mode.power => a.gain != 0 ? EvalPowerGain(u.power_g, x, a) : EvalPowerLegacy(u.power_l, x, a),
        accel_mode.lookup => EvalLookup(u.lut, ref a, x),
        _ => 1,
    };

    /// <summary>modifier::callback_template: 1 + (accel_fn(x) - 1) * range_weight</summary>
    public static unsafe double Scale(accel_union u, ref accel_args a, double x, double rangeWeight)
        => 1 + (Eval(u, ref a, x) - 1) * rangeWeight;
}

[StructLayout(LayoutKind.Sequential)]
public struct vec2f
{
    public float x;
    public float y;
}
