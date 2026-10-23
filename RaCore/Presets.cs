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
/// <param name="Summary">Plain Chinese: what this curve does, in the words a person would use.
/// Read from a sidecar &lt;name&gt;.txt when one exists, otherwise generated from the numbers.</param>
/// <param name="NoteFile">The sidecar path, or "" when the summary is generated.</param>
public sealed record Preset(string Name, string Sha, string File, string Summary, string NoteFile = "");

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
///
/// Names are meant to be read, not decoded: "压枪 1.25", not "V125". The hex sha stays in the
/// tooltip for the assistant's cross-checking, never in the button.
/// </summary>
public static class Presets
{
    /// <summary>Test hook: point the library somewhere else, so a self-test can never move or
    /// delete a real preset (same pattern as TestLog.IndexOverride).</summary>
    public static string? DirOverride;

    public static string Dir => Path.Combine(DirOverride ?? AppData.Root, "presets");

    /// <summary>Names are typed by a person and shown on a button, so spaces stay (they are
    /// plain file names in the user's own folder, never command-line arguments). Everything that
    /// could break a file name or a device profile reference is dropped.</summary>
    public static string SafeName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in (raw ?? "").Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-') sb.Append(ch);
            else if (ch is ' ' or '\t')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
        }
        string s = sb.ToString().Trim('.', '_', ' ');
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
                string name = Path.GetFileNameWithoutExtension(file);
                string noteFile = Path.ChangeExtension(file, ".txt");
                string note = File.Exists(noteFile) ? File.ReadAllText(noteFile).Trim() : "";
                outList.Add(new Preset(name, ConfigHash.Of(p), file,
                    note.Length > 0 ? note : Describe(p), File.Exists(noteFile) ? noteFile : ""));
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

    /// <summary>
    /// What this curve does, in words instead of code names. Used whenever a preset has no
    /// sidecar note, so a freshly saved preset is still readable and never just a hex string.
    /// </summary>
    public static string Describe(ProfileDto p)
    {
        var ci = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.###", ci);
        string Axis(string which, AccelDto a) => a.Mode == "noaccel"
            ? $"{which}：不加速"
            : $"{which}：{N(a.InputOffset)} 起加速，强度 {N(a.Acceleration)}，最多 {N(a.Cap.Y)} 倍";
        var parts = new List<string> { Axis("横向", p.X), Axis("纵向", p.Y) };
        if (Math.Abs(p.YxRatio - 1) > 1e-9)
            parts.Add($"纵向输出整体 ×{N(p.YxRatio)}（压枪同样的手，准星多走这一成的量）");
        if (Math.Abs(p.OutputDpi - 1000) > 1e-9) parts.Add($"输出 DPI {N(p.OutputDpi)}");
        return string.Join("　｜　", parts);
    }

    /// <summary>Save a profile as a preset. Returns the file, or null with a reason in error.</summary>
    public static string? Save(string name, ProfileDto profile, out string error, string note = "")
    {
        error = "";
        string safe = SafeName(name);
        if (safe.Length == 0) { error = "名字里没留下可用字符（中文、字母、数字都行）"; return null; }
        try
        {
            Directory.CreateDirectory(Dir);
            // The profile keeps its OWN name inside the file; the preset label is the file name.
            // Renaming it here would look tidy and break two things: the stored sha would no
            // longer equal the sha the driver reports for the live profile (so "which preset is
            // live" could never light up), and a profile with an unusable name would sail past
            // validation because the bad name got overwritten.
            var copy = profile.Clone();
            var problems = Json.Validate(new RootDto { Profiles = { copy }, Devices = new() });
            if (problems.Count > 0) { error = "这份配置本身有问题：" + string.Join("；", problems.Take(4)); return null; }
            var file = Path.Combine(Dir, safe + ".json");
            File.WriteAllText(file, Json.Serialize(new RootDto { Profiles = { copy } }));
            if (!string.IsNullOrWhiteSpace(note)) File.WriteAllText(Path.ChangeExtension(file, ".txt"), note.Trim() + "\n");
            return file;
        }
        catch (Exception e) { error = "写入失败：" + e.Message; return null; }
    }

    public static bool Delete(string name, out string error)
    {
        error = "";
        var hit = List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { error = "列表里没有这条曲线"; return false; }
        try
        {
            File.Delete(hit.File);
            if (hit.NoteFile.Length > 0 && File.Exists(hit.NoteFile)) File.Delete(hit.NoteFile);
            return true;
        }
        catch (Exception e) { error = "删不掉：" + e.Message; return false; }
    }

    /// <summary>
    /// Copy a curve file in from anywhere (for example the experiment files in
    /// D:\RawAccelUI\curves). Validated before it is accepted, because an imported
    /// junk file would otherwise only reveal itself at the moment someone writes it to the driver.
    /// </summary>
    public static string? Import(string sourceFile, string name, out string error, string note = "")
    {
        error = "";
        var p = TryLoad(sourceFile);
        if (p == null) { error = "这个文件里没有可用的 profile"; return null; }
        return Save(name, p, out error, note);
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
