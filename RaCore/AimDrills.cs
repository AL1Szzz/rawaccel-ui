namespace RaCore;

/// <summary>
/// The nine drills. They differ only in where targets go and what counts as a point, so
/// the window keeps one loop and the differences live here where they can be asserted.
///
/// The nine match the set the desktop aim trainers settled on, because each one loads hand control
/// differently and a curve that helps one can hurt another:
///   Gridshot      many fixed cells, wide snapping
///   Sixshot       six spots on a 3x2 lattice, re-jittered every set
///   Tracking      one moving target, continuous pressure
///   ThreeShot     three scattered targets, long flicks
///   SingleChain   one target, reappears immediately - steady mid-range rhythm
///   Reaction      one target after a hidden delay - one deliberate shot
///   MultiChain    several at once, clearing under mild time pressure
///   Strafe        a target that stops and goes - you may only shoot while it is stopped
///   FlashReset    a target far off to the side, then an accurate return to the middle
/// </summary>
public enum DrillKind
{
    Gridshot = 0,
    Sixshot = 1,
    Tracking = 2,
    ThreeShot = 3,
    SingleChain = 4,
    Reaction = 5,
    MultiChain = 6,
    Strafe = 7,
    FlashReset = 8,
}

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
    /// Sixshot: six spots on a 3x2 lattice, each one jittered inside its own cell and re-rolled for
    /// every set. They were six fixed points, which turned a minute of sixshot into the same six
    /// flicks over and over - it trained a route through known dots instead of a hand.
    ///
    /// One point per cell rather than a free scatter: a scatter can put two targets close enough to
    /// take with one click, and "did that count" is not a thing a drill should ask. The jitter is
    /// capped at 0.7 of the room a cell has, which keeps neighbours at least a couple of hundred px
    /// apart at any sane screen size. Seeded, so a dump and a self test can replay a set exactly.
    /// </summary>
    public static (double x, double y)[] Six(Area a, Random rng, double radiusPx = 22, int cols = 3, int rows = 2)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        double cw = a.W / cols, ch = a.H / rows;
        double pad = Math.Max(0, radiusPx) + 6;
        double jx = Math.Max(0, cw / 2 - pad) * 0.7;
        double jy = Math.Max(0, ch / 2 - pad) * 0.7;

        var list = new (double, double)[cols * rows];
        for (int i = 0; i < list.Length; i++)
        {
            int c = i % cols, r = i / cols;
            double x = a.X + cw * (c + 0.5) + (rng.NextDouble() * 2 - 1) * jx;
            double y = a.Y + ch * (r + 0.5) + (rng.NextDouble() * 2 - 1) * jy;
            list[i] = (x, y);
        }
        return list;
    }

    /// <summary>Threeshot: three spots thrown wide, never closer together than minGap.</summary>
    public static (double x, double y)[] Three(Area a, double radiusPx, Random rng, int count = 3)
        => Spread(a, radiusPx, rng, count, minGapPx: Math.Max(160, radiusPx * 5));

    /// <summary>
    /// Scattered positions with a rejection gap, so targets never overlap and never sit so close
    /// that one click could plausibly take two. Falls back to fewer points rather than squeezing.
    /// </summary>
    public static (double x, double y)[] Spread(Area a, double radiusPx, Random rng, int count, double minGapPx)
    {
        var outList = new List<(double, double)>();
        for (int attempt = 0; attempt < count * 60 && outList.Count < count; attempt++)
        {
            double x = a.X + radiusPx + rng.NextDouble() * Math.Max(0, a.W - 2 * radiusPx);
            double y = a.Y + radiusPx + rng.NextDouble() * Math.Max(0, a.H - 2 * radiusPx);
            bool ok = true;
            foreach (var (px, py) in outList)
                if ((px - x) * (px - x) + (py - y) * (py - y) < minGapPx * minGapPx) { ok = false; break; }
            if (ok) outList.Add((x, y));
        }
        return outList.ToArray();
    }

    /// <summary>
    /// FlashReset: a spot out to one side, far enough from the middle that reaching it is a real
    /// flick rather than a twitch. The side alternates so the drill does not train one direction.
    /// </summary>
    public static (double x, double y) FarSpot(Area a, double radiusPx, Random rng, bool goLeft)
    {
        double margin = radiusPx + 8;
        double x = goLeft ? a.X + margin : a.Right - margin;
        double y = a.Y + margin + rng.NextDouble() * Math.Max(0, a.H - 2 * margin);
        return (x, y);
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

    /// <summary>Strafe: the target runs for moveMs, then freezes for stopMs. Only a stopped target counts.</summary>
    public static bool StrafeStopped(double elapsedMs, double moveMs = 700, double stopMs = 320)
    {
        double period = Math.Max(1, moveMs + stopMs);
        return (Math.Max(0, elapsedMs) % period) >= moveMs;
    }

    /// <summary>How long until the strafe target changes state, for the HUD countdown.</summary>
    public static double StrafeNextChangeMs(double elapsedMs, double moveMs = 700, double stopMs = 320)
    {
        double period = Math.Max(1, moveMs + stopMs);
        double t = Math.Max(0, elapsedMs) % period;
        return t < moveMs ? moveMs - t : period - t;
    }
}

