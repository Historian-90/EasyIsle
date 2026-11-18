using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

public class GranaryAdvice
{
    public int Granary { get; init; }
    public Destination Destination { get; init; } = null!;
    public string Action { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public class RareStock
{
    public string Name { get; init; } = string.Empty;
    public int Have { get; init; }
    public int WeeklyUse { get; init; }

    /// <summary>Weeks the stock lasts at this week's pace; null when the plan doesn't use it.</summary>
    public double? WeeksLeft => WeeklyUse > 0 ? (double)Have / WeeklyUse : null;
}

/// <summary>
/// Picks granary expeditions from what the week plan uses and what's running low. Rare materials
/// (only from expeditions) count most; common ones can be gathered, so they count a little.
/// Each granary returns its area's rare material every day (2 a day at Granary I up to 6 at Granary V) and up to 24 of
/// each common material; expeditions cost 50 cowries a day for up to 7 days.
/// </summary>
public static class GranaryAdvisor
{
    private const int Days = 7;
    private const int CommonPerDay = 12;   // average of 0-24
    private const double CommonWeight = 0.25;
    private const double Baseline = 6;     // weekly "keep some around" demand for unused materials

    public static (List<GranaryAdvice> advice, List<RareStock> rares) Advise(IslandSnapshot s, WeekPlan? plan)
    {
        var advice = new List<GranaryAdvice>();
        var stock = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Count);
        var discovered = s.Isleventory.Where(p => p.Discovered).Select(p => p.PouchId).ToHashSet();
        var names = s.Isleventory.ToDictionary(p => p.PouchId, p => p.Name);

        // Scale the plan's use to a full 5-cycle week.
        var weekly = new Dictionary<uint, double>();
        if (plan != null && plan.Days.Count > 0)
            foreach (var (id, amount) in plan.MaterialUse)
                weekly[id] = amount * 5.0 / plan.Days.Count;

        var rares = s.Destinations
            .Select(d => new RareStock { Name = d.RareName, Have = stock.GetValueOrDefault(d.RarePouchId), WeeklyUse = (int)Math.Round(weekly.GetValueOrDefault(d.RarePouchId)) })
            .ToList();

        var areas = s.Destinations.Where(d => d.Unlocked).ToList();
        if (areas.Count == 0) return (advice, rares);

        var projected = new Dictionary<uint, double>();
        foreach (var (id, count) in stock) projected[id] = count;

        double Urgency(uint id) => (weekly.GetValueOrDefault(id) + Baseline) / (projected.GetValueOrDefault(id) + 1);

        var rarePerDay = 6;
        double Score(Destination d)
        {
            var score = rarePerDay * Days * Urgency(d.RarePouchId);
            foreach (var c in d.CommonPouchIds.Where(discovered.Contains))
                score += CommonWeight * CommonPerDay * Days * Urgency(c);
            return score;
        }

        var granaries = s.Expeditions.Count > 0 ? s.Expeditions : [new Expedition { Granary = 1 }, new Expedition { Granary = 2 }];
        foreach (var g in granaries)
        {
            // Rare yield per day: Granary I-V = 2-6 (level is 0-based).
            var building = g.Granary - 1 < s.GranaryBuildings.Count ? s.GranaryBuildings[g.Granary - 1] : null;
            rarePerDay = building is { Placed: true } ? building.Level + 2 : 6;
            var best = areas.OrderByDescending(Score).First();
            var current = areas.FirstOrDefault(d => d.Id == g.ExpeditionId);

            string action;
            if (g.ExpeditionId == 0)
            {
                action = $"Send to {best.Name} for {Days} days";
            }
            else if (current != null && current.Id == best.Id)
            {
                action = g.DaysRemaining > 1
                    ? $"Keep it at {best.Name} ({g.DaysRemaining} days left)"
                    : $"Extend {best.Name} for another {Days} days when it ends";
            }
            else if (current != null && Score(current) >= Score(best) * 0.8)
            {
                // Close enough: no need to switch.
                best = current;
                action = $"Keep it at {current.Name} ({g.DaysRemaining} days left)";
            }
            else
            {
                action = $"Switch to {best.Name} for {Days} days";
            }

            advice.Add(new GranaryAdvice { Granary = g.Granary, Destination = best, Action = action, Reason = Reason(best) });

            // The next granary sees this one's haul.
            projected[best.RarePouchId] = projected.GetValueOrDefault(best.RarePouchId) + rarePerDay * Days;
            foreach (var c in best.CommonPouchIds) projected[c] = projected.GetValueOrDefault(c) + CommonPerDay * Days;
        }

        return (advice, rares);

        string Reason(Destination d)
        {
            var have = stock.GetValueOrDefault(d.RarePouchId);
            var use = weekly.GetValueOrDefault(d.RarePouchId);
            var text = use >= 1
                ? $"{d.RareName}: {have} on hand, this week's plan uses about {use:N0} a week"
                : $"{d.RareName}: {have} on hand, the scarcest for its area";
            var lowCommons = d.CommonPouchIds
                .Where(c => discovered.Contains(c) && weekly.GetValueOrDefault(c) > stock.GetValueOrDefault(c) * 0.5)
                .Select(c => names.GetValueOrDefault(c, $"#{c}"))
                .Take(3)
                .ToList();
            if (lowCommons.Count > 0) text += "; also brings " + string.Join(", ", lowCommons);
            return text + ".";
        }
    }
}
