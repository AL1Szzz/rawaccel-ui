using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI.View;

/// <summary>
/// The blind curve comparison, one session: pick five variants of the player's own curve, run each
/// of them once through each of five drills in a shuffled order, then re-run the winner against the
/// original and say whether the difference is big enough to act on.
///
/// The values are hidden on purpose. A player who knows they are on "half acceleration" will aim
/// differently than one who does not, and the thing being measured is the aim, not the opinion. So
/// during the session a round is only ever "候选 3"; the real numbers appear in the report, and the
/// report is the first place anybody - including the code that draws this window - reads them back.
///
/// Two things this window is responsible for and must not hand off:
///   * the driver is left holding a candidate curve for the whole session, so it owns the restore on
///     every exit path (normal finish, abort, window close, exception);
///   * it owns the display mode for the whole session rather than per round, because 35 resolution
///     changes in a row is worse than one that stays put.
/// </summary>
public partial class CalibrationSessionWindow : Window
{
    public sealed class Options
    {
        public double RadiusPx = 22;
        public Display.Mode? Resolution;
        public double CrossArm = 9, CrossGap = 3;
        public string CrossColor = "#FFFFFFFF";
        public bool CrossDot = true;
        public string DeviceId = "";
        /// <summary>The in-game sensitivity, only used to draw the turn dial in step 01.</summary>
        public double GameSens = 1.5;
        public double[] Multipliers = AccelCandidates.DefaultMultipliers;
        public bool IncludeNoAccel = true;
        /// <summary>Uniform scales both axes together; Wrist pushes the axes apart (see AccelCandidates).</summary>
        public Plan Family = Plan.Uniform;
        /// <summary>0 = every round; a smaller number makes the session a demo you can actually finish.</summary>
        public int MaxRounds;
        /// <summary>Test hook: invent a plausible set of rounds and jump straight to the report.</summary>
        public bool DebugReport;
        /// <summary>Test hook: "round" parks the window on the round card so it can be rendered.</summary>
        public string DebugStage = "";
    }

    readonly Options opts;
    readonly CurvePush push = new();
    readonly List<AccelCandidates.Candidate> candidates = new();
    readonly List<CalibPlan.RoundResult> exploreResults = new();
    readonly List<CalibPlan.RoundResult> finalResults = new();

    List<CalibPlan.Round> rounds = new();
    int baseIndex;
    int roundPos;
    CalibPlan.Phase phase = CalibPlan.Phase.Explore;
    CalibPlan.Ranking? ranking;
    CalibPlan.Verdict? verdict;
    bool modeChanged, finished, aborting;
    int skipped;

    public ProfileDto? RecommendedProfile { get; private set; }
    /// <summary>Which profile the session actually swapped, so the caller patches that one.</summary>
    public string TestedProfile => push.SwappedProfileName;
    public string RecommendedLabel { get; private set; } = "";

    public CalibrationSessionWindow(Options opts)
    {
        this.opts = opts;
        InitializeComponent();
        Loaded += (_, _) => Begin();
    }

    // ---------- setup ----------

