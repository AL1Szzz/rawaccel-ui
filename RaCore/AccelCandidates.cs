using RaCore.Model;

namespace RaCore;

/// <summary>Which of the two axes a change is written to. A per-axis profile has two independent
/// curves, and a sweep that only touches one of them silently tests half a curve.</summary>
public enum AxisTarget { Both = 0, Horizontal = 1, Vertical = 2 }

/// <summary>Which family the blind comparison builds.</summary>
public enum Plan
{
    /// <summary>One parameter, both axes, scaled by the same multiplier. The original sweep.</summary>
    Uniform = 0,
    /// <summary>
    /// Wrist/claw: horizontal gets a later threshold and more accel when it does arrive, vertical
    /// gets an earlier threshold and more accel from the first millimetre. One strength number.
    /// </summary>
    Wrist = 1,
}

/// <summary>
/// The variants the blind comparison runs on. Each one is a complete copy of the player's profile
/// with a single number changed, so a round measures the curve and nothing else.
///
/// Which number is "the amount of acceleration" depends on the mode, and the modes are genuinely
/// different shapes - scaling classic's slope and scaling synchronous's motivity are not the same
/// act, only the same intent. <see cref="KnobFor"/> is the mapping, and it is deliberately small:
/// one knob per mode, because a sweep the player cannot describe afterwards is noise with a label.
///
/// Two things changed here after the first version shipped, and both were the same mistake in
/// different clothes - "the profile" was being treated as one curve:
///   * the sweep only ever wrote <c>X</c>. On a whole-mode profile that is correct, the driver
///     ignores Y; on a per-axis profile it means the vertical curve never moved for 35 rounds and
///     the session measured half the thing it claimed to. Every candidate now writes both axes.
///   * jump's amount was <c>acceleration</c>, which the jump evaluator never reads. Its "how much"
///     is the jump multiplier in <c>Cap.y</c>, and its threshold is the speed in <c>Cap.x</c>.
///
/// What this cannot do: two candidates are only comparable if the rest of the profile is identical,
/// and "no acceleration" is not the same kind of thing as "half as much acceleration" - it is a
/// different shape, not a weaker one. It is included as a control, and the report must say so,
/// because a player who beats their own curve against no-accel by a hair has learned very little.
/// </summary>
public static class AccelCandidates
{
    public enum Knob { Amount, Cap, Offset, None }

    /// <summary>The one parameter worth sweeping for each mode; lut stores its curve in Data.</summary>
    public static Knob KnobFor(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "classic" => Knob.Amount,
        "jump" => Knob.Amount,
        "natural" => Knob.Amount,       // decayRate: how fast the gain leaves its floor
        "synchronous" => Knob.Amount,   // motivity: how hard it pushes toward syncSpeed
        "power" => Knob.Amount,         // scale
        "lut" => Knob.Amount,           // the table itself, stretched around 1.0
        _ => Knob.None,                 // noaccel, or a mode we were never told about
    };

    /// <summary>Short human name of the swept parameter, for the report table.</summary>
    public static string KnobName(string mode, Knob knob = Knob.Amount)
    {
        if (knob == Knob.None) return "—";
        return (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "natural" => "衰减率 decayRate",
            "synchronous" => "motivity",
            "power" => "scale",
            "lut" => "查表幅度",
            "jump" => knob == Knob.Offset ? "跳跃速度 Cap.x" : "跳跃倍率 Cap.y",
            "classic" => knob == Knob.Offset ? "起始阈值 inputOffset" : "加速度 acceleration",
            _ => knob == Knob.Offset ? "起始阈值" : "加速度 acceleration",
        };
    }

    static string ModeOf(AccelDto a) => (a.Mode ?? "").Trim().ToLowerInvariant();

    public static bool Sweepable(ProfileDto p)
        => KnobFor(p.X.Mode) != Knob.None
        || (!p.Speed.Whole && KnobFor(p.Y.Mode) != Knob.None);

    public static AccelDto AxisOf(ProfileDto p, bool horizontal) => horizontal ? p.X : p.Y;

    public static double Get(ProfileDto p, Knob knob = Knob.Amount, bool horizontal = true)
        => GetFrom(AxisOf(p, horizontal), knob);

