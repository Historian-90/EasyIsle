using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

public class PlanOptions
{
    /// <summary>Let the plan use gatherable materials you don't have yet (they show up as "gather").</summary>
    public bool AllowGathering { get; set; } = true;

    /// <summary>Push Felicitous Favor items until their requests are covered.</summary>
    public bool PrioritizeFavors { get; set; }
}

public class PlannedCraft
{
    public Craft Craft { get; init; } = null!;
    public int StartHour { get; init; }
    public bool Efficient { get; init; }

    /// <summary>Items exported: 2 for efficient crafts.</summary>
    public int Count => Efficient ? 2 : 1;

    public int ValueEach { get; set; }
    public int Total => ValueEach * Count;
}

public class WorkshopPlan
{
    public int Workshop { get; init; }
    public int Level { get; init; }
    public List<PlannedCraft> Crafts { get; } = [];
    public int Hours => Crafts.Sum(c => c.Craft.Hours);
    public int Total => Crafts.Sum(c => c.Total);
}

public class Shortfall
{
    public string Name { get; init; } = string.Empty;
    public int Need { get; init; }
    public int Have { get; init; }
    public bool Gatherable { get; init; }
}

public class Plan
{
    /// <summary>Cycle being planned, 0-6 (0 = Tuesday reset day).</summary>
    public int Cycle { get; set; } = -1;

    public List<WorkshopPlan> Workshops { get; } = [];
    public List<Shortfall> Shortfalls { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> FavorItemsBoosted { get; } = [];

    /// <summary>Crafts left out because the materials on hand can't cover them.</summary>
    public SortedSet<string> SkippedForMaterials { get; } = [];
    public int Total => Workshops.Sum(w => w.Total);
    public int GrooveAfter { get; set; }
}

/// <summary>
/// Picks the best agenda for each workshop from what the island can make.
/// Value per item: floor(floor(base x (1 + workshop level/10) x (1 + groove/100)) x popularity x supply).
/// Efficient crafts (a different item sharing a theme with the one before) export two items and add
/// one groove. Each workshop gets the best 24-hour chain the remaining materials allow.
/// </summary>
public static class Planner
{
    private const int DayHours = 24;
    private static readonly double[] SupplyMultiplier = [1.6, 1.3, 1.0, 0.8, 0.6];

    public static Plan Make(IslandSnapshot s, PlanOptions options, ISet<uint> gatherablePouchIds)
    {
        var plan = new Plan();
        if (!s.DemandValid)
        {
            plan.Notes.Add("Open the Isleworks agenda once so EasyIsle can read Supply & Demand.");
            return plan;
        }

        plan.Cycle = NextWorkCycle(s);
        if (plan.Cycle < 0)
        {
            plan.Notes.Add("No working cycles are left this season. Plan again after the Tuesday reset.");
            return plan;
        }

        var crafts = s.Crafts.Where(c => c.Available && c.Hours > 0).ToList();
        var stock = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Count);
        var names = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Name);
        var used = new Dictionary<uint, int>();
        var groove = (int)s.Groove;

        // Favor requests still open: craft id -> items still needed.
        var favorNeed = new Dictionary<uint, int>();
        if (options.PrioritizeFavors)
        {
            foreach (var f in s.Favors.Where(f => f.Season == "Current" && !f.Shipped))
            {
                var need = f.Required - f.Delivered - f.Scheduled;
                if (need <= 0) continue;
                favorNeed[f.CraftId] = favorNeed.GetValueOrDefault(f.CraftId) + need;
                plan.FavorItemsBoosted.Add($"{f.Name} ({need} more)");
            }
        }

        var themeBits = BuildThemeBits(crafts);
        for (var w = 0; w < s.Workshops.Count; w++)
        {
            var ws = s.Workshops[w];
            if (!ws.Placed || ws.UnderConstruction) continue;

            var banned = new HashSet<uint>();
            List<(Craft craft, bool efficient)>? chain = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var candidates = crafts.Where(c => !banned.Contains(c.Id)).ToList();
                var values = candidates.ToDictionary(c => c.Id, c =>
                {
                    double v = ValueEach(c, ws.Level, groove);
                    if (favorNeed.GetValueOrDefault(c.Id) > 0) v *= 3;
                    return v;
                });
                chain = BestChain(candidates, values, themeBits);
                if (chain.Count == 0) break;

                var blocker = FindBlocker(chain, stock, used, gatherablePouchIds, options.AllowGathering);
                if (blocker == 0) break;
                banned.Add(blocker);
                plan.SkippedForMaterials.Add(crafts.First(c => c.Id == blocker).Name);
                chain = null;
            }

            if (chain == null || chain.Count == 0) continue;

            var wp = new WorkshopPlan { Workshop = w + 1, Level = ws.Level };
            var hour = 0;
            foreach (var (craft, efficient) in chain)
            {
                wp.Crafts.Add(new PlannedCraft { Craft = craft, StartHour = hour, Efficient = efficient });
                hour += craft.Hours;
                foreach (var m in craft.Materials) used[m.PouchId] = used.GetValueOrDefault(m.PouchId) + m.Amount;
                if (favorNeed.ContainsKey(craft.Id)) favorNeed[craft.Id] -= efficient ? 2 : 1;
            }

