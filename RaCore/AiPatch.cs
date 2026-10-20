using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RaCore.Model;

namespace RaCore;

/// <summary>
/// The paste-in half of the AI exchange: an assistant reads a round log and returns a small,
/// strictly validated JSON patch. Nothing here executes anything, and nothing outside a
/// whitelist of numeric/enum tuning fields can be touched. Unknown keys, non-finite numbers,
/// unknown modes, a stale round id or a mismatched base config are all refused.
/// </summary>
public static class ConfigHash
{
    /// <summary>Short stable hash of one profile exactly as it would be handed to the driver.</summary>
    public static string Of(ProfileDto p)
    {
        var s = Convert4.ToStruct(p);
        var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref s, 1);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
    }

    public static string Of(ra_profile p)
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref p, 1);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
    }

    /// <summary>Hash over the whole document (profiles + devices + defaults), for drift checks.</summary>
    public static string OfDoc(RootDto r)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(r));
        return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
    }
}

public sealed record PatchChange(string Path, string Old, string New, string Unit);

public sealed class PatchResult
{
    public bool Ok;
    public string Round = "";
    public string BaseConfig = "";
    public string ProfileName = "";
    public string Note = "";
    public List<string> Errors = new();
    public List<string> Warnings = new();
    public List<PatchChange> Changes = new();
    public ProfileDto? PreviewProfile;      // selected profile with the patch applied (never the driver)

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"轮次 {Round} · 配置 {BaseConfig} · 目标 profile “{ProfileName}”");
        if (!string.IsNullOrWhiteSpace(Note)) sb.AppendLine("AI 说明：" + Note);
        if (Changes.Count == 0) sb.AppendLine("（没有参数改动）");
        foreach (var c in Changes)
            sb.AppendLine($"  {c.Path}: {c.Old} → {c.New}{(string.IsNullOrEmpty(c.Unit) ? "" : "  [" + c.Unit + "]")}");
        foreach (var w in Warnings) sb.AppendLine("提醒: " + w);
        foreach (var e in Errors) sb.AppendLine("拒绝: " + e);
        return sb.ToString();
    }
}

public static class AiPatch
{
    public const string Kind = "raui.tune-patch";
    public const int Schema = 1;

    static readonly string[] Modes = { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" };
    static readonly string[] CapModes = { "in_out", "input", "output" };

    // path -> (min, max) for the whitelisted doubles; anything absent is refused
    static readonly Dictionary<string, (double min, double max)> Limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x.inputOffset"] = (0, 1000), ["y.inputOffset"] = (0, 1000),
        ["x.outputOffset"] = (-1000, 1000), ["y.outputOffset"] = (-1000, 1000),
        ["x.acceleration"] = (0, 100), ["y.acceleration"] = (0, 100),
        ["x.decayRate"] = (0, 100), ["y.decayRate"] = (0, 100),
        ["x.gamma"] = (0, 100), ["y.gamma"] = (0, 100),
        ["x.motivity"] = (0, 100), ["y.motivity"] = (0, 100),
        ["x.exponentClassic"] = (0.01, 20), ["y.exponentClassic"] = (0.01, 20),
        ["x.scale"] = (0, 1000), ["y.scale"] = (0, 1000),
        ["x.exponentPower"] = (0, 20), ["y.exponentPower"] = (0, 20),
        ["x.limit"] = (0, 1000), ["y.limit"] = (0, 1000),
        ["x.syncSpeed"] = (0, 1000), ["y.syncSpeed"] = (0, 1000),
        ["x.smooth"] = (0, 1), ["y.smooth"] = (0, 1),
        ["x.capX"] = (0, 10000), ["y.capX"] = (0, 10000),
        ["x.capY"] = (0, 100), ["y.capY"] = (0, 100),
        ["profile.outputDpi"] = (1, 8000),
        ["profile.yxRatio"] = (0.01, 100), ["profile.lrRatio"] = (0.01, 100), ["profile.udRatio"] = (0.01, 100),
        ["profile.rotation"] = (-180, 180), ["profile.snap"] = (0, 180),
        ["profile.inputSpeedCap"] = (0, 10000),
        ["profile.domainX"] = (0.001, 1000), ["profile.domainY"] = (0.001, 1000),
        ["profile.rangeX"] = (0.001, 1000), ["profile.rangeY"] = (0.001, 1000),
        ["speed.lpNorm"] = (0.1, 20),
        ["speed.inputHalflife"] = (0, 1000), ["speed.scaleHalflife"] = (0, 1000), ["speed.outputHalflife"] = (0, 1000),
    };

