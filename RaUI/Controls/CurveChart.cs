using System.Windows;
using System.Windows.Media;

namespace RaUI.Controls;

/// <summary>Lightweight vector chart: no external plotting library, drawn in OnRender.</summary>
public sealed class CurveChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<CurveSeries>), typeof(CurveChart),
        new PropertyMetadata(null, (o, e) => ((CurveChart)o).InvalidateVisual()));

    public IEnumerable<CurveSeries> Series
    {
        get => (IEnumerable<CurveSeries>)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty XLabelProperty = DependencyProperty.Register(
        nameof(XLabel), typeof(string), typeof(CurveChart), new PropertyMetadata("", (o, e) => ((CurveChart)o).InvalidateVisual()));
    public string XLabel { get => (string)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }

    public static readonly DependencyProperty YLabelProperty = DependencyProperty.Register(
        nameof(YLabel), typeof(string), typeof(CurveChart), new PropertyMetadata("", (o, e) => ((CurveChart)o).InvalidateVisual()));
    public string YLabel { get => (string)GetValue(YLabelProperty); set => SetValue(YLabelProperty, value); }

    public IEnumerable<CurvePoint> Samples
    {
        get => (IEnumerable<CurvePoint>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable<CurvePoint>), typeof(CurveChart),
        new PropertyMetadata(null, (o, e) => ((CurveChart)o).InvalidateVisual()));

    /// <summary>Measured hand-speed distribution: X = axis position, Y = 0..1 normalized count.</summary>
    public static readonly DependencyProperty HistogramProperty = DependencyProperty.Register(
        nameof(Histogram), typeof(IEnumerable<CurvePoint>), typeof(CurveChart),
        new PropertyMetadata(null, (o, e) => ((CurveChart)o).InvalidateVisual()));
    public IEnumerable<CurvePoint> Histogram
    {
        get => (IEnumerable<CurvePoint>)GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    /// <summary>Vertical reference lines (acceleration starts / caps).</summary>
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IEnumerable<double>), typeof(CurveChart),
        new PropertyMetadata(null, (o, e) => ((CurveChart)o).InvalidateVisual()));
    public IEnumerable<double> Markers
    {
        get => (IEnumerable<double>)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    Brush Grid = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    Brush Text = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    Brush Axis = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    public CurveChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;

        var series = Series?.ToList() ?? new();
        var samples = Samples?.ToList() ?? new();
        bool hasAny = series.Any(s => s.Points.Count > 0) || samples.Count > 0;

        double left = 56, right = 14, top = 14, bottom = 34;
        var plot = new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double maxX = 1, maxY = 1, minY = 0;
        foreach (var s in series)
            foreach (var p in s.Points)
            {
                if (double.IsFinite(p.X)) maxX = Math.Max(maxX, p.X);
                if (double.IsFinite(p.Y)) { maxY = Math.Max(maxY, p.Y); minY = Math.Min(minY, p.Y); }
            }
        foreach (var p in samples)
        {
            if (double.IsFinite(p.X)) maxX = Math.Max(maxX, p.X);
            if (double.IsFinite(p.Y)) { maxY = Math.Max(maxY, p.Y); minY = Math.Min(minY, p.Y); }
        }
        if (!hasAny)
        {
            var none = new FormattedText("没有可显示的曲线",
                new System.Globalization.CultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Text"), 14, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(none, new Point(plot.X + (plot.Width - none.Width) / 2, plot.Y + (plot.Height - none.Height) / 2));
            return;
        }

        maxY = Math.Max(maxY * 1.08, 1.05);
        minY = Math.Min(minY, 0);

        Point Map(double x, double y) => new(
            plot.X + plot.Width * (x / maxX),
            plot.Bottom - plot.Height * ((y - minY) / (maxY - minY)));

        // grid + ticks
        var pen = new Pen(Grid, 1); pen.Freeze();
        var axisPen = new Pen(Axis, 1); axisPen.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface("Segoe UI Variable Text");

        double xStep = NiceStep(maxX, 6);
        for (double x = 0; x <= maxX + 1e-9; x += xStep)
        {
            var p = Map(x, minY);
            dc.DrawLine(pen, new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));
            DrawLabel(dc, Fmt(x), face, 10, Text, p.X, plot.Bottom + 6, dpi, true);
        }
        double yStep = NiceStep(maxY - minY, 5);
        for (double y = Math.Ceiling(minY / yStep) * yStep; y <= maxY + 1e-9; y += yStep)
        {
            var p = Map(0, y);
            dc.DrawLine(pen, new Point(plot.Left, p.Y), new Point(plot.Right, p.Y));
            DrawLabel(dc, Fmt(y), face, 10, Text, plot.Left - 8, p.Y - 6, dpi, false);
        }

        // unit line (sens = 1 / gain = 1) - the "no acceleration" reference
        if (minY <= 1 && maxY >= 1)
        {
            var up = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            up.Freeze();
            var a = Map(0, 1); var b = Map(maxX, 1);
            dc.DrawLine(up, a, b);
        }

        dc.DrawRectangle(null, axisPen, plot);

        // measured hand-speed distribution, drawn as a band along the floor of the plot
        var hist = Histogram?.ToList() ?? new();
        if (hist.Count > 0)
        {
            var hb = new SolidColorBrush(Color.FromArgb(0x4D, 0x4C, 0xC2, 0xFF)); hb.Freeze();
            double band = plot.Height * 0.30;
            double barW = Math.Max(2, plot.Width / (hist.Count * 1.6));
            foreach (var bar2 in hist)
            {
                if (!double.IsFinite(bar2.X) || !double.IsFinite(bar2.Y)) continue;
                double x = plot.X + plot.Width * (bar2.X / maxX);
                double hh = band * Math.Clamp(bar2.Y, 0, 1);
                dc.DrawRectangle(hb, null, new Rect(x - barW / 2, plot.Bottom - hh, barW, hh));
            }
        }

        // acceleration start / cap markers
        var mkPen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xFC, 0xE1, 0x00)), 1)
        { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        mkPen.Freeze();
        foreach (var m in Markers ?? Enumerable.Empty<double>())
        {
            if (!double.IsFinite(m) || m <= 0 || m > maxX) continue;
            double x = plot.X + plot.Width * (m / maxX);
            dc.DrawLine(mkPen, new Point(x, plot.Top + 2), new Point(x, plot.Bottom - 2));
        }

        foreach (var s in series)
        {
            if (s.Points.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                bool first = true;
                foreach (var p in s.Points)
                {
                    if (!double.IsFinite(p.Y)) continue;
                    var pt = Map(p.X, p.Y);
                    if (first) { c.BeginFigure(pt, false, false); first = false; }
                    else c.LineTo(pt, true, false);
                }
            }
            geo.Freeze();
            var pen2 = new Pen(s.Brush, s.Thickness) { LineJoin = PenLineJoin.Round };
            if (s.Dashed) pen2.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);
            pen2.Freeze();
            dc.DrawGeometry(null, pen2, geo);
        }

        if (samples.Count > 0)
        {
            var fb = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); fb.Freeze();
            foreach (var p in samples)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                var pt = Map(p.X, p.Y);
                dc.DrawEllipse(fb, null, pt, 2.4, 2.4);
            }
        }

        // axis titles
        DrawLabel(dc, XLabel ?? "", face, 11, Text, plot.X + plot.Width / 2, plot.Bottom + 18, dpi, true);
        var yl = new FormattedText(YLabel ?? "", new System.Globalization.CultureInfo("zh-CN"),
            FlowDirection.LeftToRight, face, 11, Text, dpi);
        dc.PushTransform(new RotateTransform(-90, 14, plot.Y + plot.Height / 2));
        dc.DrawText(yl, new Point(14 - yl.Width / 2, plot.Y + plot.Height / 2 - yl.Height / 2));
        dc.Pop();
    }

    static void DrawLabel(DrawingContext dc, string s, Typeface f, double size, Brush b, double x, double y, double dpi, bool center)
    {
        if (string.IsNullOrEmpty(s)) return;
        var ft = new FormattedText(s, new System.Globalization.CultureInfo("zh-CN"), FlowDirection.LeftToRight, f, size, b, dpi);
        dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x - ft.Width, y));
    }

    static string Fmt(double v) => Math.Abs(v) >= 100 ? v.ToString("0") : Math.Abs(v) >= 10 ? v.ToString("0.0") : v.ToString("0.00");

    static double NiceStep(double range, int target)
    {
        if (range <= 0) return 1;
        double raw = range / target;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm switch { < 1.5 => 1, < 3 => 2, < 7 => 5, _ => 10 };
        return step * mag;
    }
}
