using System.Globalization;
using System.IO;
using System.Text;
using RaCore.Model;

namespace RaCore;

/// <summary>One saved curve, as it sits on disk.</summary>
/// <param name="Name">File-stable label, e.g. "V125".</param>
/// <param name="Sha">ConfigHash of the profile inside; this is what the driver reports back,
/// so a preset matching the live sha means "this is the curve in the driver right now".</param>
/// <param name="File">Full path.</param>
/// <param name="Summary">The handful of numbers that tell two presets apart at a glance.</param>
public sealed record Preset(string Name, string Sha, string File, string Summary);

/// <summary>
/// The preset library: named curves the UI can switch between in one click.
///
/// Why this is not the same thing as the profile list on the curve page. The driver resolves
/// a device to a profile BY NAME, and devices that are not in the device list fall back to the
/// first profile. On this machine the document carries one profile ("default") and one device
/// entry (the keyboard, disabled), so the mouse is served by profiles[0]. A preset is therefore
/// a saved SNAPSHOT of the profile that the mouse actually resolves to - the switch replaces
/// that profile's contents and keeps its name, so no device reference is ever left dangling.
///
/// Files are written in the same RootDto shape as settings.json (and as the older
/// D:\RawAccelUI\curves\*.json experiments), so one folder can hold both.
/// </summary>
public static class Presets
{
    public static string Dir => Path.Combine(AppData.Root, "presets");

    /// <summary>Names are typed by a person; keep them to what a file name and a device
    /// profile reference can both survive.</summary>
    public static string SafeName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in (raw ?? "").Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-') sb.Append(ch);
            else if (ch == ' ' && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        string s = sb.ToString().Trim('.', '_');
        return s.Length > 40 ? s[..40] : s;
    }

    public static List<Preset> List()
    {
        var outList = new List<Preset>();
        try
        {
            if (!Directory.Exists(Dir)) return outList;
            foreach (var file in Directory.GetFiles(Dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var p = TryLoad(file);
                if (p == null) continue;
                outList.Add(new Preset(Path.GetFileNameWithoutExtension(file), ConfigHash.Of(p), file, Summary(p)));
            }
        }
        catch { /* a library that cannot be read is an empty library; the UI says so */ }
        return outList;
    }

    public static ProfileDto? TryLoad(string file)
    {
        try
        {
            var doc = Json.Deserialize(File.ReadAllText(file));
            return doc.Profiles.Count > 0 ? doc.Profiles[0] : null;
        }
        catch { return null; }
    }

    /// <summary>The numbers that differ between presets, in the order a switch is thought about.</summary>
    public static string Summary(ProfileDto p)
    {
        var ci = CultureInfo.InvariantCulture;
        return $"X {p.X.Mode} {p.X.InputOffset.ToString("0.###", ci)}/{p.X.Acceleration.ToString("0.###", ci)}" +
               $" 顶{p.X.Cap.Y.ToString("0.##", ci)} · Y {p.Y.InputOffset.ToString("0.###", ci)}/{p.Y.Acceleration.ToString("0.###", ci)}" +
               $" 顶{p.Y.Cap.Y.ToString("0.##", ci)} · yx {p.YxRatio.ToString("0.###", ci)}";
    }

    /// <summary>Save a profile as a preset. Returns the file, or null with a reason in error.</summary>
    public static string? Save(string name, ProfileDto profile, out string error)
    {
        error = "";
        string safe = SafeName(name);
        if (safe.Length == 0) { error = "档位名只能是字母、数字、点、下划线或短横线"; return null; }
        try
        {
            Directory.CreateDirectory(Dir);
            var copy = profile.Clone();
            // the stored name is the preset label, so a preset re-applied into a document whose
            // slot is called something else still carries its own identity in the file
            copy.Name = safe;
            var problems = Json.Validate(new RootDto { Profiles = { copy }, Devices = new() });
            if (problems.Count > 0) { error = "这份配置本身有问题：" + string.Join("；", problems.Take(4)); return null; }
            var file = Path.Combine(Dir, safe + ".json");
            File.WriteAllText(file, Json.Serialize(new RootDto { Profiles = { copy } }));
            return file;
        }
        catch (Exception e) { error = "写入失败：" + e.Message; return null; }
    }

    public static bool Delete(string name, out string error)
    {
        error = "";
        var hit = List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { error = "没有这个档位"; return false; }
        try { File.Delete(hit.File); return true; }
        catch (Exception e) { error = "删不掉：" + e.Message; return false; }
    }

    /// <summary>
    /// Copy a curve file in from anywhere (for example the experiment files in
    /// D:\RawAccelUI\curves). Validated before it is accepted, because an imported
    /// junk file would otherwise only reveal itself at the moment someone writes it to the driver.
    /// </summary>
    public static string? Import(string sourceFile, string name, out string error)
    {
        error = "";
        var p = TryLoad(sourceFile);
        if (p == null) { error = "这个文件里没有可用的 profile"; return null; }
        return Save(name, p, out error);
    }

    /// <summary>
    /// Which profile object the driver will apply to a given device id, or the first profile
    /// when that device is not in the list. This mirrors the wire model: device_settings carries
    /// a profile NAME, and unlisted devices have nothing to name.
    /// </summary>
    public static int TargetIndex(RootDto doc, string? deviceId)
    {
        if (doc.Profiles.Count == 0) return 0;
        if (!string.IsNullOrEmpty(deviceId))
        {
            var d = doc.Devices.FirstOrDefault(x => string.Equals(x.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (d != null)
            {
                int byName = doc.Profiles.FindIndex(p => string.Equals(p.Name, d.Profile, StringComparison.Ordinal));
                if (byName >= 0) return byName;
            }
        }
        return 0;
    }
}
