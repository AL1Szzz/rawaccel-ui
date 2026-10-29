using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI;

public sealed record CurvePoint(double X, double Y);

public sealed class CurveSeries
{
    public string Name { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.White;
    public double Thickness { get; init; } = 2;
    public bool Dashed { get; init; }
    public List<CurvePoint> Points { get; init; } = new();
}

public enum CurveKind { Sensitivity, Gain }

/// <summary>A vertical reference line that says what it is: an unlabeled dashed line is noise.</summary>
public sealed record ChartMarker(double At, string Label, Brush Color);

/// <summary>A stretch of the speed axis where the curve behaves the same way, shaded so the eye
/// reads "no acceleration / accelerating / capped" without a legend.</summary>
public sealed record ChartZone(double From, double To, string Name, Brush Tint);

/// <summary>
/// The chart half of <see cref="AppState"/>: which series is drawn, the markers and shaded bands
/// derived from it, and the plain-language readout underneath. None of this reaches the driver -
/// it is all a view of the selected profile, which is why it can be rebuilt at any time.
/// </summary>
public sealed partial class AppState
{
    CurveKind curveKind = CurveKind.Sensitivity;

    public CurveKind CurveKind
    {
        get => curveKind;
        set { if (curveKind == value) return; curveKind = value; Raise(); RebuildCurves(); }
    }

    // chart display scaling (does not affect the driver at all - mirrors the official
    // "Scale by DPI and Poll Rate" menu, which is display-only too)
    double chartDpi;
    int chartPollRate;
    public double ChartDpi { get => chartDpi; set { if (chartDpi == value) return; chartDpi = value; Raise(); RebuildCurves(); } }
    public int ChartPollRate { get => chartPollRate; set { if (chartPollRate == value) return; chartPollRate = value; Raise(); RebuildCurves(); } }

    /// <summary>
    /// Both chart-scaling boxes are committed together, and each setter used to rebuild the whole
    /// curve on its own. One rebuild per user action, and the prefs file is only rewritten when
    /// the caller says this came from the UI rather than from loading prefs at startup.
    /// </summary>
    public void ApplyChartScale(double dpi, int pollRate, bool savePrefs = true)
    {
        bool changed = chartDpi != dpi || chartPollRate != pollRate;
        chartDpi = dpi;
        chartPollRate = pollRate;
        if (savePrefs)
        {
            Prefs.ChartDpi = dpi;
            Prefs.ChartPollRate = pollRate;
            SavePrefs();
        }
        if (!changed) return;
        Raise(nameof(ChartDpi));
        Raise(nameof(ChartPollRate));
        RebuildCurves();
    }

    public List<CurveSeries> Series { get; private set; } = new();
    public string XAxisLabel { get; private set; } = "输入速度 (counts/ms)";
    public string YAxisLabel { get; private set; } = "灵敏度";

    // ---------- chart output ----------

    public List<ChartMarker> Markers { get; private set; } = new();
    public List<ChartZone> Zones { get; private set; } = new();

    /// <summary>
    /// Safety net only. Every edit path calls Touch(), which now refreshes the curve synchronously,
    /// so the timer's job is just to catch a document changed from outside (ReloadFromDriver, a
    /// preset applied elsewhere). Hashing one profile struct is microseconds; the old 500ms tick
    /// serialized the whole document every time, forever, even with the window closed.
    /// </summary>
    public void StartAutoRefresh()
    {
        if (refreshTimer != null) return;
        refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refreshTimer.Tick += (_, _) => { if (!AutoRefreshPaused) RefreshCurveIfChanged(); };
        refreshTimer.Start();
    }

    System.Windows.Threading.DispatcherTimer? refreshTimer;

    /// <summary>Set while the window is hidden or minimized: nobody is looking at the chart.</summary>
    public bool AutoRefreshPaused { get; set; }

    string curveKey = "";

    /// <summary>What the drawn curves were built from: the selected profile plus the display axes.
    /// Profile-level, not document-level, so editing a device entry cannot trigger a redraw.</summary>
    string CurveKey() => selected == null ? "<none>" : $"{ConfigHash.Of(selected)}|{curveKind}|{chartDpi}|{chartPollRate}";

