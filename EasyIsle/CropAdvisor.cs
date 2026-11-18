using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

public class CropNeed
{
    public uint PouchId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint Icon { get; init; }
    public int Stock { get; init; }
    public int Waiting { get; init; }
    public double WorkshopPerWeek { get; init; }
    public double FeedPerWeek { get; set; }
    public double PerWeek => WorkshopPerWeek + FeedPerWeek;
    public int PlotsNow { get; init; }
    public int PlotsRecommended { get; set; }
    public int PlotsNeeded { get; set; }
    public int Seeds { get; init; }
    public uint SeedPouchId { get; init; }

    /// <summary>Weeks the stock (plus what the gardener holds) lasts at this pace; null when nothing uses it.</summary>
    public double? WeeksLeft => PerWeek > 0.01 ? (Stock + Waiting) / PerWeek : null;
}

public class PlotAdvice
{
    public int Slot { get; init; }
    public FarmPlot Plot { get; init; } = null!;
    public CropNeed? Recommended { get; set; }
    public bool Change => Recommended != null && Recommended.PouchId != Plot.CropPouchId;
}

public class CropPlan
{
    public List<CropNeed> Needs { get; } = [];
    public List<PlotAdvice> Plots { get; } = [];
    public List<string> Alerts { get; } = [];
    public FeedRecipe? Feed { get; set; }
    public int Animals { get; set; }
    public double FeedPerWeek { get; set; }

    /// <summary>Feed made from "any produce": how much produce of any kind it eats a week.</summary>
    public double AnyProducePerWeek { get; set; }

    /// <summary>Spare produce (beyond ~6 weeks of workshop use) available for "any produce" feed.</summary>
    public int SpareProduce { get; set; }

    /// <summary>How many crops the week plan's workshops use (for the "why" line).</summary>
    public int CropsUsedByPlan { get; set; }
}

/// <summary>
/// Splits the cropland between crops so none runs out. A plot gives 5 produce every 48 hours
/// (about 17.5 a week). Demand = what the week plan crafts with each crop + what the pasture's feed
/// needs (one feed per animal per day) + a small allowance so every crop keeps some stock for
/// future weeks. Crops that fall short of ~6 weeks of stock take plots from crops with the most to
/// spare; every other plot keeps
/// its current crop unless another crop is running short, so plots aren't replanted for nothing.
/// </summary>
public static class CropAdvisor
{
    private const double PerPlotWeek = 17.5;
    private const double Allowance = 7;          // per week, for crops no plan uses right now
    private const double TargetWeeks = 6;        // stock worth keeping on hand
    private const double CatchUpWeeks = 4;

