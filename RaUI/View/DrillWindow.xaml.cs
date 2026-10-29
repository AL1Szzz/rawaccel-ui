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
/// The fullscreen drill room: nine drills on one window, one clock, one crosshair. What changes
/// between them is where the targets are and how a point is scored, and all of that lives in
/// <see cref="DrillField"/> / <see cref="DrillRun"/> so it can be asserted without a window.
///
/// Like the ball test this only moves the pointer's story, not the driver's: it never writes to
/// Raw Accel, never injects input. The only system-wide thing it does is the display mode, and that
/// is restored on every exit path - including the one where the process is killed mid-round.
///
/// The calibration session drives this window one round at a time and reads two numbers off
/// <see cref="DrillRun"/>: SkillScore, and Flick.Consistency. The second one is why the loop records
/// where the pointer was when it scored - a curve can be fast at one distance and dead at another,
/// and no total count would ever show that.
/// </summary>
public partial class DrillWindow : Window
{
    public sealed class Config
    {
        public DrillKind Kind = DrillKind.Gridshot;
        public double Seconds = 60;
        public double RadiusPx = 22;
        /// <summary>Gridshot / multichain: how many targets are alive at once.</summary>
        public int Alive = 3;
        public int Cols = 6, Rows = 3;
        public int Seed = 20261020;
        /// <summary>FlashReset: ends on this many completed flick-and-returns instead of the clock.</summary>
        public int RepTarget;
        /// <summary>Tracking / strafe: how fast the target crosses the field, in physical px per second.</summary>
        public double TrackSpeedPx = 760;
        public Display.Mode? Resolution;

        public double CrossArm = 9, CrossGap = 3;
        public string CrossColor = "#FFFFFFFF";
        public bool CrossDot = true;

        /// <summary>Shown top-left during a calibration round: which candidate is live, never its value.</summary>
        public string BlindLabel = "";

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
        [DrillKind.ThreeShot] = "#FF7BD88F",
        [DrillKind.SingleChain] = "#FFB77BFF",
        [DrillKind.Reaction] = "#FFFFA14C",
        [DrillKind.MultiChain] = "#FF4CD9C0",
        [DrillKind.Strafe] = "#FF9AA7FF",
        [DrillKind.FlashReset] = "#FFFF7BD0",
    };

    readonly Config cfg;
    readonly DrillRun run;
    readonly List<DrillTarget> targets = new();
    readonly Random rng;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly DispatcherTimer hud = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>
    /// Watchdog for the render loop. Rendering is what keeps the round moving, and WPF stops raising
    /// it while the window is occluded or minimised - without this, a drill that lost its frames
    /// would also lose its clock and never end. 250ms is slow enough to cost nothing and fast enough
    /// that a stalled frame does not freeze the score.
    /// </summary>
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// The radius actually drawn: what the player asked for, but never below what the drill needs.
    /// Tracking on a 1440p field with a 22px dot is a pixel hunt, so it has a floor of its own.
    /// </summary>
    readonly double radiusPx;

    double scale = 1.0;
    double screenW = 1920, screenH = 1080;
    DrillField.Area area;
    int nextId = 1;
    double lastTickMs, sessionStartedMs;
    bool running, changingMode, countdownCancelled, sessionReported, buttonHeld;
    double tvx, tvy;
    TranslateTransform? trackerTf;
    TranslateTransform? crossTf;
    string endReason = "";

    // The round runs on the render loop instead of a DispatcherTimer. A Win32 timer is quantised to
    // ~15.6ms and drifts against vsync however small you make the interval, which is exactly the
    // judder a moving target shows worst; Rendering fires once per composed frame, so the ball lands
    // on the frame that gets shown.
    bool renderHooked;

    // Where the window's top-left is on screen, in physical pixels. Cached because the old code
    // asked PointToScreen on every single tick, and a fullscreen window does not move mid-round.
    double originX, originY;
    bool originKnown;

    // reaction: hidden until its delay runs out, then one deliberate shot
    bool reactVisible;
    double reactAppearAtMs, reactNextAtMs;

