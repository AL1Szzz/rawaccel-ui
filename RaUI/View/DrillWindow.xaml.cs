using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RaCore;

namespace RaUI.View;

/// <summary>
/// The fullscreen drill room: gridshot, sixshot and tracking. One window, one clock, one crosshair;
/// what changes between drills is where the targets are and how a point is scored, and all of that
/// lives in <see cref="DrillField"/> / <see cref="DrillRun"/> so it can be asserted without a window.
///
/// Like the ball test this only moves the pointer's story, not the driver's: it never writes to
/// Raw Accel, never injects input. The only system-wide thing it does is the display mode, and that
/// is restored on every exit path - including the one where the process is killed mid-round.
/// </summary>
public partial class DrillWindow : Window
{
    public sealed class Config
    {
        public DrillKind Kind = DrillKind.Gridshot;
        public double Seconds = 60;
        public double RadiusPx = 22;
        /// <summary>Gridshot: how many targets are alive at once.</summary>
        public int Alive = 3;
        public int Cols = 6, Rows = 3;
        public int Seed = 20261020;
        /// <summary>Tracking: how fast the target crosses the field, in physical px per second.</summary>
        public double TrackSpeedPx = 760;
        public Display.Mode? Resolution;

        public double CrossArm = 9, CrossGap = 3;
        public string CrossColor = "#FFFFFFFF";
        public bool CrossDot = true;

        /// <summary>Test hook: pretend the pointer is here so the layout can be rendered offscreen.</summary>
        public (double x, double y)? DebugCursorPx;
        /// <summary>Test hook: place the targets and stop - no countdown, no timers.</summary>
        public bool DebugStatic;
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }

    static readonly Dictionary<DrillKind, string> TargetFill = new()
    {
        [DrillKind.Gridshot] = "#FF4CC2FF",
        [DrillKind.Sixshot] = "#FFE8B33A",
        [DrillKind.Tracking] = "#FFFF5B5B",
    };