/// <summary>
/// Scoring for one drill round. Counts only - no window, no timers, no drawing - so the numbers a
/// player sees after a round can be checked line by line in the self tests.
///
/// Every click drill also feeds <see cref="Flick"/>: how long the pointer took to get there and how
/// far it travelled. That pair is the only output here that says anything about a curve, because a
/// curve's whole job is making a short flick and a long flick feel like the same mouse.
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

    /// <summary>FlashReset: completed flick-and-return pairs. Zero means time-limited.</summary>
    public int RepTarget;
    public int RepsDone;

    public double ReactionMsTotal;
    public int ReactionCount;

    /// <summary>Distance and time between consecutive hits, bucketed by distance.</summary>
    public readonly FlickStats Flick = new();

    double lastHitX, lastHitY, lastHitAtMs;
    bool haveLastHit;
    double runMs;
    bool finished;

    public DrillRun(DrillKind kind, double seconds)
    {
        Kind = kind;
        TimeLimitMs = Math.Max(1, seconds) * 1000.0;
    }

    public double HitRatePct => Shots == 0 ? 0 : Hits * 100.0 / Shots;
    public bool Finished => finished
        || (RepTarget > 0 ? RepsDone >= RepTarget : false)
        || ElapsedMs >= TimeLimitMs;
    public double RemainingMs => Math.Max(0, TimeLimitMs - ElapsedMs);
    public double MeanReactionMs => ReactionCount > 0 ? ReactionMsTotal / ReactionCount : 0;

    /// <summary>Hits per minute, the number gridshot players compare.</summary>
    public double HitsPerMinute => ElapsedMs > 0 ? Hits * 60_000.0 / ElapsedMs : 0;

    /// <summary>Sixshot: average seconds per cleared set of six.</summary>
    public double SecondsPerSet => SetsCleared > 0 ? ElapsedMs / 1000.0 / SetsCleared : 0;

    /// <summary>Tracking: share of the held time the view actually sat on the target.</summary>
    public double OnTargetPct => HeldMs > 0 ? OnTargetMs * 100.0 / HeldMs : 0;

    /// <summary>Tracking: share of the whole round spent on target. This is what ranking uses,
    /// because "on target 90% of the time I held the button" is flattered by holding it for a second.</summary>
    public double CoveragePct => ElapsedMs > 0 ? OnTargetMs * 100.0 / ElapsedMs : 0;

    /// <summary>0.5..1: a spammer who lands 1 in 5 keeps half credit, not none and not full.</summary>
    public double AccuracyFactor => Shots == 0 ? 0 : 0.5 + 0.5 * (double)Hits / Shots;

    /// <summary>Hits, with spam discounted. Comparable only within one drill.</summary>
    public double EffectiveHits => Hits * AccuracyFactor;

    /// <summary>
    /// The single number a round is ranked by. Different drills are in different units on purpose -
    /// they are only ever compared against the same drill - and the ranking in CalibPlan respects that.
    /// </summary>
    public double SkillScore => Kind switch
    {
        DrillKind.Tracking => OnTargetMs / Math.Max(1, ElapsedMs),
        DrillKind.Sixshot => SetsCleared * 60_000.0 / Math.Max(1, ElapsedMs),
        DrillKind.FlashReset => RepsDone,
        _ => EffectiveHits,
    };

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

    /// <summary>A click at a point. Position is what makes the flick stats possible, so it is required.</summary>
    public void Click(double atX, double atY, bool hit, bool completesSet = false)
    {
        if (finished) return;
        Shots++;
        if (!hit) return;

        Hits++;
        if (haveLastHit)
        {
            double dx = atX - lastHitX, dy = atY - lastHitY;
            Flick.Add(Math.Sqrt(dx * dx + dy * dy), ElapsedMs - lastHitAtMs);
        }
        lastHitX = atX; lastHitY = atY; lastHitAtMs = ElapsedMs; haveLastHit = true;

        if (completesSet) SetsCleared++;
    }

    /// <summary>FlashReset: one flick out and back completed.</summary>
    public void Rep()
    {
        if (finished) return;
        RepsDone++;
        if (RepTarget > 0 && RepsDone >= RepTarget) finished = true;
    }

    /// <summary>Reaction: how long the player took once the target appeared.</summary>
    public void Reaction(double ms)
    {
        if (!(ms > 0) || ms > 8000) return;
        ReactionMsTotal += ms;
        ReactionCount++;
    }

    public string Hud()
    {
        string clock = Clock(RemainingMs);
        return Kind switch
        {
            DrillKind.Gridshot => $"{clock} · {Hits} 个 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · Esc 结束",
            DrillKind.Sixshot => $"{clock} · 清掉 {SetsCleared} 组 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · Esc 结束",
            DrillKind.Tracking => $"{clock} · 跟住 {CoveragePct:0}% · 按住左键 · Esc 结束",
            DrillKind.Reaction => $"{clock} · {Hits} 个 · 平均 {MeanReactionMs:0} ms · 等它出现再点",
            DrillKind.Strafe => $"{clock} · {Hits} 个 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · 只在停下时开枪",
            DrillKind.FlashReset => $"{clock} · 完成 {RepsDone}/{RepTarget} 次 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")}",
            _ => $"{clock} · {Hits} 个 · 命中率 {(Shots == 0 ? "—" : $"{HitRatePct:0}%")} · Esc 结束",
        };
    }

    /// <summary>The line kept in the score history and shown after the round.</summary>
    public string Score() => Kind switch
    {
        DrillKind.Gridshot => $"点掉 {Hits} 个，命中率 {HitRatePct:0}%，{HitsPerMinute:0.0} 个/分钟",
        DrillKind.Sixshot => $"清掉 {SetsCleared} 组（{Hits} 个），平均 {SecondsPerSet:0.0} 秒/组，命中率 {HitRatePct:0}%",
        DrillKind.Tracking => $"跟住 {CoveragePct:0}%（{OnTargetMs / 1000.0:0.0}s / {ElapsedMs / 1000.0:0.0}s），最长连续 {LongestOnTargetMs / 1000.0:0.0}s",
        DrillKind.Reaction => $"反应 {Hits} 个，平均 {MeanReactionMs:0} ms，命中率 {HitRatePct:0}%",
        DrillKind.Strafe => $"停下时打中 {Hits} 个，命中率 {HitRatePct:0}%，{HitsPerMinute:0.0} 个/分钟",
        DrillKind.FlashReset => $"复位 {RepsDone} 次，命中率 {HitRatePct:0}%",
        _ => $"点掉 {Hits} 个，命中率 {HitRatePct:0}%，{HitsPerMinute:0.0} 个/分钟",
    };

    /// <summary>Short form for the history table, which has one narrow column per round.</summary>
    public string ShortScore() => Kind switch
    {
        DrillKind.Gridshot => $"{Hits} 个 · {HitsPerMinute:0} 每分钟",
        DrillKind.Sixshot => $"{SetsCleared} 组 · {SecondsPerSet:0.0}s 每组",
        DrillKind.Tracking => $"跟住 {CoveragePct:0}%",
        DrillKind.Reaction => $"{Hits} 个 · {MeanReactionMs:0} ms",
        DrillKind.Strafe => $"{Hits} 个 · 急停命中",
        DrillKind.FlashReset => $"{RepsDone} 次复位",
        _ => $"{Hits} 个 · {HitRatePct:0}%",
    };

    public static string Clock(double ms)
    {
        int s = (int)Math.Ceiling(ms / 1000.0);
        return $"{s / 60}:{s % 60:00}";
    }
}

