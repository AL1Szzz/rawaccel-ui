namespace RaCore;

/// <summary>
/// Group-based comparison: the tuning protocol compares CONFIGS, not single rounds, so the
/// history view groups rounds by (config, setup) and compares whole groups. Verdicts are
/// stated against the measured noise floor instead of raw deltas, because a same-config
/// spread of +-5pp in hit rate is what the baseline data actually shows.
/// </summary>
public static class History
{
    /// <summary>Noise floor for medians of ~5 same-config rounds, estimated across the baseline
    /// set of 2026-10-20/21 (10 rounds, one config: hit 60.0~76.1%, pace 83.8~102 within/between
    /// sessions, radial median jitter ~+-2pp). These are estimates, not laws - the UI says so.</summary>
    public const double HitNoisePp = 5, PaceNoise = 4, RadialNoisePp = 2;

    public sealed record RoundGroup
    {
        public required string Sha;
        public required string Mode;
        public double DistancePx, RadiusPx;
        public int Balls;
        public required List<RoundSummary> Rounds;

        public string Key => $"{Sha}|{Mode}|{DistancePx:0}|{RadiusPx:0}|{Balls}";
        public double MedianHit => Rounds.Count == 0 ? 0 : Rounds.Select(r => r.HitRate).OrderBy(x => x).Median();
        public double MedianPace => Rounds.Count == 0 ? 0 : Rounds.Select(r => r.KillsPerMinute).OrderBy(x => x).Median();
        public double MedianRadial => Rounds.Count == 0 ? 0 : Rounds.Select(r => r.RadialMedian).OrderBy(x => x).Median();
        public double MedianMaxExtent => Rounds.Count == 0 ? 0 : Rounds.Select(r => r.MaxExtentMedian).OrderBy(x => x).Median();
        public bool OpenLoopUsable => Rounds.Any() && Rounds.All(r => r.OpenLoopUsable);
        public string TimeRange()
        {
            var times = Rounds.Select(r => r.When).OrderBy(x => x).ToList();
            if (times.Count == 0) return "?";
            string a = times.First(), b = times.Last();
            string Short(string t) => t.Length >= 16 ? t[5..16] : t;   // MM-dd HH:mm
            return times.Count == 1 ? Short(a) : $"{Short(a)}~{Short(b)[6..]}";
        }
        public string Cond() => $"{Mode} · {DistancePx:0}px · {Balls}球";
    }

    /// <summary>Group rounds by config+setup, newest group first. A same-config run separated by
    /// a long gap (a different session/day) starts a NEW group: that is exactly the pair that
    /// shows warm-up drift, and merging the sessions would hide it.</summary>
    public static List<RoundGroup> Group(List<RoundSummary> rounds)
    {
        var list = new List<RoundGroup>();
        RoundGroup? cur = null;
        foreach (var r in rounds.OrderByDescending(r => r.When, StringComparer.Ordinal))
        {
            string key = $"{r.ConfigSha}|{r.Mode}|{r.DistancePx:0}|{r.RadiusPx:0}|{r.TargetCount}";
            bool sameSession = cur != null && cur.Key == key && SessionGap(cur, r) < TimeSpan.FromHours(6);
            if (!sameSession)
            {
                cur = new RoundGroup
                {
                    Sha = r.ConfigSha, Mode = r.Mode,
                    DistancePx = r.DistancePx, RadiusPx = r.RadiusPx,
                    Balls = r.TargetCount, Rounds = new List<RoundSummary>(),
                };
                list.Add(cur);
            }
            cur.Rounds.Add(r);
        }
        return list;
    }

    /// <summary>Gap between a group's oldest round and a candidate that is older still.</summary>
    static TimeSpan SessionGap(RoundGroup g, RoundSummary older)
    {
        var newest = g.Rounds.Count == 0 ? older.When : g.Rounds.Min(x => x.When);
        return DateTime.TryParse(newest, out var a) && DateTime.TryParse(older.When, out var b)
            ? a - b : TimeSpan.Zero;
    }

