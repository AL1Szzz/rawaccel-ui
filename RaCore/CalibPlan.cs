namespace RaCore;

/// <summary>
/// The shape of a blind curve comparison: which round comes when, how the rounds are ranked, and
/// what the report is allowed to claim.
///
/// Three decisions are baked in here and each one is a choice, not a necessity:
///
///   * Rounds are interleaved by candidate. Running one candidate for five rounds then the next
///     would confound the curve with warm-up, fatigue and drift, and in a test this short those are
///     bigger effects than the curve is. So no two consecutive rounds use the same candidate.
///   * Ranking is done inside each drill and only then averaged. A gridshot score and a tracking
///     score are different units and cannot be added; saying "candidate 3 scored 4.1" would be a
///     number made of two things that were never comparable.
///   * The explore phase picks a leader; the final phase is that leader against the player's own
///     curve again. One round per drill is thin evidence, so the interesting pair is re-run.
///
/// Nothing here touches a window, a timer or the driver, so the whole session can be replayed.
/// </summary>
public static class CalibPlan
{
    /// <summary>
    /// The five drills the session runs. Chosen because they load the curve differently: gridshot is
    /// distance, sixshot is a fixed route, tracking is continuous pressure, single chain is repeated
    /// mid-range flicks, reaction is one deliberate shot. A curve that helps one and hurts another is
    /// exactly the case a single number would hide.
    /// </summary>
    public static readonly DrillKind[] Items =
    {
        DrillKind.Sixshot, DrillKind.Tracking, DrillKind.SingleChain, DrillKind.Reaction, DrillKind.Gridshot,
    };

    public const double RoundSeconds = 15;
    public const double PrepareSeconds = 3;

    public enum Phase { Explore, Final, Done }

    public sealed record Round(int Ordinal, Phase Phase, DrillKind Item, int CandidateIndex, double Seconds);

    public sealed class RoundResult
    {
        public Round Round = new(0, Phase.Explore, DrillKind.Gridshot, 0, RoundSeconds);
        /// <summary>The drill's own score, higher is better. Only comparable within one drill.</summary>
        public double Skill;
        /// <summary>0..1, how even the flick speed was across distances. The number an accel curve is for.</summary>
        public double Consistency;
        public string Detail = "";
        public bool Valid = true;
        public string Problem = "";
    }

    // ---------- scheduling ----------

    public static List<Round> Explore(int candidateCount, int seed = 20261029) =>
        Schedule(Enumerable.Range(0, candidateCount), Items, seed);

    public static List<Round> Final(int baseIndex, int leaderIndex, int seed = 771) =>
        Schedule(new[] { baseIndex, leaderIndex }, Items, seed, Phase.Final);

    /// <summary>
    /// One round per candidate per drill, shuffled. The ladder asks for exactly this but with three
    /// drills at a time, so the drills are an argument here rather than always all five - the
    /// interleaving rule is the part that has to stay identical between the two.
    /// </summary>
    public static List<Round> Schedule(IEnumerable<int> candidates, IEnumerable<DrillKind> items, int seed,
                                      Phase phase = Phase.Explore)
    {
        var pairs = new List<(int cand, DrillKind item)>();
        foreach (int c in candidates)
            foreach (var item in items)
                pairs.Add((c, item));

        return Interleave(pairs, seed).Select((p, i) =>
            new Round(i + 1, phase, p.item, p.cand, RoundSeconds)).ToList();
    }

    /// <summary>
    /// Shuffles until no two neighbours share a candidate. Repairing instead of re-shuffling keeps
    /// the run bounded: with five candidates over 25 rounds a valid order always exists, and the
    /// loop is capped so a pathological input cannot hang the UI thread.
    /// </summary>
    static List<(int cand, DrillKind item)> Interleave(List<(int cand, DrillKind item)> pairs, int seed)
    {
        var rng = new Random(seed);
        var order = pairs.OrderBy(_ => rng.Next()).ToList();

        for (int attempt = 0; attempt < 200; attempt++)
        {
            bool ok = true;
            for (int i = 1; i < order.Count; i++)
            {
                if (order[i].cand != order[i - 1].cand) continue;
                int j = -1;
                for (int k = i + 1; k < order.Count; k++)
                {
                    if (order[k].cand == order[i].cand) continue;
                    if (k > i + 1 && order[k].cand == order[i + 1].cand) continue;
                    j = k; break;
                }
                if (j < 0) { ok = false; break; }
                (order[i], order[j]) = (order[j], order[i]);
            }
            if (ok) return order;
        }
        return order;
    }

    // ---------- ranking ----------

    public sealed class Ranking
    {
        /// <summary>Candidates best-first, by mean rank across the drills.</summary>
        public List<int> Order = new();
        /// <summary>Mean rank per candidate, 1 = won everything.</summary>
        public Dictionary<int, double> MeanRank = new();
        /// <summary>How many of the five drills each candidate won outright.</summary>
        public Dictionary<int, int> Wins = new();
        public Dictionary<int, double> MeanConsistency = new();
        public int Leader => Order.Count > 0 ? Order[0] : 0;

