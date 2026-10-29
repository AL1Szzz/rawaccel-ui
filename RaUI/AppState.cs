using System.ComponentModel;
using System.Runtime.CompilerServices;
using RaCore;
using RaCore.Model;

namespace RaUI;

/// <summary>
/// App state: one document, the driver, and the derived curves.
///
/// It is one object on purpose - every one of these paths mutates the same document and has to
/// raise the same notifications, so splitting it into four collaborating objects would mostly
/// produce forwarding properties that the XAML then has to traverse. What it is not is one file:
/// the rules worth reading on their own live in their own partial.
///
///   AppState.Driver.cs   - reading and writing the driver and the settings file
///   AppState.Devices.cs  - device entries, and the profile list they point into
///   AppState.Presets.cs  - "我的方案": applying a saved curve, and undoing the last apply
///   AppState.Curves.cs   - the chart: what gets drawn, and what the numbers under it mean
/// </summary>
public sealed partial class AppState : INotifyPropertyChanged
{
    public const double DEFAULT_TIME_MIN = 0.0625;
    public const double DEFAULT_TIME_MAX = 100;

    public string SettingsPath { get; }

    RootDto doc = new();
    RootDto saved = new();          // last state that was written to disk / driver
    ProfileDto selected;
    string status = "";
    string driverInfo = "驱动未连接";
    bool driverReachable;
    bool busy;

    public event PropertyChangedEventHandler PropertyChanged;

    public AppState(string settingsPath)
    {
        SettingsPath = settingsPath;
        selected = doc.Profiles.FirstOrDefault();
    }

    // ---------- bound properties ----------

    public RootDto Doc
    {
        get => doc;
        private set { doc = value; Raise(); Raise(nameof(Profiles)); Raise(nameof(SelectedProfile)); Raise(nameof(IsDirty)); }
    }

    public System.Collections.ObjectModel.ObservableCollection<ProfileDto> Profiles { get; } = new();

    public ProfileDto SelectedProfile
    {
        get => selected;
        set { selected = value; Raise(); RebuildCurves(); }
    }

    public string Status
    {
        get => status;
        set { status = value; Raise(); }
    }

    public string DriverInfo
    {
        get => driverInfo;
        set { driverInfo = value; Raise(); }
    }

    public bool DriverReachable
    {
        get => driverReachable;
        set { driverReachable = value; Raise(); Raise(nameof(CanWrite)); }
    }

    public bool Busy
    {
        get => busy;
        set { busy = value; Raise(); Raise(nameof(CanUndoSettings)); }
    }

    public bool CanWrite => DriverReachable && !Busy;

    // IsDirty used to serialize BOTH documents on every property notification (status bar, tray
    // tip, chart, preset bar all read it), which is two full JSON writes per keystroke. Hash the
    // draft once per change instead: same answer, no per-notification cost.
    string docHash = "", savedHash = "";
    public bool IsDirty => docHash != savedHash;

    /// <summary>Call after assigning <see cref="saved"/> so both sides of the dirty check agree.</summary>
    void SetSaved()
    {
        saved = doc.DeepCopy();
        savedHash = ConfigHash.OfDoc(saved);
        docHash = ConfigHash.OfDoc(doc);
    }

    /// <summary>
    /// True when what is on screen was not read from the settings file: the file was missing or
    /// unreadable AND the driver had nothing to fall back to, so the UI is showing synthesized
    /// defaults. Login-time --write must never act on that - it would replace a real curve with a
    /// default one after a transient file error.
    /// </summary>
    public bool LoadIsSynthetic { get; private set; }

    /// <summary>UI preferences (display scaling, game sens). Never written to the driver.</summary>
    public static UiPrefs Prefs { get; private set; } = UiPrefs.Load();
    public static void SavePrefs() => Prefs.Save();

    // ---------- change notification ----------

    void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new(n));

    /// <summary>Call after mutating the document in place. Refreshes the dirty flag and, when the
    /// selected profile actually changed, redraws the chart right away instead of on the next tick.</summary>
    public void Touch()
    {
        docHash = ConfigHash.OfDoc(doc);
        Raise(nameof(IsDirty));
        Raise(nameof(Doc));
        RefreshCurveIfChanged();
    }

    public void RefreshAfterExternalEdit() => Raise(nameof(SelectedProfile));
}
