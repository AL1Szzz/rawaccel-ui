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
        /// <summary>False = one sweep of five variants. True = five narrowing stages, each around the
        /// last winner, then one head to head between the result and the curve you started on.</summary>
        public bool Ladder = true;
        /// <summary>Drills per ladder stage. Five stages of three put every drill on three stages.</summary>
        public int ItemsPerStage = 3;
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
    /// <summary>The curve the session started from, kept so the last head-to-head can be against it.</summary>
    ProfileDto? startProfile;
    readonly List<CalibPlan.RoundResult> exploreResults = new();
    readonly List<CalibPlan.RoundResult> finalResults = new();

    List<CalibPlan.Round> rounds = new();
    int baseIndex;
    int roundPos;

    // ladder state: the curve carried into the next stage, and the results of the stage in progress
    List<CalibLadder.Stage> stages = new();
    readonly List<CalibPlan.RoundResult> stageResults = new();
    /// <summary>
    /// The five slots of the stage that just ended. Kept because the head to head replaces the
    /// candidate list with two curves, and a report that never ran a head to head still has to be
    /// able to show what its last stage actually compared.
    /// </summary>
    List<AccelCandidates.Candidate> lastStageCandidates = new();
    readonly List<int> exploreStage = new();
    readonly List<string> stageNotes = new();
    ProfileDto? ladderCentre;
    /// <summary>How far the centre has moved from the curve the session started on, for the recap.</summary>
    double ladderScale = 1.0;
    int stagePos;
    int roundsDone;
    int roundsTotal;
    CalibPlan.Ranking? lastStageRanking;
    CalibPlan.Phase phase = CalibPlan.Phase.Explore;
    CalibPlan.Ranking? ranking;
    CalibPlan.Verdict? verdict;
    bool modeChanged, finished, aborting;
    int skipped;
    WindowState prevState = WindowState.Normal;

    public ProfileDto? RecommendedProfile { get; private set; }
    /// <summary>Which profile the session actually swapped, so the caller patches that one.</summary>
    public string TestedProfile => push.SwappedProfileName;
    public string RecommendedLabel { get; private set; } = "";

    /// <summary>
    /// Whether the player took the result. This is a field and not DialogResult on purpose: the
    /// session is shown with ShowDialog, but a ShowDialog window that is ever hidden stops being a
    /// dialog, and setting DialogResult afterwards throws InvalidOperationException - which is
    /// exactly how "apply" used to kill the process at the end of a finished session. The window
    /// applies the result itself, so a caller that missed the return value still loses nothing.
    /// </summary>
    public bool Accepted { get; private set; }

    /// <summary>Set when the result was also stored under 我的方案.</summary>
    public bool SavedAsPlan { get; private set; }

    /// <summary>
    /// The session as it goes, written to disk after every round. The rounds used to live only in
    /// the lists above, so a crash on the report screen took the whole session with it.
    /// </summary>
    readonly CalibArchive.Session archive = new() { Id = CalibArchive.NewId() };
    string archivePath = "";
    bool archiveWarned;
    string archiveError = "";

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

        startProfile = live;
        archive.Family = (opts.Family == Plan.Wrist ? "手腕流分轴" : "两轴同步") + (opts.Ladder ? " · 五轮递进" : "");
        archive.ProfileName = live.Name ?? "";
        archive.BaseLabel = AccelCandidates.Describe(live, opts.Family);
        archive.BaseJson = CalibArchive.Embedded(live);

        string mode = (live.X.Mode ?? "").Trim().ToLowerInvariant();
        string knob = AccelCandidates.KnobName(mode);
        string what = opts.Family == Plan.Wrist
            ? "只调「手腕流强度」这一个数：它把水平的起始阈值往后推、垂直的往前推，两轴强度同时按倍率走"
            : $"只扫「{knob}」这一个参数（水平和垂直一起扫，不再只动水平那一轴）";
        string all = string.Join("、", CalibPlan.Items.Select(DrillInfo.Name));

        if (opts.Ladder)
        {
            stages = CalibLadder.Plan(CalibLadder.Steps.Length, opts.ItemsPerStage);
            ladderCentre = live;
            roundsTotal = CalibLadder.RoundCount(stages);
            int finalRounds = CalibPlan.Items.Length * 2;
            Step2Text.Text = $"02 递进 {roundsTotal - finalRounds} 轮";
            Step3Text.Text = $"03 决选 {finalRounds} 轮";

            IntroText.Text =
                $"{AccelCandidates.Describe(live, opts.Family)}\n\n" +
                $"拿你现在这条曲线（{mode}）当起点，{what}。五轮递进，一轮比一轮收窄：\n\n" +
                string.Join("\n", stages.Select((s, i) =>
                    $"  第 {i + 1} 轮（{s.Label}）：× {string.Join("  ", s.Multipliers.Select(m => m.ToString("0.##", CultureInfo.InvariantCulture)))}")) +
                "\n\n" +
                $"每轮五档、每档跑 {opts.ItemsPerStage} 个项目（每轮 3 秒准备 + 15 秒计分），" +
                $"总共 {roundsTotal} 轮约 {Math.Ceiling(roundsTotal * 18 / 60.0):0} 分钟。" +
                $"项目在五轮之间轮换，五个项目最后每个都跑过三次，不会偏科。\n\n" +
                $"某一轮如果分不出高下，中心就不动 —— 宁可停在原地，也不能让一次手热的噪声把你带偏。" +
                $"最后一轮结束，挑出来的那条会和你出发时这条正面比一轮，赢不下来就说\"先别改\"。\n\n" +
                $"五个项目：{all}。数值全程隐藏，你只会看到「候选 N」。\n\n" +
                $"第一步先确认你的手在这个灵敏度下能转多少度：转半圈，看看推了多远。它不参与评分，嫌麻烦可以跳过。";
        }
        else
        {
            candidates.AddRange(AccelCandidates.BuildFor(opts.Family, live, opts.Multipliers, opts.IncludeNoAccel));
            baseIndex = candidates.FindIndex(c => c.IsBase);
            if (baseIndex < 0) baseIndex = 0;
            archive.Candidates = candidates.Select((c, i) => new CalibArchive.CandidateRow
            {
                Index = i,
                Blind = c.BlindLabel,
                Secret = c.Secret,
                IsBase = c.IsBase,
                IsNoAccel = c.IsNoAccel,
            }).ToList();
            roundsTotal = candidates.Count * CalibPlan.Items.Length + CalibPlan.Items.Length * 2;
            Step2Text.Text = $"02 探索 {candidates.Count * CalibPlan.Items.Length} 轮";
            Step3Text.Text = $"03 决选 {CalibPlan.Items.Length * 2} 轮";

            IntroText.Text =
                $"{AccelCandidates.Describe(live, opts.Family)}\n\n" +
                $"拿你现在这条曲线（{mode}）做基准，{what}，其余原样复制，生成 {candidates.Count} 档候选" +
                $"（其中一档就是你现在这条，一档是完全不加加速的对照）。\n\n" +
                $"每档在下面五个项目上各打一轮，每轮 3 秒准备 + 15 秒计分，顺序打乱且相邻两轮不重复同一档。" +
                $"数值全程隐藏 —— 你只会看到「候选 N」，因为知道自己在打哪一档的人，打法会变。\n\n" +
                $"五个项目：{all}。它们对曲线的用法不一样，一个项目看不出来的差别，五个项目一起看才有意义。\n\n" +
                $"第一步先确认你的手在这个灵敏度下能转多少度：转半圈，看看推了多远。它不参与评分，" +
                $"只是让你对后面的数字有个尺度。嫌麻烦可以跳过。";
        }

        StatusBar.Text = DrivelessNote();

        if (opts.DebugReport) { Fabricate(); Finish(); return; }

        if (opts.DebugStage == "round")
        {
            IntroPanel.Visibility = Visibility.Collapsed;
            RoundCard.Visibility = Visibility.Visible;
            SetStep(Step2);
            RoundLast.Text = "上一轮（六目标）：清掉 4 组（30 个），平均 3.2 秒/组，命中率 91% · 跨距离一致性 0.78";
            // the ladder has no candidate list until a stage starts one, so it has to build stage 1
            // here; the single sweep only needs its rounds scheduled
            if (opts.Ladder) { StartStage(); return; }
            rounds = CalibPlan.Explore(candidates.Count);
            phase = CalibPlan.Phase.Explore;
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
        if (opts.Ladder) { FabricateLadder(); return; }

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

    /// <summary>
    /// The ladder version of the invented session. It walks the real stage machinery rather than
    /// copying it, so a stage that stops moving the centre, or a head to head that declines to
    /// recommend, shows up here the same way it would after twenty-five minutes of playing.
    ///
    /// The story it tells: three stages find something faster, then the last two cannot separate
    /// their slots and leave it alone - which is the common case and the one worth looking at.
    /// </summary>
    void FabricateLadder()
    {
        var rng = new Random(4242);
        // 2 is the centre slot, i.e. "no change wins". The "nomove" hook makes every stage tie, so
        // the report path where the centre never moves - no head to head, nothing to apply - can be
        // looked at without playing twenty-five minutes of drills that all tie.
        int[] pick = opts.DebugStage == "nomove" ? new[] { 2, 2, 2, 2, 2 } : new[] { 3, 3, 3, 2, 2 };

        stages = CalibLadder.Plan(CalibLadder.Steps.Length, opts.ItemsPerStage);
        ladderCentre = startProfile;
        if (ladderCentre == null) return;

        for (int s = 0; s < stages.Count; s++)
        {
            stagePos = s;
            var stage = stages[s];

            candidates.Clear();
            candidates.AddRange(AccelCandidates.BuildFor(opts.Family, ladderCentre, stage.Multipliers, includeNoAccel: false));
            lastStageCandidates = candidates.ToList();
            archive.Candidates = candidates.Select((c, i) => new CalibArchive.CandidateRow
            {
                Index = i, Blind = c.BlindLabel, Secret = c.Secret, IsBase = i == candidates.Count / 2,
            }).ToList();

            rounds = CalibPlan.Schedule(Enumerable.Range(0, candidates.Count), stage.Items, seed: 1000 + stagePos * 131);
            stageResults.Clear();

            foreach (var r in rounds)
            {
                // the slot this stage is meant to find wins by a margin the 3% gate can see; the
                // others fall off with their distance from the centre, so the centre itself looks
                // like an ordinary round rather than a planted loser
                double bias = 1.0 + (r.CandidateIndex == pick[s] ? 0.16 : 0.0) - 0.03 * Math.Abs(r.CandidateIndex - 2);
                var res = new CalibPlan.RoundResult
                {
                    Round = r,
                    Skill = bias * (40 + rng.NextDouble() * 12),
                    Consistency = r.Item == DrillKind.Tracking ? double.NaN
                                  : Math.Clamp(0.55 + bias * 0.2 + rng.NextDouble() * 0.1, 0, 1),
                    Detail = "（示意数据）",
                };
                stageResults.Add(res);
                exploreResults.Add(res);
                exploreStage.Add(stagePos);
            }

            FinishStage();
        }

        StartFinal();
        foreach (var r in rounds)
        {
            double bias = r.CandidateIndex == 1 ? 1.12 : 1.0;
            finalResults.Add(new CalibPlan.RoundResult
            {
                Round = r,
                Skill = bias * (42 + rng.NextDouble() * 10),
                Consistency = r.Item == DrillKind.Tracking ? double.NaN
                              : Math.Clamp(0.6 + bias * 0.2 + rng.NextDouble() * 0.08, 0, 1),
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

        if (opts.Resolution is { } m && Display.TrySet(m)) modeChanged = true;

        IntroPanel.Visibility = Visibility.Collapsed;
        RoundCard.Visibility = Visibility.Visible;
        ReportCard.Visibility = Visibility.Collapsed;
        SetStep(Step2);

        if (opts.Ladder) { StartStage(); return; }

        rounds = CalibPlan.Explore(candidates.Count);
        if (opts.MaxRounds > 0 && opts.MaxRounds < rounds.Count)
            rounds = RoundsKeepingEveryCandidate(rounds, opts.MaxRounds);

        phase = CalibPlan.Phase.Explore;
        roundPos = 0;
        ShowRound();
    }

    /// <summary>
    /// One ladder stage: five slots around the curve carried in from the previous stage, on this
    /// stage's three drills. The centre slot is that curve itself - it is re-tested every stage, so a
    /// centre that only won on noise gets another chance to be beaten instead of being locked in.
    /// </summary>
    void StartStage()
    {
        var stage = stages[stagePos];
        var centre = ladderCentre ?? startProfile;
        if (centre == null) { StatusBar.Text = "没有可比较的曲线。"; return; }

        candidates.Clear();
        candidates.AddRange(AccelCandidates.BuildFor(opts.Family, centre, stage.Multipliers, includeNoAccel: false));
        lastStageCandidates = candidates.ToList();
        // the archive is rewritten every stage: each stage renumbers its five slots, so a session
        // recovered from disk has to carry the labels that were current when it stopped
        archive.Candidates = candidates.Select((c, i) => new CalibArchive.CandidateRow
        {
            Index = i, Blind = c.BlindLabel, Secret = c.Secret, IsBase = i == candidates.Count / 2,
        }).ToList();

        rounds = CalibPlan.Schedule(Enumerable.Range(0, candidates.Count), stage.Items, seed: 1000 + stagePos * 131);
        if (opts.MaxRounds > 0 && opts.MaxRounds < rounds.Count)
            rounds = RoundsKeepingEveryCandidate(rounds, opts.MaxRounds);

        phase = CalibPlan.Phase.Explore;
        roundPos = 0;
        stageResults.Clear();
        ShowRound();
    }

    /// <summary>
    /// The last step: whatever the ladder landed on, against the curve the player walked in with.
    /// Five stages of narrowing is still five stages of fifteen-second rounds, so the result has to
    /// clear the same bar as any other recommendation before it is allowed to say "change".
    /// </summary>
    void StartFinal()
    {
        var centre = ladderCentre ?? startProfile;
        var start = startProfile;
        if (centre == null || start == null) { Finish(); return; }

        candidates.Clear();
        candidates.Add(new AccelCandidates.Candidate
        {
            Secret = "你出发时这条",
            BlindLabel = "候选 1",
            IsBase = true,
            Profile = start,
        });
        candidates.Add(new AccelCandidates.Candidate
        {
            Secret = AccelCandidates.Describe(centre, opts.Family),
            BlindLabel = "候选 2",
            Profile = centre,
        });
        archive.Candidates = candidates.Select((c, i) => new CalibArchive.CandidateRow
        {
            Index = i, Blind = c.BlindLabel, Secret = c.Secret, IsBase = c.IsBase,
        }).ToList();

        bool same = ConfigHash.Of(start) == ConfigHash.Of(centre);
        if (same)
        {
            // every stage kept the centre: there is nothing to compare it with, and running ten more
            // rounds of "this versus this" would only produce a guaranteed tie
            stageNotes.Add("五轮都没有找到更合适的，出发时那条就是最后的结果。");
            Finish();
            return;
        }

        rounds = CalibPlan.Schedule(new[] { 0, 1 }, CalibPlan.Items, seed: 771, phase: CalibPlan.Phase.Final);
        phase = CalibPlan.Phase.Final;
        baseIndex = 0;
        roundPos = 0;
        StatusBar.Text = "最后一步：挑出来的这条和你出发时这条，每个项目各打一轮。";
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
        RoundTitle.Text = phase == CalibPlan.Phase.Final
            ? $"决选 · 第 {roundPos + 1} / {total} 轮"
            : opts.Ladder
                ? $"第 {stagePos + 1} / {stages.Count} 轮（{stages[stagePos].Label}）· 第 {roundPos + 1} / {total} 轮"
                : $"探索 · 第 {roundPos + 1} / {total} 轮";
        RoundItem.Text = $"{DrillInfo.Name(r.Item)}：{DrillInfo.Hint(r.Item).Replace(" · Esc 退出", "")}";
        RoundBig.Text = $"{r.Seconds:0} 秒";
        RoundNote.Text = "3 秒准备后开始，打完自动回到这里，不用切换窗口。\n" +
                         $"本轮会用{candidates[r.CandidateIndex].BlindLabel} —— 哪一档、什么数值，全程不告诉你。";
        RoundProgress.Maximum = Math.Max(1, roundsTotal);
        RoundProgress.Value = Math.Min(roundsTotal, roundsDone + 1);
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

        // Minimized, never Hide(). This window is shown with ShowDialog, and hiding a dialog window
        // ends the dialog right there: ShowDialog returns immediately and the window stops counting
        // as one, so the "apply" button at the end would throw on DialogResult and take the app
        // with it. Minimizing keeps the dialog alive; the drill window is topmost and covers it.
        prevState = WindowState;
        WindowState = WindowState.Minimized;
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
            WindowState = prevState;
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

        bool isFinal = phase == CalibPlan.Phase.Final;
        if (isFinal)
        {
            finalResults.Add(res);
        }
        else
        {
            exploreResults.Add(res);
            // which stage an explore round came from, kept alongside it: the ladder's five stages all
            // look alike in the results list, and without this they cannot be told apart later
            exploreStage.Add(opts.Ladder ? stagePos : -1);
            if (opts.Ladder) stageResults.Add(res);
        }
        roundsDone++;
        SaveArchive();

        if (problem.Length > 0) skipped++;
        RoundLast.Text = run == null
            ? $"上一轮作废：{problem}"
            : $"上一轮（{DrillInfo.Name(r.Item)}）：{run.Score()}" +
              (run.Flick.Samples >= 2 ? $" · 跨距离一致性 {run.Flick.Consistency:0.00}" : "");
    }

    /// <summary>
    /// Writes the session as it stands. Called after every round rather than at the end, because a
    /// session that ends in a crash still has to be findable afterwards.
    /// </summary>
    void SaveArchive()
    {
        // Offscreen renders are not sessions; they must not leave files behind.
        if (opts.DebugReport || opts.DebugStage.Length > 0) return;

        var rows = new List<CalibArchive.RoundRow>(exploreResults.Count + finalResults.Count);
        for (int i = 0; i < exploreResults.Count; i++)
            rows.Add(Row(exploreResults[i], i < exploreStage.Count ? exploreStage[i] : -1));
        foreach (var r in finalResults) rows.Add(Row(r, -1));
        archive.Rounds = rows;

        static CalibArchive.RoundRow Row(CalibPlan.RoundResult r, int stage) => new()
        {
            Phase = r.Round.Phase == CalibPlan.Phase.Final ? "决选" : "探索",
            Item = DrillInfo.Name(r.Round.Item),
            Candidate = r.Round.CandidateIndex,
            Stage = stage,
            Skill = r.Skill,
            Consistency = double.IsNaN(r.Consistency) ? null : r.Consistency,
            Detail = r.Detail,
            Valid = r.Valid,
            Problem = r.Problem,
        };
        archive.StageNotes = stageNotes.ToList();
        archive.RoundsPlanned = roundsTotal;

        string? file = CalibArchive.Save(archive, out string error);
        if (file != null) { archivePath = file; return; }
        // Remembered, not shown here: the status bar is rewritten a moment later by whatever called
        // this, and a warning that gets overwritten is worse than one that rides along with the
        // report's own caveats.
        if (!archiveWarned) { archiveWarned = true; archiveError = error; }
    }

    void AdvancePhase()
    {
        if (opts.Ladder && phase == CalibPlan.Phase.Explore) { AdvanceStage(); return; }

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

    /// <summary>
    /// One ladder stage ends. The centre only moves when the stage could actually tell the slots
    /// apart; otherwise it stays put and the next, narrower stage starts from the same place. A
    /// centre moved on noise would drag every remaining stage into the wrong neighbourhood, and they
    /// would then narrow down very precisely onto the wrong number.
    /// </summary>
    void AdvanceStage()
    {
        FinishStage();
        if (stagePos < stages.Count) { SetStep(Step2); StartStage(); }
        else { SetStep(Step3); StartFinal(); }
    }

    void FinishStage()
    {
        var stage = stages[stagePos];
        int centreSlot = candidates.Count / 2;

        lastStageRanking = CalibPlan.Rank(stageResults, candidates.Count);
        int leader = lastStageRanking.Leader;
        bool moved = CalibLadder.Improved(stageResults, centreSlot, leader);

        // The stage multiplier is relative to where this stage started, so it is reported as a step
        // ("30% faster than last round") with the running total beside it. Quoting the raw ×1.30
        // alone reads like "your curve is now 1.3x", which is only true in the first stage.
        string note;
        if (moved)
        {
            double m = stage.Multipliers[leader];
            ladderScale *= m;
            ladderCentre = candidates[leader].Profile;
            note = $"第 {stagePos + 1} 轮（{stage.Label}）：选中比这轮起点{(m > 1 ? "快" : "慢")} {Math.Abs(m - 1) * 100:0}% 的那一档，" +
                   $"相对出发时累计 ×{ladderScale:0.00}";
        }
        else
        {
            note = $"第 {stagePos + 1} 轮（{stage.Label}）：五档分不出高下，中心不动（相对出发时仍是 ×{ladderScale:0.00}）";
        }
        stageNotes.Add(note);

        SaveArchive();
        StatusBar.Text = note + (stagePos + 1 < stages.Count ? "。下一轮幅度更小。" : "。");
        stagePos++;
    }

    // ---------- 04 ----------

    void Finish()
    {
        // StartFinal finishes on its own when there is nothing to compare, and Begin calls Finish
        // again right after - building the report twice would re-add every caveat and every row
        if (finished && verdict != null) return;

        finished = true;
        phase = CalibPlan.Phase.Done;

        if (opts.Ladder)
        {
            bool ranFinal = finalResults.Count > 0;
            if (!ranFinal)
            {
                // No head to head ran, because not one stage ever moved the centre. The report then
                // has to be about the last stage's five slots - ranking an empty result set would
                // draw a blank table, and ranking it against the two-curve final list would read
                // past the end of it - so that list goes back before anything is drawn.
                candidates.Clear();
                candidates.AddRange(lastStageCandidates);
                baseIndex = lastStageCandidates.Count / 2;
                archive.Candidates = lastStageCandidates.Select((c, i) => new CalibArchive.CandidateRow
                {
                    Index = i, Blind = c.BlindLabel, Secret = c.Secret, IsBase = i == baseIndex,
                }).ToList();
            }

            ranking = ranFinal ? CalibPlan.Rank(finalResults, candidates.Count)
                               : lastStageRanking ?? CalibPlan.Rank(stageResults, candidates.Count);
            verdict = ranFinal
                ? CalibPlan.DecideHeadToHead(finalResults, 0, 1, leaderWasBase: false, double.NaN,
                                             CalibPlan.Items.Length, "递进选出的这条", noAccelRan: false)
                : new CalibPlan.Verdict
                {
                    WinnerIndex = baseIndex, WinnerIsBase = true, Recommend = false,
                    Headline = "你出发时这条就是这几档里最好的。",
                    Reasons = { "五轮递进里，每一轮都没有找到明显更顺手的档位 —— 中心一次都没动。" },
                    Caveats = { "每一轮提升不到 3% 就不算数，所以也可能是几档之间本来就差不多。" },
                };
        }
        else
        {
            ranking ??= CalibPlan.Rank(exploreResults, candidates.Count);
            verdict = CalibPlan.Decide(ranking, finalResults, baseIndex);
        }

        SetStep(Step4);
        IntroPanel.Visibility = Visibility.Collapsed;
        RoundCard.Visibility = Visibility.Collapsed;
        ReportCard.Visibility = Visibility.Visible;

        VerdictText.Text = verdict.Headline;
        VerdictReasons.Text = string.Join("\n", verdict.Reasons);

        // For the ladder the table and the reveal follow the ranking above: the head to head when it
        // ran, otherwise the last stage's five slots, which is what the verdict is actually about.
        var shown = ranking!;
        BuildItemTable(shown);
        BuildReveal(shown, verdict);
        BuildLadderNotes();
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

        if (archiveError.Length > 0)
            CaveatList.Items.Add(new TextBlock
            {
                Text = "· 结果没能存盘：" + archiveError + " —— 关掉这个窗口就找不回了，先抄下上面的数值。",
                Foreground = (Brush)FindResource("WarningBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
        else if (archivePath.Length > 0)
            CaveatList.Items.Add(new TextBlock
            {
                Text = "· 这次的成绩已经存下来了：即使现在强退，也能在瞄准页「上次盲测」里找回并重新加入方案。",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });

        int winner = verdict.WinnerIndex;
        RecommendedProfile = candidates[winner].Profile;
        RecommendedLabel = candidates[winner].Secret;
        BtnApply.IsEnabled = !verdict.WinnerIsBase;
        BtnApply.Content = verdict.WinnerIsBase
            ? "你现在的就是最优"
            : opts.Ladder ? "用递进选出的这条" : $"用候选 {winner + 1}";

        foreach (var row in archive.Candidates)
        {
            if (shown.MeanRank.TryGetValue(row.Index, out var mr)) row.MeanRank = mr;
            if (shown.MeanConsistency.TryGetValue(row.Index, out var mc) && !double.IsNaN(mc)) row.MeanConsistency = mc;
        }

        archive.Finished = true;
        archive.Headline = verdict.Headline;
        archive.Reasons = verdict.Reasons.ToList();
        archive.WinnerIndex = winner;
        archive.WinnerIsBase = verdict.WinnerIsBase;
        archive.WinnerLabel = verdict.WinnerIsBase ? archive.BaseLabel : candidates[winner].Secret;
        // keep the original too: a result you can only apply and never undo is a trap
        archive.BaseJson = CalibArchive.Embedded(candidates[baseIndex].Profile);
        archive.WinnerJson = CalibArchive.Embedded(candidates[winner].Profile);
        SaveArchive();

        if (push.RestoreFailure != null)
            StatusBar.Text = "⚠ " + push.RestoreFailure + " —— 去驱动页手动应用一次你的曲线。";
        else
            StatusBar.Text = "驱动已经还原成你开测前的设置。";
    }

    /// <summary>The five stages, in order, so the report can say how the number was arrived at
    /// rather than only what it is.</summary>
    void BuildLadderNotes()
    {
        bool show = opts.Ladder && stageNotes.Count > 0;
        LadderTitle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LadderList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        LadderList.Items.Clear();
        foreach (string n in stageNotes)
            LadderList.Items.Add(new TextBlock
            {
                Text = "· " + n,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
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
        if (RecommendedProfile == null) { StatusBar.Text = "这次没有选出可应用的曲线。"; return; }

        // The window does the applying itself. Leaving it to the caller means a caller that already
        // returned (ShowDialog can end early, see OnNextRound) silently drops the result, and that
        // failure looks exactly like "I pressed apply and nothing happened".
        try
        {
            App.State.ReplaceProfile(TestedProfile, RecommendedProfile);
            bool saved = App.State.SaveToFile();
            Accepted = true;
            archive.Applied = true;
            SaveArchive();
            StatusBar.Text = saved
                ? $"已把「{RecommendedLabel}」设为当前曲线（还没写进驱动，去驱动页点应用）。"
                : "曲线已替换，但没能保存到文件，去曲线页手动应用一次。";
            Close();
        }
        catch (Exception ex)
        {
            // A throw here used to kill the process mid-session, leaving the driver on a candidate
            // curve. Anything that goes wrong now stays on screen instead.
            StatusBar.Text = "应用失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Keep the result as a preset without touching the curve in use. This is the option people
    /// actually want after fifteen minutes of testing: not "change my mouse now" but "don't throw
    /// this away, I want to be able to come back to it".
    /// </summary>
    void OnSavePlan(object sender, RoutedEventArgs e)
    {
        if (RecommendedProfile == null) { StatusBar.Text = "这次没有选出可以保存的曲线。"; return; }

        try
        {
            string name = Presets.FreeName("盲测 " + DateTime.Now.ToString("MM-dd HH:mm"));
            string when = archive.SavedAt.Length > 0 ? archive.SavedAt : archive.Id.Replace("_", " ");
            string note = $"盲测 {when}\n" +
                          archive.Headline + "\n" +
                          string.Join("\n", archive.Reasons) + "\n" +
                          "数值：" + RecommendedLabel;
            string? file = Presets.Save(name, RecommendedProfile, out string error, note);
            if (file == null) { StatusBar.Text = "存方案失败：" + error; return; }

            SavedAsPlan = true;
            archive.SavedAsPlan = true;
            archive.PlanName = name;
            SaveArchive();

            BtnSavePlan.IsEnabled = false;
            BtnSavePlan.Content = "已加入我的方案";
            StatusBar.Text = $"已存为「{name}」。它不会动你现在这条曲线 —— 去「我的方案」页随时一键换过去。";
        }
        catch (Exception ex) { StatusBar.Text = "存方案失败：" + ex.Message; }
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