        /// <summary>Per drill, candidates best-first with their scores, for the report table.</summary>
        public Dictionary<DrillKind, List<(int cand, double score)>> PerItem = new();
    }

    public static Ranking Rank(IEnumerable<RoundResult> results, int candidateCount)
    {
        var rows = results.Where(r => r.Valid).ToList();
        var rank = new Ranking();

        foreach (var item in Items)
        {
            var byItem = rows.Where(r => r.Round.Item == item).ToList();
            var perCand = new Dictionary<int, List<double>>();
            foreach (var r in byItem)
            {
                if (!perCand.TryGetValue(r.Round.CandidateIndex, out var l)) perCand[r.Round.CandidateIndex] = l = new();
                l.Add(r.Skill);
            }

            // one round per drill per candidate, but a re-run or a retry can add more; average them
            var score = perCand.ToDictionary(kv => kv.Key, kv => kv.Value.Average());
            var sorted = score.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
            rank.PerItem[item] = sorted;

            foreach (var (cand, _) in sorted)
            {
                int r = 1 + sorted.Count(o => o.Value > score[cand]);   // ties share the better rank
                rank.MeanRank.TryGetValue(cand, out var sum);
                rank.MeanRank[cand] = sum + r;
            }
        }

        int items = rank.PerItem.Count(kv => kv.Value.Count > 0);
        if (items == 0) return rank;

        foreach (int c in rank.MeanRank.Keys.ToList())
            rank.MeanRank[c] = rank.MeanRank[c] / items;

        foreach (var kv in rank.PerItem)
            if (kv.Value.Count > 0)
            {
                int winner = kv.Value[0].cand;
                rank.Wins.TryGetValue(winner, out var w);
                rank.Wins[winner] = w + 1;
            }

        // Consistency is NaN where a drill produced no flick to measure (tracking never clicks).
        // Averaging those in as zero would punish every candidate for the drills that cannot report.
        foreach (int c in rank.MeanRank.Keys)
        {
            var cons = rows.Where(r => r.Round.CandidateIndex == c)
                           .Select(r => r.Consistency)
                           .Where(v => !double.IsNaN(v)).ToList();
            rank.MeanConsistency[c] = cons.Count > 0 ? cons.Average() : double.NaN;
        }

        rank.Order = rank.MeanRank
            .OrderBy(kv => kv.Value)                                        // rank first
            .ThenByDescending(kv => rank.Wins.GetValueOrDefault(kv.Key))    // then outright wins
            .ThenByDescending(kv => double.IsNaN(rank.MeanConsistency.GetValueOrDefault(kv.Key))
                ? double.NegativeInfinity : rank.MeanConsistency.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key).ToList();

        // a candidate that never ran must not silently win on an empty mean
        for (int c = 0; c < candidateCount; c++)
            if (!rank.Order.Contains(c)) rank.Order.Add(c);

        return rank;
    }

    /// <summary>
    /// Who the final head-to-head should be against. Normally the explore leader; when the leader is
    /// the player's own curve, comparing it with itself would produce ten rounds of the same thing
    /// and a guaranteed tie, so the runner-up is the interesting opponent.
    /// </summary>
    public static int Opponent(Ranking ranking, int baseIndex)
    {
        if (ranking.Leader != baseIndex) return ranking.Leader;
        foreach (int c in ranking.Order)
            if (c != baseIndex) return c;
        return baseIndex;
    }

    // ---------- the verdict ----------

    public sealed class Verdict
    {
        public int WinnerIndex;
        public bool WinnerIsBase;
        public bool Recommend;
        /// <summary>How many of the five drills the leader won in the final head-to-head.</summary>
        public int FinalWins;
        /// <summary>Mean relative gain over the player's own curve across those drills.</summary>
        public double RelativeGain;
        public double BaseConsistency;
        public double WinnerConsistency;
        public string Headline = "";
        public List<string> Reasons = new();
        public List<string> Caveats = new();
    }

    public static Verdict Decide(Ranking explore, IReadOnlyList<RoundResult> finalResults, int baseIndex) =>
        DecideHeadToHead(finalResults, baseIndex, Opponent(explore, baseIndex),
                         leaderWasBase: explore.Leader == baseIndex,
                         baseMeanRank: explore.MeanRank.TryGetValue(baseIndex, out var mr) ? mr : double.NaN,
                         exploreItems: explore.PerItem.Count);

