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

    /// <summary>installs "app --minimized --write" so the driver gets settings at logon without a window in your face.</summary>
    public static void Enable(bool writeAtLogon, bool minimized)
    {
        var args = new List<string>();
        if (minimized) args.Add("--minimized");
        if (writeAtLogon) args.Add("--write");
        string cmd = $"\"{ExePath}\"" + (args.Count > 0 ? " " + string.Join(" ", args) : "");
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, cmd);
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, false);
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