    /// <summary>The verdict lines for the newest group versus the one before it. Pure text,
    /// no diagnosis beyond what the noise floor supports.</summary>
    public static List<string> CompareNewestTwo(List<RoundSummary> rounds)
    {
        var groups = Group(rounds);
        var lines = new List<string>();
        if (groups.Count < 2)
        {
            lines.Add(groups.Count == 0 ? "还没有任何轮次记录。" :
                      $"只有一组（{Describe(groups[0])}）。至少要两组才能对比：改一个参数，再跑 3~5 轮。");
            return lines;
        }
        var newer = groups[0];
        var older = groups[1];
        foreach (var (g, tag) in new[] { (newer, "最近"), (older, "之前") })
            lines.Add($"{tag}：{Describe(g)}");

        bool sameSha = string.Equals(newer.Sha, older.Sha, StringComparison.OrdinalIgnoreCase);
        var conds = new List<string>();
        if (!string.Equals(newer.Mode, older.Mode, StringComparison.Ordinal)) conds.Add($"模式 {older.Mode}→{newer.Mode}");
        if (Math.Abs(newer.DistancePx - older.DistancePx) > 1) conds.Add($"距离 {older.DistancePx:0}→{newer.DistancePx:0}px");
        if (newer.Balls != older.Balls) conds.Add($"球数 {older.Balls}→{newer.Balls}");
        if (Math.Abs(newer.RadiusPx - older.RadiusPx) > 0.5) conds.Add($"球半径 {older.RadiusPx:0}→{newer.RadiusPx:0}px");

        if (sameSha)
        {
            lines.Add($"两组配置相同（{Sha(newer.Sha)}）：下面的差异只是你的状态波动（热身/疲劳），不是参数效果。");
        }
        else if (conds.Count > 0)
        {
            lines.Add($"配置从 {Sha(older.Sha)} 改到 {Sha(newer.Sha)}，但测试条件也变了（{string.Join("，", conds)}）：差值不能全归因于参数。");
        }
        else
        {
            lines.Add($"配置从 {Sha(older.Sha)} 改到 {Sha(newer.Sha)}，其他条件一致：差值来自这次改动。");
        }

        double dHit = newer.MedianHit - older.MedianHit;
        double dPace = newer.MedianPace - older.MedianPace;
        double dRad = newer.MedianRadial - older.MedianRadial;
        if (sameSha)
        {
            lines.Add($"命中 {older.MedianHit:0.0}→{newer.MedianHit:0.0}%（{dHit:+0.0;-0.0}pp）   节奏 {older.MedianPace:0.0}→{newer.MedianPace:0.0} 球/分   径向中位 {older.MedianRadial:+0.0;-0.0}→{newer.MedianRadial:+0.0;-0.0}%");
        }
        else
        {
            lines.Add(Verdict("命中", older.MedianHit, newer.MedianHit, "pp", HitNoisePp));
            lines.Add(Verdict("节奏", older.MedianPace, newer.MedianPace, "球/分", PaceNoise));
            lines.Add(Verdict("径向中位", older.MedianRadial, newer.MedianRadial, "%", RadialNoisePp));
        }
        if (!newer.OpenLoopUsable || !older.OpenLoopUsable)
            lines.Add("（其中一组是旧构建，开环数字缺或不可信 —— 开环对比要等两边都有新构建的轮次）");
        else if (!sameSha && conds.Count == 0 &&
                 newer.MedianMaxExtent > 0 && older.MedianMaxExtent > 0)
            lines.Add($"开环越过头中位 {older.MedianMaxExtent:0}%→{newer.MedianMaxExtent:0}%（>100% = 甩过头；这是判断增益改动的第一尺子，噪声底还在积累，先看方向）");
        if (newer.Rounds.Count < 3 || older.Rounds.Count < 3)
            lines.Add("（有一组轮数少于 3，上面的噪声判读不可靠 —— 每组至少 3~5 轮）");
        return lines;
    }

    static string Verdict(string name, double oldV, double newV, string unit, double noise)
    {
        double d = newV - oldV;
        string arrow = $"{oldV:0.0}→{newV:0.0}{unit}（{d:+0.0;-0.0}）";
        if (Math.Abs(d) <= noise) return $"{name} {arrow}：在噪声底 ±{noise:0.#}{unit} 内，测不出差别";
        return $"{name} {arrow}：超过了噪声底 ±{noise:0.#}{unit}，方向是{(d > 0 ? "变高" : "变低")} —— 再各跑 2 轮确认";
    }

    static string Describe(RoundGroup g) =>
        $"{Sha(g.Sha)} · {g.Cond()} · {g.Rounds.Count}轮 · {g.TimeRange()} · 命中中位 {g.MedianHit:0.0}% · 节奏 {g.MedianPace:0.0}" +
        (g.OpenLoopUsable && g.MedianMaxExtent > 0 ? $" · 越过头 {g.MedianMaxExtent:0}%" : "");

    static string Sha(string s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 6 ? s[..6] : s);
}

/// <summary>Median of a list - the statistics helpers in RoundStats are private, and this
/// lives next to the only other consumer.</summary>
public static class SeqExt
{
    public static double Median(this IEnumerable<double> src)
    {
        var a = src.OrderBy(x => x).ToList();
        if (a.Count == 0) return 0;
        int n = a.Count;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }
}

/// <summary>
/// Archive-based cleanup: "clearing old logs" moves round files into logs\_archive\&lt;stamp&gt;\ and
/// prunes index.csv rows whose file is gone. Nothing is ever deleted here - emptying the
/// archive folder in Explorer is the explicit second step, and it is the user's hand on it.
/// </summary>
public static class LogArchive
{
    public sealed record Result(int Moved, List<string> Failed, string ArchiveDir);

    /// <summary>Move whole round files into an archive folder NEXT TO them (so offline tests
    /// never touch the real data dir), then rewrite index.csv without the missing rows.
    /// A file that cannot be moved is reported in Failed - nothing is dropped silently.</summary>
    public static Result Archive(IEnumerable<string> logFiles)
    {
        var failed = new List<string>();
        var files = logFiles.ToList();
        var existing = files.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            failed.AddRange(files.Select(f => Path.GetFileName(f) + ": 文件不存在"));
            return new Result(0, failed, "");
        }

        var dir = Path.Combine(Path.GetDirectoryName(existing[0])!, "_archive",
                               DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(dir);

        int moved = 0;
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) throw new FileNotFoundException(f);
                File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
                moved++;
            }
            catch (Exception e) { failed.Add(Path.GetFileName(f) + ": " + e.Message); }
        }

        var indexDir = TestLog.IndexOverride ?? Path.GetDirectoryName(existing[0])!;
        var idx = Path.Combine(indexDir, "index.csv");
        if (File.Exists(idx))
        {
            var lines = File.ReadAllLines(idx).ToList();
            var kept = lines.Where((l, i) => i == 0 || !l.StartsWith("file;") && l.Length > 0
                                             && File.Exists(Path.Combine(indexDir, l.Split(';')[0])))
                            .ToList();
            if (kept.Count != lines.Count)
            {
                var tmp = idx + ".tmp";
                File.WriteAllLines(tmp, kept);
                File.Move(tmp, idx, true);
            }
        }
        return new Result(moved, failed, dir);
    }
}