    // flash reset: out to the side, then back to the middle
    int flashPhase;          // 0 = shoot the far target, 1 = shoot the middle ring
    bool flashGoLeft = true;
    double resetRingPx;

    public string UsedResolution { get; private set; } = "";
    public string DisplayWarning { get; private set; } = "";
    public string EndReason => endReason;
    public DrillRun Run => run;

    /// <summary>A round the player abandoned part-way is data, but weak data; the caller decides.</summary>
    public bool Completed =>
        endReason == "time_up" || endReason == "reps_done" ||
        (run.RepTarget > 0 ? run.RepsDone >= run.RepTarget : run.ElapsedMs >= run.TimeLimitMs * 0.9);

    public event Action<DrillRun>? SessionDone;

    public DrillWindow(Config cfg)
    {
        this.cfg = cfg;
        run = new DrillRun(cfg.Kind, cfg.Seconds) { RepTarget = cfg.RepTarget };
        rng = new Random(cfg.Seed);
        radiusPx = cfg.RadiusPx > 0
            ? Math.Max(cfg.RadiusPx, DrillInfo.MinRadiusPx(cfg.Kind))
            : DrillInfo.DefaultRadiusPx(cfg.Kind);
        if (cfg.DebugCursorPx is not null) { screenW = 1920; screenH = 1080; }
        InitializeComponent();
        SourceInitialized += OnSourceInit;
        Loaded += OnLoaded;
        hud.Tick += (_, _) => { if (running) BannerText.Text = Banner(); };
        // dt comes from the stopwatch, so a watchdog tick overlapping a render tick splits the same
        // elapsed time between them instead of counting it twice.
        watchdog.Tick += (_, _) =>
        {
            if (running && clock.Elapsed.TotalMilliseconds - lastTickMs > 150) OnTick();
        };
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
        RefreshOrigin();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PaintCross();
        UpdateCross();
        HintText.Text = DrillInfo.Hint(cfg.Kind);

        if (cfg.DebugCursorPx is not null)
        {
            Seed();
            run.Advance(23_000);
            var (dx, dy) = cfg.DebugCursorPx.Value;
            run.Click(dx, dy, true); run.Click(dx + 90, dy, true); run.Click(dx + 200, dy, false);
            if (run.Kind == DrillKind.Tracking) { run.Tick(4200, true, true); run.Tick(1500, true, false); }
            if (run.Kind == DrillKind.Sixshot) run.Click(dx + 300, dy, true, completesSet: true);
            if (run.Kind == DrillKind.Reaction) { run.Reaction(260); run.Reaction(310); }
            if (run.Kind == DrillKind.FlashReset) run.Rep();
            BannerText.Text = Banner();
            return;
        }

        if (cfg.DebugStatic) { Seed(); BannerText.Text = Banner(); return; }
        _ = StartCountdown();
    }

    string Banner()
    {
        string hudText = run.Hud();
        return cfg.BlindLabel.Length > 0 ? $"[{cfg.BlindLabel}] {hudText}" : hudText;
    }

    void RefreshOrigin()
    {
        var o = PointToScreen(new Point(0, 0));
        originX = o.X; originY = o.Y; originKnown = true;
    }

    (double x, double y) CursorPx()
    {
        if (cfg.DebugCursorPx is { } dbg) return dbg;
        GetCursorPos(out var p);
        if (!originKnown) RefreshOrigin();
        return (p.X - originX, p.Y - originY);
    }

    // ---------- crosshair ----------

    /// <summary>
    /// Sizes and places the five crosshair pieces once, around the origin, and gives the parent
    /// canvas one transform. Moving the crosshair is then a translation on the render thread instead
    /// of four Width/Height writes and eight Canvas.Left/Top writes per frame, each of which used to
    /// invalidate layout while the tracking target was trying to move smoothly.
    /// </summary>
    void PaintCross()
    {
        var b = Brush(cfg.CrossColor);
        CArmL.Fill = b; CArmR.Fill = b; CArmT.Fill = b; CArmB.Fill = b; CDot.Fill = b;

        var p = CrosshairGeometry.Place(0, 0, cfg.CrossArm, cfg.CrossGap, scale);
        Place(CArmL, p.Left); Place(CArmR, p.Right);
        Place(CArmT, p.Top); Place(CArmB, p.Bottom);
        Place(CDot, p.Dot);

        bool arms = p.Left.W > 0;
        CArmL.Visibility = CArmR.Visibility = arms ? Visibility.Visible : Visibility.Hidden;
        CArmT.Visibility = CArmB.Visibility = arms ? Visibility.Visible : Visibility.Hidden;
        CDot.Visibility = cfg.CrossDot ? Visibility.Visible : Visibility.Hidden;

        crossTf = new TranslateTransform(0, 0);
        Cross.RenderTransform = crossTf;
    }

