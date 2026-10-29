namespace RaCore;

/// <summary>
/// The three drill types. They differ only in where targets go and what counts as a point, so
/// the window keeps one loop and the differences live here where they can be asserted.
/// </summary>
public enum DrillKind { Gridshot = 0, Sixshot = 1, Tracking = 2 }

/// <summary>A drill target, in physical desktop pixels.</summary>
public readonly record struct DrillTarget(int Id, double X, double Y, double RadiusPx)
{
    public bool Covers(double x, double y)
        => (x - X) * (x - X) + (y - Y) * (y - Y) <= RadiusPx * RadiusPx;
}

/// <summary>
/// Pure placement and motion. Every function is seeded or closed-form, so a round can be
/// replayed exactly and the self tests can check the geometry without a window.
/// </summary>
public static class DrillField
{
    public readonly record struct Area(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public bool Fits(double x, double y, double r)
            => x - r >= X && x + r <= Right && y - r >= Y && y + r <= Bottom;
    }

    /// <summary>
    /// The part of the screen a drill may use: everything above the bottom hint strip and below
    /// the banner, with a margin so a target never touches an edge where it is hard to see.
    /// </summary>
    public static Area Usable(double screenW, double screenH, double bannerKeepOut = 96, double edgeMargin = 48)
    {
        double x = edgeMargin, y = bannerKeepOut;
        double w = Math.Max(0, screenW - 2 * edgeMargin);
        double h = Math.Max(0, screenH - bannerKeepOut - edgeMargin);
        return new Area(x, y, w, h);
    }

    /// <summary>
    /// Gridshot: a fixed lattice, so the same spots come back every round and the drill trains
    /// snapping between known points instead of hunting a random field. Cell centres, top-left first.
    /// </summary>
    public static (double x, double y)[] Grid(Area a, int cols = 6, int rows = 3)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var list = new (double, double)[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                // cell centres: the lattice is inset half a cell so targets stay inside the area
                double x = a.X + a.W * (c + 0.5) / cols;
                double y = a.Y + a.H * (r + 0.5) / rows;
                list[r * cols + c] = (x, y);
            }
        return list;
    }

    /// <summary>
    /// Sixshot: six fixed positions, three up and three down. Fixed rather than random because the
    /// drill is about the route between them - a random layout turns it into gridshot with fewer targets.
    /// </summary>
    public static (double x, double y)[] Six(Area a)
    {
        double cx = a.X + a.W / 2, cy = a.Y + a.H / 2;
        double dx = a.W * 0.30, dy = a.H * 0.22;
        return new[]
        {
            (cx - dx, cy - dy), (cx, cy - dy), (cx + dx, cy - dy),
            (cx - dx, cy + dy), (cx, cy + dy), (cx + dx, cy + dy),
        };
    }

    /// <summary>
    /// Picks <paramref name="count"/> cells that are not already in use, without repeating a cell
    /// the player is currently looking at. Returns fewer when the lattice is full - the caller
    /// reports the shortfall rather than squeezing targets in.
    /// </summary>
    public static List<int> FreeCells(int cellCount, IReadOnlyCollection<int> taken, int count, Random rng)
    {
        var free = new List<int>(cellCount);
        for (int i = 0; i < cellCount; i++)
            if (!taken.Contains(i)) free.Add(i);

        var picked = new List<int>(Math.Min(count, free.Count));
        while (picked.Count < count && free.Count > 0)
        {
            int k = rng.Next(free.Count);
            picked.Add(free[k]);
            free.RemoveAt(k);
        }
        return picked;
    }

    /// <summary>
    /// One step of the tracking target. It bounces off the usable area and reverses one axis at a
    /// time so the movement reads as a strafing player rather than a ball in a box. Deterministic:
    /// same state in, same state out, which is what lets the self tests replay a round.
    /// </summary>
    public static (double x, double y, double vx, double vy) StepTracker(
        double x, double y, double vx, double vy, double dtMs, Area a, double radiusPx)
    {
        double dt = Math.Max(0, dtMs) / 1000.0;
        x += vx * dt;
        y += vy * dt;

        if (x - radiusPx < a.X) { x = a.X + radiusPx; vx = Math.Abs(vx); }
        else if (x + radiusPx > a.Right) { x = a.Right - radiusPx; vx = -Math.Abs(vx); }

        if (y - radiusPx < a.Y) { y = a.Y + radiusPx; vy = Math.Abs(vy); }
        else if (y + radiusPx > a.Bottom) { y = a.Bottom - radiusPx; vy = -Math.Abs(vy); }

        return (x, y, vx, vy);
    }
}

