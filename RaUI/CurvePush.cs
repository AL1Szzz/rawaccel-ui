using RaCore;
using RaCore.Model;

namespace RaUI;

/// <summary>
/// Puts a candidate curve into the driver for one round, then puts back exactly what was there.
///
/// This is the one place in the app that changes how somebody's mouse behaves without asking them to
/// press "apply", so it is built to be boring about it:
///   * It never writes the settings file. A blind comparison that saved each candidate to disk would
///     leave twenty-five versions of a curve the player never chose, and would silently mark the
///     document clean. The file is not touched; only the driver's live blob is.
///   * It remembers the bytes it found, not the document it was told about, so restoring cannot
///     disagree with what was really there - including a state the UI never parsed.
///   * Every write is read back and compared, the same check the normal apply path does. A write that
///     did not land must be caught here, because a round played on the wrong curve looks exactly like
///     a round played badly.
///   * If a round crashes the process the driver keeps the last candidate. Restore is therefore also
///     wired to window close and to Dispose, and a failed restore is reported loudly instead of quietly.
///
/// The driver throttles writes to roughly one a second, which is why every operation waits before
/// reading back rather than trusting the write call.
/// </summary>
public sealed class CurvePush : IDisposable
{
    const int SettleMs = 1200;

    byte[]? original;
    bool restored;

    /// <summary>Non-null when raw input to the driver is not available at all.</summary>
    public string? Error { get; private set; }

    /// <summary>Set when a restore was attempted and did not verify. The caller must shout about this.</summary>
    public string? RestoreFailure { get; private set; }

    public bool HasOriginal => original != null;

    /// <summary>
    /// The profile the candidate actually took over. Devices point at profiles by name, so this is
    /// the only reliable way to know which name to write a result back into - it can differ from the
    /// one the UI has selected, and patching the wrong one would silently change nothing.
    /// </summary>
    public string SwappedProfileName { get; private set; } = "";

    public bool Capture()
    {
        try
        {
            original = RaDevice.Read();
            return true;
        }
        catch (Exception e)
        {
            Error = "读不到驱动当前设置：" + e.Message;
            return false;
        }
    }

    public async Task<bool> PushAsync(ProfileDto candidate, string deviceId = "")
    {
        if (original == null) { Error = "还没记下原来的设置"; return false; }

        var doc = App.State.Doc.DeepCopy();
        int idx = ProfileInUse(doc, deviceId);
        if (idx < 0) { Error = "找不到要替换的曲线"; return false; }

        var copy = candidate.Clone();
        copy.Name = doc.Profiles[idx].Name;      // devices point at profiles by name; keep the name
        doc.Profiles[idx] = copy;
        SwappedProfileName = copy.Name;

        var problems = Json.Validate(doc);
        if (problems.Count > 0) { Error = "这条候选参数不能应用：" + string.Join("；", problems.Take(3)); return false; }

        byte[] blob;
        try { blob = Convert4.BuildBlob(doc); }
        catch (Exception e) { Error = "候选参数组装失败：" + e.Message; return false; }

        try
        {
            await Task.Run(() => RaDevice.Write(blob));
            await Task.Delay(SettleMs);
            if (!Matches(blob))
            {
                // the write did not take; put the original back before anything else happens
                await RestoreAsync();
                Error = "驱动没有接受这条候选（回读不一致），已还原";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Error = "写入驱动失败：" + e.Message;
            await RestoreAsync();
            return false;
        }
    }

    public async Task<bool> RestoreAsync()
    {
        if (original == null) return true;
        if (restored) return RestoreFailure == null;
        try
        {
            await Task.Run(() => RaDevice.Write(original));
            await Task.Delay(SettleMs);
            if (!Matches(original))
            {
                RestoreFailure = "还原后回读与原来的字节不一致";
                return false;
            }
            restored = true;
            return true;
        }
        catch (Exception e)
        {
            RestoreFailure = "还原失败：" + e.Message;
            return false;
        }
    }

    static bool Matches(byte[] want)
    {
        try
        {
            var back = RaDevice.Read();
            if (!back.AsSpan().SequenceEqual(want)) return false;
            // and the same thing once more through the struct round trip, because the byte compare
            // cannot tell a blob the driver accepted from one it merely echoed
            return Convert4.BuildBlob(Convert4.ReadBlob(back)).AsSpan().SequenceEqual(want);
        }
        catch { return false; }
    }

    /// <summary>
    /// Which profile the mouse is actually on. The device records a name, not an index, so the
    /// candidate has to take that name over - otherwise the swap would create a profile nobody points at.
    /// </summary>
    public static int ProfileInUse(RootDto doc, string deviceId)
    {
        if (deviceId.Length > 0)
        {
            var d = doc.Devices.FirstOrDefault(x => string.Equals(x.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (d != null)
            {
                int i = doc.Profiles.FindIndex(p => p.Name == d.Profile);
                if (i >= 0) return i;
            }
        }
        foreach (var d in doc.Devices)
        {
            if (d.Config?.Disable == true) continue;
            int i = doc.Profiles.FindIndex(p => p.Name == d.Profile);
            if (i >= 0) return i;
        }
        if (doc.Profiles.Count > 0)
        {
            var sel = App.State.SelectedProfile;
            if (sel != null)
            {
                int i = doc.Profiles.FindIndex(p => p.Name == sel.Name);
                if (i >= 0) return i;
            }
            return 0;
        }
        return -1;
    }

    public void Dispose() => _ = RestoreAsync();
}
