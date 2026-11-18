using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

public class LeavingNeed
{
    public uint PouchId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Stock { get; init; }
    public double WorkshopPerWeek { get; init; }

    /// <summary>What the pasture gives a week now, and with the advice followed.</summary>
    public double SupplyNow { get; set; }
    public double SupplyAfter { get; set; }

    public bool AtCap => Stock >= AnimalAdvisor.StackCap - 9;
    public double? WeeksLeft => WorkshopPerWeek > 0.5 ? Stock / WorkshopPerWeek : null;
}

public class AnimalMove
{
    public AnimalKind Kind { get; init; } = null!;
    public int Count { get; set; }

    /// <summary>For releases: the animals to let go (by pasture slot).</summary>
    public List<Animal> Animals { get; } = [];

    /// <summary>Animals with these leavings, easiest to find first.</summary>
    public List<SpeciesSpot> Spots { get; init; } = [];

    public string Why { get; set; } = string.Empty;
}

public class PastureAdvice
{
    public int Capacity { get; set; }
    public int Owned { get; set; }
    public List<LeavingNeed> Leavings { get; } = [];
    public List<AnimalMove> Capture { get; } = [];
    public List<AnimalMove> Release { get; } = [];
    public bool Kept { get; set; }

    /// <summary>The target pasture (kind row -> count), stored so the advice stays put.</summary>
    public Dictionary<string, int> Target { get; set; } = [];
}

/// <summary>
/// Which animals to keep for the leavings the workshops use. Each animal leaves its common leaving
/// every day and a bonus leaving with a chance set by its mood (80% when Gleeful on Premium Island
/// Greenfeed), so about 7 + 5.6 a week. Leavings the workshops run short of pull animals in; animals
/// whose leavings pile up (or sit at the 999 cap) are the first to go when the pasture is full.
/// </summary>
public static class AnimalAdvisor
{
    public const int StackCap = 999;
    private const double CommonPerWeek = 7;
    private const double BonusPerWeek = 5.6;
    private const double TargetWeeks = 6;
    private const double CatchUpWeeks = 4;

    public static string Key(uint common, uint bonus) => $"{common}-{bonus}";

