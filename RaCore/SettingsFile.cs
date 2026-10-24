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
    /// Candidate locations, most-official first. The last one is the folder this project was
    /// developed against, so the author's own layout keeps working without a special case.
    /// </summary>
    public static List<string> Candidates()
    {
        var list = new List<string>();
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
        list.Add(@"D:\RawAccel\settings.json");

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
