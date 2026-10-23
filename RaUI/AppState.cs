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

/// <summary>
/// A stretch of the hand-speed axis where the curve behaves the same way, with the share of the
/// user's actual recent movements that landed in it. That share is the number that answers
/// "does the acceleration ever engage for me" - the curve shape alone cannot.
/// </summary>
public sealed record ChartZone(double From, double To, string Name, double Share, Brush Tint);

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
    public List<ChartMarker> Markers { get; private set; } = new();
    public List<ChartZone> Zones { get; private set; } = new();
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
                // the curve starves the input queue and contaminates the very timings we record.
                // Poll the monotonic window counter, not Count: the ring buffer pins at Capacity
                // after ~2 min of movement and then looks frozen while samples keep arriving.
                var hash = ConfigHash.OfDoc(doc);
                if (hash != lastCurveHash || (Speeds?.Windows ?? 0) != lastSampleWindows)
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
    long lastSampleWindows = -1;

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
            Status = $"「{name}」没写成，已退回原来的配置（{ConfigHash.Of(before)}）";
            return false;
        }
        Status = $"「{name}」已写入并回读一致（{ConfigHash.Of(prof)}）· 鼠标可能卡了一下";
        RefreshLiveSha();
        return true;
    }

    public void SavePreset(string name)
    {
        var p = SelectedProfile ?? doc.Profiles.FirstOrDefault();
        if (p == null) { Status = "没有可保存的 profile"; return; }
        var file = RaCore.Presets.Save(name, p, out string err);
        Status = err.Length > 0 ? err
            : "已存下「" + Path.GetFileNameWithoutExtension(file) + "」。想换名字就先删掉再存一次。";
    }

    public void DeletePreset(string name)
    {
        if (!RaCore.Presets.Delete(name, out string err)) Status = err;
        else Status = "已删掉「" + name + "」（驱动没动）";
    }

    /// <summary>File-name to readable-label map for the experiment files made before the curve
    /// list existed. Anything not listed is imported under its own file name.</summary>
    static readonly Dictionary<string, (string label, string note)> CurveRename = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A-accel0.06"] = ("基线", "你用了大半年的原样：横向 3 起加速、强度 0.06、最多 1.97 倍；纵向不额外加成。怀疑哪次改动有问题，就切回这条当对照。"),
        ["V-yx1.25"] = ("压枪 1.25", "只把纵向输出整体乘 1.25（等效垂直灵敏度 1.5→1.875），横向和加速曲线都不动。压枪时同样的手下压，准星多走 25%；觉得过头就再存一条 1.2 或 1.15。"),
        ["B-accel0.12"] = ("横向加倍（旧实验）", "横向加速强度 0.06→0.12，其余同基线。这条当时没做完，留着当备选。"),
        // Y-yoffset0.75 is deliberately NOT imported: the 2026-10-22 A/B/A showed the Y accel
        // curve cannot reach spray speeds at all, so offering it as a curve only adds a button
        // that does nothing. It stays in git and in curves\ if it is ever needed again.
    };

    /// <summary>
    /// Bring the experiment files from the repo's curves\ folder into the list, under readable
    /// names, so the snapshots made over the last two days are not a second disconnected set.
    /// </summary>
    public int ImportCurveFiles()
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "curves"));
        if (!Directory.Exists(dir)) { Status = "没有找到 curves 目录：" + dir; return 0; }
        int n = 0, skipped = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(f);
            var hit = CurveRename.TryGetValue(key, out var mapped) ? mapped : (key, "");
            if (hit.Item1.Length == 0) { skipped++; continue; }
            if (RaCore.Presets.Import(f, hit.Item1, out _, hit.Item2) != null) n++;
        }
        Status = n > 0
            ? $"从 curves 导入 {n} 条曲线" + (skipped > 0 ? $"，跳过 {skipped} 条已判定无效的旧实验" : "")
            : "curves 里没有能导入的文件";
        return n;
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
        if (p == null) { Series = new(); Histogram = new(); Markers = new(); Zones = new(); Raise(nameof(Series)); return; }

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

        // live hand-speed histogram, in the same axis units, plus how much of it lands in each band
        Histogram = new List<CurvePoint>();
        double flat = double.NaN, capped = double.NaN;
        var rec = Speeds;
        if (rec != null && ShowSamples)
        {
            const int bins = 64;
            var counts = new int[bins];
            // SpeedX, not max(X,Y): the curve on this chart is the X curve, so the share numbers
            // have to be measured on the same axis or they would quietly disagree with the line.
            var speeds = rec.Snapshot().Select(s => InternalToAxis(byComponent ? s.SpeedX : s.SpeedWhole)).ToList();
            foreach (var axis in speeds)
            {
                if (axis <= 0 || axis > maxAxis) continue;
                counts[Math.Min((int)(axis / maxAxis * bins), bins - 1)]++;
            }
            int peak = counts.Max();
            if (peak > 0)
                for (int i = 0; i < bins; i++)
                    if (counts[i] > 0) Histogram.Add(new CurvePoint(maxAxis * (i + 0.5) / bins, (double)counts[i] / peak));

            int seen = speeds.Count(v => v > 0 && v <= maxAxis);
            if (seen > 0)
            {
                flat = 100.0 * speeds.Count(v => v > 0 && v < zLo) / seen;
                capped = double.IsNaN(zHi) ? 0 : 100.0 * speeds.Count(v => v >= zHi && v <= maxAxis) / seen;
            }
        }

        // The bands belong to the curve, not to the samples: they are drawn even before he moves,
        // only the percentages need data. Hiding the bands until then would hide the main point.
        Zones = new List<ChartZone>();
        if (!double.IsNaN(zLo))
        {
            double top = double.IsNaN(zHi) ? maxAxis : zHi;
            Zones.Add(new ChartZone(0, zLo, "不加速", flat, ZoneTint(0x1E)));
            Zones.Add(new ChartZone(zLo, top, "加速中", double.IsNaN(flat) ? double.NaN : Math.Max(0, 100 - flat - capped),
                                    ZoneTint(0x30)));
            if (!double.IsNaN(zHi)) Zones.Add(new ChartZone(zHi, maxAxis, "已封顶", capped, ZoneTint(0x42)));
        }

        Telemetry = DescribeSpeeds();
        lastCurveHash = ConfigHash.OfDoc(doc);
        lastSampleWindows = Speeds?.Windows ?? 0;

        Raise(nameof(Series));
        Raise(nameof(Histogram));
        Raise(nameof(Zones));
        Raise(nameof(Markers));
        Raise(nameof(Markers));
        Raise(nameof(XAxisLabel));
        Raise(nameof(YAxisLabel));
        Raise(nameof(Telemetry));
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
