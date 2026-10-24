using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI;

public sealed record CurvePoint(double X, double Y);

public sealed class CurveSeries
{
    public string Name { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.White;
    public double Thickness { get; init; } = 2;
    public bool Dashed { get; init; }
    public List<CurvePoint> Points { get; init; } = new();
}

public enum CurveKind { Sensitivity, Gain }

/// <summary>A vertical reference line that says what it is: an unlabeled dashed line is noise.</summary>
public sealed record ChartMarker(double At, string Label, Brush Color);

/// <summary>A stretch of the speed axis where the curve behaves the same way, shaded so the eye
/// reads "no acceleration / accelerating / capped" without a legend.</summary>
public sealed record ChartZone(double From, double To, string Name, Brush Tint);

/// <summary>App state: one document, the driver, and the derived curves.</summary>
public sealed class AppState : INotifyPropertyChanged
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
    RootDto previousSettings;
    public bool CanUndoSettings => previousSettings != null && !Busy;
    CurveKind curveKind = CurveKind.Sensitivity;

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

    public bool IsDirty => Json.Serialize(doc) != Json.Serialize(saved);

    public CurveKind CurveKind
    {
        get => curveKind;
        set { curveKind = value; Raise(); RebuildCurves(); }
    }

    // chart display scaling (does not affect the driver at all - mirrors the official
    // "Scale by DPI and Poll Rate" menu, which is display-only too)
    double chartDpi;
    int chartPollRate;
    public double ChartDpi { get => chartDpi; set { chartDpi = value; Raise(); RebuildCurves(); } }
    public int ChartPollRate { get => chartPollRate; set { chartPollRate = value; Raise(); RebuildCurves(); } }

    public List<CurveSeries> Series { get; private set; } = new();
    public string XAxisLabel { get; private set; } = "输入速度 (counts/ms)";
    public string YAxisLabel { get; private set; } = "灵敏度";

    // ---------- chart output ----------

    public List<ChartMarker> Markers { get; private set; } = new();
    public List<ChartZone> Zones { get; private set; } = new();