/// <summary>What each drill is, for the pickers and the round banner.</summary>
public static class DrillInfo
{
    public static string Name(DrillKind k) => k switch
    {
        DrillKind.Gridshot => "网格速击",
        DrillKind.Sixshot => "六目标",
        DrillKind.Tracking => "人物跟枪",
        DrillKind.ThreeShot => "三目标",
        DrillKind.SingleChain => "单球连击",
        DrillKind.Reaction => "反应单球",
        DrillKind.MultiChain => "多目标连击",
        DrillKind.Strafe => "摆射训练",
        _ => "背闪复位",
    };

    public static string Hint(DrillKind k) => k switch
    {
        DrillKind.Gridshot => "点掉一个，新的就出现在别的格子里 · 左键 · Esc 退出",
        DrillKind.Sixshot => "六个全点掉才算一组，下一组会重新排位置 · 左键 · Esc 退出",
        DrillKind.Tracking => "按住左键，把准星一直压在目标上 · 松手不计时不扣分 · Esc 退出",
        DrillKind.ThreeShot => "三个分散的目标，跨越大半个屏幕地甩 · 左键 · Esc 退出",
        DrillKind.SingleChain => "就一个球，点掉立刻在别处出现 · 保持节奏 · 左键 · Esc 退出",
        DrillKind.Reaction => "空屏等待，它出现再点 · 抢点算空枪 · 左键 · Esc 退出",
        DrillKind.MultiChain => "同时好几个，能清多少清多少 · 左键 · Esc 退出",
        DrillKind.Strafe => "目标走走停停，只在它停住的时候开枪 · 左键 · Esc 退出",
        _ => "先点掉边上那个，再点回中间的环 · 算一次 · 左键 · Esc 退出",
    };

