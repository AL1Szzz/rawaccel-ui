namespace RaCore;

/// <summary>
/// Where the official Raw Accel GUI keeps its settings.json. This UI writes the same file so the
/// two tools stay interchangeable - which means the path has to be FOUND, not assumed: the
/// install folder differs between machines, and a wrong guess would silently split the config
/// into two files that disagree.
/// </summary>
public static class SettingsFile
{
    /// <summary>
    /// Optional one-line file naming the settings.json to use, for an install layout no candidate
    /// list can guess. It lives in the data folder rather than in the source, so a personal path
    /// stays on the machine that owns it.
    /// </summary>
    public static string OverrideFile => Path.Combine(AppData.Root, "settings-path.txt");

    static string? ReadOverride()
    {
        try
        {
            if (!File.Exists(OverrideFile)) return null;
            var s = File.ReadAllText(OverrideFile).Trim();
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>Candidate locations, most-official first. The override, when present, wins.</summary>
    public static List<string> Candidates() => Candidates(ReadOverride());

    /// <summary>
    /// The same list with one path forced to the front - split out so the ordering is testable
    /// without writing into the real data folder.
    /// </summary>
    public static List<string> Candidates(string? extraFirst)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraFirst)) list.Add(extraFirst.Trim());
        void AddUnder(string? dir, string sub)
        {
            if (string.IsNullOrEmpty(dir)) return;
            list.Add(Path.Combine(dir, sub, "settings.json"));
            list.Add(Path.Combine(dir, "settings.json"));
        }

        const string official = "Raw Accel";
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), official);
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), official);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) AddUnder(Path.Combine(local, "Programs"), official);
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), official);
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), official);
        AddUnder(AppContext.BaseDirectory, official);

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The first candidate that exists, or null when the official file is nowhere. Never throws.</summary>
    public static string? Find()
    {
        foreach (var f in Candidates())
        {
            try { if (File.Exists(f)) return f; } catch { /* an unreadable candidate is just not it */ }
        }
        return null;
    }

    /// <summary>
    /// The file to read and write: the official one when it is there, otherwise our own data
    /// folder. There is always a writable answer, because the UI has to be able to save even on a
    /// machine where Raw Accel was installed somewhere unusual.
    /// </summary>
    public static string Default() => Find() ?? Path.Combine(AppData.Root, "settings.json");
}