            plan.Workshops.Add(wp);
        }

        // Price everything in start order across workshops, so groove earned early counts later.
        var timeline = plan.Workshops
            .SelectMany(wp => wp.Crafts.Select(c => (wp, c)))
            .OrderBy(x => x.c.StartHour).ThenBy(x => x.wp.Workshop);
        foreach (var (wp, c) in timeline)
        {
            if (c.Efficient && groove < s.GrooveMax) groove++;
            c.ValueEach = ValueEach(c.Craft, wp.Level, groove);
        }

        plan.GrooveAfter = groove;

        foreach (var (id, need) in used.OrderBy(u => names.GetValueOrDefault(u.Key)))
        {
            var have = stock.GetValueOrDefault(id);
            if (need > have)
                plan.Shortfalls.Add(new Shortfall { Name = names.GetValueOrDefault(id, $"#{id}"), Need = need, Have = have, Gatherable = gatherablePouchIds.Contains(id) });
        }

        if (plan.Workshops.Count == 0)
            plan.Notes.Add("Nothing can be made with the materials on hand.");
        return plan;
    }

    /// <summary>First cycle after today that isn't a rest day, or -1.</summary>
    public static int NextWorkCycle(IslandSnapshot s)
    {
        for (var c = s.CycleDay + 1; c < 7; c++)
            if (!s.RestDays.Contains(c)) return c;
        return -1;
    }

    public static int ValueEach(Craft c, int workshopLevel, int groove)
    {
        var supply = c.SupplyLevel is >= 0 and < 5 ? SupplyMultiplier[c.SupplyLevel] : 1.0;
        var popularity = c.PopularityPercent > 0 ? c.PopularityPercent / 100.0 : 1.0;
        var inner = Math.Floor(c.Value * (1 + workshopLevel / 10.0) * (1 + groove / 100.0) + 1e-9);
        return (int)Math.Floor(inner * popularity * supply + 1e-9);
    }

    private static Dictionary<uint, ulong> BuildThemeBits(List<Craft> crafts)
    {
        var themeIndex = new Dictionary<string, int>();
        var bits = new Dictionary<uint, ulong>();
        foreach (var c in crafts)
        {
            ulong mask = 0;
            foreach (var t in c.Themes)
            {
                if (!themeIndex.TryGetValue(t, out var i)) themeIndex[t] = i = themeIndex.Count;
                if (i < 64) mask |= 1UL << i;
            }

            bits[c.Id] = mask;
        }

        return bits;
    }

    /// <summary>Best chain within 24 hours: dynamic programming over (hours used, last craft).</summary>
    private static List<(Craft, bool)> BestChain(List<Craft> crafts, Dictionary<uint, double> values, Dictionary<uint, ulong> themes)
    {
        var n = crafts.Count;
        var best = new double[DayHours + 1, n];
        var parent = new int[DayHours + 1, n];
        for (var h = 0; h <= DayHours; h++)
        for (var i = 0; i < n; i++) { best[h, i] = double.NegativeInfinity; parent[h, i] = -2; }

        for (var i = 0; i < n; i++)
        {
            var h = crafts[i].Hours;
            if (h > DayHours) continue;
            if (values[crafts[i].Id] > best[h, i]) { best[h, i] = values[crafts[i].Id]; parent[h, i] = -1; }
        }

        for (var h = 1; h <= DayHours; h++)
        for (var last = 0; last < n; last++)
        {
            if (double.IsNegativeInfinity(best[h, last])) continue;
            var lastThemes = themes[crafts[last].Id];
            for (var j = 0; j < n; j++)
            {
                if (j == last || (themes[crafts[j].Id] & lastThemes) == 0) continue;
                var nh = h + crafts[j].Hours;
                if (nh > DayHours) continue;
                var v = best[h, last] + 2 * values[crafts[j].Id];
                if (v > best[nh, j]) { best[nh, j] = v; parent[nh, j] = last; }
            }
        }

        var bh = -1;
        var bi = -1;
        var bv = double.NegativeInfinity;
        for (var h = 1; h <= DayHours; h++)
        for (var i = 0; i < n; i++)
            if (best[h, i] > bv) { bv = best[h, i]; bh = h; bi = i; }

        var chain = new List<(Craft, bool)>();
        while (bi >= 0)
        {
            var p = parent[bh, bi];
            chain.Add((crafts[bi], p >= 0));
            var prevH = bh - crafts[bi].Hours;
            bi = p;
            bh = prevH;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>Returns a craft in the chain whose materials can't be covered, or 0 if the chain is fine.</summary>
    private static uint FindBlocker(List<(Craft craft, bool efficient)> chain, Dictionary<uint, int> stock,
                                    Dictionary<uint, int> used, ISet<uint> gatherable, bool allowGathering)
    {
        var need = new Dictionary<uint, int>(used);
        foreach (var (craft, _) in chain)
        {
            foreach (var m in craft.Materials)
            {
                need[m.PouchId] = need.GetValueOrDefault(m.PouchId) + m.Amount;
                if (allowGathering && gatherable.Contains(m.PouchId)) continue;
                if (need[m.PouchId] > stock.GetValueOrDefault(m.PouchId)) return craft.Id;
            }
        }

        return 0;
    }
}
