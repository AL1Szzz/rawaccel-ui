using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RaCore;
using RaCore.Model;

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
        SelectByTag(CmbDrill, pr.DrillKind.ToString());
        TxtDrillSeconds.Text = pr.DrillSeconds.ToString(CultureInfo.InvariantCulture);
        TxtDrillRadius.Text = pr.DrillRadiusPx.ToString("0.#", CultureInfo.InvariantCulture);
        TxtCalibMult.Text = pr.CalibMultipliers;
        TxtCalibRounds.Text = pr.CalibRounds.ToString(CultureInfo.InvariantCulture);
        ChkCalibNoAccel.IsChecked = pr.CalibNoAccel;
        SelectByTag(CmbCalibMode, pr.CalibMode.ToString());
        SelectByTag(CmbCalibPlan, pr.CalibPlan.ToString());
        TxtCrossArm.Text = pr.CrossArm.ToString("0.#", CultureInfo.InvariantCulture);
        TxtCrossGap.Text = pr.CrossGap.ToString("0.#", CultureInfo.InvariantCulture);
        ChkCrossDot.IsChecked = pr.CrossDot;
        foreach (var item in CmbCrossColor.Items.OfType<ComboBoxItem>())
            if ((string)item.Tag == pr.CrossColor) { item.IsSelected = true; break; }

        Loaded += (_, _) =>
        {
            FillDevices();
            RefreshPlan();
            RefreshCalib();
            FillRounds();
            RefreshLastCalib();
        };
        TxtSens.TextChanged += (_, _) => RefreshCalib();
        TxtDpi.TextChanged += (_, _) => RefreshCalib();
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

    // ---------- drills ----------

    int DrillIndex => int.TryParse((CmbDrill.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : 0;
    int DrillSecondsValue => Math.Clamp(ParseI(TxtDrillSeconds.Text, 60), 10, 900);
    double DrillRadiusValue => Math.Clamp(ParseD(TxtDrillRadius.Text, 22), 6, 120);
    string DrillName => DrillInfo.Name((DrillKind)DrillIndex);

    void OnStartDrill(object sender, RoutedEventArgs e) => StartDrill(true);
    void OnStartDrillKeepMode(object sender, RoutedEventArgs e) => StartDrill(false);

    void StartDrill(bool useCustomResolution)
    {
        RememberPrefs();
        DrillSummary.Text = "";
        DrillSummary.Visibility = Visibility.Collapsed;

        frozen = null;
        try { frozen = App.State.CaptureLiveCurve(DeviceId); }
        catch (Exception e) { App.State.Status = "本轮起点没能读取：" + e.Message; }

        var cfg = new DrillWindow.Config
        {
            Kind = (DrillKind)DrillIndex,
            Seconds = DrillSecondsValue,
            RadiusPx = DrillRadiusValue,
            Resolution = useCustomResolution && AppState.Prefs.UseCustomFullscreenMode ? PlannedMode() : null,
            CrossArm = CrossArm,
            CrossGap = CrossGap,
            CrossColor = CrossColor,
            CrossDot = ChkCrossDot.IsChecked == true,
        };

        var w = new DrillWindow(cfg) { Owner = Window.GetWindow(this) };
        w.SessionDone += r => Dispatcher.Invoke(() =>
        {
            string note = w.DisplayWarning.Length > 0 ? $"（{w.DisplayWarning}）" : "";
            DrillSummary.Text = "本次：" + r.Score() + note;
            DrillSummary.Visibility = Visibility.Visible;
            WriteDrillHistory(r, w);
            FillRounds();
        });
        w.Closed += (_, _) =>
        {
            try
            {
                if (!Display.Restore())
                    App.State.Status = "分辨率还原失败，请到 Windows 显示设置手动改回。";
                else if (w.DisplayWarning.Length > 0)
                    App.State.Status = w.DisplayWarning;
            }
            catch (Exception e)
            {
                App.State.Status = "分辨率还原失败，请到 Windows 显示设置手动改回：" + e.Message;
            }
            RefreshPlan();
            FillRounds();
        };
        w.Show();
        w.Activate();
    }

    void WriteDrillHistory(DrillRun r, DrillWindow w)
    {
        try
        {
            ScoreHistory.Append(new RoundRow
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Mode = DrillName,
                Cleared = r.Hits,
                Clicks = r.Shots,
                SessionMs = r.ElapsedMs,
                Balls = 0,
                DistancePx = 0,
                RadiusPx = DrillRadiusValue,
                Direction = "",
                Resolution = w.UsedResolution,
                PlanName = frozen?.ProfileName ?? "",
                ConfigSha = frozen?.ConfigSha ?? "",
                Extra = r.ShortScore(),
            });
        }
        catch (Exception e)
        {
            App.State.Status = "成绩没存下来：" + e.Message;
        }
    }

    // ---------- calibration ----------

    // ---------- blind curve session ----------

    double[] CalibMultipliers()
    {
        var parts = (TxtCalibMult.Text ?? "").Split(',', ';', ' ')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v) && v >= 0.05 && v <= 20)
            .ToArray();
        // 1.0 must be in the set or the player's own curve is never tested and the verdict would
        // always be "change something"
        if (parts.Length == 0 || !parts.Any(v => Math.Abs(v - 1.0) < 1e-9)) parts = AccelCandidates.DefaultMultipliers;
        return parts;
    }

    int CalibRounds() => Math.Clamp(ParseI(TxtCalibRounds.Text, 0), 0, 200);

    /// <summary>1 = 五轮递进（默认），0 = 单轮一次扫完。</summary>
    int CalibMode() =>
        int.TryParse((CmbCalibMode.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : 1;

    /// <summary>0 = 两轴同步扫一个参数；1 = 手腕流分轴（水平晚吃加速、垂直早吃加速）。</summary>
    Plan CalibPlan() =>
        int.TryParse((CmbCalibPlan.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) && v == 1
            ? Plan.Wrist : Plan.Uniform;

    void OnStartCalibSession(object sender, RoutedEventArgs e)
    {
        RememberPrefs();
        CalibSessionInfo.Visibility = Visibility.Collapsed;
        lastCalib = null;

        var live = App.State.SelectedProfile;
        if (live == null) { App.State.Status = "先在曲线页选一条曲线，盲测是拿它做基准的。"; return; }
        if (!App.State.DriverReachable) { App.State.Status = "驱动连不上，盲测要把候选写进驱动才能比。"; return; }
        if (!AccelCandidates.Sweepable(live))
        {
            App.State.Status = $"现在是「{live.X.Mode}」模式，没有可以扫的参数（无加速本身就是没有加速）。换一条带加速的曲线再来。";
            return;
        }

        var w = new CalibrationSessionWindow(new CalibrationSessionWindow.Options
        {
            RadiusPx = DrillRadiusValue,
            Resolution = AppState.Prefs.UseCustomFullscreenMode ? PlannedMode() : null,
            CrossArm = CrossArm,
            CrossGap = CrossGap,
            CrossColor = CrossColor,
            CrossDot = ChkCrossDot.IsChecked == true,
            DeviceId = DeviceId,
            GameSens = Sens,
            Multipliers = CalibMultipliers(),
            IncludeNoAccel = ChkCalibNoAccel.IsChecked == true,
            Family = CalibPlan(),
            Ladder = CalibMode() == 1,
            MaxRounds = CalibRounds(),
        });

        // The window applies the result itself; waiting on ShowDialog's return value is what made a
        // "apply" that ended the dialog early look like it had done nothing at all.
        w.ShowDialog();

        if (w.Accepted)
        {
            App.State.Status = $"已把「{w.RecommendedLabel}」设为当前曲线（还没写进驱动，去驱动页点应用）。";
            RefreshPlan();
        }
        else if (w.RecommendedProfile != null)
        {
            CalibSessionInfo.Text = $"结论：{w.RecommendedLabel}。没有改动你的曲线 —— 想留着就在报告里点「加入我的方案」，" +
                                    "或者在这儿用「上次盲测」再取回来。";
            CalibSessionInfo.Visibility = Visibility.Visible;
        }
        FillRounds();
        RefreshLastCalib();
    }

    // ---------- 上次盲测 ----------

    CalibArchive.Session? lastCalib;
    ProfileDto? lastCalibProfile;

    /// <summary>
    /// The most recent saved session, offered again. The point is that a session outlives its window:
    /// if the report was closed, or the process died, the rounds are still on disk and the result is
    /// still usable.
    /// </summary>
    void RefreshLastCalib()
    {
        lastCalib = CalibArchive.Latest();
        lastCalibProfile = CalibArchive.WinnerProfile(lastCalib);
        if (lastCalib == null) { LastCalibCard.Visibility = Visibility.Collapsed; return; }

        LastCalibCard.Visibility = Visibility.Visible;
        string what = lastCalibProfile == null
            ? "那次没跑到出结论，没有留下可选曲线。"
            : lastCalib.WinnerIsBase
                ? "结论是：你当时那条就是最优的。"
                : "选出的曲线：" + lastCalib.WinnerLabel;
        TxtLastCalib.Text = CalibArchive.Summarize(lastCalib) + "\n" + what +
                            (lastCalib.SavedAsPlan ? $"\n已经存为「{lastCalib.PlanName}」。" : "");
        bool usable = lastCalibProfile != null;
        BtnLastCalibPlan.IsEnabled = usable;
        BtnLastCalibApply.IsEnabled = usable;
    }

    void OnLastCalibPlan(object sender, RoutedEventArgs e)
    {
        if (lastCalibProfile == null || lastCalib == null) return;
        string name = Presets.FreeName("盲测 " + lastCalib.Id[..10].Replace("_", " "));
        string note = "来自 " + CalibArchive.Summarize(lastCalib) + "\n数值：" + lastCalib.WinnerLabel;
        string? file = Presets.Save(name, lastCalibProfile, out string error, note);
        if (file == null) { App.State.Status = "存方案失败：" + error; return; }

        lastCalib.SavedAsPlan = true;
        lastCalib.PlanName = name;
        CalibArchive.Save(lastCalib, out _);
        App.State.Status = $"上次盲测的曲线已存为「{name}」，去「我的方案」页可以一键换过去。";
        RefreshLastCalib();
    }

    void OnLastCalibApply(object sender, RoutedEventArgs e)
    {
        if (lastCalibProfile == null || lastCalib == null) return;
        string target = lastCalib.ProfileName.Length > 0
            ? lastCalib.ProfileName
            : App.State.SelectedProfile?.Name ?? "";
        App.State.ReplaceProfile(target, lastCalibProfile);
        App.State.Status = App.State.SaveToFile()
            ? $"已把上次盲测选出的「{lastCalib.WinnerLabel}」设为当前曲线（还没写进驱动，去驱动页点应用）。"
            : "曲线已替换，但没能保存到文件。";
        RefreshPlan();
        RefreshLastCalib();
    }

    void OnCalibrate(object sender, RoutedEventArgs e)
    {
        RememberPrefs();
        var cfg = new CalibrationWindow.Config
        {
            Sens = Sens,
            Dpi = Dpi,
            TargetCm360 = AppState.Prefs.TargetCm360,
        };

        var w = new CalibrationWindow(cfg) { Owner = Window.GetWindow(this) };
        w.ShowDialog();

        if (w.AppliedSens is { } sens)
        {
            TxtSens.Text = sens.ToString("0.####", CultureInfo.InvariantCulture);
            App.State.Status = $"灵敏度改成 {sens:0.####}：{Dpi:0} DPI 下 360° 要 {SourceAim.CmPer360(sens, Dpi):0.0} cm";
            RememberPrefs();
        }
        RefreshCalib();
    }

    void RefreshCalib()
    {
        double cm = SourceAim.CmPer360(Sens, Dpi);
        CalibInfo.Text = cm > 0
            ? $"现在这套设置（{Sens:0.###} @ {Dpi:0} DPI）= 转一圈 {cm:0.0} cm。觉得不顺手就进去转一圈，看着数字改。"
            : "先在上面填好灵敏度和 DPI。";
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
        public string Mode { get; init; } = "";
        public string Score { get; init; } = "";
    }

    void FillRounds()
    {
        HistoryList.ItemsSource = ScoreHistory.Recent(80).Select(r => new ScoreRow
        {
            Time = DateTime.TryParse(r.When, out var when)
                ? when.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : r.When,
            Mode = r.Mode,
            Score = r.Extra.Length > 0 ? r.Extra : $"{r.Cleared} 个 · 命中率 {r.HitRate:0}%",
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
        pr.DrillKind = DrillIndex;
        pr.CalibMultipliers = TxtCalibMult.Text;
        pr.CalibRounds = CalibRounds();
        pr.CalibNoAccel = ChkCalibNoAccel.IsChecked == true;
        pr.CalibPlan = (int)CalibPlan();
        pr.CalibMode = CalibMode();
        pr.DrillSeconds = DrillSecondsValue;
        pr.DrillRadiusPx = DrillRadiusValue;
        AppState.SavePrefs();
    }

    static double ParseD(string s, double d) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : d;
    static int ParseI(string s, int d) => int.TryParse(s, out var v) && v > 0 ? v : d;
}
