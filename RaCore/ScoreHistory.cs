using System.Globalization;

namespace RaCore;

/// <summary>
/// One click in the ball test. Distances are physical desktop pixels - this is a pointer test,
/// it does not model a game camera. Nothing here is sampled from the mouse stream: a trial is
/// just "the pointer was here, the ball was there, the button went down, it landed or it did not".
/// </summary>
public sealed class Trial
{
    public double DistancePx;             // pointer-to-ball-centre distance when the flick started
    public double StartMs, EndMs;         // when this flick began (previous click) and when the click landed
    public double OriginX, OriginY;       // where the pointer actually was after the previous click
    public double TargetX, TargetY;       // the ball that was aimed at
    public double ClickX, ClickY;
    public double ErrorPx;                // |click - ball centre|
    public bool Hit;                      // the cursor was inside that ball when the button went down
    public int TargetId, VisibleTargets;
}

/// <summary>One finished ball-test session, as it is kept in the history file.</summary>
public sealed class RoundRow
{
    public string When = "";
    public string Mode = "";              // "点掉 30 球" / "60 秒限时"
    public int Cleared;                   // balls actually clicked away
    public int Clicks;
    public double SessionMs;
    public int Balls;                     // balls on screen at once
    public double DistancePx, RadiusPx;
    public string Direction = "";
    public string Resolution = "";
    public string PlanName = "";          // the curve the driver was serving at round start
    public string ConfigSha = "";
    /// <summary>
    /// Free-form second half of the score line. The ball test leaves it empty and the drill modes put
    /// "87 个 · 43 每分钟" here; appending a column keeps the files written by earlier builds readable.
    /// </summary>
    public string Extra = "";

    public double HitRate => Clicks > 0 ? Cleared * 100.0 / Clicks : 0;

    /// <summary>"2026-10-23 01:12:04 · 点掉 30 球 · 命中率 87%（26/30） · 45.2 秒 · 压枪 1.25".</summary>
    public string Label()
    {
        var bits = new List<string> { When, Mode };
        if (Clicks > 0) bits.Add($"命中率 {HitRate:0}%（{Cleared}/{Clicks}）");
        if (SessionMs > 0) bits.Add($"{SessionMs / 1000.0:0.#} 秒");
        if (PlanName.Length > 0) bits.Add(PlanName);
        return string.Join(" · ", bits);
    }
}

/// <summary>
/// The ball-test history: one appended line per finished session, read back for the table on
/// the test page. It is a score list, not a data set - the full file is a few kilobytes and can
/// be opened in any editor.
/// </summary>
public static class ScoreHistory
{
    const string Header = "时间;成绩模式;点掉;点击数;命中率;用时秒;同屏球数;距离px;半径px;方向;分辨率;方案;配置sha;补充";
    const int MaxRows = 500;

    /// <summary>Redirect for the self tests so they never touch the real history.</summary>
    public static string? DirOverride;

    public static string FilePath => Path.Combine(DirOverride ?? AppData.Root, "test-history.csv");

    public static void Append(RoundRow r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var rows = Recent(int.MaxValue);
        rows.Add(r);
        if (rows.Count > MaxRows) rows = rows.Skip(rows.Count - MaxRows).ToList();
        Write(rows);
    }

    /// <summary>Newest first, malformed lines skipped rather than thrown on.</summary>
    public static List<RoundRow> Recent(int n = 50)
    {
        var list = new List<RoundRow>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.StartsWith("时间;") || line.Trim().Length == 0) continue;
                var f = line.Split(';');
                if (f.Length < 13) continue;
                list.Add(new RoundRow
                {
                    When = f[0], Mode = f[1],
                    Cleared = I(f[2]), Clicks = I(f[3]),
                    SessionMs = D(f[5]) * 1000.0,
                    Balls = I(f[6]), DistancePx = D(f[7]), RadiusPx = D(f[8]),
                    Direction = f[9], Resolution = f[10], PlanName = f[11], ConfigSha = f[12],
                    Extra = f.Length > 13 ? f[13] : "",
                });
            }
        }
        catch { return new List<RoundRow>(); }
        list.Reverse();
        return n > 0 && list.Count > n ? list.Take(n).ToList() : list;
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    static void Write(List<RoundRow> rows)
    {
        var lines = new List<string> { Header };
        lines.AddRange(rows.Select(r => string.Join(";",
            Clean(r.When), Clean(r.Mode), r.Cleared, r.Clicks, $"{r.HitRate:0.0}",
            N(r.SessionMs / 1000.0), r.Balls, N(r.DistancePx), N(r.RadiusPx),
            Clean(r.Direction), Clean(r.Resolution), Clean(r.PlanName), Clean(r.ConfigSha), Clean(r.Extra))));

        // atomic replace: a crash mid-write must not leave a half line in the history
        var tmp = FilePath + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, FilePath, true);
    }

    static string Clean(string s) => (s ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
