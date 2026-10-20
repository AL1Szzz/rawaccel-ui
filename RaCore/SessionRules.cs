namespace RaCore;

public enum TestMode { Balls = 0, Timed = 1 }

/// <summary>
/// Pure session rules so the counting can be asserted without a window: in Balls mode the
/// round ends when the required number of balls has been CLEARED (misses do not consume the
/// goal), in Timed mode it ends on the clock and the score is how many balls were cleared.
/// </summary>
public sealed class SessionRule
{
    public TestMode Mode = TestMode.Balls;
    public int BallGoal = 30;
    public double TimeLimitMs = 60_000;

    public int Hits;
    public int Clicks;
    public double ElapsedMs;

    public SessionRule() { }
    public SessionRule(TestMode mode, int ballGoal, double seconds)
    {
        Mode = mode;
        BallGoal = Math.Max(1, ballGoal);
        TimeLimitMs = Math.Max(1, seconds) * 1000.0;
    }

    public double HitRatePct => Clicks == 0 ? 0 : Hits * 100.0 / Clicks;
    public int RemainingBalls => Math.Max(0, BallGoal - Hits);
    public double RemainingMs => Mode == TestMode.Timed ? Math.Max(0, TimeLimitMs - ElapsedMs) : 0;

    /// <summary>Timed mode always runs out the clock; Balls mode stops as soon as the goal is cleared.</summary>
    public bool Finished => Mode == TestMode.Timed
        ? ElapsedMs >= TimeLimitMs
        : Hits >= BallGoal;

    public string EndReason => Mode == TestMode.Timed ? "time_up" : "goal_reached";

    /// <summary>Top-of-screen HUD. Deliberately short: it must not pull attention off the balls.</summary>
    public string Hud()
    {
        string rate = Clicks == 0 ? "命中率 —" : $"命中率 {HitRatePct:0}%";
        return Mode == TestMode.Balls
            ? $"还剩 {RemainingBalls} 球 · 已点掉 {Hits} · {rate} · Esc 结束"
            : $"{Clock(RemainingMs)} · 已点掉 {Hits} 球 · {rate} · Esc 结束";
    }

    /// <summary>Score line for the log: what this round actually achieved.</summary>
    public string Score()
    {
        return Mode == TestMode.Balls
            ? $"点掉 {Hits}/{BallGoal} 球，用了 {Clicks} 次点击（{ElapsedMs / 1000.0:0.0}s）"
            : $"{TimeLimitMs / 1000.0:0}s 内点掉 {Hits} 球，用了 {Clicks} 次点击，{KillsPerMinute:0.0} 球/分钟";
    }

    public double KillsPerMinute => ElapsedMs > 0 ? Hits * 60_000.0 / ElapsedMs : 0;

    static string Clock(double ms)
    {
        int s = (int)Math.Ceiling(ms / 1000.0);
        return $"{s / 60}:{s % 60:00}";
    }
}