    public static double GetFrom(AccelDto a, Knob knob = Knob.Amount) => knob switch
    {
        Knob.Cap => a.Cap.X,
        Knob.Offset => ThresholdOf(a),
        _ => AmountOf(a),
    };

    /// <summary>
    /// "How much acceleration", per mode. Jump has no acceleration field at all - the driver reads
    /// only its cap pair, so the honest amount is the multiplier the gain jumps to.
    /// </summary>
    static double AmountOf(AccelDto a) => ModeOf(a) switch
    {
        "natural" => a.DecayRate,
        "synchronous" => a.Motivity,
        "power" => a.Scale,
        "lut" => LutAmount(a),
        "jump" => a.Cap.Y,
        _ => a.Acceleration,
    };

    /// <summary>
    /// The input speed where the multiplier leaves its floor, in the axis's own units. Jump keeps
    /// that speed in Cap.x; classic and natural keep it in inputOffset; synchronous, power and lut
    /// have no floor to move - their gain rises from the very first count.
    /// </summary>
    public static bool HasThreshold(AccelDto a) => ModeOf(a) is "classic" or "natural" or "jump";

    public static double ThresholdOf(AccelDto a) => ModeOf(a) == "jump" ? a.Cap.X : a.InputOffset;

    public static void SetThreshold(AccelDto a, double v)
    {
        v = Math.Max(0, v);
        if (ModeOf(a) == "jump") { a.Cap.X = v; return; }

        // classic/natural with an input-side cap: an offset at or past the cap leaves an empty
        // acceleration range, which is a curve the official GUI refuses to write and the driver
        // reads as garbage. Stop short of it rather than shipping an invalid candidate.
        bool outputCap = string.Equals(a.CapMode, "output", StringComparison.OrdinalIgnoreCase);
        if (!outputCap && a.Cap.X > 0) v = Math.Min(v, a.Cap.X * 0.9);
        a.InputOffset = v;
    }

    public static void Set(ProfileDto p, double v, Knob knob = Knob.Amount, AxisTarget axis = AxisTarget.Both)
    {
        if (axis is AxisTarget.Both or AxisTarget.Horizontal) SetOn(p.X, v, knob);
        if (axis is AxisTarget.Both or AxisTarget.Vertical) SetOn(p.Y, v, knob);
    }

    public static void SetOn(AccelDto a, double v, Knob knob = Knob.Amount)
    {
        switch (knob)
        {
            case Knob.Cap: a.Cap.X = Math.Max(0, v); break;
            case Knob.Offset: SetThreshold(a, v); break;
            default:
                switch (ModeOf(a))
                {
                    case "natural": a.DecayRate = v; break;
                    case "synchronous": a.Motivity = v; break;
                    case "power": a.Scale = v; break;
                    case "lut": ScaleLut(a, v); break;
                    case "jump": a.Cap.Y = v; break;
                    default: a.Acceleration = v; break;
                }
                break;
        }
    }

    /// <summary>
    /// A lut is a table of output/input ratios, so "how much acceleration" is how far the table
    /// stands away from 1.0. Scaling the distance from 1.0 keeps the shape and the zero point.
    /// </summary>
    static double LutAmount(AccelDto a)
    {
        if (a.Data.Count == 0) return 0;
        return a.Data.Average(d => Math.Abs(d - 1));
    }

    static void ScaleLut(AccelDto a, double target)
    {
        double now = LutAmount(a);
        if (a.Data.Count == 0) return;
        double k = now > 1e-9 ? target / now : 0;
        for (int i = 0; i < a.Data.Count; i++)
            a.Data[i] = (float)(1 + (a.Data[i] - 1) * k);
    }

    public sealed class Candidate
    {
        /// <summary>Position in the session, 0-based. The player only ever sees this, never the value.</summary>
        public int Index;
        /// <summary>Shown after the session. During it, "候选 3" is all anyone learns.</summary>
        public string Secret = "";
        /// <summary>Short public label for the round banner - must not reveal the value.</summary>
        public string BlindLabel = "";
        public double KnobValue;
        /// <summary>The multiplier or strength this candidate was built from; what the report groups by.</summary>
        public double Strength;
        public bool IsBase;
        public bool IsNoAccel;
        /// <summary>True for the wrist family, where one strength moves four numbers at once.</summary>
        public bool IsSplit;
        public ProfileDto Profile = new();
    }

