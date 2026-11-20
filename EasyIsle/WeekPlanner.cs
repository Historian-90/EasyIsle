using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

public class DayPlan
{
    public int Cycle { get; init; }
    public List<WorkshopPlan> Workshops { get; } = [];
    public int Total => (int)Math.Clamp(Workshops.Sum(w => (long)w.Total), int.MinValue, int.MaxValue);
}

public class WeekPlan
{
    public List<DayPlan> Days { get; } = [];
    public List<Shortfall> Shortfalls { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> FavorItemsBoosted { get; } = [];
    public SortedSet<string> SkippedForMaterials { get; } = [];
    public int Total => (int)Math.Clamp(Days.Sum(d => (long)d.Total), int.MinValue, int.MaxValue);
    public int GrooveAfter { get; set; }

    /// <summary>Materials the whole plan uses (MJIItemPouch row -> amount).</summary>
    public Dictionary<uint, int> MaterialUse { get; } = [];

    /// <summary>Set when moving a rest day would earn noticeably more.</summary>
    public string? RestAdvice { get; set; }

    /// <summary>The chosen craft ids per cycle and workshop (kept so the plan can stay put).</summary>
    public List<List<List<uint>>> Chains { get; set; } = [];

    /// <summary>True when this plan was restored from the saved one instead of searched again.</summary>
    public bool Kept { get; set; }

    public int PeaksKnown { get; set; }
    public int PeaksPartly { get; set; }
    public int PeaksUnknown { get; set; }
}

/// <summary>
/// Plans every remaining working cycle of the week together. Supply for each item comes from its
/// predicted peak pattern plus every item the plan exports before it (earlier cycles, and earlier the
/// same cycle), so the plan spreads production instead of flooding one item. Search: start greedy,
/// then repeatedly swap single workshop-days for better chains while the week total improves.
/// </summary>
public sealed class WeekPlanner
{
    private const int DayHours = 24;
    private const int ChainsPerSlot = 24;
    private const int ItemsConsidered = 36;
    private const int Passes = 4;
    private const double FavorUnitBonus = 400;

    private readonly IslandSnapshot s;
    private readonly PlanOptions options;
    private readonly ISet<uint> gatherable;
    private readonly List<Craft> crafts;
    private readonly Dictionary<uint, Craft> byId;
    private readonly Dictionary<uint, Forecast> forecasts;
    private readonly Dictionary<uint, ulong> themes = [];
    private readonly Dictionary<uint, int> stock;
    private readonly Dictionary<uint, int> favorNeed = [];
    private readonly List<int> workshopLevels = [];

    public WeekPlanner(IslandSnapshot snapshot, PlanOptions options, ISet<uint> gatherablePouchIds, Dictionary<uint, Forecast> forecasts)
    {
        s = snapshot;
        this.options = options;
        gatherable = gatherablePouchIds;
        this.forecasts = forecasts;
        crafts = s.Crafts.Where(c => c.Available && c.Hours > 0 && c.Hours <= DayHours).ToList();
        byId = crafts.ToDictionary(c => c.Id);
        stock = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Count);

        var themeIndex = new Dictionary<string, int>();
        foreach (var c in crafts)
        {
            ulong mask = 0;
            foreach (var t in c.Themes)
            {
                if (!themeIndex.TryGetValue(t, out var i)) themeIndex[t] = i = themeIndex.Count;
                if (i < 64) mask |= 1UL << i;
            }

            themes[c.Id] = mask;
        }

        foreach (var ws in s.Workshops)
            if (ws.Placed && !ws.UnderConstruction) workshopLevels.Add(ws.Level);

        if (options.PrioritizeFavors)
        {
            foreach (var f in s.Favors.Where(f => f.Season == "Current" && !f.Shipped))
            {
                var need = f.Required - f.Delivered - f.Scheduled;
                if (need <= 0 || !byId.ContainsKey(f.CraftId)) continue;
                favorNeed[f.CraftId] = favorNeed.GetValueOrDefault(f.CraftId) + need;
            }
        }
    }