    public static CropPlan Advise(IslandSnapshot s, WeekPlan? week, uint feedChoice)
    {
        var plan = new CropPlan();
        var seedsByCrop = s.Isleventory.Where(p => !p.IsProduce && p.CropRow != 0).GroupBy(p => p.CropRow).ToDictionary(g => g.Key, g => g.First());

        // A crop counts once you've grown it or have its seeds (a crop never harvested shows as
        // undiscovered, but its seeds can still be planted).
        var produce = s.Isleventory
            .Where(p => p.IsProduce && (p.Discovered || (seedsByCrop.TryGetValue(p.CropRow, out var sd) && sd.Count > 0)))
            .ToList();
        if (produce.Count == 0 || s.FarmPlots.Count == 0) return plan;

        // Workshop demand from the week plan, scaled to a full 5-cycle week.
        var workshop = new Dictionary<uint, double>();
        if (week != null && week.Days.Count > 0)
            foreach (var (id, amount) in week.MaterialUse)
                workshop[id] = amount * 5.0 / week.Days.Count;

        plan.CropsUsedByPlan = workshop.Keys.Count(id => s.Isleventory.Any(p => p.PouchId == id && p.IsProduce));

        // Feed: the chosen recipe (or the last one in the game's order, the premium feed).
        plan.Animals = s.Animals.Count;
        plan.Feed = s.FeedRecipes.FirstOrDefault(r => r.OutputPouchId == feedChoice)
                    ?? s.FeedRecipes.OrderByDescending(r => r.Order).FirstOrDefault();
        var feed = new Dictionary<uint, double>();
        if (plan.Feed != null && plan.Animals > 0)
        {
            plan.FeedPerWeek = plan.Animals * 7;
            var feedStock = s.Isleventory.FirstOrDefault(p => p.PouchId == plan.Feed.OutputPouchId)?.Count ?? 0;
            var craftsPerWeek = plan.FeedPerWeek / plan.Feed.Yield;
            foreach (var m in plan.Feed.Materials)
            {
                if (m.PouchId == 0) plan.AnyProducePerWeek += m.Amount * craftsPerWeek;
                else feed[m.PouchId] = feed.GetValueOrDefault(m.PouchId) + m.Amount * craftsPerWeek;
            }

            if (feedStock < plan.FeedPerWeek)
                plan.Alerts.Add($"{plan.Feed.OutputName}: {feedStock} left, the pasture uses about {plan.FeedPerWeek:N0} a week. Make more soon.");
        }

        foreach (var p in produce)
        {
            seedsByCrop.TryGetValue(p.CropRow, out var seed);
            plan.Needs.Add(new CropNeed
            {
                PouchId = p.PouchId,
                Name = p.Name,
                Icon = p.Icon,
                Stock = p.Count,
                Waiting = s.FarmPlots.Where(f => f.CropPouchId == p.PouchId).Sum(f => f.Yield),
                WorkshopPerWeek = workshop.GetValueOrDefault(p.PouchId),
                FeedPerWeek = feed.GetValueOrDefault(p.PouchId),
                PlotsNow = s.FarmPlots.Count(f => f.CropPouchId == p.PouchId),
                Seeds = seed?.Count ?? 0,
                SeedPouchId = seed?.PouchId ?? 0,
            });
        }

        // "Any produce" feed: eat into the crops with the most to spare. Only if spare produce runs
        // short does it add demand, spread over the crops with the most seeds.
        plan.SpareProduce = (int)plan.Needs.Sum(n => Math.Max(0, n.Stock + n.Waiting - TargetWeeks * n.PerWeek));
        if (plan.AnyProducePerWeek > 0 && plan.SpareProduce < TargetWeeks * plan.AnyProducePerWeek)
        {
            var growers = plan.Needs.Where(n => n.Seeds > 0).OrderByDescending(n => n.Seeds).Take(4).ToList();
            foreach (var g in growers) g.FeedPerWeek += plan.AnyProducePerWeek / growers.Count;
        }

        // How many plots each crop needs: enough to keep up with its use, plus catching up to
        // TargetWeeks of stock over CatchUpWeeks.
        foreach (var n in plan.Needs)
        {
            var steady = n.PerWeek / PerPlotWeek;
            var catchUp = (TargetWeeks * n.PerWeek - n.Stock - n.Waiting) / (PerPlotWeek * CatchUpWeeks);
            // Plenty on hand (twice the target): no plots needed for now.
            var plenty = n.Stock + n.Waiting >= 2 * TargetWeeks * n.PerWeek;
            n.PlotsNeeded = n.PerWeek < 0.5 || plenty ? 0 : (int)Math.Ceiling(Math.Max(steady, catchUp) - 0.05);
        }

        // Start from what's planted, so healthy plots are left alone.
        foreach (var plot in s.FarmPlots)
        {
            var a = new PlotAdvice { Slot = plot.Slot, Plot = plot, Recommended = plan.Needs.FirstOrDefault(n => n.PouchId == plot.CropPouchId) };
            plan.Plots.Add(a);
            if (a.Recommended != null) a.Recommended.PlotsRecommended++;
        }

        double Cover(CropNeed n) => (n.Stock + n.Waiting + n.PlotsRecommended * PerPlotWeek * CatchUpWeeks) / (n.PerWeek + Allowance);

        // Short crops take empty plots first, then plots from the crop with the most to spare.
        foreach (var shortCrop in plan.Needs.Where(n => n.PlotsNeeded > n.PlotsRecommended).OrderBy(n => (n.Stock + n.Waiting) / Math.Max(n.PerWeek, 0.1)).ToList())
        {
            while (shortCrop.PlotsRecommended < shortCrop.PlotsNeeded)
            {
                var donor = plan.Plots.FirstOrDefault(a => a.Recommended == null)
                            ?? plan.Plots
                                .Where(a => a.Recommended != null && a.Recommended != shortCrop && a.Recommended.PlotsRecommended > a.Recommended.PlotsNeeded)
                                .OrderByDescending(a => Cover(a.Recommended!))
                                .FirstOrDefault();
                if (donor == null) break;
                if (donor.Recommended != null) donor.Recommended.PlotsRecommended--;
                donor.Recommended = shortCrop;
                shortCrop.PlotsRecommended++;
            }
        }

        // Any plot still empty grows whatever is lowest on stock (and has seeds).
        foreach (var a in plan.Plots.Where(a => a.Recommended == null))
        {
            var pick = plan.Needs.Where(n => n.Seeds > 0).OrderBy(Cover).FirstOrDefault();
            if (pick == null) break;
            a.Recommended = pick;
            pick.PlotsRecommended++;
        }

        // Things to do now.
        var waiting = s.FarmPlots.Sum(f => f.Yield);
        var full = s.FarmPlots.Count(f => f.Yield >= 50);
        if (full > 0)
            plan.Alerts.Add($"The gardener's storage is full on {full} plot(s) and care has stopped there. Talk to the gardener and Collect All ({waiting:N0} produce waiting).");
        else if (waiting >= 200)
            plan.Alerts.Add($"{waiting:N0} produce is waiting with the gardener. Collect it so EasyIsle (and your workshops) can count it.");
        foreach (var n in plan.Needs.Where(n => n.PlotsRecommended > 0 && n.Seeds < n.PlotsRecommended * 7))
        {
            var buy = n.PlotsRecommended * 7 - n.Seeds;
            plan.Alerts.Add(n.Seeds == 0
                ? $"Out of {n.Name} seeds: buy about {buy} from the Produce Producer (2 cowries each) before planting."
                : $"{n.Name} seeds are low ({n.Seeds}): buy about {buy} more for two weeks of planting.");
        }

        return plan;
    }
}