    static readonly Dictionary<string, string> Chinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x.inputOffset"] = "横向输入阈值", ["y.inputOffset"] = "纵向输入阈值",
        ["x.acceleration"] = "横向加速强度", ["y.acceleration"] = "纵向加速强度",
        ["x.capY"] = "横向封顶增益", ["y.capY"] = "纵向封顶增益",
        ["x.capX"] = "横向封顶点", ["y.capX"] = "纵向封顶点",
        ["x.mode"] = "横向模式", ["y.mode"] = "纵向模式",
        ["x.exponentClassic"] = "横向指数", ["y.exponentClassic"] = "纵向指数",
        ["x.outputOffset"] = "横向输出偏移", ["y.outputOffset"] = "纵向输出偏移",
        ["x.gain"] = "横向增益模式", ["y.gain"] = "纵向增益模式",
        ["x.capMode"] = "横向封顶方式", ["y.capMode"] = "纵向封顶方式",
        ["x.smooth"] = "横向模式平滑", ["y.smooth"] = "纵向模式平滑",
        ["x.decayRate"] = "横向衰减", ["y.decayRate"] = "纵向衰减",
        ["x.gamma"] = "横向 gamma", ["y.gamma"] = "纵向 gamma",
        ["x.motivity"] = "横向 motivity", ["y.motivity"] = "纵向 motivity",
        ["x.scale"] = "横向 scale", ["y.scale"] = "纵向 scale",
        ["x.exponentPower"] = "纵向/横向幂指数", ["y.exponentPower"] = "纵向幂指数",
        ["x.limit"] = "横向 limit", ["y.limit"] = "纵向 limit",
        ["x.syncSpeed"] = "横向同步速度", ["y.syncSpeed"] = "纵向同步速度",
        ["profile.outputDpi"] = "输出 DPI", ["profile.yxRatio"] = "纵向灵敏度比",
        ["profile.lrRatio"] = "向左灵敏度比", ["profile.udRatio"] = "向上灵敏度比",
        ["profile.rotation"] = "旋转角度", ["profile.snap"] = "角度吸附",
        ["profile.inputSpeedCap"] = "输入速度上限",
        ["profile.domainX"] = "横向速度域权重", ["profile.domainY"] = "纵向速度域权重",
        ["profile.rangeX"] = "横向加速域权重", ["profile.rangeY"] = "纵向加速域权重",
        ["speed.whole"] = "整体/分量计算",
        ["speed.lpNorm"] = "速度范数",
        ["speed.inputHalflife"] = "输入平滑半衰期",
        ["speed.scaleHalflife"] = "缩放平滑半衰期",
        ["speed.outputHalflife"] = "输出平滑半衰期",
    };

    /// <summary>Parse and validate. Never mutates the document; use Apply() on the preview after the user confirms.</summary>
    public static PatchResult Parse(string text, RootDto doc, string selectedProfileName, string? expectRound = null)
    {
        var r = new PatchResult { ProfileName = selectedProfileName };
        var (json, containerError) = ExtractJson(text ?? "");
        if (containerError != null) { r.Errors.Add(containerError); return r; }

        JsonDocument dom;
        try { dom = JsonDocument.Parse(json!); }
        catch (JsonException e) { r.Errors.Add("不是合法 JSON：" + e.Message); return r; }

        using (dom)
        {
            if (dom.RootElement.ValueKind != JsonValueKind.Object)
            { r.Errors.Add("顶层必须是 JSON 对象"); return r; }

            string? kind = Str(dom.RootElement, "kind");
            if (kind != Kind) { r.Errors.Add($"kind 必须是 \"{Kind}\"（收到 \"{kind ?? "缺失"}\"）"); return r; }

            if (!dom.RootElement.TryGetProperty("schema", out var sch) || sch.ValueKind != JsonValueKind.Number ||
                sch.GetInt32() != Schema)
            { r.Errors.Add($"schema 必须是 {Schema}"); return r; }

            r.Round = Str(dom.RootElement, "round") ?? "";
            r.BaseConfig = Str(dom.RootElement, "baseConfig") ?? "";
            r.ProfileName = Str(dom.RootElement, "profile") ?? selectedProfileName;
            r.Note = TrimNote(Str(dom.RootElement, "note"));

            if (string.IsNullOrWhiteSpace(r.Round)) r.Errors.Add("缺少 round（本轮日志文件名）");
            else if (expectRound != null && !string.Equals(r.Round, expectRound, StringComparison.OrdinalIgnoreCase))
                r.Errors.Add($"round 对不上：当前日志是 {expectRound}，方案写的是 {r.Round}");

            if (string.Equals(r.ProfileName, selectedProfileName, StringComparison.OrdinalIgnoreCase))
                r.Warnings.Add("只会改动当前选中的 profile");
            if (!string.Equals(r.ProfileName, selectedProfileName, StringComparison.OrdinalIgnoreCase))
            { r.Errors.Add($"profile “{r.ProfileName}” 不是当前选中的 “{selectedProfileName}”，先切过去再粘贴"); return r; }

            if (!doc.Profiles.Any(p => string.Equals(p.Name, r.ProfileName, StringComparison.Ordinal)))
            { r.Errors.Add($"文档里没有名为 {r.ProfileName} 的 profile"); return r; }
            var selected = doc.Profiles.First(p => string.Equals(p.Name, r.ProfileName, StringComparison.Ordinal));

            string liveHash = ConfigHash.Of(selected);
            if (string.IsNullOrWhiteSpace(r.BaseConfig)) r.Errors.Add("缺少 baseConfig（该轮日志 config 段的哈希）");
            else if (r.BaseConfig != liveHash)
                r.Errors.Add($"baseConfig 与当前配置不一致（方案基于 {r.BaseConfig}，当前是 {liveHash}）。" +
                             "要么配置在这轮之后被改过，要么这是另一轮的方案；请让 AI 基于最新日志重出");

            if (!dom.RootElement.TryGetProperty("changes", out var ch) || ch.ValueKind != JsonValueKind.Object)
            { r.Errors.Add("缺少 changes 对象"); return r; }

            var preview = selected.Clone();
            var applied = new List<Action>();

            foreach (var section in ch.EnumerateObject())
            {
                string sec = section.Name.ToLowerInvariant();
                if (sec is not ("x" or "y" or "profile" or "speed"))
                { r.Errors.Add($"未知改动段 “{section.Name}”，只允许 x / y / profile / speed"); continue; }
                if (section.Value.ValueKind != JsonValueKind.Object)
                { r.Errors.Add($"{sec} 必须是对象"); continue; }

                foreach (var f in section.Value.EnumerateObject())
                {
                    string path = sec + "." + f.Name.ToLowerInvariant();
                    if (string.Equals(f.Name, "data", StringComparison.OrdinalIgnoreCase))
                    { r.Errors.Add("不接受 data（查找表数组），请在曲线页手工编辑 LUT"); continue; }

                    if (!Known(preview, path)) { r.Errors.Add($"未知字段 {path}"); continue; }

                    switch (f.Value.ValueKind)
                    {
                        case JsonValueKind.Number:
                        {
                            if (!Limits.TryGetValue(path, out var lim))
                            { r.Errors.Add($"{path} 不接受数值改动"); break; }
                            var target = Get(preview, path);
                            if (target == null) { r.Errors.Add($"{path} 不是数值字段"); break; }
                            double v = f.Value.GetDouble();
                            if (!double.IsFinite(v)) { r.Errors.Add($"{path} 不是有限数字"); break; }
                            if (v < lim.min || v > lim.max)
                            { r.Errors.Add($"{path} = {Num(v)} 超出安全范围 [{Num(lim.min)}, {Num(lim.max)}]"); break; }
                            if (Math.Abs(v - target.Value) < 1e-12) { r.Warnings.Add($"{path} 与当前值相同，已忽略"); break; }
                            double old = target.Value;
                            applied.Add(() => Set(preview, path, v));
                            r.Changes.Add(new PatchChange(path + " " + Label(path), Num(old), Num(v), Unit(path)));
                            break;
                        }
                        case JsonValueKind.True or JsonValueKind.False:
                        {
                            if (path is not ("x.gain" or "y.gain" or "speed.whole"))
                            { r.Errors.Add($"{path} 不接受布尔值"); break; }
                            bool v = f.Value.GetBoolean();
                            bool old = GetBool(preview, path);
                            if (v == old) { r.Warnings.Add($"{path} 与当前值相同，已忽略"); break; }
                            applied.Add(() => SetBool(preview, path, v));
                            r.Changes.Add(new PatchChange(path + " " + Label(path), old ? "true" : "false",
                                                            v ? "true" : "false", ""));
                            break;
                        }
                        case JsonValueKind.String:
                        {
                            string v = f.Value.GetString() ?? "";
                            if (path is "x.mode" or "y.mode")
                            {
                                if (!Modes.Contains(v.ToLowerInvariant()))
                                { r.Errors.Add($"{path} 模式 “{v}” 不存在，可用：{string.Join("/", Modes)}"); break; }
                                string old = preview.X.Mode;
                                applied.Add(() => SetStr(preview, path, v.ToLowerInvariant()));
                                r.Changes.Add(new PatchChange(path + " " + Label(path), old, v.ToLowerInvariant(), ""));
                            }
                            else if (path is "x.capmode" or "y.capmode")
                            {
                                if (!CapModes.Contains(v.ToLowerInvariant()))
                                { r.Errors.Add($"{path} “{v}” 不存在，可用：{string.Join("/", CapModes)}"); break; }
                                string old = sec == "x" ? preview.X.CapMode : preview.Y.CapMode;
                                applied.Add(() => SetStr(preview, path, v.ToLowerInvariant()));
                                r.Changes.Add(new PatchChange(path + " " + Label(path), old, v.ToLowerInvariant(), ""));
                            }
                            else r.Errors.Add($"{path} 不接受字符串");
                            break;
                        }
                        default:
                            r.Errors.Add($"{path} 的值类型不支持（只接受数字 / true false / 模式字符串）");
                            break;
                    }
                }
            }

            if (r.Errors.Count == 0)
            {
                foreach (var a in applied) a();
                r.PreviewProfile = preview;
                r.Ok = true;
                DescribeEffects(preview, r);
            }
            return r;
        }
    }

    /// <summary>What the patch does to the curve, so the user sees an effect instead of a number.</summary>
    static void DescribeEffects(ProfileDto p, PatchResult r)
    {
        var s = Convert4.ToStruct(p);
        var ax = CurveAnalysis.Analyze(s, true);
        var ay = CurveAnalysis.Analyze(s, false);
        r.Warnings.Add($"改完后的静态曲线：横向 {Start(ax)} 封顶 {Cap(ax)} 峰值增益 {ax.PeakGain:0.00}；" +
                       $"纵向 {Start(ay)} 封顶 {Cap(ay)} 峰值增益 {ay.PeakGain:0.00}");
        r.Warnings.Add("曲线按单包、未平滑计算；设了半衰期时实际响应会比它滞后");
        if (Math.Abs(ax.PeakGain - ay.PeakGain) > 0.15)
            r.Warnings.Add($"横纵峰值增益差 {Math.Abs(ax.PeakGain - ay.PeakGain):0.00}，斜向甩动会被两轴拉成不同角度");
    }

    static string Start(AxisProfile a) => a.Threshold <= 0 ? "全程不加速起点 0" : $"{a.Threshold:0.00} counts/ms 起加速";
    static string Cap(AxisProfile a) => double.IsInfinity(a.CapSpeed) ? "30 counts/ms 内不封顶" : $"在 {a.CapSpeed:0.00} counts/ms 封顶";

    public static string SerializeSuggestion(string round, string baseConfig, string profile,
                                             IEnumerable<PatchChange> changes, string note = "")
    {
        var sb = new StringBuilder();
        // PatchChange.Path looks like "x.inputOffset 横向输入阈值": section . field, then a label
        string Section(string path) => path.Split(' ')[0].Split('.')[0];
        string Field(string path) => path.Split(' ')[0].Split('.')[1];

        sb.Append("{\n  \"kind\": \"").Append(Kind).Append("\",\n  \"schema\": ").Append(Schema)
          .Append(",\n  \"round\": \"").Append(round).Append("\",\n  \"baseConfig\": \"").Append(baseConfig)
          .Append("\",\n  \"profile\": \"").Append(profile).Append("\"");
        if (!string.IsNullOrWhiteSpace(note))
            sb.Append(",\n  \"note\": \"").Append(note.Replace("\"", "'")).Append("\"");
        sb.Append(",\n  \"changes\": {\n");
        var lines = new List<string>();
        foreach (var g in changes.GroupBy(c => Section(c.Path)))
        {
            var inner = string.Join(",\n      ", g.Select(c =>
            {
                string key = Field(c.Path);
                string val = c.New.TrimStart('"').TrimEnd('"');
                bool isNum = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                return $"\"{key}\": " + (isNum ? val : "\"" + val + "\"");
            }));
            lines.Add($"    \"{g.Key}\": {{\n      {inner}\n    }}");
        }
        sb.Append(string.Join(",\n", lines));
        sb.Append("\n  }\n}");
        return sb.ToString();
    }

    // ---------- accessors ----------

    /// <summary>Everything the patch format is allowed to touch; anything else is refused.</summary>
    static bool Known(ProfileDto p, string path)
    {
        if (Limits.ContainsKey(path)) return Get(p, path) != null;
        return path is "x.gain" or "y.gain" or "speed.whole" or "x.mode" or "y.mode" or "x.capmode" or "y.capmode";
    }

    static double? Get(ProfileDto p, string path)
    {
        var ax = path.StartsWith("x.") ? p.X : path.StartsWith("y.") ? p.Y : null;
        if (ax != null) return path[2..] switch
        {
            "inputoffset" => ax.InputOffset, "outputoffset" => ax.OutputOffset, "acceleration" => ax.Acceleration,
            "decayrate" => ax.DecayRate, "gamma" => ax.Gamma, "motivity" => ax.Motivity,
            "exponentclassic" => ax.ExponentClassic, "scale" => ax.Scale, "exponentpower" => ax.ExponentPower,
            "limit" => ax.Limit, "syncspeed" => ax.SyncSpeed, "smooth" => ax.Smooth,
            "capx" => ax.Cap.X, "capy" => ax.Cap.Y,
            _ => null,
        };
        return path switch
        {
            "profile.outputdpi" => p.OutputDpi, "profile.yxratio" => p.YxRatio,
            "profile.lrratio" => p.LrRatio, "profile.udratio" => p.UdRatio,
            "profile.rotation" => p.Rotation, "profile.snap" => p.Snap,
            "profile.inputspeedcap" => p.InputSpeedCap,
            "profile.domainx" => p.Domain.X, "profile.domainy" => p.Domain.Y,
            "profile.rangex" => p.Range.X, "profile.rangey" => p.Range.Y,
            "speed.lpnorm" => p.Speed.LpNorm,
            "speed.inputhalflife" => p.Speed.InputHalflife,
            "speed.scalehalflife" => p.Speed.ScaleHalflife,
            "speed.outputhalflife" => p.Speed.OutputHalflife,
            _ => null,
        };
    }

    static void Set(ProfileDto p, string path, double v)
    {
        var ax = path.StartsWith("x.") ? p.X : path.StartsWith("y.") ? p.Y : null;
        if (ax != null)
        {
            switch (path[2..])
            {
                case "inputoffset": ax.InputOffset = v; break;
                case "outputoffset": ax.OutputOffset = v; break;
                case "acceleration": ax.Acceleration = v; break;
                case "decayrate": ax.DecayRate = v; break;
                case "gamma": ax.Gamma = v; break;
                case "motivity": ax.Motivity = v; break;
                case "exponentclassic": ax.ExponentClassic = v; break;
                case "scale": ax.Scale = v; break;
                case "exponentpower": ax.ExponentPower = v; break;
                case "limit": ax.Limit = v; break;
                case "syncspeed": ax.SyncSpeed = v; break;
                case "smooth": ax.Smooth = v; break;
                case "capx": ax.Cap.X = v; break;
                case "capy": ax.Cap.Y = v; break;
            }
            return;
        }
        switch (path)
        {
            case "profile.outputdpi": p.OutputDpi = v; break;
            case "profile.yxratio": p.YxRatio = v; break;
            case "profile.lrratio": p.LrRatio = v; break;
            case "profile.udratio": p.UdRatio = v; break;
            case "profile.rotation": p.Rotation = v; break;
            case "profile.snap": p.Snap = v; break;
            case "profile.inputspeedcap": p.InputSpeedCap = v; break;
            case "profile.domainx": p.Domain.X = v; break;
            case "profile.domainy": p.Domain.Y = v; break;
            case "profile.rangex": p.Range.X = v; break;
            case "profile.rangey": p.Range.Y = v; break;
            case "speed.lpnorm": p.Speed.LpNorm = v; break;
            case "speed.inputhalflife": p.Speed.InputHalflife = v; break;
            case "speed.scalehalflife": p.Speed.ScaleHalflife = v; break;
            case "speed.outputhalflife": p.Speed.OutputHalflife = v; break;
        }
    }

    static bool GetBool(ProfileDto p, string path) => path switch
    {
        "x.gain" => p.X.Gain, "y.gain" => p.Y.Gain, "speed.whole" => p.Speed.Whole, _ => false,
    };

    static void SetBool(ProfileDto p, string path, bool v)
    {
        switch (path)
        {
            case "x.gain": p.X.Gain = v; break;
            case "y.gain": p.Y.Gain = v; break;
            case "speed.whole": p.Speed.Whole = v; break;
        }
    }

    static void SetStr(ProfileDto p, string path, string v)
    {
        switch (path)
        {
            case "x.mode": p.X.Mode = v; break;
            case "y.mode": p.Y.Mode = v; break;
            case "x.capmode": p.X.CapMode = v; break;
            case "y.capmode": p.Y.CapMode = v; break;
        }
    }

    static string Label(string path) => Chinese.TryGetValue(path, out var s) ? s : "";

    static string Unit(string path) => path.EndsWith("halflife", StringComparison.OrdinalIgnoreCase) ? "ms"
        : path.EndsWith("inputoffset", StringComparison.OrdinalIgnoreCase) || path.EndsWith("acceleration", StringComparison.OrdinalIgnoreCase)
          || path.EndsWith("syncspeed", StringComparison.OrdinalIgnoreCase)
            ? "counts/ms（未归一化时）" : "";

    static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string? TrimNote(string? s) => s == null ? null : (s.Length > 400 ? s[..400] + "…" : s);

    /// <summary>Accepts a fenced block or surrounding prose, but only if exactly one balanced top-level object is present.</summary>
    static (string? json, string? error) ExtractJson(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return (null, "粘贴框是空的");

        // strip a single markdown fence if present
        if (t.StartsWith("```"))
        {
            int nl = t.IndexOf('\n');
            int end = t.LastIndexOf("```", StringComparison.Ordinal);
            if (nl < 0 || end < nl) return (null, "代码块没有闭合");
            t = t[(nl + 1)..end].Trim();
        }

        int open = t.IndexOf('{');
        if (open < 0) return (null, "没找到 JSON 对象（要以 { 开头、} 结尾）");
        int depth = 0; bool inStr = false, esc = false; int close = -1;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) { close = i; break; }
                    continue;
            }
            if (close >= 0) break;
        }
        if (close < 0) return (null, "JSON 花括号没有配对");

        string json = t[open..(close + 1)];
        string tail = t[(close + 1)..].Trim();
        if (tail.Contains('{')) return (null, "文本里有多余的 {，只能粘贴一个 JSON 对象");
        return (json, null);
    }
}
