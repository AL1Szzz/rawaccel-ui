using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RaCore;

namespace RaUI.View;

/// <summary>Point-and-click ball practice with settings and a short score history.</summary>
public partial class TestPage : UserControl
{
    readonly List<Trial> trials = new();
    AppState.CurveContext? frozen;
    FlickWindow? lastWindow;
    SessionRule? lastRule;

    public TestPage()
    {
        InitializeComponent();
        CmbDir.SelectedIndex = 0;

        var pr = AppState.Prefs;
        TxtSens.Text = pr.GameSensitivity.ToString("0.###", CultureInfo.InvariantCulture);
        TxtDpi.Text = pr.MouseDpi.ToString("0", CultureInfo.InvariantCulture);
        TxtDistance.Text = pr.TestDistancePx.ToString("0", CultureInfo.InvariantCulture);
        TxtRadius.Text = pr.TestRadiusPx.ToString("0", CultureInfo.InvariantCulture);
        TxtTrials.Text = pr.TestTrials.ToString(CultureInfo.InvariantCulture);
        TxtSeconds.Text = pr.TestSeconds.ToString(CultureInfo.InvariantCulture);
        TxtSeed.Text = pr.TestSeed.ToString(CultureInfo.InvariantCulture);
        SelectByTag(CmbMode, pr.TestMode.ToString());
        SelectBalls(pr.TestBalls);
        TxtCrossArm.Text = pr.CrossArm.ToString("0.#", CultureInfo.InvariantCulture);
        TxtCrossGap.Text = pr.CrossGap.ToString("0.#", CultureInfo.InvariantCulture);
        ChkCrossDot.IsChecked = pr.CrossDot;
        foreach (var item in CmbCrossColor.Items.OfType<ComboBoxItem>())
            if ((string)item.Tag == pr.CrossColor) { item.IsSelected = true; break; }

        Loaded += (_, _) =>
        {
            FillDevices();
            RefreshPlan();
            FillRounds();
        };
        TxtDistance.TextChanged += (_, _) => RefreshPlan();
        TxtRadius.TextChanged += (_, _) => RefreshPlan();
        CmbBalls.SelectionChanged += (_, _) => RefreshPlan();
        CmbMode.SelectionChanged += (_, _) => RefreshPlan();
        TxtTrials.TextChanged += (_, _) => RefreshPlan();
        TxtSeconds.TextChanged += (_, _) => RefreshPlan();
        CmbDir.SelectionChanged += (_, _) => RefreshPlan();
    }