    public static PastureAdvice Advise(IslandSnapshot s, WeekPlan? week, Dictionary<string, int> saved)
    {
        var advice = new PastureAdvice { Capacity = Math.Max(s.PastureCapacity, s.Animals.Count), Owned = s.Animals.Count };
        if (s.AnimalKinds.Count == 0) return advice;

        var stock = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Count);
        var names = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Name);
        var workshop = new Dictionary<uint, double>();
        if (week != null && week.Days.Count > 0)
            foreach (var (id, amount) in week.MaterialUse)
                workshop[id] = amount * 5.0 / week.Days.Count;

        // One kind per pair of leavings (several animals can share the same pair).
        var kinds = s.AnimalKinds
            .GroupBy(k => Key(k.CommonPouchId, k.BonusPouchId))
            .ToDictionary(g => g.Key, g => g.OrderBy(k => k.Row).First());
        var leavingIds = kinds.Values.SelectMany(k => new[] { k.CommonPouchId, k.BonusPouchId }).Distinct().ToList();

        // Weekly amount each leaving should come in at.
        var wanted = new Dictionary<uint, double>();
        foreach (var id in leavingIds)
        {
            var use = workshop.GetValueOrDefault(id);
            var have = stock.GetValueOrDefault(id);
            wanted[id] = use < 0.5 || have >= 2 * TargetWeeks * use ? 0 : use + Math.Max(0, TargetWeeks * use - have) / CatchUpWeeks;
        }

        var owned = s.Animals.Where(a => a.CommonPouchId != 0)
            .GroupBy(a => Key(a.CommonPouchId, a.BonusPouchId))
            .ToDictionary(g => g.Key, g => g.Count());

        Dictionary<string, int> target;
        var stillToDo = saved.Count > 0 && !SameCounts(saved, owned);
        if (stillToDo)
        {
            target = new Dictionary<string, int>(saved);
            advice.Kept = true;
        }
        else
        {
            target = Choose(kinds, owned, wanted, stock, advice.Capacity);
        }

        advice.Target = target;

        double Supply(Dictionary<string, int> herd, uint id) => herd.Sum(h =>
            kinds.TryGetValue(h.Key, out var k) ? h.Value * ((k.CommonPouchId == id ? CommonPerWeek : 0) + (k.BonusPouchId == id ? BonusPerWeek : 0)) : 0);

        foreach (var id in leavingIds.OrderBy(i => names.GetValueOrDefault(i)))
        {
            advice.Leavings.Add(new LeavingNeed
            {
                PouchId = id,
                Name = names.GetValueOrDefault(id, $"#{id}"),
                Stock = stock.GetValueOrDefault(id),
                WorkshopPerWeek = workshop.GetValueOrDefault(id),
                SupplyNow = Supply(owned, id),
                SupplyAfter = Supply(target, id),
            });
        }

        // Moves: what to catch and what to let go.
        foreach (var (key, count) in target)
        {
            var extra = count - owned.GetValueOrDefault(key);
            if (extra <= 0 || !kinds.TryGetValue(key, out var k)) continue;
            advice.Capture.Add(new AnimalMove
            {
                Kind = k,
                Count = extra,
                Spots = Spots(k, names),
                Why = Why(k, wanted, stock, names),
            });
        }

        foreach (var (key, count) in owned)
        {
            var spare = count - target.GetValueOrDefault(key);
            if (spare <= 0 || !kinds.TryGetValue(key, out var k)) continue;
            var move = new AnimalMove { Kind = k, Count = spare, Spots = Spots(k, names) };
            move.Animals.AddRange(s.Animals.Where(a => Key(a.CommonPouchId, a.BonusPouchId) == key).OrderBy(a => a.Mood).Take(spare));
            var c = names.GetValueOrDefault(k.CommonPouchId);
            var b = names.GetValueOrDefault(k.BonusPouchId);
            move.Why = stock.GetValueOrDefault(k.CommonPouchId) >= StackCap - 9
                ? $"{c} is at the {StackCap} cap, so its leavings go to waste"
                : $"its leavings ({c}, {b}) are needed least";
            advice.Release.Add(move);
        }

        return advice;
    }

    private static bool SameCounts(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Where(x => x.Value > 0).All(x => b.GetValueOrDefault(x.Key) == x.Value)
        && b.Where(x => x.Value > 0).All(x => a.GetValueOrDefault(x.Key) == x.Value);

    /// <summary>Greedy: fill free spots with the most useful kind, then swap while it clearly helps.</summary>
    private static Dictionary<string, int> Choose(Dictionary<string, AnimalKind> kinds, Dictionary<string, int> owned,
                                                  Dictionary<uint, double> wanted, Dictionary<uint, int> stock, int capacity)
    {
        var herd = new Dictionary<string, int>(owned);

        double Give(AnimalKind k, uint id) => (k.CommonPouchId == id ? CommonPerWeek : 0) + (k.BonusPouchId == id ? BonusPerWeek : 0);
        double SupplyOf(uint id) => herd.Sum(h => kinds.TryGetValue(h.Key, out var k) ? h.Value * Give(k, id) : 0);
        double Short(uint id) => Math.Max(0, wanted.GetValueOrDefault(id) - SupplyOf(id));

        // How much of what this kind gives is actually wanted right now.
        double Gain(AnimalKind k) => Math.Min(Short(k.CommonPouchId), Give(k, k.CommonPouchId))
                                      + (k.BonusPouchId != k.CommonPouchId ? Math.Min(Short(k.BonusPouchId), Give(k, k.BonusPouchId)) : 0);

        // How much would go missing without one of these.
        double Loss(AnimalKind k)
        {
            double Part(uint id)
            {
                var without = SupplyOf(id) - Give(k, id);
                return Math.Min(Give(k, id), Math.Max(0, wanted.GetValueOrDefault(id) - without));
            }

            return Part(k.CommonPouchId) + (k.BonusPouchId != k.CommonPouchId ? Part(k.BonusPouchId) : 0);
        }

        for (var step = 0; step < 40; step++)
        {
            var best = kinds.Values.OrderByDescending(Gain).First();
            var gain = Gain(best);
            if (gain < 2) break;

            if (herd.Values.Sum() < capacity)
            {
                herd[Key(best.CommonPouchId, best.BonusPouchId)] = herd.GetValueOrDefault(Key(best.CommonPouchId, best.BonusPouchId)) + 1;
                continue;
            }

            // Pasture full: let go of the least useful animal if the newcomer is clearly better.
            var candidates = herd.Where(h => h.Value > 0 && kinds.ContainsKey(h.Key)).Select(h => kinds[h.Key]).ToList();
            if (candidates.Count == 0) break;
            var worst = candidates
                .OrderBy(Loss)
                .ThenByDescending(k => stock.GetValueOrDefault(k.CommonPouchId))
                .First();
            if (gain - Loss(worst) < 3) break;
            herd[Key(worst.CommonPouchId, worst.BonusPouchId)]--;
            herd[Key(best.CommonPouchId, best.BonusPouchId)] = herd.GetValueOrDefault(Key(best.CommonPouchId, best.BonusPouchId)) + 1;
        }

        // Room left over: fill it with whatever keeps the thinnest stocks topped up (not leavings at the cap).
        while (herd.Values.Sum() < capacity)
        {
            double Thin(uint id) => stock.GetValueOrDefault(id) >= StackCap - 9 ? 0 : 1.0 / (1 + stock.GetValueOrDefault(id) / 50.0);
            var pick = kinds.Values
                .Where(k => stock.GetValueOrDefault(k.CommonPouchId) < StackCap - 9)
                .OrderByDescending(k => Thin(k.CommonPouchId) + Thin(k.BonusPouchId))
                .FirstOrDefault();
            if (pick == null) break;
            herd[Key(pick.CommonPouchId, pick.BonusPouchId)] = herd.GetValueOrDefault(Key(pick.CommonPouchId, pick.BonusPouchId)) + 1;
        }

        return herd.Where(h => h.Value > 0).ToDictionary(h => h.Key, h => h.Value);
    }

    private static List<SpeciesSpot> Spots(AnimalKind k, Dictionary<uint, string> names) =>
        SpeciesTable.Match(names.GetValueOrDefault(k.CommonPouchId, string.Empty), names.GetValueOrDefault(k.BonusPouchId, string.Empty), k.Size)
            .OrderBy(x => x.Rare).ThenBy(x => x.Weather.Length > 0 && x.Time.Length > 0).ToList();

    private static string Why(AnimalKind k, Dictionary<uint, double> wanted, Dictionary<uint, int> stock, Dictionary<uint, string> names)
    {
        var parts = new[] { k.CommonPouchId, k.BonusPouchId }.Distinct()
            .Where(id => wanted.GetValueOrDefault(id) > 0)
            .Select(id => $"{names.GetValueOrDefault(id)} ({stock.GetValueOrDefault(id)} on hand)")
            .ToList();
        if (parts.Count > 0) return "for " + string.Join(" and ", parts);
        var low = new[] { k.CommonPouchId, k.BonusPouchId }.Distinct().OrderBy(id => stock.GetValueOrDefault(id)).First();
        return $"spare room: tops up {names.GetValueOrDefault(low)} ({stock.GetValueOrDefault(low)} on hand)";
    }

    public static string Restraint(char size) => size switch { 'S' => "Makeshift Net", 'M' => "Makeshift Restraint", _ => "Makeshift Soporific" };
}