    public static Dictionary<uint, Forecast> Forecasts(IslandSnapshot s)
    {
        var season = IslandReaderSeason(s);
        s.SupplyHistory.TryGetValue(season, out var days);
        var result = new Dictionary<uint, Forecast>();
        foreach (var c in s.Crafts)
        {
            var readings = new Dictionary<int, int>();
            if (days != null)
                foreach (var (day, values) in days)
                    if (values.TryGetValue(c.Id, out var raw)) readings[day] = raw;
            result[c.Id] = PeakModel.Build(readings);
        }

        return result;
    }

    private static string IslandReaderSeason(IslandSnapshot s)
    {
        var now = s.TakenUtc;
        var daysSince = ((int)now.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        var start = now.Date.AddDays(-daysSince).AddHours(8);
        if (start > now) start = start.AddDays(-7);
        return start.ToString("yyyy-MM-dd");
    }

    /// <summary>Working cycles left this week (after today, not rest days).</summary>
    public static List<int> WorkingCycles(IslandSnapshot s, IEnumerable<int>? restDays = null)
    {
        var rest = (restDays ?? s.RestDays).Where(d => d < 7).ToHashSet();
        return Enumerable.Range(s.CycleDay + 1, Math.Max(0, 6 - s.CycleDay)).Where(c => !rest.Contains(c)).ToList();
    }

    /// <summary>A plan saved earlier today; reused as long as it fits the same cycles and workshops.</summary>
    public WeekLock? Saved { get; init; }

    public WeekPlan Make()
    {
        var plan = new WeekPlan();
        if (!s.DemandValid)
        {
            plan.Notes.Add("Open the Isleworks agenda once so EasyIsle can read Supply & Demand.");
            return plan;
        }

        foreach (var f in forecasts.Where(f => byId.ContainsKey(f.Key)).Select(f => f.Value))
        {
            if (f.Known) plan.PeaksKnown++;
            else if (f.Patterns.Count is > 0 and < PeakModel.PatternCount) plan.PeaksPartly++;
            else plan.PeaksUnknown++;
        }

        var cycles = WorkingCycles(s);
        if (cycles.Count == 0)
        {
            plan.Notes.Add("No working cycles are left this season. Plan again after the Tuesday reset.");
            return plan;
        }

        if (workshopLevels.Count == 0)
        {
            plan.Notes.Add("No finished workshops.");
            return plan;
        }

        if (Saved != null && Saved.Cycles.SequenceEqual(cycles) && Saved.Chains.Count == cycles.Count
            && Saved.Chains.All(d => d.Count == workshopLevels.Count) && Saved.Chains.All(d => d.All(c => c.All(byId.ContainsKey))))
        {
            var kept = Saved.Chains.Select(d => d.Select(c => c.ToList()).ToArray()).ToArray();
            Fill(plan, cycles, kept);
            plan.Kept = true;
            plan.RestAdvice = Saved.RestAdvice;
            plan.Chains = Saved.Chains;
            return plan;
        }

        var best = Search(cycles, out var bestScore);
        Fill(plan, cycles, best);
        plan.Chains = best.Select(d => d.Select(c => c.ToList()).ToList()).ToList();

        // Rest day: would a different working cycle be worth more than one we're resting?
        var restFuture = s.RestDays.Where(d => d < 7 && d > s.CycleDay).ToList();
        if (restFuture.Count > 0)
        {
            var all = Enumerable.Range(s.CycleDay + 1, Math.Max(0, 6 - s.CycleDay)).ToList();
            var allChains = Search(all, out _);
            var perDay = Evaluate(all, allChains, out _).perDay;
            var dayValues = all.Select((c, i) => (cycle: c, value: perDay[i])).ToList();
            var weakest = dayValues.OrderBy(d => d.value).First();
            var gain = 0.0;
            foreach (var r in restFuture)
            {
                var restValue = dayValues.First(d => d.cycle == r).value;
                gain = Math.Max(gain, restValue - weakest.value);
            }

            if (!restFuture.Contains(weakest.cycle) && gain > 300)
            {
                var moveFrom = restFuture.OrderByDescending(r => dayValues.First(d => d.cycle == r).value).First();
                plan.RestAdvice = $"Rest on cycle {weakest.cycle + 1} instead of cycle {moveFrom + 1}: about {gain:N0} more this week. In the agenda's Rest Cycle Settings, swap {moveFrom + 1} for {weakest.cycle + 1} (current season) before cycle {weakest.cycle + 1} starts.";
            }
        }

        _ = bestScore;
        return plan;
    }

    // ---------------------------------------------------------------- search

    private List<uint>[][] Search(List<int> cycles, out double score)
    {
        var chains = new List<uint>[cycles.Count][];
        for (var d = 0; d < cycles.Count; d++)
        {
            chains[d] = new List<uint>[workshopLevels.Count];
            for (var w = 0; w < workshopLevels.Count; w++) chains[d][w] = [];
        }

        score = Evaluate(cycles, chains, out _).total;
        for (var pass = 0; pass < Passes; pass++)
        {
            var improved = false;
            for (var d = 0; d < cycles.Count; d++)
            {
                for (var w = 0; w < workshopLevels.Count; w++)
                {
                    var current = chains[d][w];
                    var choices = CandidateChains(cycles, chains, d, w);
                    foreach (var option in choices)
                    {
                        if (option.SequenceEqual(current)) continue;
                        chains[d][w] = option;
                        var v = Evaluate(cycles, chains, out _).total;
                        if (v > score + 0.5)
                        {
                            score = v;
                            current = option;
                            improved = true;
                        }
                    }

                    chains[d][w] = current;
                }
            }

            if (!improved && pass > 0) break;
        }

        return chains;
    }

    /// <summary>Best chains for one workshop-day, priced with the supply the rest of the plan leaves.</summary>
    private List<List<uint>> CandidateChains(List<int> cycles, List<uint>[][] chains, int d, int w)
    {
        var cycle = cycles[d];
        var level = workshopLevels[w];
        var groove = (int)s.Groove;

        // Items exported before this cycle, plus by other workshops on this cycle.
        var earlier = new Dictionary<uint, int>();
        for (var dd = 0; dd < cycles.Count; dd++)
        {
            for (var ww = 0; ww < chains[dd].Length; ww++)
            {
                if (dd > d || (dd == d && ww == w)) continue;
                var chain = chains[dd][ww];
                for (var i = 0; i < chain.Count; i++)
                    earlier[chain[i]] = earlier.GetValueOrDefault(chain[i]) + (i > 0 ? 2 : 1);
            }
        }

        var value = new Dictionary<uint, double>();
        foreach (var c in crafts)
        {
            var mult = forecasts.TryGetValue(c.Id, out var f) ? f.SupplyMultiplier(cycle, earlier.GetValueOrDefault(c.Id)) : 1.0;
            double v = Planner.ValueEach(c, level, groove, mult);
            if (favorNeed.ContainsKey(c.Id)) v += FavorUnitBonus;
            value[c.Id] = v;
        }

        // Keep the search small: the best items plus anything that shares a theme with them.
        var pool = crafts.OrderByDescending(c => value[c.Id] / c.Hours).Take(ItemsConsidered).ToList();
        foreach (var fid in favorNeed.Keys)
            if (byId.TryGetValue(fid, out var fc) && !pool.Contains(fc)) pool.Add(fc);

        return KBestChains(pool, value, ChainsPerSlot);
    }

    private readonly record struct Entry(double Value, int PrevHour, int PrevItem, int PrevRank);

    /// <summary>K best chains of efficient crafts within 24 hours (first craft normal, the rest efficient).</summary>
    private List<List<uint>> KBestChains(List<Craft> pool, Dictionary<uint, double> value, int k)
    {
        var n = pool.Count;
        var table = new List<Entry>[DayHours + 1, n];
        for (var h = 0; h <= DayHours; h++)
        for (var i = 0; i < n; i++) table[h, i] = [];

        for (var i = 0; i < n; i++)
            table[pool[i].Hours, i].Add(new Entry(value[pool[i].Id], -1, -1, -1));

        for (var h = 1; h <= DayHours; h++)
        {
            for (var last = 0; last < n; last++)
            {
                var entries = table[h, last];
                if (entries.Count == 0) continue;
                entries.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (entries.Count > k) entries.RemoveRange(k, entries.Count - k);
                var lastThemes = themes[pool[last].Id];
                for (var j = 0; j < n; j++)
                {
                    if (j == last || (themes[pool[j].Id] & lastThemes) == 0) continue;
                    var nh = h + pool[j].Hours;
                    if (nh > DayHours) continue;
                    var add = 2 * value[pool[j].Id];
                    for (var r = 0; r < entries.Count; r++)
                        table[nh, j].Add(new Entry(entries[r].Value + add, h, last, r));
                }
            }
        }

        var finals = new List<(double v, int h, int i, int r)>();
        for (var h = 1; h <= DayHours; h++)
        for (var i = 0; i < n; i++)
        {
            var entries = table[h, i];
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (entries.Count > k) entries.RemoveRange(k, entries.Count - k);
            for (var r = 0; r < entries.Count; r++) finals.Add((entries[r].Value, h, i, r));
        }

        var result = new List<List<uint>>();
        var seen = new HashSet<string>();
        foreach (var (_, h0, i0, r0) in finals.OrderByDescending(f => f.v))
        {
            var chain = new List<uint>();
            int h = h0, i = i0, r = r0;
            while (i >= 0)
            {
                chain.Add(pool[i].Id);
                var e = table[h, i][r];
                h = e.PrevHour;
                i = e.PrevItem;
                r = e.PrevRank;
            }

            chain.Reverse();
            if (seen.Add(string.Join(",", chain))) result.Add(chain);
            if (result.Count >= k) break;
        }

        return result;
    }

    // ---------------------------------------------------------------- evaluation

    private sealed class Export
    {
        public int Day;
        public int Workshop;
        public int Index;
        public uint Item;
        public int Start;
        public int End;
        public bool Efficient;
        public int Value;
    }

    /// <summary>Simulates the week: groove, supply from earlier exports, materials, favors.</summary>
    private (double total, double[] perDay, List<Export> exports, int groove, Dictionary<uint, int> used) Evaluate(
        List<int> cycles, List<uint>[][] chains, out double favorScore)
    {
        var perDay = new double[cycles.Count];
        var exported = new Dictionary<uint, int>();
        var groove = (int)s.Groove;
        var all = new List<Export>();
        var used = new Dictionary<uint, int>();
        var favorMade = new Dictionary<uint, int>();

        for (var d = 0; d < cycles.Count; d++)
        {
            var day = new List<Export>();
            for (var w = 0; w < chains[d].Length; w++)
            {
                var hour = 0;
                var chain = chains[d][w];
                for (var i = 0; i < chain.Count; i++)
                {
                    var c = byId[chain[i]];
                    day.Add(new Export { Day = d, Workshop = w, Index = i, Item = c.Id, Start = hour, End = hour + c.Hours, Efficient = i > 0 });
                    hour += c.Hours;
                    foreach (var m in c.Materials) used[m.PouchId] = used.GetValueOrDefault(m.PouchId) + m.Amount;
                }
            }

            // Groove rises as efficient crafts start; supply rises as items are exported.
            var grooveAt = new Dictionary<Export, int>();
            foreach (var e in day.OrderBy(e => e.Start).ThenBy(e => e.Workshop))
            {
                if (e.Efficient && groove < s.GrooveMax) groove++;
                grooveAt[e] = groove;
            }

            var sameDay = new Dictionary<uint, int>();
            foreach (var group in day.GroupBy(e => e.End).OrderBy(g => g.Key))
            {
                foreach (var e in group)
                {
                    var c = byId[e.Item];
                    var extra = exported.GetValueOrDefault(e.Item) + sameDay.GetValueOrDefault(e.Item);
                    var mult = forecasts.TryGetValue(e.Item, out var f) ? f.SupplyMultiplier(cycles[d], extra) : 1.0;
                    e.Value = Planner.ValueEach(c, workshopLevels[e.Workshop], grooveAt[e], mult);
                    perDay[d] += e.Value * (e.Efficient ? 2 : 1);
                }

                foreach (var e in group)
                {
                    var count = e.Efficient ? 2 : 1;
                    sameDay[e.Item] = sameDay.GetValueOrDefault(e.Item) + count;
                    if (favorNeed.ContainsKey(e.Item)) favorMade[e.Item] = favorMade.GetValueOrDefault(e.Item) + count;
                }
            }

            foreach (var (item, count) in sameDay) exported[item] = exported.GetValueOrDefault(item) + count;
            all.AddRange(day);
        }

        var total = perDay.Sum();

        // Materials: anything you can't gather must be on hand.
        foreach (var (id, need) in used)
        {
            var have = stock.GetValueOrDefault(id);
            if (need <= have) continue;
            if (options.AllowGathering && gatherable.Contains(id)) continue;
            total -= 100000 + (need - have) * 1000;
        }

        favorScore = 0;
        foreach (var (id, need) in favorNeed)
            favorScore += Math.Min(need, favorMade.GetValueOrDefault(id)) * FavorUnitBonus;
        total += favorScore;

        return (total, perDay, all, groove, used);
    }

    private void Fill(WeekPlan plan, List<int> cycles, List<uint>[][] chains)
    {
        var result = Evaluate(cycles, chains, out _);
        plan.GrooveAfter = result.groove;
        for (var d = 0; d < cycles.Count; d++)
        {
            var dp = new DayPlan { Cycle = cycles[d] };
            for (var w = 0; w < chains[d].Length; w++)
            {
                var wp = new WorkshopPlan { Workshop = w + 1, Level = workshopLevels[w] };
                foreach (var e in result.exports.Where(e => e.Day == d && e.Workshop == w).OrderBy(e => e.Index))
                    wp.Crafts.Add(new PlannedCraft { Craft = byId[e.Item], StartHour = e.Start, Efficient = e.Efficient, ValueEach = e.Value });
                if (wp.Crafts.Count > 0) dp.Workshops.Add(wp);
            }

            plan.Days.Add(dp);
        }

        var names = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Name);
        foreach (var (id, need) in result.used) plan.MaterialUse[id] = need;
        foreach (var (id, need) in result.used.OrderBy(u => names.GetValueOrDefault(u.Key)))
        {
            var have = stock.GetValueOrDefault(id);
            if (need > have)
                plan.Shortfalls.Add(new Shortfall { Name = names.GetValueOrDefault(id, $"#{id}"), Need = need, Have = have, Gatherable = gatherable.Contains(id) });
        }

        foreach (var (id, need) in favorNeed)
        {
            var made = result.exports.Where(e => e.Item == id).Sum(e => e.Efficient ? 2 : 1);
            plan.FavorItemsBoosted.Add($"{byId[id].Name} ({Math.Min(made, need)}/{need} planned)");
        }
    }
}

/// <summary>A week plan kept for the rest of the day (CJ: advice has to sit still).</summary>
[Serializable]
public class WeekLock
{
    public string Season { get; set; } = string.Empty;
    public int CycleDay { get; set; }
    public string Options { get; set; } = string.Empty;
    public List<int> Cycles { get; set; } = [];
    public List<List<List<uint>>> Chains { get; set; } = [];
    public string? RestAdvice { get; set; }
}