    void Begin()
    {
        var live = App.State.SelectedProfile;
        if (live == null)
        {
            StatusBar.Text = "界面里没有选中的曲线，先去曲线页选一条再开测。";
            BtnRound.IsEnabled = false;
            return;
        }

        candidates.AddRange(AccelCandidates.BuildFor(opts.Family, live, opts.Multipliers, opts.IncludeNoAccel));
        baseIndex = candidates.FindIndex(c => c.IsBase);
        if (baseIndex < 0) baseIndex = 0;

        string mode = (live.X.Mode ?? "").Trim().ToLowerInvariant();
        string knob = AccelCandidates.KnobName(mode);
        string what = opts.Family == Plan.Wrist
            ? "只调「手腕流强度」这一个数：它把水平的起始阈值往后推、垂直的往前推，两轴强度同时按倍率走"
            : $"只扫「{knob}」这一个参数（水平和垂直一起扫，不再只动水平那一轴）";
        IntroText.Text =
            $"{AccelCandidates.Describe(live, opts.Family)}\n\n" +
            $"拿你现在这条曲线（{mode}）做基准，{what}，其余原样复制，生成 {candidates.Count} 档候选" +
            $"（其中一档就是你现在这条，一档是完全不加加速的对照）。\n\n" +
            $"每档在下面五个项目上各打一轮，每轮 3 秒准备 + 15 秒计分，顺序打乱且相邻两轮不重复同一档。" +
            $"数值全程隐藏 —— 你只会看到「候选 N」，因为知道自己在打哪一档的人，打法会变。\n\n" +
            $"五个项目：{string.Join("、", CalibPlan.Items.Select(DrillInfo.Name))}。它们对曲线的用法不一样，" +
            $"一个项目看不出来的差别，五个项目一起看才有意义。\n\n" +
            $"第一步先确认你的手在这个灵敏度下能转多少度：转半圈，看看推了多远。它不参与评分，" +
            $"只是让你对后面的数字有个尺度。嫌麻烦可以跳过。";

        StatusBar.Text = DrivelessNote();

        if (opts.DebugReport) { Fabricate(); Finish(); return; }

        if (opts.DebugStage == "round")
        {
            rounds = CalibPlan.Explore(candidates.Count);
            phase = CalibPlan.Phase.Explore;
            IntroPanel.Visibility = Visibility.Collapsed;
            RoundCard.Visibility = Visibility.Visible;
            SetStep(Step2);
            RoundLast.Text = "上一轮（六目标）：清掉 4 组（30 个），平均 3.2 秒/组，命中率 91% · 跨距离一致性 0.78";
            ShowRound();
        }
    }

    /// <summary>
    /// Test hook. The report is the part of this window with the most ways to be wrong - dynamic
    /// columns, NaN handling, a verdict that changes shape - and it can only be reached after
    /// thirty-five rounds of actually playing. This invents the rounds so the report can be rendered
    /// and eyeballed in one step.
    /// </summary>
    void Fabricate()
    {
        var rng = new Random(4242);
        rounds = CalibPlan.Explore(candidates.Count);
        phase = CalibPlan.Phase.Explore;

        foreach (var r in rounds)
        {
            // candidate 2 (a mid multiplier) is made to win slightly, so the report shows a real
            // verdict rather than "your curve is already the best"
            double bias = r.CandidateIndex == 3 ? 1.18 : 1.0 - 0.04 * r.CandidateIndex;
            double skill = bias * (40 + rng.NextDouble() * 12);
            exploreResults.Add(new CalibPlan.RoundResult
            {
                Round = r,
                Skill = skill,
                Consistency = r.Item == DrillKind.Tracking ? double.NaN : Math.Clamp(0.55 + bias * 0.2 + rng.NextDouble() * 0.1, 0, 1),
                Detail = "（示意数据）",
            });
        }

        ranking = CalibPlan.Rank(exploreResults, candidates.Count);
        rounds = CalibPlan.Final(baseIndex, ranking.Leader);
        phase = CalibPlan.Phase.Final;
        foreach (var r in rounds)
        {
            double bias = r.CandidateIndex == ranking.Leader ? 1.12 : 1.0;
            finalResults.Add(new CalibPlan.RoundResult
            {
                Round = r,
                Skill = bias * (42 + rng.NextDouble() * 10),
                Consistency = r.Item == DrillKind.Tracking ? double.NaN : Math.Clamp(0.6 + bias * 0.2 + rng.NextDouble() * 0.08, 0, 1),
                Detail = "（示意数据）",
            });
        }
    }

    string DrivelessNote()
    {
        if (!App.State.DriverReachable) return "驱动现在连不上，候选写不进去，成绩会全部作废。先去确认驱动装好了。";
        return "";
    }

    // ---------- 01 ----------

    void OnTurnCheck(object sender, RoutedEventArgs e)
    {
        var w = new CalibrationWindow(new CalibrationWindow.Config
        {
            Sens = SensFromPrefs(),
            Dpi = DpiFromPrefs(),
            LapDeg = 180,
        });
        w.ShowDialog();
        bool moved = w.RawPackets > 0 && w.RawCounts != 0;
        TurnResult.Text = moved
            ? $"半圈推了 {Math.Abs(TurnMeasure.CmOf(w.RawCounts, DpiFromPrefs())):0.0} cm，" +
              $"也就是转一圈大约 {Math.Abs(TurnMeasure.CmOf(w.RawCounts, DpiFromPrefs())) * 2:0.0} cm。" +
              "记住这个尺度：后面报告里的差别，得放到它上面看才说得清。"
            : "没有转，或者没收到鼠标的原始输入。可以跳过，不影响后面的评分。";
    }