    static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var i in box.Items.OfType<ComboBoxItem>())
            if ((string)(i.Tag ?? "") == tag) { i.IsSelected = true; return; }
        box.SelectedIndex = 0;
    }

    void SelectBalls(int n)
    {
        foreach (var i in CmbBalls.Items.OfType<ComboBoxItem>())
            if (i.Content?.ToString() == n.ToString(CultureInfo.InvariantCulture)) { i.IsSelected = true; return; }
        CmbBalls.SelectedIndex = 2;
    }

    void FillDevices()
    {
        string keep = AppState.Prefs.TestDeviceId;
        CmbDevice.Items.Clear();
        CmbDevice.Items.Add(new ComboBoxItem { Content = "所有鼠标（不区分）", Tag = "" });
        try
        {
            foreach (var d in App.State.EnumerateDevices())
                CmbDevice.Items.Add(new ComboBoxItem { Content = Trunc(d.Name.Length > 0 ? d.Name : d.Id, 26), Tag = d.Id });
        }
        catch (Exception e)
        {
            CmbDevice.Items.Add(new ComboBoxItem { Content = "枚举失败: " + Trunc(e.Message, 20), Tag = "" });
        }
        foreach (var i in CmbDevice.Items.OfType<ComboBoxItem>())
            if ((string)(i.Tag ?? "") == keep) { i.IsSelected = true; return; }
        CmbDevice.SelectedIndex = 0;
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------- inputs ----------

    double Sens => ParseD(TxtSens.Text, AppState.Prefs.GameSensitivity);
    double Dpi => ParseD(TxtDpi.Text, AppState.Prefs.MouseDpi);
    double DistancePx => Math.Clamp(ParseD(TxtDistance.Text, 420), 40, 4000);
    double RadiusPx => Math.Clamp(ParseD(TxtRadius.Text, 26), 4, 200);
    int Balls => ParseI((CmbBalls.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "3", 3);
    int BallGoal => Math.Clamp(ParseI(TxtTrials.Text, 30), 1, 2000);
    int Seconds => Math.Clamp(ParseI(TxtSeconds.Text, 60), 5, 3600);
    TestMode Mode => (CmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "1" ? TestMode.Timed : TestMode.Balls;
    string ModeLabel => Mode == TestMode.Balls ? $"点掉 {BallGoal} 球" : $"{Seconds} 秒限时";
    int DirMode => int.TryParse((CmbDir.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : 0;
    string DirName => (CmbDir.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全向";
    int Seed => (int)Math.Clamp(ParseD(TxtSeed.Text, 20261020), 1, int.MaxValue);
    double CrossArm => Math.Max(0, ParseD(TxtCrossArm.Text, 9));
    double CrossGap => Math.Max(0, ParseD(TxtCrossGap.Text, 3));
    string CrossColor => (CmbCrossColor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFFFFFFF";
    string DeviceId => (CmbDevice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    Display.Mode PlannedMode()
    {
        var pr = AppState.Prefs;
        if (!pr.UseCustomFullscreenMode || pr.FullscreenWidth == 0) return Display.Current();
        var modes = Display.AvailableModes();
        var match = modes.FirstOrDefault(m => m.Width == pr.FullscreenWidth && m.Height == pr.FullscreenHeight);
        if (match.Width > 0)
        {
            if (pr.FullscreenRefresh > 0)
            {
                var withHz = modes.FirstOrDefault(m => m.Width == pr.FullscreenWidth && m.Height == pr.FullscreenHeight
                                                      && m.Refresh == pr.FullscreenRefresh);
                if (withHz.Width > 0) return withHz;
            }
            return match;
        }
        return Display.Current();
    }

    void RefreshPlan()
    {
        var target = PlannedMode();
        var cur = Display.Current();
        ModeInfo.Text = $"当前桌面：{cur.Width}×{cur.Height} @ {cur.Refresh}Hz。" +
                        (target.Width > 0 && (target.Width != cur.Width || target.Height != cur.Height)
                            ? $"测试会切到 {target.Width}×{target.Height} @ {target.Refresh}Hz，结束还原。"
                            : "测试保持当前分辨率，只开无边框全屏。");

        double w = target.Width > 0 ? target.Width : cur.Width;
        double h = target.Height > 0 ? target.Height : cur.Height;
        double maxUsable = Math.Min(w, h) * 0.42;
        string fit = DistancePx > maxUsable
            ? $"注意：{DistancePx:0}px 在 {w:0}×{h:0} 上经常放不下，球会被少放或直接提示没空间；建议不超过 {maxUsable:0}px。"
            : $"{DistancePx:0}px 在这个分辨率下放得开（可用半径上限约 {maxUsable:0}px）。";
        PlanInfo.Text = $"{Balls} 个球同屏，{ModeLabel} · 点掉的跳到 {DistancePx:0}px 外 · 球半径 {RadiusPx:0}px · {DirName}。{fit}";
    }

    // ---------- run ----------

    void OnStart(object sender, RoutedEventArgs e) => Start(true);
    void OnStartKeepMode(object sender, RoutedEventArgs e) => Start(false);

    void Start(bool useCustomResolution)
    {
        RememberPrefs();
        trials.Clear();
        Summary.Text = "";
        Summary.Visibility = Visibility.Collapsed;
        lastWindow = null;
        lastRule = null;

        // Which curve the mouse is served by when the round starts - recorded with the score so
        // the history says WHAT produced it. Read-only: starting a test never writes to the driver.
        frozen = null;
        try { frozen = App.State.CaptureLiveCurve(DeviceId); }
        catch (Exception e) { App.State.Status = "本轮起点没能读取：" + e.Message; }

        var cfg = new FlickWindow.Config
        {
            DistancePx = DistancePx,
            RadiusPx = RadiusPx,
            TargetCount = Balls,
            Mode = Mode,
            Trials = BallGoal,
            Seconds = Seconds,
            DirectionMode = DirMode,
            Seed = Seed,
            CrossArm = CrossArm,
            CrossGap = CrossGap,
            CrossColor = CrossColor,
            CrossDot = ChkCrossDot.IsChecked == true,
            Resolution = useCustomResolution && AppState.Prefs.UseCustomFullscreenMode ? PlannedMode() : null,
        };

        var w = new FlickWindow(cfg) { Owner = Window.GetWindow(this) };
        w.TrialDone += t => Dispatcher.Invoke(() => trials.Add(t));
        w.SessionDone += list => Dispatcher.Invoke(() =>
        {
            trials.Clear();
            trials.AddRange(list);
            lastWindow = w;
            lastRule = w.Rule;
            int cleared = trials.Count(t => t.Hit);
            double hitRate = trials.Count == 0 ? 0 : cleared * 100.0 / trials.Count;
            Summary.Text = $"本次：点掉{cleared}球，命中{hitRate:0}%，用时{w.Rule.ElapsedMs / 1000.0:0.#}秒" +
                           (w.RoundNotice.Length > 0 ? $"（{w.RoundNotice}）" : "");
            Summary.Visibility = Visibility.Visible;
        });
        w.Closed += (_, _) =>
        {
            string displayStatus = w.DisplayWarning;
            try
            {
                bool retrying = Display.HasPendingChange;
                if (!Display.Restore())
                    displayStatus = "分辨率还原失败，请到 Windows 显示设置手动改回。";
                else if (retrying)
                    displayStatus = "分辨率已在关闭窗口时重试还原。";
            }
            catch (Exception e)
            {
                displayStatus = "分辨率还原失败，请到 Windows 显示设置手动改回：" + e.Message;
            }
            try
            {
                RefreshPlan();
                WriteHistory();
                FillRounds();
            }
            finally
            {
                if (displayStatus.Length > 0) App.State.Status = displayStatus;
            }
        };
        w.Show();
        w.Activate();
    }

    // ---------- score history ----------

    void WriteHistory()
    {
        if (trials.Count == 0) return;
        try
        {
            ScoreHistory.Append(new RoundRow
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Mode = ModeLabel,
                Cleared = trials.Count(t => t.Hit),
                Clicks = trials.Count,
                SessionMs = lastRule?.ElapsedMs ?? 0,
                Balls = Balls,
                DistancePx = DistancePx,
                RadiusPx = RadiusPx,
                Direction = DirName,
                Resolution = lastWindow?.UsedResolution ?? "",
                PlanName = frozen?.ProfileName ?? "",
                ConfigSha = frozen?.ConfigSha ?? "",
            });
        }
        catch (Exception e)
        {
            App.State.Status = "成绩没存下来：" + e.Message;
        }
    }

    public sealed class ScoreRow
    {
        public string Time { get; init; } = "";
        public int Cleared { get; init; }
        public string HitRate { get; init; } = "";
    }

    void FillRounds()
    {
        HistoryList.ItemsSource = ScoreHistory.Recent(80).Select(r => new ScoreRow
        {
            Time = DateTime.TryParse(r.When, out var when)
                ? when.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : r.When,
            Cleared = r.Cleared,
            HitRate = $"{r.HitRate:0}%",
        }).ToList();
    }

    // ---------- prefs ----------

    void RememberPrefs()
    {
        var pr = AppState.Prefs;
        pr.GameSensitivity = Sens;
        pr.MouseDpi = Dpi;
        pr.TestDistancePx = DistancePx;
        pr.TestRadiusPx = RadiusPx;
        pr.TestTrials = BallGoal;
        pr.TestSeconds = Seconds;
        pr.TestMode = (int)Mode;
        pr.TestBalls = Balls;
        pr.TestSeed = Seed;
        pr.TestDeviceId = DeviceId;
        pr.CrossArm = CrossArm;
        pr.CrossGap = CrossGap;
        pr.CrossColor = CrossColor;
        pr.CrossDot = ChkCrossDot.IsChecked == true;
        AppState.SavePrefs();
    }

    static double ParseD(string s, double d) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : d;
    static int ParseI(string s, int d) => int.TryParse(s, out var v) && v > 0 ? v : d;
}
