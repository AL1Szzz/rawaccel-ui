namespace RaCore;

/// <summary>
/// One shared data folder for every build of this UI (%LOCALAPPDATA%\RawAccelUI). The ball-test
/// history and the UI prefs used to live next to the executable, so shipping a new release
/// silently orphaned them; everything now resolves here, with a fallback to the executable
/// directory when app-data is not usable.
/// </summary>
public static class AppData
{
    static readonly Lazy<string> root = new(Resolve);

    public static string Root => root.Value;

    /// <summary>What the folder actually resolved to, for the settings page to display.</summary>
    public static string Location => Root;

    static string Resolve()
    {
        string target;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            target = string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "RawAccelUI");
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".write");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
        }
        catch
        {
            return AppContext.BaseDirectory;      // read-only profile or redirected folder
        }

        Migrate(target);
        return target;
    }

    /// <summary>
    /// Pick up prefs that an older build left next to the exe, once. Never overwrites, so an
    /// interrupted migration cannot lose the newer copy.
    /// </summary>
    static void Migrate(string target)
    {
        try
        {
            var local = AppContext.BaseDirectory;
            if (string.Equals(local, target, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var name in new[] { "raui.ui.json", "test-history.csv" })
            {
                var dst = Path.Combine(target, name);
                var src = Path.Combine(local, name);
                if (!File.Exists(dst) && File.Exists(src)) File.Copy(src, dst);
            }
        }
        catch { /* a failed migration must not stop the app; the old files are still there */ }
    }
}