    void OnSkipTurn(object sender, RoutedEventArgs e) => StartExplore();

    double SensFromPrefs() => opts.GameSens > 0 ? opts.GameSens : 1.5;
    static double DpiFromPrefs() => AppState.Prefs.MouseDpi > 0 ? AppState.Prefs.MouseDpi : 800;

    // ---------- 02 / 03 ----------

    void StartExplore()
    {
        if (!App.State.DriverReachable)
        {
            StatusBar.Text = "驱动不可用，不能开测。";
            return;
        }
        if (!push.Capture())
        {
            StatusBar.Text = push.Error ?? "读不到驱动当前设置";
            return;
        }

        rounds = CalibPlan.Explore(candidates.Count);
        if (opts.MaxRounds > 0 && opts.MaxRounds < rounds.Count)
            rounds = RoundsKeepingEveryCandidate(rounds, opts.MaxRounds);

        phase = CalibPlan.Phase.Explore;
        roundPos = 0;

        if (opts.Resolution is { } m && Display.TrySet(m)) modeChanged = true;

        IntroPanel.Visibility = Visibility.Collapsed;
        RoundCard.Visibility = Visibility.Visible;
        ReportCard.Visibility = Visibility.Collapsed;
        SetStep(Step2);
        ShowRound();
    }

    /// <summary>
    /// Trims a session to fewer rounds without leaving a candidate untested: dropping whole rounds
    /// would silently drop a curve, and the report would then rank something that never ran.
    /// </summary>
    static List<CalibPlan.Round> RoundsKeepingEveryCandidate(List<CalibPlan.Round> all, int keep)
    {
        int perCandidate = Math.Max(1, keep / Math.Max(1, all.Select(r => r.CandidateIndex).Distinct().Count()));
        // filter the original list rather than concatenating the groups: the interleaved order is
        // the whole point, and grouping would hand back 5 rounds of candidate 1 then 5 of candidate 2
        var kept = new HashSet<CalibPlan.Round>(
            all.GroupBy(r => r.CandidateIndex).SelectMany(g => g.Take(perCandidate)));
        var ordered = all.Where(kept.Contains).ToList();
        return ordered.Count > 0 ? ordered : all.Take(keep).ToList();
    }

    void ShowRound()
    {
        if (roundPos >= rounds.Count) { AdvancePhase(); return; }

        var r = rounds[roundPos];
        int total = rounds.Count;
        RoundTitle.Text = phase == CalibPlan.Phase.Explore
            ? $"探索 · 第 {roundPos + 1} / {total} 轮"
            : $"决选 · 第 {roundPos + 1} / {total} 轮";
        RoundItem.Text = $"{DrillInfo.Name(r.Item)}：{DrillInfo.Hint(r.Item).Replace(" · Esc 退出", "")}";
        RoundBig.Text = $"{r.Seconds:0} 秒";
        RoundNote.Text = "3 秒准备后开始，打完自动回到这里，不用切换窗口。\n" +
                         $"本轮会用{candidates[r.CandidateIndex].BlindLabel} —— 哪一档、什么数值，全程不告诉你。";
        RoundProgress.Maximum = Math.Max(1, rounds.Count);
        RoundProgress.Value = roundPos + 1;
        BtnRound.IsEnabled = true;
        BtnRound.Content = "开始本轮";
    }

