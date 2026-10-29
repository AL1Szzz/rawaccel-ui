using System.IO;
using RaCore;
using RaCore.Model;

namespace RaUI;

/// <summary>
/// Everything that crosses the boundary: reading and writing the driver, and reading and writing
/// the settings file. The two are treated as separate outcomes on purpose - a driver write that
/// succeeded and a file save that failed must not be reported as one thing.
/// </summary>
public sealed partial class AppState
{
    // ---------- startup ----------

    public void StartupLoad()
    {
        ProbeDriver();

        bool loadedFile = false, parseFailed = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                Doc = Json.Deserialize(File.ReadAllText(SettingsPath));
                loadedFile = true;
                SetSaved();
            }
        }
        catch (Exception e)
        {
            parseFailed = true;
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
                    SetSaved();
                    Status = "settings.json 不存在或读不出来，已载入驱动当前活动配置（界面里的改动不会自动写文件）";
                }
                else
                {
                    Doc = OfflineDefaults();
                    SetSaved();
                    Status = "读不到设置文件、驱动也没连上：下面显示的是默认参数，不是当前生效的曲线";
                }
            }
            catch (Exception e)
            {
                Doc = OfflineDefaults();
                SetSaved();
                Status = "读取驱动失败：" + e.Message + "（显示的是默认参数，不是生效值）";
            }
        }

        // A file we could not read, or a driver holding nothing, means what is on screen came from
        // us and not from the user. Login --write checks this: after one transient read error it
        // must not overwrite a working curve with the built-in default one.
        LoadIsSynthetic = parseFailed || (!loadedFile && (!DriverReachable || doc.Profiles.Count == 0));

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

    // ---------- settings file ----------

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
            SetSaved();
            Raise(nameof(IsDirty));
            LoadIsSynthetic = false;            // the file on disk is now this document
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
            SetSaved();
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

    // ---------- the driver write path ----------

    /// <summary>Writes the whole document to the driver. The driver enforces a 1s throttle.</summary>
    /// <param name="fromLogon">Login autostart path: refuses to act on a document that was not read
    /// from the user's own file, so one bad read cannot swap in the built-in default curve.</param>
    public async Task<bool> WriteToDriverAsync(bool fromLogon = false)
    {
        if (Busy) { Status = "请等待上一次应用结束"; return false; }
        if (!DriverReachable) { Status = "驱动不可用，无法应用"; return false; }
        if (fromLogon && LoadIsSynthetic)
        {
            Status = "登录时没有自动写入：设置文件读不出来或驱动是空的，界面里是默认参数，写下去等于换了一套你没选过的曲线";
            return false;
        }
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
}
