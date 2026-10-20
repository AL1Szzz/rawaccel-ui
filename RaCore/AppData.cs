namespace RaCore;

/// <summary>
/// One shared data folder for every build of this UI (%LOCALAPPDATA%\RawAccelUI). Logs, the
/// AI-patch audit trail and the UI prefs used to live next to the executable, so shipping a
/// new release-5 silently orphaned the rounds measured with release-4 and made before/after
/// comparisons impossible. Falls back to the executable directory when app-data is not usable.
/// </summary>
public static class AppData
{
    static readonly Lazy<string> root = new(Resolve);

    public static string Root => root.Value;
    public static string LogDir => Path.Combine(Root, "logs");
    public static string AuditDir => Path.Combine(Root, "patches");

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
    /// Copy the per-directory state over once. Never overwrites: the newest exe wins only when
    /// the shared copy is still missing, so an interrupted migration cannot lose rounds.
    /// </summary>
    static void Migrate(string target)
    {
        try
        {
            var local = AppContext.BaseDirectory;
            CopyInto(Path.Combine(local, "logs"), Path.Combine(target, "logs"));
            CopyInto(Path.Combine(local, "patches"), Path.Combine(target, "patches"));
            var prefs = Path.Combine(target, "raui.ui.json");
            if (!File.Exists(prefs) && File.Exists(Path.Combine(local, "raui.ui.json")))
                File.Copy(Path.Combine(local, "raui.ui.json"), prefs);
        }
        catch { /* a failed migration must not stop the app; the old files are still there */ }
    }

    static void CopyInto(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                var dst = Path.Combine(to, Path.GetFileName(f));
                if (!File.Exists(dst)) File.Copy(f, dst);
            }
        }
        catch { }
    }
}