    /// <summary>How many targets are alive at once, and whether the round ends on the clock or on a count.</summary>
    public static int DefaultAlive(DrillKind k) => k switch
    {
        DrillKind.Gridshot => 3,
        DrillKind.Sixshot => 6,
        DrillKind.ThreeShot => 3,
        DrillKind.MultiChain => 5,
        _ => 1,
    };

    /// <summary>
    /// The smallest target this drill will draw, however small the player asked for. Tracking asks
    /// for more than the click drills: its target is the thing being followed across a full screen,
    /// and a 22px dot out at the edge is a pixel hunt dressed up as a tracking drill.
    /// </summary>
    public static double MinRadiusPx(DrillKind k) => k switch
    {
        DrillKind.Tracking => 34,
        DrillKind.Strafe => 28,
        _ => 0,
    };

    /// <summary>What the radius box offers for this drill before the player touches it.</summary>
    public static double DefaultRadiusPx(DrillKind k) => k switch
    {
        DrillKind.Tracking => 36,
        DrillKind.Strafe => 30,
        _ => 22,
    };

    public static double DefaultSeconds(DrillKind k) => k switch
    {
        DrillKind.Gridshot => 60,
        DrillKind.MultiChain => 60,
        DrillKind.FlashReset => 60,
        _ => 30,
    };

    public static int DefaultReps(DrillKind k) => k == DrillKind.FlashReset ? 8 : 0;

    /// <summary>Drills where a click is the point. Tracking is held, everything else is clicked.</summary>
    public static bool IsClickDrill(DrillKind k) => k != DrillKind.Tracking;
}
