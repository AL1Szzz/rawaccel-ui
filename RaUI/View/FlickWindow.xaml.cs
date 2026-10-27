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
/// Borderless fullscreen ball practice. Many balls sit on screen at once and are clicked one
/// after another; a clicked ball jumps to a fresh spot, so the run is continuous and never
/// re-centres the pointer. Each click is scored on its own: from wherever the pointer actually
/// was after the previous click, to the ball that was under it.
/// Distances are physical desktop pixels - this is a pointer test, it does not model a game camera.
/// </summary>
public partial class FlickWindow : Window
{
    public sealed class Config
    {
        public double DistancePx = 420;             // physical px from pointer to ball centre
        public double RadiusPx = 26;
        public int TargetCount = 1;               // balls alive on screen at once
        public TestMode Mode = TestMode.Balls;    // clear a number of balls, or clear as many as possible in time
        public int Trials = 20;                   // Balls mode: how many balls must be CLEARED
        public double Seconds = 60;               // Timed mode: how long the round runs
        public int DirectionMode;                 // 0 any 1 right 2 left 3 down 4 up 5 horizontal
        public int Seed = 20261020;
        public Display.Mode? Resolution;          // null = keep the current desktop mode

        // crosshair, in physical pixels
        public double CrossArm = 9;
        public double CrossGap = 3;
        public string CrossColor = "#FFFFFFFF";
        public bool CrossDot = true;

        /// <summary>Test hook: pretend the pointer is here (physical px) so the layout can be rendered offscreen.</summary>
        public (double x, double y)? DebugCursorPx;
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    readonly Config cfg;
    readonly SessionRule rule;
    readonly List<Trial> done = new();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly DispatcherTimer follow = new() { Interval = TimeSpan.FromMilliseconds(8) };
    readonly DispatcherTimer hud = new() { Interval = TimeSpan.FromMilliseconds(200) };

    double scale = 1.0;                 // device pixels per DIP
    double screenW = 1920;
    double screenH = 1080;
    List<BallTarget> balls = new();
    double flickOriginX, flickOriginY, flickStartMs;
    int nextBallId = 1, respawnStuck;
    double sessionStartedMs;
    bool running;
    bool countdownCancelled;
    bool sessionReported;
    bool changingMode;
    string endReason = "";

    public string UsedResolution { get; private set; } = "";
    public string DisplayWarning { get; private set; } = "";
    /// <summary>Something about the setup the player should know (e.g. the screen was too small).</summary>
    public string RoundNotice { get; private set; } = "";
    public string EndReason => endReason;
    public SessionRule Rule => rule;
    public bool Completed => rule.Finished;

    public event Action<Trial>? TrialDone;
    public event Action<List<Trial>>? SessionDone;

    public FlickWindow(Config cfg)
    {
        this.cfg = cfg;
        rule = new SessionRule(cfg.Mode, cfg.Trials, cfg.Seconds);
        InitializeComponent();
        SourceInitialized += OnSourceInit;
        Loaded += OnLoaded;
        follow.Tick += (_, _) => UpdateCross();
        hud.Tick += (_, _) => TickHud();
    }

    /// <summary>Timed mode has to end on the clock even when the player stops clicking.</summary>
    void TickHud()
    {
        if (!running) return;
        rule.ElapsedMs = clock.Elapsed.TotalMilliseconds - sessionStartedMs;
        BannerText.Text = rule.Hud();
        if (rule.Finished) Finish(rule.EndReason);
    }

    // ---------- window setup ----------

    void OnSourceInit(object sender, EventArgs e)
    {
        var src = PresentationSource.FromVisual(this);
        scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        if (cfg.DebugCursorPx is not null)
        {
            // offscreen visual check: never move the window on screen and never touch the display mode
            Width = 1920 / scale; Height = 1080 / scale;
            screenW = 1920; screenH = 1080;
            UsedResolution = "debug 1920x1080 (offscreen, no mode change)";
            return;
        }

        if (cfg.Resolution is { } m)
        {
            changingMode = true;
            if (!Display.TrySet(m))
            {
                changingMode = false;
                DisplayWarning = $"没能切到 {m.Width}×{m.Height}@{m.Refresh}Hz，本轮用当前分辨率跑";
            }
        }

        // re-read the scale after a mode change: the DIP size of the desktop moves with it
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

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PaintCross();
        follow.Start();
        UpdateCross();

        if (cfg.DebugCursorPx is not null)
        {
            // render the HUD the way a live round would show it (used by --dump-flick)
            rule.Clicks = 7; rule.Hits = 5; rule.ElapsedMs = rule.TimeLimitMs * 0.4;
            BannerText.Text = rule.Hud();
            SpawnInitial(); UpdateCross(); return;
        }
        _ = StartCountdown();
    }