    public const double NoAccelSentinel = -1;

    /// <summary>
    /// Builds the candidate list. <paramref name="multipliers"/> are applied to the current value of
    /// the swept knob on both axes; 1.0 must be among them or the player's own curve is never tested
    /// and the "recommendation" would always be to change something.
    /// </summary>
    public static List<Candidate> Build(ProfileDto baseProfile, IReadOnlyList<double> multipliers,
                                        bool includeNoAccel = true, AxisTarget axis = AxisTarget.Both)
    {
        var list = new List<Candidate>();

        if (includeNoAccel)
        {
            var flat = baseProfile.Clone();
            flat.X.Mode = "noaccel";
            flat.Y.Mode = "noaccel";
            // The mode alone is what removes the acceleration - the driver never reads a parameter
            // in noaccel mode. Zeroing them too used to make the control the one candidate the
            // document validator refused (acceleration must be > 0), which is a control nobody can
            // push, on a session whose whole point is comparing against it.
            list.Add(new Candidate
            {
                Secret = "无加速（对照）",
                KnobValue = NoAccelSentinel,
                Strength = 0,
                IsNoAccel = true,
                Profile = flat,
            });
        }

        foreach (double m in multipliers)
        {
            var p = baseProfile.Clone();
            bool touchX = axis is AxisTarget.Both or AxisTarget.Horizontal;
            bool touchY = axis is AxisTarget.Both or AxisTarget.Vertical;
            double vx = GetFrom(p.X) * m, vy = GetFrom(p.Y) * m;
            if (touchX) SetOn(p.X, vx);
            if (touchY) SetOn(p.Y, vy);

            string modeX = ModeOf(p.X), modeY = ModeOf(p.Y);
            string text = (touchX, touchY) switch
            {
                (true, true) => modeX == modeY
                    ? $"{KnobName(modeX)} 水平 {vx:0.####} / 垂直 {vy:0.####}（两轴各 ×{m:0.##}）"
                    : $"水平 {KnobName(modeX)} {vx:0.####} / 垂直 {KnobName(modeY)} {vy:0.####}（各 ×{m:0.##}）",
                (true, false) => $"水平 {KnobName(modeX)} {vx:0.####}（×{m:0.##}，垂直不动）",
                _ => $"垂直 {KnobName(modeY)} {vy:0.####}（×{m:0.##}，水平不动）",
            };
            list.Add(new Candidate
            {
                Secret = text,
                KnobValue = vx,
                Strength = m,
                IsBase = Math.Abs(m - 1.0) < 1e-9,
                Profile = p,
            });
        }

        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i;
            list[i].BlindLabel = $"候选 {i + 1}";
        }
        return list;
    }

    /// <summary>
    /// The wrist/claw family. One strength number, four consequences, because that is what the
    /// complaint actually was: "my hand barely moves, so a big horizontal swipe should get
    /// acceleration and a small one should not, and vertical should get some from the first
    /// millimetre because I can only move down a few of them."
    ///
    ///   strength s is applied as:  horizontal threshold x s, horizontal amount x s,
    ///                              vertical threshold / s, vertical amount x s
    ///
    /// s = 1 is exactly the player's own curve, so the base candidate is in the set and the sweep
    /// can honestly say "yours was best". Raising the horizontal threshold is what keeps small
    /// corrections at 1:1 - below the threshold the multiplier does not leave 1.0 at all, whatever
    /// the amount is - and the matching amount increase is what makes the far end still arrive.
    /// Lowering the vertical threshold is what makes a two-millimetre pull-down count.
    ///
    /// It forces per-axis mode (<c>Speed.Whole = false</c>): in whole mode the driver never reads the
    /// vertical parameters, and a split that cannot be delivered is not a candidate.
    ///
    /// This moves four numbers per candidate, which breaks the "one knob" rule the uniform sweep
    /// keeps. That is the trade: the thing being tuned here is a relationship between the axes, not a
    /// parameter, and the report says so rather than dressing it up as a single number.
    /// </summary>
    public static List<Candidate> BuildWrist(ProfileDto baseProfile, IReadOnlyList<double> multipliers,
                                             bool includeNoAccel = true)
    {
        var list = new List<Candidate>();

        if (includeNoAccel)
        {
            var flat = baseProfile.Clone();
            flat.X.Mode = "noaccel";
            flat.Y.Mode = "noaccel";
            list.Add(new Candidate
            {
                Secret = "无加速（对照）",
                KnobValue = NoAccelSentinel,
                Strength = 0,
                IsNoAccel = true,
                Profile = flat,
            });
        }

        double xAmt = GetFrom(baseProfile.X), yAmt = GetFrom(baseProfile.Y);
        bool xHasFloor = HasThreshold(baseProfile.X), yHasFloor = HasThreshold(baseProfile.Y);
        double xOff = xHasFloor ? GetFrom(baseProfile.X, Knob.Offset) : 0;
        double yOff = yHasFloor ? GetFrom(baseProfile.Y, Knob.Offset) : 0;

        foreach (double raw in multipliers)
        {
            double s = Math.Max(0.05, raw);
            var p = baseProfile.Clone();
            p.Speed.Whole = false;

            SetOn(p.X, xAmt * s);
            SetOn(p.Y, yAmt * s);
            if (xHasFloor) SetOn(p.X, xOff * s, Knob.Offset);
            if (yHasFloor) SetOn(p.Y, yOff / s, Knob.Offset);

            string xPart = xHasFloor
                ? $"水平阈值 {xOff:0.###}→{GetFrom(p.X, Knob.Offset):0.###}、{KnobName(ModeOf(p.X))} {xAmt:0.####}→{GetFrom(p.X):0.####}"
                : $"水平 {KnobName(ModeOf(p.X))} {xAmt:0.####}→{GetFrom(p.X):0.####}";
            string yPart = yHasFloor
                ? $"垂直阈值 {yOff:0.###}→{GetFrom(p.Y, Knob.Offset):0.###}、{KnobName(ModeOf(p.Y))} {yAmt:0.####}→{GetFrom(p.Y):0.####}"
                : $"垂直 {KnobName(ModeOf(p.Y))} {yAmt:0.####}→{GetFrom(p.Y):0.####}";

            list.Add(new Candidate
            {
                Secret = $"手腕流 ×{s:0.##}：{xPart}；{yPart}",
                KnobValue = s,
                Strength = s,
                IsBase = Math.Abs(s - 1.0) < 1e-9,
                IsSplit = true,
                Profile = p,
            });
        }

        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i;
            list[i].BlindLabel = $"候选 {i + 1}";
        }
        return list;
    }

    public static List<Candidate> BuildFor(Plan plan, ProfileDto baseProfile, IReadOnlyList<double> multipliers,
                                           bool includeNoAccel = true)
        => plan == Plan.Wrist
            ? BuildWrist(baseProfile, multipliers, includeNoAccel)
            : Build(baseProfile, multipliers, includeNoAccel);

    /// <summary>What the session is sweeping, in one line, for the intro card.</summary>
    public static string Describe(ProfileDto p, Plan plan)
    {
        string shape = p.Speed.Whole
            ? "整体模式（水平和垂直共用一条曲线）"
            : "分轴模式（水平和垂直各算各的）";
        string x = $"水平 {p.X.Mode}：{AxisText(p.X)}";
        string y = $"垂直 {p.Y.Mode}：{AxisText(p.Y)}";
        string how = plan == Plan.Wrist
            ? "手腕流：水平的起始阈值往后推、垂直的往前推，两轴的强度都按倍率走 —— 大幅度横移才吃加速，小幅定位不吃；垂直一点点就吃。"
            : "两轴同步：水平和垂直的同一个参数一起按倍率缩放。";
        return $"{shape} · {x} · {y}\n\n{how}";
    }

    static string AxisText(AccelDto a)
    {
        string amount = $"{KnobName(ModeOf(a))} = {GetFrom(a):0.####}";
        return HasThreshold(a) ? $"{KnobName(ModeOf(a), Knob.Offset)} = {ThresholdOf(a):0.###}，{amount}" : amount;
    }

    /// <summary>The multipliers used unless the player asks for something else: a wide enough spread
    /// that a 15-second round can actually tell them apart, with 1.0 (their own curve) in the middle.</summary>
    public static readonly double[] DefaultMultipliers = { 0.5, 1.0, 1.5, 2.0 };
}