    /// <summary>Re-evaluating the whole curve costs ~120ms, so the chart only rebuilds when the
    /// document actually changed - not on every animation frame.</summary>
    public void StartAutoRefresh()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var hash = ConfigHash.OfDoc(doc);
            if (hash != lastCurveHash) RebuildCurves();
        };
        timer.Start();
    }

    string lastCurveHash = "";

    /// <summary>UI preferences (display scaling, game sens). Never written to the driver.</summary>
    public static UiPrefs Prefs { get; private set; } = UiPrefs.Load();
    public static void SavePrefs() => Prefs.Save();

    /// <summary>
    /// The curve expressed in units you can act on: cm/s of hand motion, degrees/s of
    /// crosshair travel, and the effective in-game sensitivity at each stage.
    /// </summary>
    public string PhysicsReadout(ProfileDto p)
    {
        double dpi = Prefs.MouseDpi, sens = Prefs.GameSensitivity, poll = Prefs.ChartPollRate;
        if (dpi <= 0 || sens <= 0) return "填上鼠标 DPI 和游戏灵敏度后，这里会给出 cm/s 与 °/s 的对照。";

        double CmsToCms(double countsPerMs) => countsPerMs * 2.54 * 1000 / dpi;         // cm/s
        double CmsToDeg(double countsPerMs) => countsPerMs * SourceAim.DegreesPerCount(sens) * 1000;

        var ax = Analyze(p, true);
        var ay = Analyze(p, false);
        double perPacket = poll > 0 ? 1000.0 / poll : 1.0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"eDPI {dpi * sens:0} · {SourceAim.CmPer360(sens, dpi):0.0} cm/360（{SourceAim.CmPer360(sens, dpi) / 2:0.0} cm/180°）");
        sb.Append($"   |   X 阈值 {ax.threshold:0.00} c/ms = {CmsToCms(ax.threshold):0} cm/s = {CmsToDeg(ax.threshold):0} °/s" +
                  $"（{poll:0}Hz 下每包 {ax.threshold * perPacket:0.0} counts = {ax.threshold * perPacket / dpi * 25.4:0.00} mm）");
        if (!double.IsInfinity(ax.capSpeed))
            sb.Append($"，封顶于 {CmsToCms(ax.capSpeed):0} cm/s，峰值有效灵敏度 {sens * ax.capValue:0.00}");
        sb.Append($"\nY 阈值 {ay.threshold:0.00} c/ms = {CmsToCms(ay.threshold):0} cm/s = {CmsToDeg(ay.threshold):0} °/s");
        if (!double.IsInfinity(ay.capSpeed))
            sb.Append($"，封顶于 {CmsToCms(ay.capSpeed):0} cm/s，峰值有效灵敏度 {sens * ay.capValue:0.00}");
        return sb.ToString();
    }

    // ---------- devices & presets ----------

    public DeviceConfigDto DefaultConfig => Doc.DefaultDeviceConfig;

    public List<MouseDevice> EnumerateDevices() => DeviceEnum.ListMice();

    public DeviceDto FindDevice(string id) =>
        doc.Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public DeviceDto AddDevice(MouseDevice dev)
    {
        var entry = new DeviceDto
        {
            Id = dev.Id,
            Name = dev.Name,
            Profile = doc.Profiles.FirstOrDefault()?.Name ?? "default",
            Config = doc.DefaultDeviceConfig.Clone(),
        };
        doc.Devices.Add(entry);
        Touch();
        return entry;
    }

    public void RemoveDevice(string id)
    {
        doc.Devices.RemoveAll(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        Touch();
    }

    public ProfileDto AddProfile(string name)
    {
        var src = SelectedProfile ?? doc.Profiles.FirstOrDefault();
        var p = src?.Clone() ?? new ProfileDto();
        p.Name = string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim();
        int n = 2;
        while (doc.Profiles.Any(x => x.Name == p.Name)) p.Name = name + " " + n++;
        doc.Profiles.Add(p);
        SyncProfileList();
        SelectedProfile = p;
        Touch();
        return p;
    }

    public void RemoveProfileAt(int index)
    {
        if (doc.Profiles.Count <= 1) { Status = "至少保留一个配置文件"; return; }
        if (index < 0 || index >= doc.Profiles.Count) return;
        var removed = doc.Profiles[index];
        var fallback = doc.Profiles[(index + 1) % doc.Profiles.Count];
        doc.Profiles.RemoveAt(index);
        // devices that pointed at the deleted profile would otherwise fall back to the driver default
        int reassigned = 0;
        foreach (var d in doc.Devices.Where(d => d.Profile == removed.Name)) { d.Profile = fallback.Name; reassigned++; }
        SyncProfileList();
        RebuildCurves();
        Touch();
        Status = reassigned > 0
            ? $"已删除“{removed.Name}”，把 {reassigned} 个设备改挂到“{fallback.Name}”"
            : "已删除 " + removed.Name;
    }

    public void RenameSelectedProfile(string name)
    {
        var p = SelectedProfile;
        if (p == null || string.IsNullOrWhiteSpace(name)) return;
        string old = p.Name;
        name = name.Trim();
        if (name == old) return;
        if (doc.Profiles.Any(x => x.Name == name)) { Status = "已经有叫“" + name + "”的配置了"; return; }
        if (name.Length > 63) { Status = "配置名太短或太长（建议不超过 63 个字符）"; return; }
        p.Name = name;
        // devices reference profiles by name, so they must follow the rename
        int moved = 0;
        foreach (var d in doc.Devices.Where(d => d.Profile == old)) { d.Profile = name; moved++; }
        Touch();
        SyncProfileList();
        SelectedProfile = doc.Profiles.FirstOrDefault(x => x.Name == name);
        Status = $"配置已从“{old}”改名为“{name}”，{moved} 个设备的指向已一起更新";
    }

    void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new(n));

    /// <summary>Call after mutating the document in place so IsDirty/status refresh.</summary>
    public void Touch()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(Doc));
    }

    /// <summary>Where acceleration starts / stops rising. Delegates to RaCore so the
    /// chart, the readout and the trainer can never disagree.</summary>
    public (double threshold, double capSpeed, double capValue, double maxValue) Analyze(ProfileDto p, bool horizontal)
    {
        var a = CurveAnalysis.Analyze(Convert4.ToStruct(p), horizontal);
        return (a.Threshold, a.CapSpeed, a.PeakGain, a.PeakGain);
    }

    // ---------- startup / io ----------

    public void StartupLoad()
    {
        ProbeDriver();

        bool loadedFile = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                Doc = Json.Deserialize(File.ReadAllText(SettingsPath));
                loadedFile = true;
                saved = doc.DeepCopy();
            }
        }
        catch (Exception e)
        {
            Status = "读取 settings.json 失败：" + e.Message;
        }

        if (!loadedFile)
        {
            // prefer the live driver state; otherwise fall back to the official defaults
            try
            {
                if (DriverReachable)
                {
                    Doc = Convert4.ReadBlob(RaDevice.Read());
                    saved = doc.DeepCopy();
                    Status = "settings.json 不存在，已载入驱动当前活动配置";
                }
                else
                {
                    Doc = OfflineDefaults();
                    saved = doc.DeepCopy();
                }
            }
            catch (Exception e)
            {
                Doc = OfflineDefaults();
                saved = doc.DeepCopy();
                Status = "读取驱动失败：" + e.Message;
            }
        }

        SyncProfileList();
        RebuildCurves();
    }

    public void ProbeDriver()
    {
        try
        {
            var v = RaDevice.GetVersion();
            DriverReachable = true;
            DriverInfo = $"驱动 v{v.major}.{v.minor}.{v.patch} · 已连接";
        }
        catch (RaDriverAbsentException)
        {
            DriverReachable = false;
            DriverInfo = "驱动未安装（installer.exe + 重启）";
        }
        catch (Exception e)
        {
            DriverReachable = false;
            DriverInfo = "驱动不可用：" + e.Message;
        }
    }

    static RootDto OfflineDefaults()
    {
        var r = new RootDto();
        r.Profiles.Add(new ProfileDto());
        return r;
    }

    void SyncProfileList()
    {
        // a driver that holds nothing really means nothing: give the UI one editable profile
        // instead of pretending a curve is active
        if (doc.Profiles.Count == 0) doc.Profiles.Add(new ProfileDto());
        Profiles.Clear();
        foreach (var p in doc.Profiles) Profiles.Add(p);
        selected = Profiles.FirstOrDefault();
        Raise(nameof(SelectedProfile));
    }

    public void RefreshAfterExternalEdit() => Raise(nameof(SelectedProfile));

    public bool SaveToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var backup = SettingsPath + ".bak";
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, backup, true);

            // one rolling .bak is not enough to walk a tuning chain back to the baseline,
            // so every overwrite also drops a timestamped copy in the shared data dir
            try
            {
                var backupDir = Path.Combine(AppData.Root, "config-backups");
                if (File.Exists(SettingsPath))
                {
                    Directory.CreateDirectory(backupDir);
                    var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Copy(SettingsPath, Path.Combine(backupDir, $"settings-{stamp}.json"), true);
                    foreach (var old in Directory.GetFiles(backupDir, "settings-*.json").OrderByDescending(f => f).Skip(60))
                        File.Delete(old);
                }
            }
            catch { /* a failed extra backup must not block saving */ }

            string temp = SettingsPath + ".tmp";
            try
            {
                File.WriteAllText(temp, Json.Serialize(doc));
                File.Move(temp, SettingsPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            saved = doc.DeepCopy();
            Raise(nameof(IsDirty));
            Status = "设置已保存";
            return true;
        }
        catch (Exception e)
        {
            Status = "保存失败：" + e.Message;
            return false;
        }
    }

    public void ReloadFromDriver()
    {
        try
        {
            Doc = Convert4.ReadBlob(RaDevice.Read());
            saved = doc.DeepCopy();
            SyncProfileList();
            RebuildCurves();
            Raise(nameof(IsDirty));
            Status = "已从驱动读取当前活动配置";
        }
        catch (Exception e)
        {
            Status = "读取驱动失败：" + e.Message;
        }
    }

    /// <summary>
    /// Freezes which curve the mouse is actually served by right now, so a score row can name
    /// the plan it was achieved with. Falls back to the file / draft and says so, because those
    /// are not what the mouse went through.
    /// </summary>
    public CurveContext CaptureLiveCurve(string deviceId)
    {
        var c = new CurveContext { DeviceId = deviceId };
        ProbeDriver();

        if (DriverReachable)
        {
            try
            {
                var live = Convert4.ReadBlob(RaDevice.Read());
                c.Source = "driver（本轮真正生效的配置）";
                c.DeviceName = live.Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
                int idx = RaCore.Presets.TargetIndex(live, deviceId);
                if (idx >= live.Profiles.Count)
                {
                    c.Source = "driver 里是空的（当前所有鼠标都没有加速）";
                    c.ProfileName = "(none)";
                    c.ConfigSha = "000000000000";
                    return c;
                }
                c.ProfileName = live.Profiles[idx].Name;
                c.ConfigSha = ConfigHash.Of(live.Profiles[idx]);
                return c;
            }
            catch (Exception e)
            {
                c.Source = "driver 读取失败，改用界面草稿：" + e.Message;
            }
        }
        else
        {
            c.Source = File.Exists(SettingsPath) ? "settings.json（驱动没连上，本轮不代表实际生效值）" : "界面草稿（驱动没连上）";
        }

        var sel = SelectedProfile ?? doc.Profiles.FirstOrDefault();
        c.ProfileName = sel?.Name ?? "(none)";
        c.DeviceName = doc.Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
        c.ConfigSha = sel == null ? "000000000000" : ConfigHash.Of(sel);
        return c;
    }

    public sealed class CurveContext
    {
        public string Source = "unknown";
        public string ProfileName = "";
        public string ConfigSha = "";
        public string DeviceId = "";
        public string DeviceName = "";
    }

    /// <summary>Writes the whole document to the driver. The driver enforces a 1s throttle.</summary>
    public async Task<bool> WriteToDriverAsync()
    {
        if (Busy) { Status = "请等待上一次应用结束"; return false; }
        if (!DriverReachable) { Status = "驱动不可用，无法应用"; return false; }
        var target = doc.DeepCopy(); // Never compare a live editable document after an await.
        byte[] before = null;
        bool attempted = false;
        Busy = true;
        Raise(nameof(CanWrite));
        Status = "正在应用设置…";
        try
        {
            var problems = Json.Validate(target);
            if (problems.Count > 0)
            {
                Status = "参数不能应用：" + string.Join("；", problems.Take(4));
                return false;
            }
            before = RaDevice.Read();
            var blob = Convert4.BuildBlob(target);
            attempted = true;
            await Task.Run(() => RaDevice.Write(blob));
            await Task.Delay(1200);
            var back = Convert4.ReadBlob(RaDevice.Read());
            if (!Convert4.BuildBlob(back).AsSpan().SequenceEqual(blob))
                throw new InvalidOperationException("应用后的设置与提交内容不一致");

            // Driver success and disk success are distinct. A disk failure must not be labelled
            // as a failed driver write, nor mark the unsaved document as clean.
            previousSettings = Convert4.ReadBlob(before);
            Raise(nameof(CanUndoSettings));
            bool persisted = SaveToFile();
            if (persisted) Status = "设置已应用并保存";
            else Status = "设置已应用，但未能保存到文件：" + Status;
            RefreshLiveSha();
            return true;
        }
        catch (Exception e)
        {
            string reason = e is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 5
                ? "驱动拒绝访问（错误 5）" : e.Message;
            if (attempted && before != null)
            {
                try
                {
                    await Task.Delay(1200);
                    await Task.Run(() => RaDevice.Write(before));
                    await Task.Delay(1200);
                    if (!RaDevice.Read().AsSpan().SequenceEqual(before))
                        throw new InvalidOperationException("恢复后的回读不一致");
                    Status = "应用失败，驱动已恢复原设置：" + reason;
                }
                catch (Exception restore)
                { Status = "应用失败，驱动状态未确认：" + reason + "；恢复失败：" + restore.Message; }
            }
            else Status = "设置未应用：" + reason;
            return false;
        }
        finally
        {
            Busy = false;
            Raise(nameof(CanWrite));
        }
    }

    /// <summary>Clears driver state; every mouse goes back to untouched raw input.</summary>
    public async Task DisableDriverAsync()
    {
        if (!DriverReachable) return;
        Busy = true;
        try
        {
            await Task.Run(() => RaDevice.Reset());
            Status = "已清空驱动设置：所有鼠标恢复原始输入（重启后仍会保持空，除非再次写入）";
        }
        catch (Exception e) { Status = "清空失败：" + e.Message; }
        finally { Busy = false; Raise(nameof(CanWrite)); }
    }

    // ---------- presets: one click to put a saved curve into the driver ----------

    /// <summary>
    /// Sha of the profile the driver is serving the mouse right now, read back from the driver
    /// (never inferred from what we last wrote - an external writer, another login, or a dropped
    /// write all make the two disagree, and the whole point of the preset bar is to show the truth).
    /// </summary>
    public string LiveProfileSha { get; private set; } = "";

    /// <summary>The first attached mouse, as the driver identifies it. Null when enumeration fails.</summary>
    string MouseDeviceId()
    {
        try { return EnumerateDevices().FirstOrDefault()?.Id; } catch { return null; }
    }

    public List<RaCore.Preset> ListPresets() => RaCore.Presets.List();

    /// <summary>Reads the driver. No write, no state change beyond the sha string.</summary>
    public void RefreshLiveSha()
    {
        if (!DriverReachable) { SetLiveSha(""); return; }
        try
        {
            var live = Convert4.ReadBlob(RaDevice.Read());
            int idx = RaCore.Presets.TargetIndex(live, MouseDeviceId());
            SetLiveSha(idx < live.Profiles.Count ? ConfigHash.Of(live.Profiles[idx]) : "");
        }
        catch (Exception e) { SetLiveSha(""); Status = "读驱动失败：" + e.Message; }
    }

    /// <summary>Raise only on an actual change: the curve page rebuilds its preset bar from this
    /// property, and it also reads the driver to fill it, so an unconditional Raise loops.</summary>
    void SetLiveSha(string sha)
    {
        if (string.Equals(LiveProfileSha, sha, StringComparison.Ordinal)) return;
        LiveProfileSha = sha;
        Raise(nameof(LiveProfileSha));
    }

    /// <summary>
    /// Put a preset into the slot the mouse resolves to and write it through the SAME path as the
    /// "write to driver" button: validate, write, read back, and roll the bytes back if the read
    /// back does not match. The profile NAME is kept (only its contents are replaced) because
    /// device entries reference profiles by name - renaming the slot would dangle those pointers.
    /// </summary>
    public async Task<bool> ApplyPresetAsync(string name)
    {
        if (Busy) { Status = "请等待上一次应用结束"; return false; }
        var found = ListPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found == null) { Status = "列表里没有这条曲线：" + name; return false; }
        var prof = RaCore.Presets.TryLoad(found.File);
        if (prof == null) { Status = "这条曲线的文件读不出来：" + found.File; return false; }

        int idx = RaCore.Presets.TargetIndex(doc, MouseDeviceId());
        if (idx >= doc.Profiles.Count) { Status = "文档里没有可写入的 profile"; return false; }
        var before = doc.Profiles[idx].Clone();
        prof.Name = before.Name;

        doc.Profiles[idx] = prof;
        Profiles[idx] = prof;
        SelectedProfile = prof;
        Touch();

        bool ok = await WriteToDriverAsync();
        if (!ok)
        {
            doc.Profiles[idx] = before;
            Profiles[idx] = before;
            SelectedProfile = before;
            Touch();
            // Preserve the write/rollback error; reverting the editor is not proof of driver recovery.
            return false;
        }
        RefreshLiveSha();
        return true;
    }

    public async Task<bool> UndoLastSettingsAsync()
    {
        if (!CanUndoSettings || !DriverReachable) return false;
        var restore = previousSettings.DeepCopy();
        var draft = doc.DeepCopy();
        ReplaceDocument(restore);
        bool ok = await WriteToDriverAsync();
        if (ok)
        {
            previousSettings = null;
            Raise(nameof(CanUndoSettings));
        }
        else ReplaceDocument(draft);
        return ok;
    }

    void ReplaceDocument(RootDto value)
    {
        doc = value.DeepCopy();
        SyncProfileList();
        RebuildCurves();
        Touch();
    }

    // ---------- curve math (identical to the driver's own headers) ----------

    /// <summary>
    /// Display axis -> the speed the driver actually compares against.
    /// With DPI normalization off, speed_internal == counts/ms and physical in/s = counts/ms*1000/DPI.
    /// With normalization on, speed_internal is already the 1000-dpi-equivalent in/s.
    /// </summary>
    double AxisToInternal(double axis)
    {
        bool normalized = Doc.DefaultDeviceConfig.Dpi > 0;
        if (ChartDpi <= 0) return axis;                       // axis is counts/ms already
        return normalized ? axis : axis * ChartDpi / 1000.0;  // axis is in/s
    }

    double InternalToAxis(double v)
    {
        bool normalized = Doc.DefaultDeviceConfig.Dpi > 0;
        if (ChartDpi <= 0) return v;
        return normalized ? v : v * 1000.0 / ChartDpi;
    }

    public void RebuildCurves()
    {
        var p = selected;
        if (p == null) { Series = new(); Markers = new(); Zones = new(); Raise(nameof(Series)); return; }

        double time = 1.0;   // 1 ms: feeding speed*time gives counts/ms directly
        var ms = new ModifierSettings(Convert4.ToStruct(p));
        var mod = new Modifier(ms);

        double OutSpeedAtInternal(double v, bool horizontal)
        {
            var sp = new SpeedProcessor();
            sp.Init(ms.prof.speed_processor_args);
            var input = horizontal ? new vec2d { x = v * time, y = 0 } : new vec2d { x = 0, y = v * time };
            var o = mod.Modify(input, sp, 1, time);   // dpiFactor = 1: axis conversion handled above
            return M.Magnitude(o) / time;
        }

        double maxAxis = ChartDpi > 0 ? 40 : 30;   // in/s or counts/ms
        int n = 240;

        var sens = new List<CurvePoint>();
        var gain = new List<CurvePoint>();

        for (int i = 1; i <= n; i++)
        {
            double axis = maxAxis * i / n;
            double v = AxisToInternal(axis);
            double o = OutSpeedAtInternal(v, true);
            double h = v * 1e-3 + 1e-6;
            double g = (OutSpeedAtInternal(v + h, true) - OutSpeedAtInternal(Math.Max(v - h, 0), true)) / (2 * h);

            sens.Add(new CurvePoint(axis, o / Math.Max(v, 1e-9)));
            gain.Add(new CurvePoint(axis, g));
        }

        string unit = ChartDpi > 0 ? $"in/s @ {ChartDpi:0} DPI" : "counts/ms";
        XAxisLabel = $"手速 ({unit})";
        YAxisLabel = CurveKind == CurveKind.Gain ? "增益 (d输出/d输入)" : "灵敏度 (输出/输入)";

        Brush accent = Brush("AccentBrush");
        Series = CurveKind == CurveKind.Gain
            ? new List<CurveSeries> { new() { Name = "增益", Brush = accent, Points = gain } }
            : new List<CurveSeries> { new() { Name = "灵敏度", Brush = accent, Points = sens } };

        // Labelled markers. An unlabelled dashed line is just noise on the chart, which is what
        // the four yellow lines used to be. X and Y get different colours so 起/顶 can be traced
        // back to the axis they belong to.
        var axX = Analyze(p, true);
        var axY = Analyze(p, false);
        var xCol = Brush("AccentBrush");
        var yCol = YMarkerBrush;
        Markers = new List<ChartMarker>();
        void AddMark(double internalV, string label, Brush col)
        {
            if (internalV <= 0 || double.IsInfinity(internalV)) return;
            double axis = InternalToAxis(internalV);
            if (axis is > 0 and < 400) Markers.Add(new ChartMarker(axis, label, col));
        }
        AddMark(axX.threshold, "X起", xCol);
        AddMark(axX.capSpeed, "X顶", xCol);
        AddMark(axY.threshold, "Y起", yCol);
        AddMark(axY.capSpeed, "Y顶", yCol);

        // The plotted curve is the X curve, so the bands are X bands: below the X threshold the
        // multiplier is exactly 1.0 however hard he moves, above the X cap it stops rising.
        double zLo = axX.threshold > 0 && !double.IsInfinity(axX.threshold) ? InternalToAxis(axX.threshold) : double.NaN;
        double zHi = axX.capSpeed > 0 && !double.IsInfinity(axX.capSpeed) ? InternalToAxis(axX.capSpeed) : double.NaN;

        // The bands come from the curve alone: below the X threshold the multiplier is exactly
        // 1.0 however fast the hand moves, above the X cap it stops rising.
        Zones = new List<ChartZone>();
        if (!double.IsNaN(zLo))
        {
            double top = double.IsNaN(zHi) ? maxAxis : zHi;
            Zones.Add(new ChartZone(0, zLo, "不加速", ZoneTint(0x1E)));
            Zones.Add(new ChartZone(zLo, top, "加速中", ZoneTint(0x30)));
            if (!double.IsNaN(zHi)) Zones.Add(new ChartZone(zHi, maxAxis, "已封顶", ZoneTint(0x42)));
        }

        lastCurveHash = ConfigHash.OfDoc(doc);

        Raise(nameof(Series));
        Raise(nameof(Zones));
        Raise(nameof(Markers));
        Raise(nameof(XAxisLabel));
        Raise(nameof(YAxisLabel));
    }


    static SolidColorBrush Brush(string key) =>
        (System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush) ?? Brushes.DeepSkyBlue;

    /// <summary>Y gets its own colour because the chart plots the X curve; a Y line in the same
    /// accent blue would read as "this is the curve's own threshold".</summary>
    static readonly SolidColorBrush YMarkerBrush = Frozen(Color.FromRgb(0xFF, 0xC2, 0x4D));

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Zone shading: the same hue at three depths, so the eye reads "more accelerated"
    /// left to right without needing a legend.</summary>
    static SolidColorBrush ZoneTint(byte alpha) => Frozen(Color.FromArgb(alpha, 0x4C, 0xC2, 0xFF));
}
