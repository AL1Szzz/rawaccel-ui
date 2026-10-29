using RaCore.Model;

namespace RaCore;

/// <summary>
/// The set of points the curve page plots. It used to live inside RaUI's AppState, which put the
/// only consumer of the driver's own curve math somewhere the self tests could not reach. It is
/// next to CurveAnalysis now: both read a profile exactly the way the driver runs it, so the
/// chart, the markers and the readout cannot drift apart.
/// </summary>
public static class CurveSampler
{
    public const int SampleCount = 240;

    /// <summary>
    /// How far the x axis reaches. A chart DPI means the axis is in/s (40 covers a fast flick);
    /// without one it is raw counts/ms, where 30 is already past any hand speed worth plotting.
    /// </summary>
    public static double MaxAxis(double chartDpi) => chartDpi > 0 ? 40 : 30;

    /// <summary>
    /// Display axis -&gt; the speed the driver actually compares against. With DPI normalization
    /// on, the driver has already divided by the device DPI, so the internal speed is the
    /// 1000-dpi-equivalent in/s and needs no conversion.
    /// </summary>
    public static double AxisToInternal(double axis, double chartDpi, bool normalized)
        => chartDpi <= 0 ? axis : (normalized ? axis : axis * chartDpi / 1000.0);

    public static double InternalToAxis(double v, double chartDpi, bool normalized)
        => chartDpi <= 0 ? v : (normalized ? v : v * 1000.0 / chartDpi);

    /// <summary>One point on the chart: sensitivity is output/input, gain is d(output)/d(input).</summary>
    public readonly record struct CurveSample(double Axis, double Sensitivity, double Gain);

    /// <summary>
    /// Samples the horizontal curve only - that is the one the chart plots. Each point costs three
    /// trips through the modifier (the value plus two for the central difference), so
    /// <paramref name="withGain"/> is skipped when the page is showing sensitivity.
    /// </summary>
    public static CurveSample[] Sample(ra_profile prof, double chartDpi, bool normalized, bool withGain = true)
    {
        const double time = 1.0;   // 1 ms: feeding speed*time gives counts/ms directly
        var ms = new ModifierSettings(prof);
        var mod = new Modifier(ms);

        // A fresh processor per evaluation: this describes the static curve, not a smoothed
        // live response, which is what the numbers under the chart claim to be.
        double Out(double v)
        {
            var sp = new SpeedProcessor();
            sp.Init(ms.prof.speed_processor_args);
            var input = new vec2d { x = v * time, y = 0 };
            return M.Magnitude(mod.Modify(input, sp, 1, time)) / time;
        }

        double maxAxis = MaxAxis(chartDpi);
        var points = new CurveSample[SampleCount];

        for (int i = 0; i < SampleCount; i++)
        {
            double axis = maxAxis * (i + 1) / SampleCount;
            double v = AxisToInternal(axis, chartDpi, normalized);
            double o = Out(v);

            double gain = 0;
            if (withGain)
            {
                double h = v * 1e-3 + 1e-6;
                gain = (Out(v + h) - Out(Math.Max(v - h, 0))) / (2 * h);
            }

            points[i] = new CurveSample(axis, o / Math.Max(v, 1e-9), gain);
        }

        return points;
    }
}
