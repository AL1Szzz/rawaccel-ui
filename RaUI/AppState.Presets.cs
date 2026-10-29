using System.IO;
using RaCore;
using RaCore.Model;

namespace RaUI;

/// <summary>
/// "我的方案": putting a saved curve into the driver in one click, and taking it back out again.
/// Applying a preset goes through the same write path as the Apply button - it does not get a
/// shortcut because it looks convenient - and the profile NAME is preserved, because device
/// entries reference profiles by name.
/// </summary>
public sealed partial class AppState
{
    RootDto previousSettings;
    public bool CanUndoSettings => previousSettings != null && !Busy;

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
}
