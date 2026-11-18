using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

/// <summary>
/// Each week every craft follows one of 12 supply patterns that ends in a peak (lowest supply) on
/// cycle 2-7, weak (Insufficient) or strong (Nonexistent). The pattern moves the item's hidden supply
/// points by a fixed amount each cycle; exports add points on top (1 per item). Reading Supply &amp;
/// Demand once per cycle narrows down which pattern each item is on.
/// </summary>
public static class PeakModel
{
    public const int PatternCount = 12;

    // Change in supply points on each cycle (index 0 = cycle 1), per pattern.
    // Order: 2 weak, 2 strong, 3 weak, 3 strong, ..., 7 weak, 7 strong.
    private static readonly int[][] Steps =
    [
        [-4, -4, 10, 0, 0, 0, 0],
        [-8, -7, 15, 0, 0, 0, 0],
        [0, -4, -4, 10, 0, 0, 0],
        [0, -8, -7, 15, 0, 0, 0],
        [0, 0, -4, -4, 10, 0, 0],
        [0, 0, -8, -7, 15, 0, 0],
        [0, 0, 0, -4, -4, 10, 0],
        [0, 0, 0, -8, -7, 15, 0],
        [0, -1, 5, -4, -4, -4, 10],
        [0, -1, 8, -7, -8, -7, 15],
        [0, -1, 8, -3, -4, -4, -4],
        [0, -1, 8, 0, -7, -8, -7],
    ];

    private static readonly int[,] Cumulative = BuildCumulative();

    private static int[,] BuildCumulative()
    {
        var c = new int[PatternCount, 7];
        for (var p = 0; p < PatternCount; p++)
        {
            var sum = 0;
            for (var d = 0; d < 7; d++)
            {
                sum += Steps[p][d];
                c[p, d] = sum;
            }
        }

        return c;
    }

    public static int PeakCycle(int pattern) => pattern / 2 + 1; // 0-based cycle index (1 = cycle 2)
    public static bool Strong(int pattern) => pattern % 2 == 1;

    /// <summary>Supply points from the pattern alone on a cycle (0-6).</summary>
    public static int PatternPoints(int pattern, int cycle) => Cumulative[pattern, Math.Clamp(cycle, 0, 6)];

    /// <summary>0 Nonexistent, 1 Insufficient, 2 Sufficient, 3 Surplus, 4 Overflowing.</summary>
    public static int Bucket(int points) => points switch
    {
        < -8 => 0,
        < 0 => 1,
        < 8 => 2,
        < 16 => 3,
        _ => 4,
    };

    /// <summary>0 Skyrocketing, 1 Increasing, 2 None, 3 Decreasing, 4 Plummeting.</summary>
    public static int Shift(int previous, int current)
    {
        var diff = current - previous;
        if (diff < -5) return 0;
        if (diff < 0) return 1;
        if (diff == 0) return 2;
        return diff < 6 ? 3 : 4;
    }

    /// <summary>
    /// Works out which patterns fit the readings taken so far this week.
    /// </summary>
    /// <param name="readings">cycle (0-6) -> supply/demand byte (supply in the high nibble).</param>
    public static Forecast Build(IReadOnlyDictionary<int, int> readings)
    {
        var candidates = Enumerable.Range(0, PatternCount).ToList();
        var lastDay = -1;
        var lastBucket = 2;
        foreach (var (day, raw) in readings.OrderBy(r => r.Key))
        {
            var bucket = raw >> 4;
            var shift = raw & 0xF;
            List<int> next;
            if (day == 0)
            {
                // Cycle 1: only cycle-2 peaks show up (as Insufficient).
                if (bucket <= 1)
                {
                    next = candidates.Where(p => PeakCycle(p) == 1).ToList();
                    if (shift == 2) next = next.Where(Strong).ToList();
                    else if (shift == 1) next = next.Where(p => !Strong(p)).ToList();
                }
                else
                {
                    next = candidates.Where(p => PeakCycle(p) != 1).ToList();
                }
            }
            else
            {
                // The demand shift follows the pattern alone; our own exports only push supply up.
                next = candidates.Where(p =>
                    Shift(PatternPoints(p, day - 1), PatternPoints(p, day)) == shift
                    && bucket >= Bucket(PatternPoints(p, day))).ToList();
            }

            if (next.Count > 0) candidates = next;
            lastDay = day;
            lastBucket = bucket;
        }

        var forecast = new Forecast { LastReadingDay = lastDay };
        foreach (var p in candidates)
        {
            // Points our earlier exports added: the least that explains the last reading.
            var offset = 0;
            if (lastDay >= 0)
            {
                var basePoints = PatternPoints(p, lastDay);
                while (offset < 40 && Bucket(basePoints + offset) < lastBucket) offset++;
            }

            forecast.Patterns.Add((p, offset));
        }

        return forecast;
    }

    /// <summary>Plain-language peak description for a set of patterns.</summary>
    public static string Describe(Forecast f)
    {
        if (f.Patterns.Count == 0 || f.Patterns.Count == PatternCount) return "unknown";
        var cycles = f.Patterns.Select(p => PeakCycle(p.Pattern) + 1).Distinct().OrderBy(c => c).ToList();
        var strengths = f.Patterns.Select(p => Strong(p.Pattern)).Distinct().ToList();
        var strength = strengths.Count == 1 ? (strengths[0] ? " strong" : " weak") : string.Empty;
        if (cycles.Count == 1) return $"cycle {cycles[0]}{strength}";
        var consecutive = cycles.Last() - cycles.First() == cycles.Count - 1;
        return consecutive ? $"cycle {cycles.First()}-{cycles.Last()}" : "cycle " + string.Join(" or ", cycles);
    }
}

public class Forecast
{
    /// <summary>Possible patterns, each with the export points already added before the last reading.</summary>
    public List<(int Pattern, int Offset)> Patterns { get; } = [];

    public int LastReadingDay { get; init; } = -1;

    public bool Known => Patterns.Count == 1;

    /// <summary>Average supply multiplier on a cycle, with extra exported items since the last reading.</summary>
    public double SupplyMultiplier(int cycle, int extraItems)
    {
        if (Patterns.Count == 0) return 1.0;
        var sum = 0.0;
        foreach (var (p, offset) in Patterns)
            sum += Planner.SupplyMultiplierFor(PeakModel.Bucket(PeakModel.PatternPoints(p, cycle) + offset + extraItems));
        return sum / Patterns.Count;
    }
}
