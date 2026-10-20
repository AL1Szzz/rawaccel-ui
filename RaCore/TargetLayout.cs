namespace RaCore;

/// <summary>A placed ball, in physical desktop pixels.</summary>
public readonly record struct BallTarget(int Id, double X, double Y, double DistancePx, double AngleRad);

/// <summary>
/// Pure, seeded ball placement so the geometry can be asserted without opening a window.
/// Rejected positions: off screen, under the banner strip, or overlapping another ball.
/// A batch never invents a place; if the screen cannot hold the requested count the caller
/// must report the shortfall instead of silently testing fewer balls.
/// </summary>
public static class TargetLayout
{
    public static List<BallTarget> Place(double originX, double originY, double screenW, double screenH,
                                         double distancePx, double radiusPx, int count, int seed,
                                         int directionMode = 0, double bannerKeepOutPx = 90, int attemptsPerBall = 240,
                                         IReadOnlyList<BallTarget>? avoid = null)
    {
        var list = new List<BallTarget>(Math.Max(0, count));
        if (count <= 0 || screenW <= 0 || screenH <= 0 || radiusPx <= 0) return list;
        distancePx = Math.Max(1, distancePx);
        double margin = radiusPx + 6;
        // Balls must not look like a cluster. 3.5 radii still allowed a 39px edge gap at
        // r=26, which reads as "touching"; the gap must also scale with the flick distance,
        // otherwise a respawn 700px away can land right beside an untouched ball.
        double separation = Math.Max(radiusPx * 5, distancePx * 0.25);
        var rng = new Random(seed);

        for (int id = 1; id <= count; id++)
        {
            bool placed = false;
            for (int a = 0; a < attemptsPerBall && !placed; a++)
            {
                double angle = Angle(rng, directionMode);
                double x = originX + Math.Cos(angle) * distancePx;
                double y = originY + Math.Sin(angle) * distancePx;

                if (x < margin || x > screenW - margin) continue;
                if (y < bannerKeepOutPx + margin || y > screenH - margin) continue;
                if (list.Any(t => Dist(t.X, t.Y, x, y) < separation)) continue;
                if (avoid != null && avoid.Any(t => Dist(t.X, t.Y, x, y) < separation)) continue;

                list.Add(new BallTarget(id, x, y, Dist(originX, originY, x, y), angle));
                placed = true;
            }
            if (!placed) break;         // no room for this one: stop, do not squeeze it in
        }
        return list;
    }

    /// <summary>
    /// Where the pointer is aiming: the ball actually under the cursor wins, otherwise the
    /// nearest one, flagged so a miss is not silently attributed to a ball the player never chose.
    /// </summary>
    public static (int index, bool underCursor) Pick(IReadOnlyList<BallTarget> balls, double radiusPx, double x, double y)
    {
        if (balls.Count == 0) return (-1, false);
        for (int i = 0; i < balls.Count; i++)
            if (Dist(balls[i].X, balls[i].Y, x, y) <= radiusPx) return (i, true);

        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < balls.Count; i++)
        {
            double d = Dist(balls[i].X, balls[i].Y, x, y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, false);
    }

    static double Angle(Random rng, int mode) => mode switch
    {
        1 => (rng.NextDouble() - 0.5) * Math.PI / 2,                                  // right
        2 => Math.PI + (rng.NextDouble() - 0.5) * Math.PI / 2,                        // left
        3 => Math.PI / 2 + (rng.NextDouble() - 0.5) * Math.PI / 2,                    // down
        4 => -Math.PI / 2 + (rng.NextDouble() - 0.5) * Math.PI / 2,                   // up
        5 => rng.Next(2) == 0 ? (rng.NextDouble() - 0.5) * Math.PI / 4               // horizontal both ways
                             : Math.PI + (rng.NextDouble() - 0.5) * Math.PI / 4,
        _ => rng.NextDouble() * 2 * Math.PI,
    };

    static double Dist(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>Radial (+ = beyond the target) and tangential error of a click relative to the aim direction.</summary>
    public static (double radial, double tangential, double error, double radialPct) Score(
        double clickX, double clickY, double originX, double originY, double targetX, double targetY)
    {
        double ax = targetX - originX, ay = targetY - originY;
        double len = Math.Sqrt(ax * ax + ay * ay);
        if (len < 1e-6) { double e = Dist(clickX, clickY, targetX, targetY); return (0, 0, e, 0); }
        ax /= len; ay /= len;
        double cx = clickX - originX, cy = clickY - originY;
        double along = cx * ax + cy * ay;                      // how far along the aim direction the pointer went
        double radial = along - len;                           // + = overshoot
        double tang = -cx * ay + cy * ax;                      // perpendicular offset from the aim line
        return (radial, tang, Dist(clickX, clickY, targetX, targetY), radial / len * 100.0);
    }
}
