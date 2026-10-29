using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RaCore;

namespace RaUI.View;

/// <summary>
/// The 360° turn test, full screen. The dial is turned by raw mouse counts, and a lap is "the red
/// mark came back to the top" - which the player judges by eye, not by a number.
///
/// Read <see cref="TurnMeasure"/> before trusting what this window says. It measures the hand
/// (counts, and the centimetres those counts are worth at the stated DPI); it does not measure the
/// game, because the angle it draws is the angle the sensitivity implies. What it is genuinely good
/// for: showing how far a full turn actually is, and turning "I want 30 cm for a 360" into the
/// sensitivity to type into the game.
/// </summary>
public partial class CalibrationWindow : Window
{
    public sealed class Config
    {
        public double Sens = 1.5;
        public double Dpi = 600;
        public double TargetCm360 = 30;
        /// <summary>360 for a full lap, 180 for the half turn the calibration session starts with.</summary>
        public double LapDeg = 360;
        /// <summary>Test hook: draw the dial at a fixed angle, no raw input, no timer.</summary>
        public bool DebugStatic;
    }

    readonly Config cfg;
    readonly TurnMeasure turn = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    RawMouse? raw;
    long lastDx;
    bool lapAnnounced;
    bool ready;

    /// <summary>Set when the player asked for a sensitivity; the caller fills it in and re-renders.</summary>
    public double? AppliedSens { get; private set; }

    /// <summary>Live raw-input totals, for the --calib-live check: proves the dial is being fed.</summary>
    public long RawCounts => turn.Counts;
    public long RawPackets => turn.Packets;
    public string RawError => raw?.Error ?? "";

    public CalibrationWindow(Config cfg)
    {
        this.cfg = cfg;
        turn.Sensitivity = cfg.Sens;
        turn.Dpi = cfg.Dpi;
        turn.LapDeg = cfg.LapDeg;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        timer.Tick += (_, _) => OnTick();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (cfg.DebugStatic)
        {
            Width = 1920;
            Height = 1080;
        }
        else
        {
            // full screen, primary monitor, in DIPs so a scaled display still fills correctly
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        BuildDial();

        TxtSens.Text = cfg.Sens.ToString("0.###", CultureInfo.InvariantCulture);
        TxtDpi.Text = cfg.Dpi.ToString("0", CultureInfo.InvariantCulture);
        TxtTarget.Text = cfg.TargetCm360.ToString("0.#", CultureInfo.InvariantCulture);

        if (cfg.DebugStatic)
        {
            turn.Add(4200);
            ready = true;
            Refresh();
            return;
        }

        raw = RawMouse.Attach(this);
        if (raw.Error != null)
        {
            SubText.Text = "收不到鼠标的原始输入：" + raw.Error;
            SubText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x95, 0x95));
        }
        else
        {
            SubText.Text = cfg.LapDeg < 360 ? "推鼠标，让红色的起始标记转到正下方。这半圈推了多远，乘二就是你的 cm/360。"
                                        : "推鼠标，让红色的起始标记转回正上方。这一圈推了多远，就是你的 cm/360。";
            lastDx = raw.Dx;
            timer.Start();
        }

