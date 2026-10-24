using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaUI;

/// <summary>
/// UI-side preferences. Deliberately NOT the driver's settings.json: this file only holds
/// display scaling and trainer inputs, so it can never break Raw Accel compatibility.
/// </summary>
public sealed class UiPrefs
{
    [JsonPropertyName("chartDpi")] public double ChartDpi { get; set; } = 600;
    [JsonPropertyName("chartPollRate")] public int ChartPollRate { get; set; } = 2000;

    [JsonPropertyName("gameSensitivity")] public double GameSensitivity { get; set; } = 1.5;
    [JsonPropertyName("mouseDpi")] public double MouseDpi { get; set; } = 600;

    /// <summary>Physical desktop px between the pointer and a ball centre when it is highlighted.</summary>
    [JsonPropertyName("testDistancePx")] public double TestDistancePx { get; set; } = 420;
    [JsonPropertyName("testRadiusPx")] public double TestRadiusPx { get; set; } = 26;
    [JsonPropertyName("testTrials")] public int TestTrials { get; set; } = 30;
    /// <summary>0 = clear N balls, 1 = clear as many as possible in the time limit.</summary>
    [JsonPropertyName("testMode")] public int TestMode { get; set; }
    [JsonPropertyName("testSeconds")] public int TestSeconds { get; set; } = 60;
    /// <summary>Balls alive on screen; a clicked one respawns elsewhere.</summary>
    [JsonPropertyName("testBalls")] public int TestBalls { get; set; } = 3;
    [JsonPropertyName("testSeed")] public int TestSeed { get; set; } = 20261020;
    /// <summary>Driver device id whose curve is recorded with a round; empty = the one the UI serves.</summary>
    [JsonPropertyName("testDeviceId")] public string TestDeviceId { get; set; } = "";

    /// <summary>Crosshair drawn at the pointer hotspot during the test (physical pixels).</summary>
    [JsonPropertyName("crossArm")] public double CrossArm { get; set; } = 9;
    [JsonPropertyName("crossGap")] public double CrossGap { get; set; } = 3;
    [JsonPropertyName("crossColor")] public string CrossColor { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("crossDot")] public bool CrossDot { get; set; } = true;

    /// <summary>false = borderless fullscreen at the desktop mode; true = switch the display first.</summary>
    [JsonPropertyName("useCustomFullscreenMode")] public bool UseCustomFullscreenMode { get; set; }
    [JsonPropertyName("fullscreenWidth")] public int FullscreenWidth { get; set; }
    /// <summary>0 means "not chosen yet": the current desktop mode is used.</summary>
    [JsonPropertyName("fullscreenHeight")] public int FullscreenHeight { get; set; }
    [JsonPropertyName("fullscreenRefresh")] public int FullscreenRefresh { get; set; }

    /// <summary>常驻托盘：图标存在时关窗口不等于退出。</summary>
    [JsonPropertyName("trayEnabled")] public bool TrayEnabled { get; set; } = true;
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; } = true;
    /// <summary>Only used for an --minimized (autostart) launch: start without a window at all.</summary>
    [JsonPropertyName("startHidden")] public bool StartHidden { get; set; } = true;

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Shared across builds so a new release keeps the same prefs and log history.</summary>
    public static string Path => System.IO.Path.Combine(RaCore.AppData.Root, "raui.ui.json");

    /// <summary>Set when loading or saving failed, so the UI can say so instead of silently reverting.</summary>
    public static string? LastError { get; private set; }

    public static UiPrefs Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var p = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(Path));
                if (p == null) LastError = "偏好文件是空的，已用默认值";
                else { LastError = null; return p; }
            }
            else LastError = null;
        }
        catch (Exception e)
        {
            LastError = "偏好文件读不了（" + e.Message + "），已用默认值";
        }
        return new UiPrefs();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); LastError = null; }
        catch (Exception e) { LastError = "偏好没存下来：" + e.Message; }
    }
}