    static void Place(System.Windows.Shapes.Rectangle r, RaCore.CrossRect c)
    {
        r.Width = c.W; r.Height = c.H;
        Canvas.SetLeft(r, c.X); Canvas.SetTop(r, c.Y);
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
        if (crossTf == null) return;
        var (px, py) = CursorPx();
        crossTf.X = px / scale;
        crossTf.Y = py / scale;
    }

    /// <summary>
    /// Deliberately does nothing. A mouse at 4-8kHz raises far more WM_MOUSEMOVE messages than there
    /// are frames, and updating the crosshair in the handler meant dozens of layout invalidations per
    /// frame, which is what made the moving target stutter. The render loop below already moves it
    /// once per composed frame, which is the fastest it can be seen anyway.
    /// </summary>
    void OnPointerMove(object sender, MouseEventArgs e) { }

    // ---------- layout ----------

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
        resetRingPx = Math.Max(18, radiusPx * 0.9);

        switch (cfg.Kind)
        {
            case DrillKind.Gridshot:
                for (int i = 0; i < Math.Max(1, cfg.Alive); i++) SpawnAtFreeCell();
                break;

            case DrillKind.Sixshot:
                SpawnSix();
                break;

            case DrillKind.Tracking:
            case DrillKind.Strafe:
                double cx = area.X + area.W / 2, cy = area.Y + area.H / 2;
                targets.Add(new DrillTarget(nextId++, cx, cy, radiusPx));
                double a = rng.NextDouble() * Math.PI * 2;
                tvx = Math.Cos(a) * cfg.TrackSpeedPx;
                tvy = Math.Sin(a) * cfg.TrackSpeedPx;
                break;

            case DrillKind.ThreeShot:
            case DrillKind.MultiChain:
                foreach (var (x, y) in DrillField.Spread(area, radiusPx, rng, Math.Max(1, cfg.Alive),
                                                         Math.Max(160, radiusPx * 5)))
                    targets.Add(new DrillTarget(nextId++, x, y, radiusPx));
                break;

            case DrillKind.SingleChain:
                SpawnScattered(1);
                break;

            case DrillKind.Reaction:
                // offscreen dumps have no clock, so the target is shown outright; a real round keeps
                // the screen empty until its delay runs out
                bool showNow = cfg.DebugStatic || cfg.DebugCursorPx is not null;
                reactVisible = showNow;
                reactAppearAtMs = 0;
                reactNextAtMs = showNow ? 0 : 400 + rng.NextDouble() * 1100;
                if (showNow) SpawnScattered(1);
                break;

            case DrillKind.FlashReset:
                flashPhase = 0;
                SpawnFlash();
                break;
        }
        DrawAll();
    }

    void SpawnAtFreeCell()
    {
        var cells = DrillField.Grid(Field(), cfg.Cols, cfg.Rows);
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
        targets.Add(new DrillTarget(nextId++, x, y, radiusPx));
    }

    /// <summary>Six spots, re-jittered from the round's own rng, so the next set is a new set.</summary>
    void SpawnSix()
    {
        foreach (var (x, y) in DrillField.Six(Field(), rng, radiusPx))
            targets.Add(new DrillTarget(nextId++, x, y, radiusPx));
    }

    void SpawnScattered(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var pts = DrillField.Spread(Field(), radiusPx, rng, 1, Math.Max(160, radiusPx * 5));
            if (pts.Length == 0) return;
            targets.Add(new DrillTarget(nextId++, pts[0].x, pts[0].y, radiusPx));
        }
    }

    void SpawnFlash()
    {
        var area = Field();
        if (flashPhase == 0)
        {
            var (x, y) = DrillField.FarSpot(area, radiusPx, rng, flashGoLeft);
            targets.Add(new DrillTarget(nextId++, x, y, radiusPx));
        }
        else
        {
            // the return: a ring in the middle, drawn hollow so it reads as "come back" and not "shoot"
            targets.Add(new DrillTarget(nextId++, area.X + area.W / 2, area.Y + area.H / 2, resetRingPx));
        }
    }

    void DrawAll()
    {
        Surface.Children.Clear();
        trackerTf = null;
        foreach (var t in targets)
        {
            bool hollow = cfg.Kind == DrillKind.FlashReset && flashPhase == 1;
            bool moving = cfg.Kind is DrillKind.Tracking or DrillKind.Strafe;
            var e = new Ellipse
            {
                Width = t.RadiusPx * 2 / scale,
                Height = t.RadiusPx * 2 / scale,
                Fill = hollow ? Brushes.Transparent : Brush(TargetFill[cfg.Kind]),
                Stroke = new SolidColorBrush(Color.FromArgb((byte)(hollow ? 255 : 224), 0xFF, 0xFF, 0xFF)),
                StrokeThickness = (hollow ? 3 : 1.5) / scale,
                IsHitTestVisible = false,
            };

            if (moving)
            {
                // moved by transform, not by Canvas.Left: re-layouting the canvas every frame while
                // the target crosses it is what turned a smooth bounce into a stutter
                Canvas.SetLeft(e, 0);
                Canvas.SetTop(e, 0);
                var tf = new TranslateTransform(0, 0);
                e.RenderTransform = tf;
                trackerTf = tf;
            }
            else
            {
                Canvas.SetLeft(e, (t.X - t.RadiusPx) / scale);
                Canvas.SetTop(e, (t.Y - t.RadiusPx) / scale);
            }
            Surface.Children.Add(e);
        }
        MoveTracker();
    }

    void MoveTracker()
    {
        if (trackerTf == null || targets.Count == 0) return;
        var t = targets[0];
        trackerTf.X = (t.X - t.RadiusPx) / scale;
        trackerTf.Y = (t.Y - t.RadiusPx) / scale;
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
        string goal = cfg.Kind switch
        {
            DrillKind.Tracking => "按住左键跟住目标",
            DrillKind.Reaction => "它出现再点，抢点算空枪",
            DrillKind.Strafe => "只在它停住的时候开枪",
            DrillKind.FlashReset => "先点边上的，再点回中间",
            _ => "点得越快越准越好",
        };
        string limit = run.RepTarget > 0 ? $"{run.RepTarget} 次" : $"{cfg.Seconds:0} 秒";
        for (int i = 3; i > 0 && !countdownCancelled; i--)
        {
            BannerText.Text = $"{i}   {goal}（{limit}）· Esc 退出";
            await Task.Delay(650);
        }
        if (countdownCancelled) return;

        running = true;
        sessionStartedMs = clock.Elapsed.TotalMilliseconds;
        lastTickMs = sessionStartedMs;
        run.ElapsedMs = 0;
        RefreshOrigin();
        Seed();
        HookRender();
        hud.Start();
        watchdog.Start();
        BannerText.Text = Banner();
        UpdateCross();
    }

    void HookRender()
    {
        if (renderHooked) return;
        renderHooked = true;
        CompositionTarget.Rendering += OnRender;
    }

    void UnhookRender()
    {
        if (!renderHooked) return;
        renderHooked = false;
        CompositionTarget.Rendering -= OnRender;
    }

    void OnRender(object? sender, EventArgs e)
    {
        if (running) OnTick();
    }

    void OnTick()
    {
        if (!running) return;
        double now = clock.Elapsed.TotalMilliseconds;
        double dt = Math.Max(0, now - lastTickMs);
        lastTickMs = now;

        bool moving = cfg.Kind is DrillKind.Tracking or DrillKind.Strafe;
        bool frozen = cfg.Kind == DrillKind.Strafe && DrillField.StrafeStopped(run.ElapsedMs);

        if (moving && targets.Count > 0 && !frozen)
        {
            var t = targets[0];
            var (x, y, vx, vy) = DrillField.StepTracker(t.X, t.Y, tvx, tvy, dt, Field(), t.RadiusPx);
            tvx = vx; tvy = vy;
            targets[0] = t with { X = x, Y = y };
            MoveTracker();
        }

        if (cfg.Kind == DrillKind.Tracking && targets.Count > 0)
        {
            var (cx, cy) = CursorPx();
            run.Tick(dt, buttonHeld, targets[0].Covers(cx, cy));
        }

        if (cfg.Kind == DrillKind.Reaction)
        {
            if (!reactVisible && run.ElapsedMs >= reactNextAtMs)
            {
                reactVisible = true;
                reactAppearAtMs = run.ElapsedMs;
                // the old code spawned and never drew: the target went into the list, a click on it
                // scored, and the screen stayed empty for the whole round
                SpawnScattered(1);
                DrawAll();
            }
        }

        run.Advance(dt);
        UpdateCross();
        if (run.Finished) Finish(run.RepTarget > 0 && run.RepsDone >= run.RepTarget ? "reps_done" : "time_up");
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!running || e.ChangedButton != MouseButton.Left) return;

        if (cfg.Kind == DrillKind.Tracking) { buttonHeld = true; return; }

        var (px, py) = CursorPx();

        if (cfg.Kind == DrillKind.Reaction)
        {
            // clicking the empty screen before it appears is a miss, not nothing: otherwise waiting
            // out the delay while clicking would score a perfect round
            if (!reactVisible) { run.Click(px, py, false); BannerText.Text = Banner(); return; }

            int ri = HitIndex(px, py);
            run.Click(px, py, ri >= 0);
            if (ri >= 0) run.Reaction(run.ElapsedMs - reactAppearAtMs);
            reactVisible = false;
            targets.Clear();
            reactNextAtMs = run.ElapsedMs + 400 + rng.NextDouble() * 1100;
            DrawAll();
            BannerText.Text = Banner();
            return;
        }

        int idx = HitIndex(px, py);

        if (cfg.Kind == DrillKind.Strafe)
        {
            bool stopped = DrillField.StrafeStopped(run.ElapsedMs);
            run.Click(px, py, idx >= 0 && stopped);
            if (idx >= 0 && stopped)
            {
                targets.Clear();
                SpawnScattered(1);
                double a = rng.NextDouble() * Math.PI * 2;
                tvx = Math.Cos(a) * cfg.TrackSpeedPx;
                tvy = Math.Sin(a) * cfg.TrackSpeedPx;
            }
            DrawAll();
            BannerText.Text = Banner();
            return;
        }

        if (cfg.Kind == DrillKind.FlashReset)
        {
            bool hit = idx >= 0;
            run.Click(px, py, hit);
            if (!hit) { BannerText.Text = Banner(); return; }

            targets.Clear();
            if (flashPhase == 0)
            {
                flashPhase = 1;
                SpawnFlash();
            }
            else
            {
                run.Rep();
                flashPhase = 0;
                flashGoLeft = !flashGoLeft;
                if (!run.Finished) SpawnFlash();
            }
            DrawAll();
            BannerText.Text = Banner();
            if (run.Finished) Finish("reps_done");
            return;
        }

        bool completesSet = cfg.Kind == DrillKind.Sixshot && idx >= 0 && targets.Count == 1;
        run.Click(px, py, idx >= 0, completesSet);
        if (idx < 0) { BannerText.Text = Banner(); return; }

        targets.RemoveAt(idx);
        if (cfg.Kind == DrillKind.Sixshot && targets.Count == 0) SpawnSix();
        else if (cfg.Kind == DrillKind.Gridshot) SpawnAtFreeCell();
        else if (cfg.Kind is DrillKind.SingleChain or DrillKind.ThreeShot or DrillKind.MultiChain) SpawnScattered(1);
        DrawAll();
        UpdateCross();
        BannerText.Text = Banner();
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
        UnhookRender();
        hud.Stop();
        watchdog.Stop();
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
        UnhookRender();
        hud.Stop();
        watchdog.Stop();
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
