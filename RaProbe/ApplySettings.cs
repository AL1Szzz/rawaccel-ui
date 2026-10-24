using RaCore;
using RaCore.Model;

namespace RaProbe;

/// <summary>
/// Write a settings.json to the driver, with the same safety net the UI uses: validate first,
/// show what will change, write, read back, compare, and roll back to the previous bytes if the
/// read-back does not match. Read-only unless --yes is given.
/// </summary>
internal static class ApplySettings
{
    public static int Run(string[] args)
    {
        int i = Array.FindIndex(args, a => a == "--apply-settings");
        string path = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--")
            ? args[i + 1]
            : SettingsFile.Find() ?? "";
        bool go = args.Contains("--yes");

        if (path.Length == 0)
        {
            Console.WriteLine("没找到官方 settings.json，请用 --apply-settings <路径> 指定。找过：");
            foreach (var c in SettingsFile.Candidates()) Console.WriteLine("   " + c);
            return 2;
        }
        if (!File.Exists(path)) { Console.WriteLine("文件不存在: " + path); return 2; }
        RootDto doc;
        try { doc = Json.Deserialize(File.ReadAllText(path)); }
        catch (Exception e) { Console.WriteLine("解析失败: " + e.Message); return 2; }

        var problems = Json.Validate(doc);
        Console.WriteLine($"target file : {path}");
        Console.WriteLine($"validation  : {(problems.Count == 0 ? "OK" : string.Join("; ", problems))}");
        if (problems.Count > 0) return 2;

        byte[] before;
        try { before = RaDevice.Read(); }
        catch (Exception e) { Console.WriteLine("驱动读取失败: " + e.Message); return 2; }
        var live = Convert4.ReadBlob(before);

        Console.WriteLine($"driver now  : {Describe(live)}");
        Console.WriteLine($"file says   : {Describe(doc)}");
        Diff(live, doc);

        if (!go) { Console.WriteLine("\n没有 --yes，只看不写。"); return 0; }

        var blob = Convert4.BuildBlob(doc);
        Console.WriteLine($"\nwriting {blob.Length} bytes (the driver throttles writes to 1/s; the mouse may hitch ~1s)…");
        try { RaDevice.Write(blob); }
        catch (Exception e) { Console.WriteLine("写入失败: " + e.Message); return 1; }

        Thread.Sleep(1600);
        var back = Convert4.ReadBlob(RaDevice.Read());
        Console.WriteLine("read back   : " + Describe(back));

        bool same = back.Profiles.Count == doc.Profiles.Count;
        for (int p = 0; same && p < doc.Profiles.Count; p++)
            same &= ConfigHash.Of(back.Profiles[p]) == ConfigHash.Of(doc.Profiles[p]);
        if (same) { Console.WriteLine("=> 已生效并回读一致"); return 0; }

        Console.WriteLine("=> 回读与提交不一致，退回写入前的字节…");
        try
        {
            RaDevice.Write(before);
            Thread.Sleep(1600);
            Console.WriteLine("   退回后驱动 = " + Describe(Convert4.ReadBlob(RaDevice.Read())));
        }
        catch (Exception e) { Console.WriteLine("   退回失败: " + e.Message); }
        return 1;
    }

    static string Describe(RootDto r)
    {
        var p = r.Profiles.FirstOrDefault();
        if (p == null) return "(no profile)";
        return $"profile '{p.Name}' sha {ConfigHash.Of(p)}  X(offset {p.X.InputOffset}, accel {p.X.Acceleration}, cap {p.X.Cap.X}/{p.X.Cap.Y} {p.X.CapMode})" +
               $"  Y(offset {p.Y.InputOffset}, accel {p.Y.Acceleration}, cap {p.Y.Cap.X}/{p.Y.Cap.Y})  outDPI {p.OutputDpi}";
    }

    static void Diff(RootDto a, RootDto b)
    {
        var pa = a.Profiles.FirstOrDefault(); var pb = b.Profiles.FirstOrDefault();
        if (pa == null || pb == null) return;
        void Cmp(string what, double x, double y)
        {
            if (Math.Abs(x - y) > 1e-12) Console.WriteLine($"   change: {what} {Num(x)} -> {Num(y)}");
        }
        Cmp("X.inputOffset", pa.X.InputOffset, pb.X.InputOffset);
        Cmp("X.acceleration", pa.X.Acceleration, pb.X.Acceleration);
        Cmp("X.cap.x", pa.X.Cap.X, pb.X.Cap.X);
        Cmp("X.cap.y", pa.X.Cap.Y, pb.X.Cap.Y);
        Cmp("Y.inputOffset", pa.Y.InputOffset, pb.Y.InputOffset);
        Cmp("Y.acceleration", pa.Y.Acceleration, pb.Y.Acceleration);
        Cmp("Y.cap.x", pa.Y.Cap.X, pb.Y.Cap.X);
        Cmp("Y.cap.y", pa.Y.Cap.Y, pb.Y.Cap.Y);
        Cmp("outputDpi", pa.OutputDpi, pb.OutputDpi);
        Cmp("yxRatio", pa.YxRatio, pb.YxRatio);
        if (pa.X.Mode != pb.X.Mode) Console.WriteLine($"   change: X.mode {pa.X.Mode} -> {pb.X.Mode}");
        if (pa.Y.Mode != pb.Y.Mode) Console.WriteLine($"   change: Y.mode {pa.Y.Mode} -> {pb.Y.Mode}");
        if (a.Devices.Count != b.Devices.Count)
            Console.WriteLine($"   note: 设备条目 {a.Devices.Count} -> {b.Devices.Count}");
    }

    static string Num(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
