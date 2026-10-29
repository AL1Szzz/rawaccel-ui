using RaCore.Model;

namespace RaCore;

/// <summary>
/// The variants the blind comparison runs on. Each one is a complete copy of the player's profile
/// with a single number changed, so a round measures the curve and nothing else.
///
/// Which number is "the amount of acceleration" depends on the mode, and the modes are genuinely
/// different shapes - scaling classic's slope and scaling synchronous's motivity are not the same
/// act, only the same intent. <see cref="KnobFor"/> is the mapping, and it is deliberately small:
/// one knob per mode, because a sweep the player cannot describe afterwards is noise with a label.
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
            _ => "加速度 acceleration",
        };
    }

    public static bool Sweepable(ProfileDto p)
        => KnobFor(p.X.Mode) != Knob.None;

    public static double Get(ProfileDto p, Knob knob = Knob.Amount)
    {
        var a = p.X;
        return knob switch
        {
            Knob.Cap => a.Cap.X,
            Knob.Offset => a.InputOffset,
            _ => a.Mode.Trim().ToLowerInvariant() switch
            {
                "natural" => a.DecayRate,
                "synchronous" => a.Motivity,
                "power" => a.Scale,
                "lut" => LutAmount(a),
                _ => a.Acceleration,
            },
        };
    }

    public static void Set(ProfileDto p, double v, Knob knob = Knob.Amount)
    {
        var a = p.X;
        switch (knob)
        {
            case Knob.Cap: a.Cap.X = v; break;
            case Knob.Offset: a.InputOffset = v; break;
            default:
                switch (a.Mode.Trim().ToLowerInvariant())
                {
                    case "natural": a.DecayRate = v; break;
                    case "synchronous": a.Motivity = v; break;
                    case "power": a.Scale = v; break;
                    case "lut": ScaleLut(a, v); break;
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
        public bool IsBase;
        public bool IsNoAccel;
        public ProfileDto Profile = new();
    }

    public const double NoAccelSentinel = -1;

    /// <summary>
    /// Builds the candidate list. <paramref name="multipliers"/> are applied to the current value of
    /// the swept knob; 1.0 must be among them or the player's own curve is never tested and the
    /// "recommendation" would always be to change something.
    /// </summary>
    public static List<Candidate> Build(ProfileDto baseProfile, IReadOnlyList<double> multipliers, bool includeNoAccel = true)
    {
        var list = new List<Candidate>();
        var mode = (baseProfile.X.Mode ?? "").Trim().ToLowerInvariant();

        if (includeNoAccel)
        {
            var flat = baseProfile.Clone();
            flat.X.Mode = "noaccel";
            flat.Y.Mode = "noaccel";
            flat.X.Acceleration = 0;
            flat.Y.Acceleration = 0;
            list.Add(new Candidate
            {
                Secret = "无加速（对照）",
                KnobValue = NoAccelSentinel,
                IsNoAccel = true,
                Profile = flat,
            });
        }

        double current = Get(baseProfile);
        foreach (double m in multipliers)
        {
            var p = baseProfile.Clone();
            double v = current * m;
            Set(p, v);
            list.Add(new Candidate
            {
                Secret = mode == "lut"
                    ? $"查表幅度 ×{m:0.##}"
                    : $"{KnobName(mode)} = {v:0.####}（当前 ×{m:0.##}）",
                KnobValue = v,
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

    /// <summary>The multipliers used unless the player asks for something else: a wide enough spread
    /// that a 15-second round can actually tell them apart, with 1.0 (their own curve) in the middle.</summary>
    public static readonly double[] DefaultMultipliers = { 0.5, 1.0, 1.5, 2.0 };
}