    (double x, double y) CursorPx()
    {
        if (cfg.DebugCursorPx is { } dbg) return dbg;
        GetCursorPos(out var p);
        var origin = PointToScreen(new Point(0, 0));   // device pixels
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

    static SolidColorBrush Hex(byte a, byte r, byte g, byte b2)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b2)); br.Freeze(); return br;
    }

    /// <summary>Arms are specified in physical pixels; the canvas works in DIPs.</summary>
    void UpdateCross()
    {
        var (px, py) = CursorPx();
        double x = px / scale, y = py / scale;
        double arm = Math.Max(0, cfg.CrossArm / scale);
        double gap = Math.Max(0, cfg.CrossGap / scale);
        double thickPhys = 2.0;                                  // constant on every display
        double thick = thickPhys / scale;

        CArmL.Height = thick; CArmR.Height = thick; CArmT.Width = thick; CArmB.Width = thick;
        CArmL.Width = arm; CArmR.Width = arm; CArmT.Height = arm; CArmB.Height = arm;
        CArmL.Visibility = CArmR.Visibility = arm > 0 ? Visibility.Visible : Visibility.Hidden;
        CArmT.Visibility = CArmB.Visibility = arm > 0 ? Visibility.Visible : Visibility.Hidden;
        CDot.Width = thick; CDot.Height = thick;

        Canvas.SetLeft(CArmL, x - gap - arm); Canvas.SetTop(CArmL, y - thick / 2);
        Canvas.SetLeft(CArmR, x + gap); Canvas.SetTop(CArmR, y - thick / 2);
        Canvas.SetTop(CArmT, y - gap - arm); Canvas.SetLeft(CArmT, x - thick / 2);
        Canvas.SetTop(CArmB, y + gap); Canvas.SetLeft(CArmB, x - thick / 2);
        Canvas.SetTop(CDot, y - thick / 2); Canvas.SetLeft(CDot, x - thick / 2);
    }

    void OnPointerMove(object sender, MouseEventArgs e) => UpdateCross();

    // ---------- session ----------

    async Task StartCountdown()
    {
        for (int i = 3; i > 0 && !countdownCancelled; i--)
        {
            string goal = rule.Mode == TestMode.Balls
                ? $"点掉 {rule.BallGoal} 个球"
                : $"{rule.TimeLimitMs / 1000.0:0} 秒，看能点掉几个";
            BannerText.Text = $"{i}   准备：{cfg.TargetCount} 个球同屏，{goal} · 点掉的跳到别处 · 左键 · Esc 退出";
            await Task.Delay(700);
        }
        if (countdownCancelled) return;

        running = true;
        sessionStartedMs = clock.Elapsed.TotalMilliseconds;
        rule.ElapsedMs = 0;
        BannerText.Text = rule.Hud();
        hud.Start();
        SpawnInitial();
        UpdateCross();
    }

    /// <summary>Spawn the first set of balls around wherever the pointer is now.</summary>
    void SpawnInitial()
    {
        int want = Math.Max(1, cfg.TargetCount);
        var (cx, cy) = CursorPx();
        balls = TargetLayout.Place(cx, cy, screenW, screenH, cfg.DistancePx, cfg.RadiusPx,
                                   want, cfg.Seed, cfg.DirectionMode);
        for (int i = 0; i < balls.Count; i++) balls[i] = balls[i] with { Id = i + 1 };
        nextBallId = balls.Count + 1;

        if (balls.Count == 0)
        {
            BannerText.Text = "屏幕放不下任何球：把距离或球半径调小（本轮结束）";
            Finish("no_room");
            return;
        }
        if (balls.Count < want)
            RoundNotice = $"屏幕只放不下 {want} 个球里的 {balls.Count} 个：把距离或半径调小一点";

        flickOriginX = cx; flickOriginY = cy;
        flickStartMs = clock.Elapsed.TotalMilliseconds;
        DrawBalls();
    }

    /// <summary>
    /// A clicked ball jumps to a new spot at the configured distance from where the click
    /// happened, never overlapping the balls still on screen. Shrinking the distance is tried
    /// before giving up, because a stuck ball would end the round.
    /// </summary>
    void Respawn(int index, double fromX, double fromY)
    {
        var others = balls.Where((_, i) => i != index).ToList();
        double want = cfg.DistancePx;
        foreach (double k in new[] { 1.0, 0.8, 0.62, 0.45, 0.3 })
        {
            var placed = TargetLayout.Place(fromX, fromY, screenW, screenH, want * k, cfg.RadiusPx, 1,
                                            nextBallId * 7919 + cfg.Seed, cfg.DirectionMode, avoid: others);
            if (placed.Count == 0) continue;
            balls[index] = placed[0] with { Id = nextBallId++ };
            DrawBalls();
            return;
        }
        // left where it was rather than end the round, and the banner says so instead of hiding it
        respawnStuck++;
        RoundNotice = $"{respawnStuck} 个球在屏幕上找不到新位置，留在原地继续";
    }

    void DrawBalls()
    {
        Surface.Children.Clear();
        double r = cfg.RadiusPx / scale;

        for (int i = 0; i < balls.Count; i++)
        {
            var b = balls[i];
            var holder = new Canvas { Width = r * 2 + 12 / scale, Height = r * 2 + 12 / scale };
            var fill = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = Brush(cfg.CrossColor == "#FFFFFFFF" ? "#FF4CC2FF" : cfg.CrossColor),
                Stroke = Hex(0xE0, 0xFF, 0xFF, 0xFF), StrokeThickness = 1.5 / scale,
            };
            Canvas.SetLeft(fill, 6 / scale); Canvas.SetTop(fill, 6 / scale);
            holder.Children.Add(fill);

            Canvas.SetLeft(holder, (b.X - cfg.RadiusPx) / scale - 6 / scale);
            Canvas.SetTop(holder, (b.Y - cfg.RadiusPx) / scale - 6 / scale);
            Surface.Children.Add(holder);
        }
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!running || e.ChangedButton != MouseButton.Left || balls.Count == 0) return;

        var (cpx, cpy) = CursorPx();
        var (idx, underCursor) = TargetLayout.Pick(balls, cfg.RadiusPx, cpx, cpy);
        if (idx < 0) return;
        var tgt = balls[idx];

        // the flick runs from where the pointer actually was when the last ball was clicked
        // (or from the round start), so the distance and direction are the real ones
        double aimDistance = Math.Sqrt(Math.Pow(tgt.X - flickOriginX, 2) + Math.Pow(tgt.Y - flickOriginY, 2));

        var trial = new Trial
        {
            DistancePx = aimDistance,
            StartMs = flickStartMs,
            EndMs = clock.Elapsed.TotalMilliseconds,
            OriginX = flickOriginX, OriginY = flickOriginY,
            TargetX = tgt.X, TargetY = tgt.Y,
            ClickX = cpx, ClickY = cpy,
            ErrorPx = TargetLayout.Error(cpx, cpy, tgt.X, tgt.Y),
            Hit = underCursor,
            TargetId = tgt.Id, VisibleTargets = balls.Count,
        };
        done.Add(trial);
        rule.Clicks++;
        if (trial.Hit) rule.Hits++;
        rule.ElapsedMs = clock.Elapsed.TotalMilliseconds - sessionStartedMs;
        TrialDone?.Invoke(trial);

        // every click starts the next flick, whether it landed or not
        flickOriginX = cpx; flickOriginY = cpy;
        flickStartMs = clock.Elapsed.TotalMilliseconds;

        if (rule.Finished) { Finish(rule.EndReason); return; }
        if (trial.Hit) Respawn(idx, cpx, cpy);
        else { DrawBalls(); UpdateCross(); }
        BannerText.Text = rule.Hud();
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        countdownCancelled = true;
        running = false;
        Finish(rule.Clicks > 0 ? "stopped_early" : "aborted_before_first_click");
    }

    void Finish(string reason)
    {
        endReason = reason;
        running = false;
        follow.Stop();
        hud.Stop();
        if (!sessionReported)
        {
            sessionReported = true;
            string note = RoundNotice.Length > 0 ? "（" + RoundNotice + "）" : "";
            BannerText.Text = rule.Clicks > 0 ? $"{rule.Score()} · 正在还原…{note}" : "本轮没有有效点击，正在还原…";
            SessionDone?.Invoke(new List<Trial>(done));
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
        follow.Stop();
        hud.Stop();                             // timed mode ends on the clock; a closed window must not keep a tick running
        countdownCancelled = true;
        running = false;
        RestoreDisplay();                       // covers Alt+F4 / owner shutdown
        if (!sessionReported)
        {
            sessionReported = true;
            endReason = "closed";
            SessionDone?.Invoke(new List<Trial>(done));
        }
    }
}