    /// <summary>
    /// The verdict for one head-to-head pair, independent of how the pair was picked. The single
    /// sweep picks its challenger from the explore ranking; the ladder's challenger is whatever five
    /// stages of narrowing landed on. Only the way they are described differs, so the bar they have
    /// to clear is the same code in both.
    /// </summary>
    public static Verdict DecideHeadToHead(IReadOnlyList<RoundResult> finalResults, int baseIndex, int opponent,
                                          bool leaderWasBase, double baseMeanRank, int exploreItems,
                                          string challengerLabel = "", bool noAccelRan = true)
    {
        string who = challengerLabel.Length > 0 ? challengerLabel : $"候选 {opponent + 1}";
        var v = new Verdict { WinnerIndex = opponent, WinnerIsBase = opponent == baseIndex };

        var rows = finalResults.Where(r => r.Valid).ToList();
        var pairs = new List<(DrillKind item, double baseScore, double leadScore, double baseCons, double leadCons)>();

        foreach (var item in Items)
        {
            var b = rows.Where(r => r.Round.Item == item && r.Round.CandidateIndex == baseIndex).ToList();
            var l = rows.Where(r => r.Round.Item == item && r.Round.CandidateIndex == opponent).ToList();
            if (b.Count == 0 || l.Count == 0) continue;
            pairs.Add((item, b.Average(r => r.Skill), l.Average(r => r.Skill),
                       b.Average(r => r.Consistency), l.Average(r => r.Consistency)));
        }

        if (pairs.Count == 0)
        {
            v.Headline = "决选没有跑完，结论不成立。";
            v.Caveats.Add("没有任何一个项目同时拿到原值和领先档的成绩，这次不算数。");
            return v;
        }

        v.FinalWins = pairs.Count(p => p.leadScore > p.baseScore);
        v.RelativeGain = pairs.Average(p =>
            (p.leadScore - p.baseScore) / Math.Max(1e-9, Math.Abs(p.baseScore)));

        // Consistency only exists for the click drills; if none of them ran, there is nothing to
        // gate on and the bar falls back to the score alone rather than to "no".
        var withCons = pairs.Where(p => !double.IsNaN(p.baseCons) && !double.IsNaN(p.leadCons)).ToList();
        if (withCons.Count > 0)
        {
            v.BaseConsistency = withCons.Average(p => p.baseCons);
            v.WinnerConsistency = withCons.Average(p => p.leadCons);
        }
        else
        {
            v.BaseConsistency = double.NaN;
            v.WinnerConsistency = double.NaN;
        }

        // The bar is deliberately high. One round per drill is thin, and the cost of a wrong
        // "you should change" is a player chasing a number that was noise.
        bool keepsConsistency = withCons.Count == 0 || v.WinnerConsistency >= v.BaseConsistency - 0.02;
        v.Recommend = opponent != baseIndex && v.FinalWins >= 3 && v.RelativeGain >= 0.02 && keepsConsistency;

        // The explore phase is one round per drill per candidate; when it cannot separate the
        // player's curve from the runner-up, the head-to-head is the tie-breaker, and it is allowed
        // to overturn the explore ordering rather than only to confirm it.
        if (v.Recommend)
        {
            v.WinnerIndex = opponent;
            v.WinnerIsBase = false;
            v.Headline = $"有更合适的：{who}。";
            v.Reasons.Add(leaderWasBase
                ? $"探索阶段它排第 {baseMeanRank:0.0}，决选里换了环境仍然赢 {v.FinalWins}/{pairs.Count} 个项目。"
                : $"决选里它在 {v.FinalWins}/{pairs.Count} 个项目上超过你现在这条。");
            v.Reasons.Add($"平均高出 {v.RelativeGain * 100:0.#}%" +
                          (withCons.Count > 0
                              ? $"；跨距离一致性 {v.WinnerConsistency:0.00} 对比 {v.BaseConsistency:0.00}"
                              : "（这次没记到足够的甩枪，一致性无从判断）"));
        }
        else
        {
            v.WinnerIndex = baseIndex;
            v.WinnerIsBase = true;
            if (leaderWasBase)
            {
                v.Headline = "你现在的曲线就是这几档里最好的。";
                v.Reasons.Add(double.IsNaN(baseMeanRank)
                    ? $"五轮递进之后，最合适的那一档就是你出发时那条。"
                    : $"探索阶段它在 {exploreItems} 个项目里平均排第 {baseMeanRank:0.0} 名。");
            }
            else
            {
                v.Headline = "领先档没有压过你现在这条，先别改。";
                v.Reasons.Add($"决选只赢了 {v.FinalWins}/{pairs.Count} 个项目，平均差 {v.RelativeGain * 100:0.#}%。");
                v.Reasons.Add("差别小到一次复测就可能反过来 —— 保持原样比追这个数更稳。");
            }
        }

        v.Caveats.Add("每档每个项目只跑了一轮（决选两轮）。15 秒一轮的成绩波动本来就大，这测的是偏好，不是测量。");
        v.Caveats.Add("只有在你日常那个坐姿、那块鼠标垫、这个分辨率下的结果；换个桌面或换只鼠标就不算。");
        if (noAccelRan)
            v.Caveats.Add("无加速那一档是形状不同的对照，不是\"更弱的加速\"；赢它一点说明不了太多。");
        v.Caveats.Add("如果某一档让你手明显不舒服，哪怕分高也别选 —— 这里只记录了成绩，没记录难受。");
        return v;
    }
}
