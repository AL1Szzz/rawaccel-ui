namespace RaCore;

/// <summary>
/// One number a player cannot measure by feel: does the curve make a short flick and a long flick
/// take proportionally long, or does one of them feel like a different mouse?
///
/// A drill gives us one timing per hit (how long since the previous hit) and one distance (how far
/// the pointer travelled to get there). Those pairs are bucketed by distance, and the buckets are
/// compared: if the pointer covers the same pixels per second whether the target was close or far,
/// the curve is consistent, and muscle memory built at one distance carries to another. If the short
/// flicks are fast and the long ones crawl - or the other way round - the curve is fighting the hand.
///
/// Consistency is the thing an acceleration curve is supposed to buy. Raw speed is not: a curve that
/// doubles everything scores the same as a curve that halves everything, which is why the calibration
/// compares candidates on consistency first and only uses speed to break ties.
///
/// Pure arithmetic - no clock, no window - so a round can be replayed in the self tests.
/// </summary>
public sealed class FlickStats
{
    /// <summary>Upper bounds of the distance buckets, in physical pixels. The last one is open.</summary>
    public static readonly double[] BandEdges = { 220, 520, 900, double.MaxValue };

    public static readonly string[] BandNames = { "近距离", "中距离", "远距离", "超远距离" };

    readonly double[] distSum = new double[BandEdges.Length];
    readonly double[] msSum = new double[BandEdges.Length];
    readonly int[] count = new int[BandEdges.Length];

    public int Samples { get; private set; }

    public static int BandOf(double distPx)
    {
        double d = Math.Abs(distPx);
        for (int i = 0; i < BandEdges.Length; i++)
            if (d < BandEdges[i]) return i;
        return BandEdges.Length - 1;
    }

    public void Add(double distPx, double ms)
    {
        if (!(distPx >= 0) || !(ms > 0) || !double.IsFinite(ms)) return;
        int b = BandOf(distPx);
        distSum[b] += distPx;
        msSum[b] += ms;
        count[b]++;
        Samples++;
    }

    public int Count(int band) => count[band];

    /// <summary>Pixels per second in one bucket, or 0 when the bucket is empty.</summary>
    public double PxPerSec(int band) => count[band] > 0 && msSum[band] > 0 ? distSum[band] * 1000.0 / msSum[band] : 0;

    public double[] BandSpeeds()
    {
        var v = new double[BandEdges.Length];
        for (int i = 0; i < v.Length; i++) v[i] = PxPerSec(i);
        return v;
    }

    /// <summary>
    /// 0..1, higher is better. 1 means every bucket with data was covered at the same speed.
    /// Computed as 1 - coefficient of variation over the buckets that actually got a sample, so a
    /// round where the player never made a long flick is judged on the distances they did use
    /// rather than being punished for the ones they did not.
    /// </summary>
    public double Consistency
    {
        get
        {
            var v = BandSpeeds().Where(s => s > 0).ToArray();
            if (v.Length < 2) return 0;              // one distance cannot be consistent with itself
            double mean = v.Average();
            if (mean <= 0) return 0;
            double var = v.Sum(s => (s - mean) * (s - mean)) / v.Length;
            double cv = Math.Sqrt(var) / mean;
            return Math.Clamp(1 - cv, 0, 1);
        }
    }

    /// <summary>Pixels per second over everything, the tie-breaker when consistency is close.</summary>
    public double OverallPxPerSec
    {
        get
        {
            double d = distSum.Sum(), t = msSum.Sum();
            return t > 0 ? d * 1000.0 / t : 0;
        }
    }

    public void Clear()
    {
        Array.Clear(distSum); Array.Clear(msSum); Array.Clear(count);
        Samples = 0;
    }

    /// <summary>One line per band, for the report table.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        for (int i = 0; i < BandEdges.Length; i++)
        {
            if (count[i] == 0) continue;
            parts.Add($"{BandNames[i]} {PxPerSec(i):0} px/s（{count[i]} 次）");
        }
        return parts.Count == 0 ? "没有记到甩枪" : string.Join(" · ", parts);
    }
}
