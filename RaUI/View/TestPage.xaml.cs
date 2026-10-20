using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI.View;

/// <summary>
/// Aim test page. It measures and logs; it does not judge. Tuning advice comes from the
/// assistant that reads a log, and comes back as a validated JSON patch which this page
/// previews and only then writes to the driver.
/// </summary>
public partial class TestPage : UserControl
{
    readonly List<Trial> trials = new();
    AppState.RoundContext? frozen;   // context captured BEFORE the round, never after
    FlickWindow? lastWindow;
    SessionRule? lastRule;
    string logPath = "";
    string roundId = "";
    string pendingPatchNote = "";    // stamped into the NEXT round so before/after is traceable
    PatchResult? patch;
    ProfileDto? beforePatch;         // for "revert"

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
            ShowSummary();
            FillRounds(selectNewest: true);
            if (string.IsNullOrEmpty(roundId))
            {
                var latest = TestLog.LatestRound();
                if (latest.Length > 0)
                {
                    roundId = System.IO.Path.GetFileNameWithoutExtension(latest);
                    logPath = latest;
                    LogInfo.Text = "上次运行的最新日志：" + latest;
                }
            }
        };
        TxtDistance.TextChanged += (_, _) => RefreshPlan();
        TxtRadius.TextChanged += (_, _) => RefreshPlan();
        CmbBalls.SelectionChanged += (_, _) => RefreshPlan();
        CmbMode.SelectionChanged += (_, _) => RefreshPlan();
        TxtTrials.TextChanged += (_, _) => RefreshPlan();
        TxtSeconds.TextChanged += (_, _) => RefreshPlan();
        CmbA.SelectionChanged += (_, _) => ShowComparison();
        CmbB.SelectionChanged += (_, _) => ShowComparison();
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
        PlanInfo.Text = $"{Balls} 个球同屏，{ModeLabel} · 点掉的跳到 {DistancePx:0}px 外 · 球半径 {RadiusPx:0}px · {DirName} · 种子 {Seed}。{fit}";
    }

    // ---------- run ----------

    void OnStart(object sender, RoutedEventArgs e) => Start(true);
    void OnStartKeepMode(object sender, RoutedEventArgs e) => Start(false);

    void Start(bool useCustomResolution)
    {
        RememberPrefs();
        trials.Clear();
        ResultList.Items.Clear();
        Flags.Text = "";
        lastRule = null;
        App.State.Speeds?.Clear();

        // freeze what is actually in the driver BEFORE the round, while nothing has moved yet
        frozen = null;
        try { frozen = App.State.CaptureRoundContext(DeviceId); }
        catch (Exception e) { frozen = null; App.State.Status = "本轮起点没能读取：" + e.Message; }

        roundId = TestLog.NewRoundId();
        var cfg = new FlickWindow.Config
        {
            AngleDeg = AppState.Prefs.TestAngleDeg,
            DistancePx = DistancePx,
            RadiusPx = RadiusPx,
            TargetCount = Balls,
            Mode = Mode,
            Trials = BallGoal,
            Seconds = Seconds,
            Sensitivity = Sens,
            Dpi = Dpi,
            DirectionMode = DirMode,
            Seed = Seed,
            DeviceId = DeviceId,
            CrossArm = CrossArm,
            CrossGap = CrossGap,
            CrossColor = CrossColor,
            CrossDot = ChkCrossDot.IsChecked == true,
            Resolution = useCustomResolution && AppState.Prefs.UseCustomFullscreenMode ? PlannedMode() : null,
        };

        var w = new FlickWindow(cfg) { Owner = Window.GetWindow(this) };
        w.TrialDone += t => Dispatcher.Invoke(() =>
        {
            trials.Add(t);
            AddRow(t);
            ShowSummary(w);
        });
        w.SessionDone += list => Dispatcher.Invoke(() =>
        {
            foreach (var t in list) if (!trials.Contains(t)) trials.Add(t);
            lastWindow = w;
            lastRule = w.Rule;
            ShowSummary(w);
            RefreshPlan();
            WriteLog();
            FillRounds(selectNewest: true);
        });
        w.Closed += (_, _) => { try { Display.Restore(); } catch { } };
        w.Show();
        w.Activate();
    }

    void AddRow(Trial t)
    {
        var brush = new SolidColorBrush(t.Hit ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0xFF, 0x99, 0xA4));
        ResultList.Items.Add(new TextBlock
        {
            Text = $"{trials.Count,3}  {(t.Hit ? "点掉" : (t.BallUnderCursor ? "偏了" : "空点"))}  距离 {t.DistancePx,5:0}px  " +
                   $"径向 {t.RadialPct,+6:0.0}%  切向 {t.TangentialPx,+6:0.0}px  误差 {t.ErrorPx,4:0}px  " +
                   $"{t.EndMs - t.StartMs,4:0}ms  " +
                   (t.SampleCount > 0 ? $"{t.PeakSpeed,5:0.0}c/ms" : "  n/a") +
                   (t.TelemetryTruncated ? " 截断" : ""),
            Foreground = brush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 1),
        });
        ResultList.ScrollIntoView(ResultList.Items[^1]);
    }

    // ---------- log ----------

    void WriteLog()
    {
        if (frozen == null || trials.Count == 0) return;
        try
        {
            var rec = App.State.Speeds;
            var sp = rec?.Percentiles(byComponent: frozen.Profile.speed_processor_args.whole == 0)
                     ?? (0, 0, 0, 0, 0, 0);
            logPath = TestLog.RoundPath(roundId);
            TestLog.Write(logPath, new RoundData
            {
                RoundId = roundId,
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DriverVersion = frozen.DriverVersion,
                ProfileName = frozen.ProfileName,
                ConfigSha = frozen.ConfigSha,
                ConfigSource = frozen.Source,
                ProfileJson = frozen.ProfileJson,
                Profile = frozen.Profile,
                DeviceId = frozen.DeviceId,
                DeviceName = frozen.DeviceName,
                Sensitivity = Sens,
                MouseDpi = Dpi,
                PollRate = AppState.Prefs.ChartPollRate,
                Mode = ModeLabel,
                Score = lastRule?.Score() ?? "",
                SessionMs = lastRule?.ElapsedMs ?? 0,
                TargetDistancePx = DistancePx,
                RadiusPx = RadiusPx,
                TargetCount = Balls,
                Seed = Seed,
                Direction = DirName,
                Resolution = lastWindow?.UsedResolution ?? "",
                Trials = new List<Trial>(trials),
                OutputSpeed = (sp.p50, sp.p75, sp.p95, sp.peak),
                TelemetryNote = (lastWindow?.TelemetryNote ?? "") + (lastWindow?.DisplayWarning ?? "") +
                                (lastWindow != null ? $" | end={lastWindow.EndReason}" : ""),
                AppliedPatch = pendingPatchNote,
            });
            LogInfo.Text = "本轮日志：" + logPath;
        }
        catch (Exception e)
        {
            App.State.Status = "写日志失败：" + e.Message;
            LogInfo.Text = "写日志失败：" + e.Message;
        }
    }

    void ShowSummary(FlickWindow? w = null)
    {
        if (trials.Count == 0)
        {
            Summary.Text = "还没测。点“开始全屏测试”，倒计时结束后连着点球。";
            Flags.Text = "";
            return;
        }
        var st = RoundStats.Compute(trials, RadiusPx, DistancePx);
        var rule = w?.Rule ?? lastRule;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"点掉 {st.BallsCleared} 球 / {st.Clicks} 次点击   命中 {st.HitRate:0.0}%   " +
                      (st.KillsPerMinute > 0 ? $"{st.KillsPerMinute:0.0} 球/分钟   " : ""));
        if (rule != null)
            sb.AppendLine(rule.Mode == TestMode.Balls
                ? $"目标 {rule.BallGoal} 球，还剩 {rule.RemainingBalls}，用时 {rule.ElapsedMs / 1000.0:0.0}s"
                : $"限时 {rule.TimeLimitMs / 1000.0:0}s，已用 {rule.ElapsedMs / 1000.0:0.0}s，剩 {rule.RemainingMs / 1000.0:0.0}s");
        sb.AppendLine($"径向中位 {st.RadialMedian:+0.0;-0.0}%（MAE {st.RadialMae:0.0}%、标准差 {st.RadialSd:0.0}%）   " +
                      $"过头 {st.OvershootCount:0} / 正中 {st.OnTargetCount:0} / 不足 {st.UndershootCount:0}");
        sb.Append($"切向中位 {st.TangMedianAbs:0.0}px   单发用时中位 {st.TimeMedian:0}ms   距离 {st.DistanceMin:0}~{st.DistanceMax:0}px   " +
                  $"速度采到 {st.MeasuredCount}/{st.Count}" +
                  (w != null && !string.IsNullOrEmpty(w.UsedResolution) ? $"   {w.UsedResolution}" : ""));
        if (st.PrimaryCount > 0)
            sb.Append($"\n开环：最远冲到 {st.MaxExtentMedian:0}%（飞过球面的 {st.OvershootCount2}/{st.PrimaryCount} 次）、" +
                      $"入球用时 {st.TimeToTargetMedian:0}ms；第一下停点 {st.PrimaryRadialMedian:+0.0;-0.0}%" +
                      (st.HitExtentMedian > 0 && st.HitExtentMedian < 95 ? "（路径重建有丢失，最远%只能当下限）" : ""));
        Summary.Text = sb.ToString();
        Flags.Text = st.Quality.Count == 0 ? "本轮没有质量警告。"
            : "数据质量警告：\n  · " + string.Join("\n  · ", st.Quality);
    }

    // ---------- round picker / A-B comparison ----------

    void FillRounds(bool selectNewest)
    {
        var rounds = TestLog.Rounds(60);
        string keepA = (CmbA.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string keepB = (CmbB.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

        CmbA.Items.Clear();
        CmbB.Items.Clear();
        CmbA.Items.Add(new ComboBoxItem { Content = "（不选）", Tag = "" });
        CmbB.Items.Add(new ComboBoxItem { Content = "（不选）", Tag = "" });
        foreach (var r in rounds)
        {
            CmbA.Items.Add(new ComboBoxItem { Content = r.Label(), Tag = r.File });
            CmbB.Items.Add(new ComboBoxItem { Content = r.Label(), Tag = r.File });
        }
        if (rounds.Count == 0) { Compare.Text = "logs\\ 里还没有 v3 日志。"; return; }

        void Pick(ComboBox box, string keep, int fallbackIndex)
        {
            foreach (var i in box.Items.OfType<ComboBoxItem>())
                if ((string)(i.Tag ?? "") == keep && keep.Length > 0) { i.IsSelected = true; return; }
            box.SelectedIndex = fallbackIndex;
        }
        if (selectNewest)
        {
            // newest becomes B (the round to copy), the one before it becomes A (the baseline)
            CmbB.SelectedIndex = 1;
            CmbA.SelectedIndex = rounds.Count > 1 ? 2 : 0;
        }
        else
        {
            Pick(CmbA, keepA, rounds.Count > 1 ? 2 : 0);
            Pick(CmbB, keepB, 1);
        }
    }

    void OnUseBAsCurrent(object sender, RoutedEventArgs e)
    {
        var b = (CmbB.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        if (b.Length == 0) { App.State.Status = "先在 B 里选一轮"; return; }
        roundId = System.IO.Path.GetFileNameWithoutExtension(b);
        logPath = b;
        LogInfo.Text = "已把 B 作为当前轮：" + System.IO.Path.GetFileName(b);
        App.State.Status = "粘贴 AI 方案时会按这一轮校验 round";
    }

    void OnCopySelected(object sender, RoutedEventArgs e)
    {
        var f = (CmbB.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        try
        {
            if (f.Length == 0 || !System.IO.File.Exists(f)) { App.State.Status = "先在 B 里选一轮日志"; return; }
            Clipboard.SetText(System.IO.File.ReadAllText(f));
            App.State.Status = "已复制 " + System.IO.Path.GetFileName(f) + "，直接贴给 AI";
        }
        catch (Exception ex) { App.State.Status = "复制失败：" + ex.Message; }
    }

    void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        var f = (CmbB.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        try
        {
            if (f.Length == 0 || !System.IO.File.Exists(f)) { OpenDir(); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = f, UseShellExecute = true });
        }
        catch (Exception ex) { App.State.Status = "打不开日志：" + ex.Message; }
    }

    void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try { OpenDir(); }
        catch (Exception ex) { App.State.Status = "打不开目录：" + ex.Message; }
    }

    void OpenDir()
    {
        System.IO.Directory.CreateDirectory(TestLog.LogDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = TestLog.LogDir, UseShellExecute = true });
    }

    void ShowComparison()
    {
        if (CmbA == null || CmbB == null || Compare == null) return;   // fires during InitializeComponent
        var fa = (CmbA.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var fb = (CmbB.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        if (fa.Length == 0 || fb.Length == 0 || fa == fb)
        {
            Compare.Text = "选两轮（A 当基准、B 当对比）就能看到差值。";
            return;
        }
        var a = TestLog.ReadSummary(fa);
        var b = TestLog.ReadSummary(fb);
        if (a == null || b == null) { Compare.Text = "这两轮里没有可读的机读段（v3 之前的日志没有）。"; return; }
        a.File = System.IO.Path.GetFileName(fa);
        b.File = System.IO.Path.GetFileName(fb);
        Compare.Text = string.Join("\n", TestLog.Compare(a, b));
    }

    // ---------- AI patch ----------

    void OnPaste(object sender, RoutedEventArgs e)
    {
        try { TxtPatch.Text = Clipboard.GetText(); }
        catch (Exception ex) { App.State.Status = "剪贴板读不了：" + ex.Message; }
    }

    void OnValidate(object sender, RoutedEventArgs e)
    {
        var p = App.State.SelectedProfile;
        if (p == null) { PatchNote.Text = "先选一个 profile。"; return; }
        patch = AiPatch.Parse(TxtPatch.Text, App.State.Doc, p.Name,
                              string.IsNullOrEmpty(roundId) ? null : roundId);
        BtnConfirm.IsEnabled = patch.Ok;
        PatchDiff.Text = patch.Describe();
        if (!patch.Ok)
        {
            BtnConfirm.IsEnabled = false;
            App.State.Status = "方案被拒绝，驱动没有动";
            PatchNote.Text = "被拒绝的原因见上面；驱动和配置文件都没动。";
        }
        else App.State.Status = "方案已通过校验，确认后才写驱动";
    }

    async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var p = App.State.SelectedProfile;
        if (patch == null || !patch.Ok || patch.PreviewProfile == null || p == null) return;
        BtnConfirm.IsEnabled = false;
        BtnRevert.IsEnabled = false;
        beforePatch = p.Clone();

        var res = await App.State.ApplyProfileAsync(patch.PreviewProfile, p.Name, TxtPatch.Text.Trim());
        PatchNote.Text = res.Applied
            ? "已写入驱动并回读一致。下一轮日志会写明“本轮之前应用了这些改动”。"
            : "没写成，驱动保持原样：" + res.Message;
        if (res.AuditFile != null) PatchNote.Text += "\n审计记录：" + res.AuditFile;
        if (res.Applied)
        {
            pendingPatchNote = "round " + roundId + " 的方案已确认写入：\n" +
                               string.Join("\n", patch.Changes.Select(c => "  " + c.Path + ": " + c.Old + " → " + c.New)) +
                               (string.IsNullOrWhiteSpace(patch.Note) ? "" : "\n  note: " + patch.Note);
        }
        BtnRevert.IsEnabled = res.Applied && beforePatch != null;
        BtnConfirm.IsEnabled = !res.Applied;
    }

    async void OnRevert(object sender, RoutedEventArgs e)
    {
        var p = App.State.SelectedProfile;
        if (beforePatch == null || p == null) return;
        BtnRevert.IsEnabled = false;
        var res = await App.State.ApplyProfileAsync(beforePatch, beforePatch.Name, "(revert) 撤回上一次确认的改动");
        PatchNote.Text = res.Applied
            ? "已退回改动前的配置；下一轮日志会写明退回了什么。"
            : "退回失败：" + res.Message;
        if (res.Applied)
        {
            pendingPatchNote = "撤回了上一轮之后应用的改动，回到 profile " + beforePatch.Name + " 的旧参数";
            beforePatch = null;
        }
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