    public void RefreshCurveIfChanged()
    {
        if (CurveKey() != curveKey) RebuildCurves();
    }

    // ---------- readout ----------

    /// <summary>
    /// The curve expressed in units you can act on: cm/s of hand motion, degrees/s of
    /// crosshair travel, and the effective in-game sensitivity at each stage.
    /// </summary>
    public string PhysicsReadout(ProfileDto p)
    {
        double dpi = Prefs.MouseDpi, sens = Prefs.GameSensitivity, poll = chartPollRate;
        if (dpi <= 0 || sens <= 0) return "填上鼠标 DPI 和游戏灵敏度后，这里会给出 cm/s 与 °/s 的对照。";

        // What unit the curve's own x axis is in depends on DPI normalization: with a device DPI
        // filled in, the driver pre-divides by it, so the internal speed is already in/s against a
        // 1000-dpi reference. Dividing that by the real DPI again reported the wrong cm/s for every
        // DPI except 1000. Convert to counts/ms first, then to physical units once.
        bool normalized = doc.DefaultDeviceConfig.Dpi > 0;
        double CountsPerMs(double internalV) => normalized ? internalV * dpi / 1000.0 : internalV;
        double CmsToCms(double countsPerMs) => countsPerMs * 2.54 * 1000 / dpi;         // cm/s
        double CmsToDeg(double countsPerMs) => countsPerMs * SourceAim.DegreesPerCount(sens) * 1000;

        var ax = Analyze(p, true);
        var ay = Analyze(p, false);
        double perPacket = poll > 0 ? 1000.0 / poll : 1.0;
        double axCpm = CountsPerMs(ax.threshold), ayCpm = CountsPerMs(ay.threshold);

        var sb = new System.Text.StringBuilder();
        sb.Append($"eDPI {dpi * sens:0} · {SourceAim.CmPer360(sens, dpi):0.0} cm/360（{SourceAim.CmPer360(sens, dpi) / 2:0.0} cm/180°）");
        sb.Append($"   |   X 阈值 {ax.threshold:0.00} {(normalized ? "in/s" : "c/ms")} = {axCpm:0.00} c/ms = {CmsToCms(axCpm):0} cm/s = {CmsToDeg(axCpm):0} °/s" +
                  $"（{poll:0}Hz 下每包 {axCpm * perPacket:0.0} counts = {axCpm * perPacket / dpi * 25.4:0.00} mm）");
        if (!double.IsInfinity(ax.capSpeed))
        {
            double capCpm = CountsPerMs(ax.capSpeed);
            sb.Append($"，封顶于 {CmsToCms(capCpm):0} cm/s，峰值有效灵敏度 {sens * ax.capValue:0.00}");
        }
        sb.Append($"\nY 阈值 {ay.threshold:0.00} {(normalized ? "in/s" : "c/ms")} = {ayCpm:0.00} c/ms = {CmsToCms(ayCpm):0} cm/s = {CmsToDeg(ayCpm):0} °/s");
        if (!double.IsInfinity(ay.capSpeed))
            sb.Append($"，封顶于 {CmsToCms(CountsPerMs(ay.capSpeed)):0} cm/s，峰值有效灵敏度 {sens * ay.capValue:0.00}");
        if (normalized) sb.Append($"\n（DPI 归一化开着：曲线横轴本身已是 in/s，上面按设备 DPI {doc.DefaultDeviceConfig.Dpi} 换算回 counts）");
        return sb.ToString();
    }

    /// <summary>Where acceleration starts / stops rising. Delegates to RaCore so the
    /// chart, the readout and the trainer can never disagree.</summary>
    public (double threshold, double capSpeed, double capValue, double maxValue) Analyze(ProfileDto p, bool horizontal)
    {
        var a = CurveAnalysis.Analyze(Convert4.ToStruct(p), horizontal);
        return (a.Threshold, a.CapSpeed, a.PeakGain, a.PeakGain);
    }

    // ---------- drawing ----------