    async void OnNextRound(object sender, RoutedEventArgs e)
    {
        if (roundPos >= rounds.Count) return;
        var r = rounds[roundPos];
        BtnRound.IsEnabled = false;

        var cand = candidates[r.CandidateIndex];
        bool ok = await push.PushAsync(cand.Profile, opts.DeviceId);
        if (!ok)
        {
            Record(r, null, push.Error ?? "候选没能写进驱动");
            StatusBar.Text = push.Error ?? "候选没能写进驱动";
            roundPos++;
            ShowRound();
            return;
        }

        Hide();
        var cfg = new DrillWindow.Config
        {
            Kind = r.Item,
            Seconds = r.Seconds,
            RadiusPx = opts.RadiusPx,
            Alive = DrillInfo.DefaultAlive(r.Item),
            RepTarget = r.Item == DrillKind.FlashReset ? Math.Min(8, DrillInfo.DefaultReps(r.Item)) : 0,
            Seed = 1000 + roundPos * 37 + r.CandidateIndex,
            CrossArm = opts.CrossArm,
            CrossGap = opts.CrossGap,
            CrossColor = opts.CrossColor,
            CrossDot = opts.CrossDot,
            BlindLabel = cand.BlindLabel,
        };

        var w = new DrillWindow(cfg);
        w.Closed += (_, _) => Dispatcher.Invoke(() =>
        {
            Record(r, w.Run, w.Completed ? "" : "本轮提前结束，成绩不计入");
            Show();
            Activate();
            roundPos++;
            ShowRound();
        });
        w.Show();
        w.Activate();
    }

    void Record(CalibPlan.Round r, DrillRun? run, string problem)
    {
        var res = new CalibPlan.RoundResult
        {
            Round = r,
            Skill = run?.SkillScore ?? 0,
            // tracking never clicks, so there is no flick to measure; NaN says "no data" rather than
            // "consistency zero", which would otherwise drag every candidate's average down
            Consistency = run == null ? double.NaN
                          : run.Flick.Samples >= 2 ? run.Flick.Consistency : double.NaN,
            Detail = run?.Score() ?? "",
            Valid = run != null && problem.Length == 0,
            Problem = problem,
        };

        (phase == CalibPlan.Phase.Explore ? exploreResults : finalResults).Add(res);

        if (problem.Length > 0) skipped++;
        RoundLast.Text = run == null
            ? $"上一轮作废：{problem}"
            : $"上一轮（{DrillInfo.Name(r.Item)}）：{run.Score()}" +
              (run.Flick.Samples >= 2 ? $" · 跨距离一致性 {run.Flick.Consistency:0.00}" : "");
    }

    void AdvancePhase()
    {
        if (phase == CalibPlan.Phase.Explore)
        {
            ranking = CalibPlan.Rank(exploreResults, candidates.Count);
            int leader = ranking.Leader;
            rounds = CalibPlan.Final(baseIndex, CalibPlan.Opponent(ranking, baseIndex));
            if (opts.MaxRounds > 0 && opts.MaxRounds < rounds.Count) rounds = rounds.Take(opts.MaxRounds).ToList();
            phase = CalibPlan.Phase.Final;
            roundPos = 0;
            SetStep(Step3);
            int vs = CalibPlan.Opponent(ranking, baseIndex);
            StatusBar.Text = leader == vs
                ? $"探索阶段领先的是候选 {vs + 1}。接下来它和你的原值各跑 5 轮做确认。"
                : $"探索阶段你自己那条就领先，和它最接近的是候选 {vs + 1}。接下来两边各跑 5 轮做确认。";
            ShowRound();
            return;
        }

        Finish();
    }

    // ---------- 04 ----------

