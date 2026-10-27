using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace RaUI;

/// <summary>
/// Login-time autostart, written into HKCU\...\Run (no admin needed).
/// The app re-applies settings.json to the driver at logon when --write is passed,
/// which is what the official writer.exe Startup shortcut does.
/// </summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RawAccelModernUI";

    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;

    public static string CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled => CurrentCommand() != null;

    /// <summary>
    /// installs "app --minimized --write" so the driver gets settings at logon without a window in
    /// your face. The settings path travels with it: without --settings, a login run resolves the
    /// file by guessing again, so anyone pointed at a non-default settings.json (a second install,
    /// a portable copy, settings-path.txt) would silently autostart against a DIFFERENT file.
    /// </summary>
    public static void Enable(bool writeAtLogon, bool minimized, string? settingsPath = null)
    {
        var args = new List<string>();
        if (minimized) args.Add("--minimized");
        if (writeAtLogon) args.Add("--write");
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            args.Add("--settings");
            args.Add("\"" + Path.GetFullPath(settingsPath) + "\"");
        }
        string cmd = $"\"{ExePath}\"" + (args.Count > 0 ? " " + string.Join(" ", args) : "");
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, cmd);
    }

    /// <summary>The settings file a given login command would use, or null when it does not pin one.</summary>
    public static string? SettingsPathOf(string? command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        int i = command.IndexOf("--settings", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = command[(i + "--settings".Length)..].Trim();
        if (rest.Length == 0) return null;
        if (rest[0] == '"')
        {
            int end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : rest[1..];
        }
        int sp = rest.IndexOf(' ');
        return sp > 0 ? rest[..sp] : rest;
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, false);
    }

    /// <summary>
    /// Put back a command string captured with CurrentCommand() - used by --autostart-check so a
    /// test round trip can never leave the user's login entry changed.
    /// </summary>
    public static void WriteRaw(string? command)
    {
        if (string.IsNullOrEmpty(command)) { Disable(); return; }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, command);
    }

    /// <summary>Reports the legacy official writer.exe Startup shortcut, if the user still has it.</summary>
    public static (bool exists, string path, string args)? LegacyWriterShortcut()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        foreach (var f in Directory.GetFiles(dir, "*.lnk"))
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"))!;
                dynamic sc = sh.CreateShortcut(f);
                string target = sc.TargetPath;
                if (Path.GetFileName(target).Equals("writer.exe", StringComparison.OrdinalIgnoreCase))
                    return (true, f, sc.Arguments);
            }
            catch { /* shell link reading is best effort */ }
        }
        return null;
    }
}
