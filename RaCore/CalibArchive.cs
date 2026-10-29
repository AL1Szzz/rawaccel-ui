using System.Text.Json;
using System.Text.Json.Serialization;
using RaCore.Model;

namespace RaCore;

/// <summary>
/// Every blind comparison writes itself to disk, one file per session, and rewrites the file after
/// every round.
///
/// It exists because of one afternoon: the report screen threw on "apply", the process died, and
/// thirty-five rounds of work went with it - the rounds only ever lived in the window's own lists.
/// A session is fifteen minutes of somebody's life and the whole reason the window has a report at
/// all, so it is saved while it is happening rather than at the end, when there may not be an end.
///
/// The file is not a settings file and is never loaded as one. It carries the winning and original
/// curves as their own embedded documents, so a result can be re-applied or saved as a preset long
/// after the session, without touching anything the mouse is currently using.
/// </summary>
public static class CalibArchive
{
    /// <summary>Test hook: point the library at a scratch folder so a self-test cannot disturb real sessions.</summary>
    public static string? DirOverride;

    public static string Dir => Path.Combine(DirOverride ?? AppData.Root, "calib-sessions");

    public sealed class CandidateRow
    {
        public int Index { get; set; }
        public string Blind { get; set; } = "";
        public string Secret { get; set; } = "";
        public bool IsBase { get; set; }
        public bool IsNoAccel { get; set; }
        /// <summary>Null until the report exists; NaN would not survive a JSON round trip.</summary>
        public double? MeanRank { get; set; }
        public double? MeanConsistency { get; set; }
    }

    public sealed class RoundRow
    {
        public string Phase { get; set; } = "";
        public string Item { get; set; } = "";
        public int Candidate { get; set; }
        public double Skill { get; set; }
        /// <summary>Null for drills that never clicked (tracking), where there is nothing to measure.</summary>
        public double? Consistency { get; set; }
        public string Detail { get; set; } = "";
        public bool Valid { get; set; } = true;
        public string Problem { get; set; } = "";
    }

    public sealed class Session
    {
        public string Id { get; set; } = "";
        public string SavedAt { get; set; } = "";
        public string Family { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string BaseLabel { get; set; } = "";
        public int RoundsPlanned { get; set; }
        public List<CandidateRow> Candidates { get; set; } = new();
        public List<RoundRow> Rounds { get; set; } = new();

        public bool Finished { get; set; }
        public string Headline { get; set; } = "";
        public List<string> Reasons { get; set; } = new();
        public int WinnerIndex { get; set; } = -1;
        public bool WinnerIsBase { get; set; }
        public string WinnerLabel { get; set; } = "";

        /// <summary>The winning curve as a whole settings document, so it can be re-applied later.</summary>
        public string WinnerJson { get; set; } = "";
        /// <summary>The curve the player had before the session, for the same reason.</summary>
        public string BaseJson { get; set; } = "";

        public bool Applied { get; set; }
        public bool SavedAsPlan { get; set; }
        public string PlanName { get; set; } = "";
    }

    public static string NewId() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

    /// <summary>Returns the file written, or null with a reason in <paramref name="error"/>.</summary>
    public static string? Save(Session s, out string error)
    {
        error = "";
        if (s == null) { error = "没有内容可存"; return null; }
        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = NewId();
        s.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        try
        {
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, SafeFile(s.Id) + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(s, Options));
            return file;
        }
        catch (Exception e) { error = "存档写入失败：" + e.Message; return null; }
    }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>File names come from a timestamp and must never be able to escape the folder.</summary>
    public static string SafeFile(string id)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in (id ?? "").Trim())
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
        return sb.Length == 0 ? NewId() : sb.ToString();
    }

    public static List<Session> List(int max = 20)
    {
        var outList = new List<Session>();
        try
        {
            if (!Directory.Exists(Dir)) return outList;
            foreach (var file in Directory.GetFiles(Dir, "*.json")
                         .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, max)))
            {
                var s = Load(file);
                if (s != null) outList.Add(s);
            }
        }
        catch { /* an unreadable archive is an empty archive; the UI just offers nothing */ }
        return outList;
    }

    public static Session? Latest() => List(1).FirstOrDefault();

    public static Session? Load(string file)
    {
        try { return JsonSerializer.Deserialize<Session>(File.ReadAllText(file)); }
        catch { return null; }
    }

    /// <summary>The curve a session ended on, ready to apply or to save as a preset. Null when the
    /// session never reached a report.</summary>
    public static ProfileDto? WinnerProfile(Session s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.WinnerJson)) return null;
        try
        {
            var doc = Json.Deserialize(s.WinnerJson);
            return doc.Profiles.Count > 0 ? doc.Profiles[0] : null;
        }
        catch { return null; }
    }

    public static ProfileDto? BaseProfile(Session s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.BaseJson)) return null;
        try
        {
            var doc = Json.Deserialize(s.BaseJson);
            return doc.Profiles.Count > 0 ? doc.Profiles[0] : null;
        }
        catch { return null; }
    }

    /// <summary>A whole settings document holding just this profile, for Json.Serialize.</summary>
    public static string Embedded(ProfileDto p) =>
        Json.Serialize(new RootDto { Profiles = { p.Clone() } });

    /// <summary>One line a player can read later: when it ran and what it decided.</summary>
    public static string Summarize(Session s)
    {
        if (s == null) return "";
        string when = s.SavedAt.Length > 0 ? s.SavedAt : s.Id.Replace("_", " ");
        string what = s.Finished
            ? (s.Headline.Length > 0 ? s.Headline : "没有结论")
            : $"没跑完（{s.Rounds.Count} 轮）";
        return $"{when} · {what}";
    }
}
