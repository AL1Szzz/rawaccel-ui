using RaCore;
using RaCore.Model;

namespace RaUI;

/// <summary>
/// Device entries, and the profile list they point into. The one rule that keeps biting here is
/// that a device references its profile BY NAME, so deleting or renaming a profile has to move
/// every device that pointed at it - otherwise the driver silently falls back to its default.
/// </summary>
public sealed partial class AppState
{
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
}