    void Finish()
    {
        finished = true;
        phase = CalibPlan.Phase.Done;
        ranking ??= CalibPlan.Rank(exploreResults, candidates.Count);
        verdict = CalibPlan.Decide(ranking, finalResults, baseIndex);

        SetStep(Step4);
        IntroPanel.Visibility = Visibility.Collapsed;
        RoundCard.Visibility = Visibility.Collapsed;
        ReportCard.Visibility = Visibility.Visible;

        VerdictText.Text = verdict.Headline;
        VerdictReasons.Text = string.Join("\n", verdict.Reasons);

        BuildItemTable(ranking);
        BuildReveal(ranking, verdict);
        CaveatList.Items.Clear();
        foreach (string c in verdict.Caveats)
            CaveatList.Items.Add(new TextBlock
            {
                Text = "· " + c,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });

        if (skipped > 0)
            CaveatList.Items.Add(new TextBlock
            {
                Text = $"· 有 {skipped} 轮被作废（提前退出或写入失败），这些轮次没有进入排名。",
                Foreground = (Brush)FindResource("WarningBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });

        int winner = verdict.WinnerIndex;
        RecommendedProfile = candidates[winner].Profile;
        RecommendedLabel = candidates[winner].Secret;
        BtnApply.IsEnabled = !verdict.WinnerIsBase;
        BtnApply.Content = verdict.WinnerIsBase ? "你现在的就是最优" : $"用候选 {winner + 1}";

        if (push.RestoreFailure != null)
            StatusBar.Text = "⚠ " + push.RestoreFailure + " —— 去驱动页手动应用一次你的曲线。";
        else
            StatusBar.Text = "驱动已经还原成你开测前的设置。";
    }

    void BuildItemTable(CalibPlan.Ranking ranking)
    {
        var table = new DataTable();
        table.Columns.Add("项目", typeof(string));
        for (int i = 0; i < candidates.Count; i++) table.Columns.Add("C" + i, typeof(string));
        table.Columns.Add("最优", typeof(string));

        foreach (var item in CalibPlan.Items)
        {
            if (!ranking.PerItem.TryGetValue(item, out var row) || row.Count == 0) continue;
            var cells = new object[table.Columns.Count];
            cells[0] = DrillInfo.Name(item);
            var score = row.ToDictionary(x => x.cand, x => x.score);
            for (int i = 0; i < candidates.Count; i++)
                cells[1 + i] = score.TryGetValue(i, out var v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";
            cells[table.Columns.Count - 1] = $"候选 {row[0].cand + 1}";
            table.Rows.Add(cells);
        }

        ItemGrid.Columns.Clear();
        ItemGrid.Columns.Add(new GridViewColumn { Header = "项目", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("项目") });
        for (int i = 0; i < candidates.Count; i++)
            ItemGrid.Columns.Add(new GridViewColumn
            {
                Header = "候选 " + (i + 1),
                Width = 78,
                DisplayMemberBinding = new System.Windows.Data.Binding("C" + i),
            });
        ItemGrid.Columns.Add(new GridViewColumn { Header = "最优", Width = 74, DisplayMemberBinding = new System.Windows.Data.Binding("最优") });
        ItemTable.ItemsSource = table.DefaultView;
    }

    void BuildReveal(CalibPlan.Ranking ranking, CalibPlan.Verdict verdict)
    {
        RevealList.Items.Clear();
        foreach (int i in ranking.Order)
        {
            var c = candidates[i];
            string rank = ranking.MeanRank.TryGetValue(i, out var mr) ? $"平均第 {mr:0.0} 名" : "";
            string cons = ranking.MeanConsistency.TryGetValue(i, out var mc) && !double.IsNaN(mc)
                ? $" · 一致性 {mc:0.00}" : "";
            string tag = i == baseIndex ? " · 你现在这条" : c.IsNoAccel ? " · 对照" : "";
            bool winner = i == verdict.WinnerIndex;

            RevealList.Items.Add(new TextBlock
            {
                Text = $"{c.BlindLabel}：{c.Secret}{tag} — {rank}{cons}" + (winner ? "  ← 领先" : ""),
                Foreground = (Brush)(winner ? FindResource("AccentBrush") : FindResource("TextSecondaryBrush")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }
    }

    void OnApply(object sender, RoutedEventArgs e)
    {
        if (RecommendedProfile == null) return;
        DialogResult = true;
        Close();
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnAbort(object sender, RoutedEventArgs e)
    {
        aborting = true;
        Close();
    }

    // ---------- teardown ----------

    void SetStep(Border on)
    {
        foreach (var b in new[] { Step1, Step2, Step3, Step4 })
            b.Style = (Style)(b == on ? FindResource("ChipOn") : FindResource("Chip"));
    }

    async void OnClosed(object? sender, EventArgs e)
    {
        if (modeChanged)
        {
            modeChanged = false;
            if (!Display.Restore() && !aborting)
                App.State.Status = "分辨率没能自动还原，去 Windows 显示设置里手动改回来。";
        }

        if (push.HasOriginal)
        {
            bool ok = await push.RestoreAsync();
            if (!ok && push.RestoreFailure != null)
                App.State.Status = "⚠ 曲线没能还原：" + push.RestoreFailure + " —— 去驱动页手动应用一次。";
        }
        push.Dispose();

        if (!finished && !aborting)
            App.State.Status = "校准提前结束，驱动已还原成开测前的设置。";
    }
}
