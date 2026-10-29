using static RaCore.CalibPlan;

namespace RaCore;

/// <summary>
/// A blind comparison that narrows down instead of guessing once.
///
/// One round of five variants tells you which of five numbers is best - and with fifteen-second
/// rounds, that answer is mostly noise, so the honest result is usually "they are all the same".
/// What people actually do by hand is the thing this encodes: start with a wide sweep, take the
/// winner, sweep again around it but tighter, and repeat until the steps are too small to feel.
///
/// The plan is geometric. Each stage spans <c>k^-2 .. k^2</c> around the current centre, and k
/// shrinks every stage, so the same five-slot shape covers less ground each time:
///
///     1  大幅   0.59  0.77  1.00  1.30  1.69     (fastest/slowest span 2.9x)
///     2  中幅   0.69  0.83  1.00  1.20  1.44     (2.1x)
///     3  中偏小 0.78  0.88  1.00  1.13  1.28     (1.6x)
///     4  中偏低 0.86  0.93  1.00  1.08  1.17     (1.4x)
///     5  最小   0.92  0.96  1.00  1.04  1.08     (1.2x)
///
/// Two things keep the narrowing honest:
///
///   * The centre is always the previous winner, and it is re-run in the next stage. A centre that
///     only won on noise gets five more chances to be beaten, instead of being locked in.
///   * The drills rotate. Three per stage over five stages puts every drill on exactly three stages,
///     so no stage is judged on one drill's mood and no drill is quietly dropped.
///
/// Nothing here touches a window or the driver, so the ladder can be replayed in a test.
/// </summary>
public static class CalibLadder
{
    /// <summary>Per-stage step. The span is step^4 across the five slots.</summary>
    public static readonly double[] Steps = { 1.30, 1.20, 1.13, 1.08, 1.04 };

    public static readonly string[] Labels = { "大幅", "中幅", "中偏小", "中偏低", "最小" };

    public const int Slots = 5;

    public sealed record Stage(int Index, string Label, double[] Multipliers, DrillKind[] Items)
    {
        /// <summary>Fastest slot divided by slowest slot: how much ground this stage covers.</summary>
        public double Span => Multipliers[^1] / Multipliers[0];
        /// <summary>The middle slot, i.e. the curve carried in from the last stage.</summary>
        public int CentreSlot => Multipliers.Length / 2;
    }

    /// <summary>
    /// The five stages. <paramref name="itemsPerStage"/> drills rotate through Items, wrapping, so
    /// with three drills and five items every item is used the same number of times overall.
    /// </summary>
    public static List<Stage> Plan(int stages = 5, int itemsPerStage = 3)
    {
        int n = Math.Clamp(stages, 1, Steps.Length);
        int per = Math.Clamp(itemsPerStage, 1, Items.Length);
        var list = new List<Stage>();

        for (int i = 0; i < n; i++)
        {
            double k = Steps[i];
            var mult = new double[Slots];
            for (int s = 0; s < Slots; s++) mult[s] = Math.Pow(k, s - Slots / 2);
            mult[Slots / 2] = 1.0;                       // exactly, not k^0 with float drift

            var items = new DrillKind[per];
            for (int j = 0; j < per; j++) items[j] = Items[(i * per + j) % Items.Length];

            list.Add(new Stage(i, Labels[i], mult, items));
        }
        return list;
    }

    /// <summary>Total scored rounds: one per slot per drill per stage, plus the final head to head.</summary>
    public static int RoundCount(List<Stage> stages, bool withFinal = true)
    {
        int n = stages.Sum(s => s.Multipliers.Length * s.Items.Length);
        return withFinal ? n + Items.Length * 2 : n;
    }

    /// <summary>
    /// Whether a stage actually moved the centre. A stage that cannot separate its slots must leave
    /// the centre alone: moving it because one round happened to be fast would drag the four
    /// remaining stages into the wrong neighbourhood, and they would then narrow down very precisely
    /// to the wrong number. Staying put is the cheap, correct answer when the data is thin.
    /// </summary>
    public static bool Improved(IReadOnlyList<RoundResult> stageResults, int centreSlot, int candidateSlot,
                               double minGain = 0.03)
    {
        if (stageResults.Count == 0) return false;
        if (candidateSlot == centreSlot) return false;

        var centre = stageResults.Where(r => r.Valid && r.Round.CandidateIndex == centreSlot).ToList();
        var other = stageResults.Where(r => r.Valid && r.Round.CandidateIndex == candidateSlot).ToList();
        if (centre.Count == 0 || other.Count == 0) return false;

        // compare drill by drill: the two slots ran the same drills once each
        var gains = new List<double>();
        foreach (var item in Items)
        {
            var c = centre.FirstOrDefault(r => r.Round.Item == item);
            var o = other.FirstOrDefault(r => r.Round.Item == item);
            if (c == null || o == null) continue;
            if (Math.Abs(c.Skill) < 1e-9) continue;
            gains.Add((o.Skill - c.Skill) / Math.Abs(c.Skill));
        }
        if (gains.Count == 0) return false;

        return gains.Average() >= minGain;
    }
}
