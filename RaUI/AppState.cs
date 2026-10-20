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

public enum CurveKind { Sensitivity, Gain, OutputVelocity }

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
        set { busy = value; Raise(); }
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

    // ---------- live hand-speed telemetry ----------

    public MouseMonitor? Monitor { get; private set; }

    public SpeedRecorder? Speeds => Monitor?.Recorder;

    bool showSamples;
    public bool ShowSamples
    {
        get => showSamples;
        set { showSamples = value; Raise(); RebuildCurves(); }
    }

    public List<CurvePoint> Histogram { get; private set; } = new();
    public List<double> Markers { get; private set; } = new();
    public string Telemetry { get; private set; } = "未开始采样";

    public void AttachMonitor(Window w)
    {
        if (Monitor != null) return;
        try
        {
            Monitor = new MouseMonitor(w);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                // only rebuild when something actually changed; a 120ms full re-evaluation of
                // the curve starves the input queue and contaminates the very timings we record
                var hash = ConfigHash.OfDoc(doc);
                if (hash != lastCurveHash || (Speeds?.Count ?? 0) != lastSampleCount)
                {
                    RebuildCurves();
                    Raise(nameof(Telemetry));
                }
            };
            timer.Start();
            Raise(nameof(Monitor));
        }
        catch (Exception e)
        {
            Status = "实时采样启动失败：" + e.Message;
        }
    }

    string lastCurveHash = "";
    int lastSampleCount = -1;

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
        double CmsToDeg(double countsPerMs) => countsPerMs * RaCore.Cs2.DegreesPerCount(sens) * 1000;

        var ax = Analyze(p, true);
        var ay = Analyze(p, false);
        double perPacket = poll > 0 ? 1000.0 / poll : 1.0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"eDPI {dpi * sens:0} · {RaCore.Cs2.CmPer360(sens, dpi):0.0} cm/360（{RaCore.Cs2.CmPer360(sens, dpi) / 2:0.0} cm/180°）");
        sb.Append($"   |   X 阈值 {ax.threshold:0.00} c/ms = {CmsToCms(ax.threshold):0} cm/s = {CmsToDeg(ax.threshold):0} °/s" +
                  $"（{poll:0}Hz 下每包 {ax.threshold * perPacket:0.0} counts = {ax.threshold * perPacket / dpi * 25.4:0.00} mm）");
        if (!double.IsInfinity(ax.capSpeed))
            sb.Append($"，封顶于 {CmsToCms(ax.capSpeed):0} cm/s，峰值有效灵敏度 {sens * ax.capValue:0.00}");
        sb.Append($"\nY 阈值 {ay.threshold:0.00} c/ms = {CmsToCms(ay.threshold):0} cm/s = {CmsToDeg(ay.threshold):0} °/s");
        if (!double.IsInfinity(ay.capSpeed))
            sb.Append($"，封顶于 {CmsToCms(ay.capSpeed):0} cm/s，峰值有效灵敏度 {sens * ay.capValue:0.00}");
        return sb.ToString();
    }

    public string DescribeSpeeds()
    {
        var r = Speeds;
        if (r == null) return "未开始采样";
        if (!Monitor!.Active) return "WM_INPUT 注册失败，实时采样不可用";
        var snap = r.Snapshot();
        if (snap.Count < 5) return "移动鼠标开始采样…";
        var p = r.Percentiles(byComponent: SelectedProfile?.Speed.Whole == false);
        string F(double v) => ChartDpi > 0
            ? $"{v:0.00} counts/ms（{Cs2.CmsToCmPerSec(v, ChartDpi):0} cm/s）"
            : $"{v:0.00} counts/ms";
        return $"中位 {F(p.p50)}   ·   常用上沿 {F(p.p75)}   ·   甩动峰值 {F(p.p95)}   ·   样本 {snap.Count}";
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

    public void SaveToFile()
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

            File.WriteAllText(SettingsPath, Json.Serialize(doc));
            saved = doc.DeepCopy();
            Raise(nameof(IsDirty));
            Status = "已保存 " + Path.GetFileName(SettingsPath) + "（旧版备份为 .bak）";
        }
        catch (Exception e)
        {
            Status = "保存失败：" + e.Message;
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
    /// Freezes what the driver is ACTUALLY running right now, so a round log can never be
    /// attributed to a curve that was only sitting in the UI. Falls back to the file / draft
    /// and says so, because those are not what the mouse went through.
    /// </summary>
    public RoundContext CaptureRoundContext(string deviceId)
    {
        var c = new RoundContext { DeviceId = deviceId };
        ProbeDriver();
        c.DriverVersion = DriverInfo;

        if (DriverReachable)
        {
            try
            {
                var blob = RaDevice.Read();
                var fromDriver = Convert4.ReadBlob(blob);
                c.BlobBefore = blob;
                c.DeviceName = fromDriver.Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
                var p = fromDriver.Profiles.FirstOrDefault(x => x.Name == (SelectedProfile?.Name ?? ""))
                        ?? fromDriver.Profiles.FirstOrDefault();
                if (p == null)
                {
                    c.Source = "driver 里是空的（当前所有鼠标都没有加速）";
                    c.Profile = default;
                    c.ProfileName = "(none)";
                    c.ConfigSha = "000000000000";
                    c.Doc = fromDriver;
                    return c;
                }
                c.Source = "driver（本轮真正生效的配置）";
                c.Doc = fromDriver;
                c.ProfileName = p.Name;
                c.Profile = Convert4.ToStruct(p);
                c.ConfigSha = ConfigHash.Of(p);
                c.ProfileJson = Json.Serialize(fromDriver);
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

        var sel = SelectedProfile;
        c.Doc = doc;
        c.ProfileName = sel?.Name ?? "(none)";
        c.Profile = sel == null ? default : Convert4.ToStruct(sel);
        c.ConfigSha = sel == null ? "000000000000" : ConfigHash.Of(sel);
        c.ProfileJson = Json.Serialize(doc);
        c.DeviceName = doc.Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
        return c;
    }

    public sealed class RoundContext
    {
        public string Source = "unknown";
        public string DriverVersion = "";
        public string ProfileName = "";
        public string ConfigSha = "";
        public string ProfileJson = "";
        public string DeviceId = "";
        public string DeviceName = "";
        public ra_profile Profile;
        public RootDto Doc = new();
        public byte[]? BlobBefore;
    }

    /// <summary>
    /// Transactional apply of an accepted AI patch: the whole document goes to the driver, the
    /// driver is read back and compared, and a mismatch restores the bytes captured beforehand.
    /// Nothing is trusted from the "write returned true" alone.
    /// </summary>
    public async Task<PatchApply> ApplyProfileAsync(ProfileDto patched, string profileName, string patchText)
    {
        if (!DriverReachable)
            return new PatchApply(false, false, false, "驱动没连上，无法写入（改动也没有丢，可以稍后再试）", null);
        if (Busy) return new PatchApply(false, false, false, "上一次写入还没结束", null);

        Busy = true; Raise(nameof(CanWrite));
        try
        {
            byte[]? before = null;
            try { before = RaDevice.Read(); } catch { before = null; }

            int idx = doc.Profiles.FindIndex(p => p.Name == profileName);
            if (idx < 0) return new PatchApply(false, false, false, $"文档里找不到 profile “{profileName}”", null);
            var old = doc.Profiles[idx].Clone();
            doc.Profiles[idx] = patched;
            Profiles[idx] = patched;
            SelectedProfile = patched;
            Touch();

            // validate the document that is about to go to the kernel, not the one before it
            var problems = Json.Validate(doc);
            if (problems.Count > 0)
            {
                doc.Profiles[idx] = old; Profiles[idx] = old; SelectedProfile = old; Touch();
                Status = "改动被拦下（驱动没动）：" + string.Join("；", problems.Take(6));
                return new PatchApply(false, false, false, Status, null);
            }

            string audit = TestLog.WriteAudit(DateTime.Now.ToString("patch-yyyy-MM-dd_HH-mm-ss-fff",
                System.Globalization.CultureInfo.InvariantCulture), patchText, "attempted",
                Json.Serialize(new RootDto { Profiles = { old } }), Json.Serialize(new RootDto { Profiles = { patched } }));

            var blob = Convert4.BuildBlob(doc);
            await Task.Run(() => RaDevice.Write(blob));
            await Task.Delay(1200);                    // driver WRITE_DELAY is 1s; read after it settles

            var back = Convert4.ReadBlob(RaDevice.Read());
            var backProfile = back.Profiles.FirstOrDefault(p => p.Name == profileName);
            bool same = backProfile != null && ConfigHash.Of(backProfile) == ConfigHash.Of(patched);

            if (!same)
            {
                bool restored = false;
                if (before != null)
                {
                    try { await Task.Run(() => RaDevice.Write(before)); restored = true; } catch { }
                }
                doc.Profiles[idx] = old;
                Profiles[idx] = old;
                SelectedProfile = old;
                Touch();
                TestLog.WriteAudit(DateTime.Now.ToString("patch-rolledback-yyyy-MM-dd_HH-mm-ss-fff",
                    System.Globalization.CultureInfo.InvariantCulture), patchText,
                    restored ? "readback_mismatch_rolled_back" : "readback_mismatch_write_failed",
                    Json.Serialize(new RootDto { Profiles = { old } }), Json.Serialize(new RootDto { Profiles = { patched } }));
                Status = restored
                    ? "写入后回读与提交不一致，已退回改动前的配置"
                    : "写入后回读不一致，且退回失败：请点“从驱动重新读取”确认现状";
                return new PatchApply(false, false, restored, Status, audit);
            }

            saved = doc.DeepCopy();
            Raise(nameof(IsDirty));
            string fileNote = "";
            try { SaveToFile(); fileNote = "，并已存进 settings.json（重启后仍然有效）"; }
            catch (Exception e) { fileNote = "，但 settings.json 保存失败：" + e.Message; }
            Status = "已写入驱动并回读校验一致" + fileNote;
            return new PatchApply(true, true, false, Status, audit);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Status = e.NativeErrorCode == 5
                ? "写入被拒绝（错误 5）：当前进程没有打开驱动设备的权限"
                : "写入失败（Win32 " + e.NativeErrorCode + "）：" + e.Message;
            return new PatchApply(false, false, false, Status, null);
        }
        catch (Exception e)
        {
            Status = "写入失败：" + e.Message;
            return new PatchApply(false, false, false, Status, null);
        }
        finally
        {
            Busy = false; Raise(nameof(CanWrite));
        }
    }

    public sealed record PatchApply(bool Applied, bool Verified, bool Restored, string Message, string? AuditFile);

    /// <summary>Writes the whole document to the driver. The driver enforces a 1s throttle.</summary>
    public async Task<bool> WriteToDriverAsync()
    {
        if (!DriverReachable) { Status = "驱动不可用，无法写入"; return false; }
        Busy = true;
        Raise(nameof(CanWrite));
        Status = "写入中…（驱动有 1 秒节流）";
        try
        {
            var problems = Json.Validate(doc);
            if (problems.Count > 0)
            {
                Status = "写入被拦下：" + string.Join("；", problems.Take(6));
                return false;
            }

            var before = RaDevice.Read();
            var blob = Convert4.BuildBlob(doc);
            await Task.Run(() => RaDevice.Write(blob));
            await Task.Delay(1200);
            var back = Convert4.ReadBlob(RaDevice.Read());
            bool same = ConfigHash.OfDoc(back) == ConfigHash.OfDoc(doc);
            if (!same)
            {
                try { await Task.Run(() => RaDevice.Write(before)); Status = "回读与提交不一致，已退回写入前的驱动状态"; }
                catch (Exception e) { Status = "回读不一致且退回失败：" + e.Message; }
                return false;
            }
            saved = doc.DeepCopy();
            Raise(nameof(IsDirty));
            Status = "已写入驱动并回读校验一致（约 1 秒后对所有鼠标生效）";
            return true;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Status = e.NativeErrorCode == 5
                ? "写入被拒绝（错误 5）：驱动句柄权限问题"
                : "写入失败（Win32 " + e.NativeErrorCode + "）：" + e.Message;
            return false;
        }
        catch (Exception e)
        {
            Status = "写入失败：" + e.Message;
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

    // ---------- curve math (identical to the driver's own headers) ----------

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
        if (p == null) { Series = new(); Histogram = new(); Markers = new(); Raise(nameof(Series)); return; }

        double time = 1.0;   // 1 ms: feeding speed*time gives counts/ms directly
        var ms = new ModifierSettings(Convert4.ToStruct(p));
        var mod = new Modifier(ms);
        bool byComponent = !p.Speed.Whole;

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
        var vel = new List<CurvePoint>();

        for (int i = 1; i <= n; i++)
        {
            double axis = maxAxis * i / n;
            double v = AxisToInternal(axis);
            double o = OutSpeedAtInternal(v, true);
            double h = v * 1e-3 + 1e-6;
            double g = (OutSpeedAtInternal(v + h, true) - OutSpeedAtInternal(Math.Max(v - h, 0), true)) / (2 * h);

            vel.Add(new CurvePoint(axis, InternalToAxis(o)));
            sens.Add(new CurvePoint(axis, o / Math.Max(v, 1e-9)));
            gain.Add(new CurvePoint(axis, g));
        }

        string unit = ChartDpi > 0 ? $"in/s @ {ChartDpi:0} DPI" : "counts/ms";
        XAxisLabel = $"输入速度 ({unit})";
        YAxisLabel = CurveKind switch
        {
            CurveKind.Sensitivity => "灵敏度 (输出/输入)",
            CurveKind.Gain => "增益 (d输出/d输入)",
            _ => "输出速度",
        };

        Brush accent = Brush("Accent"), grey = Brush("TextTertiary");
        Series = CurveKind switch
        {
            CurveKind.Sensitivity => new() { new() { Name = "灵敏度", Brush = accent, Points = sens } },
            CurveKind.Gain => new List<CurveSeries> { new() { Name = "增益", Brush = accent, Points = gain } },
            _ => new()
            {
                new() { Name = "输出速度", Brush = accent, Points = vel },
                new() { Name = "输入速度 (y=x)", Brush = grey, Dashed = true, Thickness = 1,
                        Points = Enumerable.Range(1, n).Select(i => new CurvePoint(maxAxis * i / n, maxAxis * i / n)).ToList() },
            },
        };

        // markers: where acceleration starts and where it stops rising, per axis
        Markers = new List<double>();
        foreach (bool horiz in new[] { true, false })
        {
            var a = Analyze(p, horiz);
            if (a.threshold > 0) Markers.Add(InternalToAxis(a.threshold));
            if (!double.IsInfinity(a.capSpeed) && a.capSpeed > 0) Markers.Add(InternalToAxis(a.capSpeed));
        }

        // live hand-speed histogram, in the same axis units
        Histogram = new List<CurvePoint>();
        var rec = Speeds;
        if (rec != null && ShowSamples)
        {
            const int bins = 64;
            var counts = new int[bins];
            foreach (var s in rec.Snapshot())
            {
                double vInternal = byComponent ? Math.Max(s.SpeedX, s.SpeedY) : s.SpeedWhole;
                double axis = InternalToAxis(vInternal);
                if (axis <= 0 || axis > maxAxis) continue;
                counts[Math.Min((int)(axis / maxAxis * bins), bins - 1)]++;
            }
            int peak = counts.Max();
            if (peak > 0)
                for (int i = 0; i < bins; i++)
                    if (counts[i] > 0) Histogram.Add(new CurvePoint(maxAxis * (i + 0.5) / bins, (double)counts[i] / peak));
        }

        Telemetry = DescribeSpeeds();
        lastCurveHash = ConfigHash.OfDoc(doc);
        lastSampleCount = Speeds?.Count ?? 0;

        Raise(nameof(Series));
        Raise(nameof(Histogram));
        Raise(nameof(Markers));
        Raise(nameof(XAxisLabel));
        Raise(nameof(YAxisLabel));
        Raise(nameof(Telemetry));
    }


    static SolidColorBrush Brush(string key) =>
        (System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush) ?? Brushes.DeepSkyBlue;
}