        TxtSens.TextChanged += (_, _) => SyncInputs();
        TxtDpi.TextChanged += (_, _) => SyncInputs();
        ready = true;
        Refresh();
    }

    /// <summary>Dial marks: a tick every 10°, a long one every 90°.</summary>
    void BuildDial()
    {
        const double cx = 200, cy = 200, outer = 194;
        var tickBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        var majorBrush = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        // the start mark is red so it reads as the same reference as the arrow pinned at the top:
        // one lap is "the red tick is back under the red arrow"
        var startBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5B, 0x5B));

        for (int i = 0; i < 36; i++)
        {
            double deg = i * 10;
            double rad = (deg - 90) * Math.PI / 180;
            bool major = i % 9 == 0;
            bool start = i == 0;
            double inner = outer - (major ? 26 : 12);

            Dial.Children.Add(new Line
            {
                X1 = cx + Math.Cos(rad) * outer, Y1 = cy + Math.Sin(rad) * outer,
                X2 = cx + Math.Cos(rad) * inner, Y2 = cy + Math.Sin(rad) * inner,
                Stroke = start ? startBrush : major ? majorBrush : tickBrush,
                StrokeThickness = start ? 3 : major ? 2 : 1,
                SnapsToDevicePixels = false,
            });

            if (!major) continue;
            var label = new TextBlock
            {
                Text = start ? "起点" : $"{deg:0}°",
                Foreground = start ? startBrush : labelBrush,
                FontSize = 11,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double r = outer - 42;
            Canvas.SetLeft(label, cx + Math.Cos(rad) * r - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, cy + Math.Sin(rad) * r - label.DesiredSize.Height / 2);
            Dial.Children.Add(label);
        }
    }

    void OnTick()
    {
        if (raw == null) return;
        long d = raw.Dx - lastDx;
        if (d != 0) { turn.Add(d); lastDx = raw.Dx; }
        Refresh();
    }

    void Refresh()
    {
        if (!ready) return;

        // the dial turns the way a camera would: push the mouse right, the marks move left
        DialRotate.Angle = -turn.AngleDeg;
        AngleText.Text = $"{turn.AngleAbsDeg:0}°";
        CmText.Text = $"{Math.Abs(turn.Cm):0.0} cm";
        CountsText.Text = turn.Counts.ToString(CultureInfo.InvariantCulture);
        ImpliedText.Text = turn.CmPer360Implied > 0 ? $"{turn.CmPer360Implied:0.0} cm/360" : "—";

        AngleText.Foreground = turn.Full
            ? new SolidColorBrush(Color.FromRgb(0x9E, 0xE4, 0x93))
            : Brushes.White;

        if (turn.Full && !lapAnnounced)
        {
            lapAnnounced = true;
            LapText.Text = $"{cfg.LapDeg:0}° = {Math.Abs(turn.Cm):0.0} cm";
            ResultText.Text = $"转满 {cfg.LapDeg:0}°：{Math.Abs(turn.Cm):0.0} cm，换算成整圈是 {turn.CmPer360Measured:0.0} cm/360";
        }
        else if (!turn.Full)
        {
            LapText.Text = turn.Packets == 0 ? "还在等鼠标动" : $"还差 {Math.Max(0, cfg.LapDeg - turn.AngleAbsDeg):0}°";
        }
    }

    void SyncInputs()
    {
        turn.Sensitivity = Parse(TxtSens.Text, turn.Sensitivity, 0.01, 20);
        turn.Dpi = Parse(TxtDpi.Text, turn.Dpi, 100, 32000);
        Refresh();
    }

    void OnApply(object sender, RoutedEventArgs e) => Apply();

    void Apply()
    {
        double target = Parse(TxtTarget.Text, 30, 2, 400);
        double dpi = turn.Dpi;
        double sens = TurnMeasure.SensFor(target, dpi);
        if (sens <= 0) { ResultText.Text = "算不出来：先把 DPI 和目标的 cm/360 填对"; return; }

        AppliedSens = sens;
        TxtSens.Text = sens.ToString("0.####", CultureInfo.InvariantCulture);
        ResultText.Text = $"想要 {target:0.#} cm 转一圈 → 游戏灵敏度填 {sens:0.####}（退出后可以填进测试设置）";
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key == Key.R && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            turn.Reset();
            lastDx = raw?.Dx ?? 0;
            lapAnnounced = false;
            ResultText.Text = "";
            LapText.Text = "重新开始";
            Refresh();
        }
    }

    void OnClosed(object sender, EventArgs e)
    {
        timer.Stop();
        raw?.Dispose();
        raw = null;
    }

    static double Parse(string s, double fallback, double min, double max)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, min, max)
            : fallback;
}