    readonly Config cfg;
    readonly DrillRun run;
    readonly List<DrillTarget> targets = new();
    readonly Random rng;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(8) };
    readonly DispatcherTimer hud = new() { Interval = TimeSpan.FromMilliseconds(120) };

    double scale = 1.0;
    double screenW = 1920, screenH = 1080;
    DrillField.Area area;
    int nextId = 1;
    double lastTickMs, sessionStartedMs;
    bool running, changingMode, countdownCancelled, sessionReported, buttonHeld;
    double tvx, tvy;
    Ellipse? tracker;
    string endReason = "";

    public string UsedResolution { get; private set; } = "";
    public string DisplayWarning { get; private set; } = "";
    public string EndReason => endReason;
    public DrillRun Run => run;

    public event Action<DrillRun>? SessionDone;

    public DrillWindow(Config cfg)
    {
        this.cfg = cfg;
        run = new DrillRun(cfg.Kind, cfg.Seconds);
        rng = new Random(cfg.Seed);
        if (cfg.DebugCursorPx is not null) { screenW = 1920; screenH = 1080; }
        InitializeComponent();
        SourceInitialized += OnSourceInit;
        Loaded += OnLoaded;
        tick.Tick += (_, _) => OnTick();
        hud.Tick += (_, _) => { if (running) BannerText.Text = run.Hud(); };
    }

    // ---------- window setup ----------

    void OnSourceInit(object sender, EventArgs e)
    {
        var src = PresentationSource.FromVisual(this);
        scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        if (cfg.DebugCursorPx is not null)
        {
            Width = 1920 / scale; Height = 1080 / scale;
            screenW = 1920; screenH = 1080;
            UsedResolution = "debug 1920x1080 (offscreen, no mode change)";
        }
        else
        {
            if (cfg.Resolution is { } m)
            {
                changingMode = true;
                if (!Display.TrySet(m))
                {
                    changingMode = false;
                    DisplayWarning = $"没能切到 {m.Width}×{m.Height}@{m.Refresh}Hz，本轮用当前分辨率跑";
                }
            }

            // a mode change moves the DIP size of the desktop, so the scale is re-read after it
            src = PresentationSource.FromVisual(this);
            scale = src?.CompositionTarget?.TransformToDevice.M11 ?? scale;

            var (gx, gy, gw, gh) = Display.PrimaryGeometry();
            if (gw <= 0 || gh <= 0)
            {
                gw = (int)(SystemParameters.PrimaryScreenWidth * scale);
                gh = (int)(SystemParameters.PrimaryScreenHeight * scale);
            }
            Left = gx / scale; Top = gy / scale;
            Width = gw / scale; Height = gh / scale;
            screenW = gw; screenH = gh;

            var now = Display.Current();
            UsedResolution = $"{(now.Width > 0 ? now.Width : gw)}x{(now.Height > 0 ? now.Height : gh)}@{now.Refresh}";
        }

        area = DrillField.Usable(screenW, screenH);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PaintCross();
        UpdateCross();
        HintText.Text = Hint();

        if (cfg.DebugCursorPx is not null)
        {
            Seed();
            run.Advance(23_000);
            run.Click(true); run.Click(true); run.Click(false);
            if (run.Kind == DrillKind.Tracking) { run.Tick(4200, true, true); run.Tick(1500, true, false); }
            if (run.Kind == DrillKind.Sixshot) run.Click(true, completesSet: true);
            BannerText.Text = run.Hud();
            return;
        }

        if (cfg.DebugStatic) { Seed(); BannerText.Text = run.Hud(); return; }
        _ = StartCountdown();
    }

    string Hint() => cfg.Kind switch
    {
        DrillKind.Gridshot => $"点掉一个，新的就出现在别的格子里 · 同屏 {cfg.Alive} 个 · 左键 · Esc 退出",
        DrillKind.Sixshot => "六个全点掉才算一组，下一组回到同样的位置 · 左键 · Esc 退出",
        _ => "按住左键，把准星一直压在目标上 · 松手不计时不扣分 · Esc 退出",
    };

    (double x, double y) CursorPx()
    {
        if (cfg.DebugCursorPx is { } dbg) return dbg;
        GetCursorPos(out var p);
        var origin = PointToScreen(new Point(0, 0));
        return (p.X - origin.X, p.Y - origin.Y);
    }

    // ---------- crosshair ----------

    void PaintCross()
    {
        var b = Brush(cfg.CrossColor);
        CArmL.Fill = b; CArmR.Fill = b; CArmT.Fill = b; CArmB.Fill = b; CDot.Fill = b;
        CDot.Visibility = cfg.CrossDot ? Visibility.Visible : Visibility.Hidden;
    }

    static SolidColorBrush Brush(string hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze(); return b;
        }
        catch { return Brushes.White; }
    }

    void UpdateCross()
    {
        var (px, py) = CursorPx();
        var p = CrosshairGeometry.Place(px, py, cfg.CrossArm, cfg.CrossGap, scale);

        CArmL.Width = p.Left.W; CArmL.Height = p.Left.H;
        CArmR.Width = p.Right.W; CArmR.Height = p.Right.H;
        CArmT.Width = p.Top.W; CArmT.Height = p.Top.H;
        CArmB.Width = p.Bottom.W; CArmB.Height = p.Bottom.H;
        CDot.Width = p.Dot.W; CDot.Height = p.Dot.H;

        bool arms = p.Left.W > 0;
        CArmL.Visibility = CArmR.Visibility = arms ? Visibility.Visible : Visibility.Hidden;
        CArmT.Visibility = CArmB.Visibility = arms ? Visibility.Visible : Visibility.Hidden;

        Canvas.SetLeft(CArmL, p.Left.X); Canvas.SetTop(CArmL, p.Left.Y);
        Canvas.SetLeft(CArmR, p.Right.X); Canvas.SetTop(CArmR, p.Right.Y);
        Canvas.SetLeft(CArmT, p.Top.X); Canvas.SetTop(CArmT, p.Top.Y);
        Canvas.SetLeft(CArmB, p.Bottom.X); Canvas.SetTop(CArmB, p.Bottom.Y);
        Canvas.SetLeft(CDot, p.Dot.X); Canvas.SetTop(CDot, p.Dot.Y);
    }

    void OnPointerMove(object sender, MouseEventArgs e) => UpdateCross();

    // ---------- targets ----------

    /// <summary>
    /// The play area, computed on first use rather than at SourceInitialized: a window that is only
    /// ever rendered offscreen can raise Loaded first, and an all-zero area would silently stack every
    /// target on the origin instead of failing loudly.
    /// </summary>
    DrillField.Area Field()
    {
        if (area.W > 0 && area.H > 0) return area;
        double w = screenW > 0 ? screenW : SystemParameters.PrimaryScreenWidth * scale;
        double h = screenH > 0 ? screenH : SystemParameters.PrimaryScreenHeight * scale;
        area = DrillField.Usable(w, h);
        return area;
    }

    void Seed()
    {
        var area = Field();
        targets.Clear();
        nextId = 1;
        switch (cfg.Kind)
        {
            case DrillKind.Gridshot:
                for (int i = 0; i < Math.Max(1, cfg.Alive); i++) SpawnAtFreeCell();
                break;
            case DrillKind.Sixshot:
                SpawnSix();
                break;
            default:
                double cx = area.X + area.W / 2, cy = area.Y + area.H / 2;
                targets.Add(new DrillTarget(nextId++, cx, cy, cfg.RadiusPx));
                double a = rng.NextDouble() * Math.PI * 2;
                tvx = Math.Cos(a) * cfg.TrackSpeedPx;
                tvy = Math.Sin(a) * cfg.TrackSpeedPx;
                break;
        }
        DrawAll();
    }

    void SpawnAtFreeCell()
    {
        var cells = DrillField.Grid(area, cfg.Cols, cfg.Rows);
        var taken = new HashSet<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            int nearest = 0; double best = double.MaxValue;
            for (int c = 0; c < cells.Length; c++)
            {
                double dx = cells[c].x - targets[i].X, dy = cells[c].y - targets[i].Y;
                double d = dx * dx + dy * dy;
                if (d < best) { best = d; nearest = c; }
            }
            taken.Add(nearest);
        }
        var pick = DrillField.FreeCells(cells.Length, taken, 1, rng);
        if (pick.Count == 0) return;
        var (x, y) = cells[pick[0]];
        targets.Add(new DrillTarget(nextId++, x, y, cfg.RadiusPx));
    }

    void SpawnSix()
    {
        foreach (var (x, y) in DrillField.Six(area))
            targets.Add(new DrillTarget(nextId++, x, y, cfg.RadiusPx));
    }

    void DrawAll()
    {
        Surface.Children.Clear();
        tracker = null;
        foreach (var t in targets)
        {
            var e = new Ellipse
            {
                Width = t.RadiusPx * 2 / scale,
                Height = t.RadiusPx * 2 / scale,
                Fill = Brush(TargetFill[cfg.Kind]),
                Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.5 / scale,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(e, (t.X - t.RadiusPx) / scale);
            Canvas.SetTop(e, (t.Y - t.RadiusPx) / scale);
            Surface.Children.Add(e);
            if (cfg.Kind == DrillKind.Tracking) tracker = e;
        }
    }

    void MoveTracker()
    {
        if (tracker == null || targets.Count == 0) return;
        var t = targets[0];
        Canvas.SetLeft(tracker, (t.X - t.RadiusPx) / scale);
        Canvas.SetTop(tracker, (t.Y - t.RadiusPx) / scale);
    }

    /// <summary>The target under the pointer, or none. Drills never give credit for a near miss.</summary>
    int HitIndex(double px, double py)
    {
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].Covers(px, py)) return i;
        return -1;
    }

    // ---------- loop ----------

    async Task StartCountdown()
    {
        string goal = cfg.Kind == DrillKind.Tracking ? "按住左键跟住目标" : "点得越快越准越好";
        for (int i = 3; i > 0 && !countdownCancelled; i--)
        {
            BannerText.Text = $"{i}   {goal}（{cfg.Seconds:0} 秒）· Esc 退出";
            await Task.Delay(650);
        }
        if (countdownCancelled) return;

        running = true;
        sessionStartedMs = clock.Elapsed.TotalMilliseconds;
        lastTickMs = sessionStartedMs;
        run.ElapsedMs = 0;
        Seed();
        tick.Start();
        hud.Start();
        BannerText.Text = run.Hud();
        UpdateCross();
    }

    void OnTick()
    {
        if (!running) return;
        double now = clock.Elapsed.TotalMilliseconds;
        double dt = Math.Max(0, now - lastTickMs);
        lastTickMs = now;

        if (cfg.Kind == DrillKind.Tracking && targets.Count > 0)
        {
            var t = targets[0];
            var (x, y, vx, vy) = DrillField.StepTracker(t.X, t.Y, tvx, tvy, dt, area, t.RadiusPx);
            tvx = vx; tvy = vy;
            targets[0] = t with { X = x, Y = y };
            MoveTracker();

            var (cx, cy) = CursorPx();
            bool on = targets[0].Covers(cx, cy);
            run.Tick(dt, buttonHeld, on);
        }

        run.Advance(dt);
        UpdateCross();
        if (run.Finished) Finish("time_up");
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!running || e.ChangedButton != MouseButton.Left) return;

        if (cfg.Kind == DrillKind.Tracking) { buttonHeld = true; return; }

        var (px, py) = CursorPx();
        int idx = HitIndex(px, py);
        bool completesSet = cfg.Kind == DrillKind.Sixshot && idx >= 0 && targets.Count == 1;
        run.Click(idx >= 0, completesSet);
        if (idx < 0) { BannerText.Text = run.Hud(); return; }

        targets.RemoveAt(idx);
        if (cfg.Kind == DrillKind.Sixshot && targets.Count == 0) SpawnSix();
        else if (cfg.Kind == DrillKind.Gridshot) SpawnAtFreeCell();
        DrawAll();
        UpdateCross();
        BannerText.Text = run.Hud();
        if (run.Finished) Finish("time_up");
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) buttonHeld = false;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        countdownCancelled = true;
        running = false;
        Finish(run.Shots > 0 || run.HeldMs > 0 ? "stopped_early" : "aborted_before_start");
    }

    void Finish(string reason)
    {
        endReason = reason;
        running = false;
        tick.Stop();
        hud.Stop();
        if (!sessionReported)
        {
            sessionReported = true;
            BannerText.Text = run.Score() + " · 正在还原…";
            SessionDone?.Invoke(run);
        }
        RestoreDisplay();
        Close();
    }

    void RestoreDisplay()
    {
        if (!changingMode) return;
        changingMode = false;
        if (!Display.Restore())
            DisplayWarning = (DisplayWarning.Length > 0 ? DisplayWarning + "；" : "") +
                             "分辨率没能自动还原，去 Windows 显示设置里手动改回来";
    }

    void OnClosed(object sender, EventArgs e)
    {
        tick.Stop();
        hud.Stop();
        countdownCancelled = true;
        running = false;
        RestoreDisplay();
        if (!sessionReported)
        {
            sessionReported = true;
            endReason = "closed";
            SessionDone?.Invoke(run);
        }
    }
}