/// <summary>
/// Scoring for one drill round. Counts only - no window, no timers, no drawing - so the numbers a
/// player sees after a round can be checked line by line in the self tests.
///
/// All three drills run on the clock. Gridshot and sixshot score on hits per second; tracking
/// scores on the fraction of the round the view was on the target while the button was held.
/// </summary>
public sealed class DrillRun
{
    public readonly DrillKind Kind;
    public readonly double TimeLimitMs;

    public double ElapsedMs;
    public int Shots;                 // clicks, right or wrong
    public int Hits;                  // clicks that landed on a target
    public int SetsCleared;           // sixshot: complete sets of six
    public double HeldMs;             // tracking: time the button was down
    public double OnTargetMs;         // tracking: time the button was down and the view was on target
    public double LongestOnTargetMs;  // tracking: the longest unbroken stretch

    double runMs;
    bool finished;

    public DrillRun(DrillKind kind, double seconds)
    {
        Kind = kind;
        TimeLimitMs = Math.Max(1, seconds) * 1000.0;
    }

    public double HitRatePct => Shots == 0 ? 0 : Hits * 100.0 / Shots;
    public bool Finished => finished || ElapsedMs >= TimeLimitMs;
    public double RemainingMs => Math.Max(0, TimeLimitMs - ElapsedMs);

    /// <summary>Hits per minute, the number gridshot players compare.</summary>
    public double HitsPerMinute => ElapsedMs > 0 ? Hits * 60_000.0 / ElapsedMs : 0;

    /// <summary>Sixshot: average seconds per cleared set of six.</summary>
    public double SecondsPerSet => SetsCleared > 0 ? ElapsedMs / 1000.0 / SetsCleared : 0;

    /// <summary>Tracking: share of the held time the view actually sat on the target.</summary>
    public double OnTargetPct => HeldMs > 0 ? OnTargetMs * 100.0 / HeldMs : 0;

    public void Tick(double dtMs, bool buttonHeld, bool onTarget)
    {
        if (finished) return;
        if (Kind == DrillKind.Tracking && buttonHeld)
        {
            HeldMs += dtMs;
            if (onTarget) { OnTargetMs += dtMs; runMs += dtMs; LongestOnTargetMs = Math.Max(LongestOnTargetMs, runMs); }
            else runMs = 0;
        }
    }

    /// <summary>Time only advances while the round is live; a paused window must not eat the clock.</summary>
    public void Advance(double dtMs)
    {
        if (finished) return;
        ElapsedMs += Math.Max(0, dtMs);
        if (ElapsedMs >= TimeLimitMs) { ElapsedMs = TimeLimitMs; finished = true; }
    }

    public void Click(bool hit, bool completesSet = false)
    {
        if (finished) return;
        Shots++;
        if (!hit) return;
        Hits++;
        if (completesSet) SetsCleared++;
    }

    public string Hud()
    {
        string clock = Clock(RemainingMs);
        return Kind switch
        {
            DrillKind.Gridshot => $"{clock} · {Hits} 个 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · Esc 结束",
            DrillKind.Sixshot => $"{clock} · 清掉 {SetsCleared} 组 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · Esc 结束",
            _ => $"{clock} · 跟住 {OnTargetPct:0}% · 按住左键 · Esc 结束",
        };
    }

    /// <summary>The line kept in the score history and shown after the round.</summary>
    public string Score() => Kind switch
    {
        DrillKind.Gridshot => $"点掉 {Hits} 个，命中率 {HitRatePct:0}%，{HitsPerMinute:0.0} 个/分钟",
        DrillKind.Sixshot => $"清掉 {SetsCleared} 组（{Hits} 个），平均 {SecondsPerSet:0.0} 秒/组，命中率 {HitRatePct:0}%",
        _ => $"跟住 {OnTargetPct:0}%（{OnTargetMs / 1000.0:0.0}s / {HeldMs / 1000.0:0.0}s），最长连续 {LongestOnTargetMs / 1000.0:0.0}s",
    };

    /// <summary>Short form for the history table, which has one narrow column per round.</summary>
    public string ShortScore() => Kind switch
    {
        DrillKind.Gridshot => $"{Hits} 个 · {HitsPerMinute:0} 每分钟",
        DrillKind.Sixshot => $"{SetsCleared} 组 · {SecondsPerSet:0.0}s 每组",
        _ => $"跟住 {OnTargetPct:0}%",
    };

    public static string Clock(double ms)
    {
        int s = (int)Math.Ceiling(ms / 1000.0);
        return $"{s / 60}:{s % 60:00}";
    }
}