    public void RebuildCurves()
    {
        var p = selected;
        if (p == null) { Series = new(); Markers = new(); Zones = new(); Raise(nameof(Series)); return; }

        bool normalized = Doc.DefaultDeviceConfig.Dpi > 0;
        bool wantGain = CurveKind == CurveKind.Gain;

        // Sampling lives in RaCore next to CurveAnalysis: it is the driver's own math, and it was
        // the only consumer of it that the self tests could not reach. Only the series being shown
        // is computed - the gain column costs two extra trips through the modifier per point.
        var samples = CurveSampler.Sample(Convert4.ToStruct(p), ChartDpi, normalized, withGain: wantGain);
        double maxAxis = CurveSampler.MaxAxis(ChartDpi);

        string unit = ChartDpi > 0 ? $"in/s @ {ChartDpi:0} DPI" : "counts/ms";
        XAxisLabel = $"手速 ({unit})";
        YAxisLabel = wantGain ? "增益 (d输出/d输入)" : "灵敏度 (输出/输入)";

        var points = new List<CurvePoint>(samples.Length);
        foreach (var s in samples) points.Add(new CurvePoint(s.Axis, wantGain ? s.Gain : s.Sensitivity));

        Brush accent = Brush("AccentBrush");
        Series = new List<CurveSeries>
        {
            new() { Name = wantGain ? "增益" : "灵敏度", Brush = accent, Points = points },
        };

        // Labelled markers. An unlabelled dashed line is just noise on the chart, which is what
        // the four yellow lines used to be. X and Y get different colours so 起/顶 can be traced
        // back to the axis they belong to.
        var axX = Analyze(p, true);
        var axY = Analyze(p, false);
        var xCol = Brush("AccentBrush");
        var yCol = YMarkerBrush;
        Markers = new List<ChartMarker>();
        void AddMark(double internalV, string label, Brush col)
        {
            if (internalV <= 0 || double.IsInfinity(internalV)) return;
            double axis = CurveSampler.InternalToAxis(internalV, ChartDpi, normalized);
            if (axis is > 0 and < 400) Markers.Add(new ChartMarker(axis, label, col));
        }
        AddMark(axX.threshold, "X起", xCol);
        AddMark(axX.capSpeed, "X顶", xCol);
        AddMark(axY.threshold, "Y起", yCol);
        AddMark(axY.capSpeed, "Y顶", yCol);

        // The plotted curve is the X curve, so the bands are X bands: below the X threshold the
        // multiplier is exactly 1.0 however hard he moves, above the X cap it stops rising.
        double zLo = axX.threshold > 0 && !double.IsInfinity(axX.threshold) ? CurveSampler.InternalToAxis(axX.threshold, ChartDpi, normalized) : double.NaN;
        double zHi = axX.capSpeed > 0 && !double.IsInfinity(axX.capSpeed) ? CurveSampler.InternalToAxis(axX.capSpeed, ChartDpi, normalized) : double.NaN;

        Zones = new List<ChartZone>();
        if (!double.IsNaN(zLo))
        {
            double top = double.IsNaN(zHi) ? maxAxis : zHi;
            Zones.Add(new ChartZone(0, zLo, "不加速", ZoneTint(0x1E)));
            Zones.Add(new ChartZone(zLo, top, "加速中", ZoneTint(0x30)));
            if (!double.IsNaN(zHi)) Zones.Add(new ChartZone(zHi, maxAxis, "已封顶", ZoneTint(0x42)));
        }

        curveKey = CurveKey();

        Raise(nameof(Series));
        Raise(nameof(Zones));
        Raise(nameof(Markers));
        Raise(nameof(XAxisLabel));
        Raise(nameof(YAxisLabel));
    }


    static SolidColorBrush Brush(string key) =>
        (System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush) ?? Brushes.DeepSkyBlue;

    /// <summary>Y gets its own colour because the chart plots the X curve; a Y line in the same
    /// accent blue would read as "this is the curve's own threshold".</summary>
    static readonly SolidColorBrush YMarkerBrush = Frozen(Color.FromRgb(0xFF, 0xC2, 0x4D));

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Zone shading: the same hue at three depths, so the eye reads "more accelerated"
    /// left to right without needing a legend.</summary>
    static SolidColorBrush ZoneTint(byte alpha) => Frozen(Color.FromArgb(alpha, 0x4C, 0xC2, 0xFF));
}
