using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace EasyIsle;

/// <summary>Reads the island's state from game memory and the game's data sheets.</summary>
public sealed unsafe class IslandReader
{
    private readonly Configuration config;

    public IslandReader(Configuration config)
    {
        this.config = config;
    }

    public IslandSnapshot Read()
    {
        var snap = new IslandSnapshot();
        var mgr = MJIManager.Instance();
        if (mgr == null)
        {
            snap.Problems.Add("Island data isn't loaded (MJIManager missing).");
            return snap;
        }

        snap.ManagerFound = true;
        snap.OnIsland = mgr->IsPlayerInSanctuary;
        var island = &mgr->IslandState;
        snap.Rank = island->CurrentRank;
        snap.Xp = island->CurrentXP;
        snap.Groove = mgr->CurrentGroove;
        for (var i = 0; i < island->LandmarkIds.Length; i++)
            if (island->LandmarkIds[i] != 0 && island->LandmarkUnderConstruction[i] == 0) snap.LandmarksBuilt++;
        snap.GrooveMax = snap.LandmarksBuilt switch { 0 => 10, 1 => 15, 2 => 20, 3 => 25, 4 => 35, _ => 45 };
        snap.CycleDay = mgr->CurrentCycleDay;
        foreach (var d in mgr->CraftworksRestDays) snap.RestDays.Add(d);
        snap.DemandValid = !mgr->DemandDirty;
        snap.PopularityRow = mgr->CurrentPopularity;
        snap.NextPopularityRow = mgr->NextPopularity;
        snap.CabinLevel = island->CabinLevel;

        for (var i = 0; i < 4; i++)
        {
            snap.Workshops.Add(new Building
            {
                Placed = island->Workshops.PlaceId[i] != 0,
                Level = island->Workshops.BuildingLevel[i],
                UnderConstruction = island->Workshops.UnderConstruction[i] != 0,
                HoursToCompletion = island->Workshops.HoursToCompletion[i],
            });
        }

        for (var i = 0; i < 2; i++)
        {
            snap.GranaryBuildingsRaw.Add(new Building
            {
                Placed = island->Granaries.PlaceId[i] != 0,
                Level = island->Granaries.BuildingLevel[i],
                UnderConstruction = island->Granaries.UnderConstruction[i] != 0,
                HoursToCompletion = island->Granaries.HoursToCompletion[i],
            });
        }

        snap.RawIslandState = Convert.ToHexString(new ReadOnlySpan<byte>(island, IslandState.StructSize));
        snap.Farm = FromFarmPasture(island->Farm);
        snap.Pasture = FromFarmPasture(island->Pasture);

        ReadCrafts(mgr, snap);
        ReadIsleventory(mgr, snap);
        ReadExpeditions(mgr, snap);
        ReadFarm(mgr, snap);
        ReadPasture(mgr, snap);
        ReadFavors(mgr, snap);
        ReadSchedule(snap);
        RecordSupply(mgr, snap);

        if (!snap.DemandValid)
            snap.Problems.Add("Supply & Demand isn't loaded yet: open the Isleworks agenda once.");
        return snap;
    }

    /// <summary>Weekly reset: Tuesday 08:00 UTC.</summary>
    public static DateTime SeasonStartUtc(DateTime nowUtc)
    {
        var daysSince = ((int)nowUtc.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        var start = nowUtc.Date.AddDays(-daysSince).AddHours(8);
        return start > nowUtc ? start.AddDays(-7) : start;
    }

    /// <summary>Keeps the first Supply &amp; Demand reading of each cycle, so peaks can be worked out later.</summary>
    private void RecordSupply(MJIManager* mgr, IslandSnapshot snap)
    {
        if (snap.DemandValid)
        {
            var season = SeasonStartUtc(DateTime.UtcNow).ToString("yyyy-MM-dd");
            if (!config.SupplyHistory.TryGetValue(season, out var cycles))
                config.SupplyHistory[season] = cycles = [];
            if (!cycles.ContainsKey(snap.CycleDay))
            {
                var shifts = mgr->SupplyAndDemandShifts;
                var day = new Dictionary<uint, int>();
                foreach (var c in snap.Crafts)
                    if (c.Id < shifts.Length) day[c.Id] = shifts[(int)c.Id];
                cycles[snap.CycleDay] = day;
                foreach (var old in config.SupplyHistory.Keys.OrderByDescending(k => k).Skip(4).ToList())
                    config.SupplyHistory.Remove(old);
                config.Save();
            }
        }

        snap.SupplyHistory = config.SupplyHistory;
    }

    private static Building FromFarmPasture(MJIFarmPasture fp) => new()
    {
        Placed = fp.Level > 0 || fp.UnderConstruction,
        Level = fp.Level,
        UnderConstruction = fp.UnderConstruction,
        HoursToCompletion = fp.HoursToCompletion,
    };

    private static string Name(Item? item) => item?.Name.ExtractText() ?? string.Empty;

    private static string PouchName(uint pouchRow)
    {
        var row = Plugin.DataManager.GetExcelSheet<MJIItemPouch>().GetRowOrDefault(pouchRow);
        return row == null ? $"#{pouchRow}" : Name(row.Value.Item.ValueNullable);
    }

    private void ReadCrafts(MJIManager* mgr, IslandSnapshot snap)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<MJICraftworksObject>();
        var popSheet = Plugin.DataManager.GetExcelSheet<MJICraftworksPopularity>();
        var popNow = popSheet.GetRowOrDefault(mgr->CurrentPopularity);
        var popNext = popSheet.GetRowOrDefault(mgr->NextPopularity);
        var shifts = mgr->SupplyAndDemandShifts;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Item.RowId == 0) continue;
            var craft = new Craft
            {
                Id = row.RowId,
                Name = Name(row.Item.ValueNullable),
                RankRequired = row.LevelReq,
                Available = row.LevelReq <= snap.Rank,
                Hours = row.CraftingTime,
                Value = row.Value,
            };
            foreach (var theme in row.Theme)
                if (theme.RowId != 0 && theme.ValueNullable is { } t) craft.Themes.Add(t.Name.ExtractText());
            for (var i = 0; i < row.Material.Count; i++)
            {
                var mat = row.Material[i];
                var amount = i < row.Amount.Count ? row.Amount[i] : 0;
                if (mat.RowId == 0 || amount == 0) continue;
                craft.Materials.Add(new Ingredient { PouchId = mat.RowId, Name = PouchName(mat.RowId), Amount = amount });
            }

            if (snap.DemandValid && row.RowId < shifts.Length)
            {
                var b = shifts[(int)row.RowId];
                craft.Supply = ((CraftworkSupply)(b >> 4)).ToString();
                craft.DemandShift = ((CraftworkDemandShift)(b & 0xF)).ToString();
                craft.SupplyLevel = b >> 4;
            }

            craft.Popularity = Popularity(popNow, row.RowId);
            craft.NextPopularity = Popularity(popNext, row.RowId);
            craft.PopularityPercent = PopularityRatio(popNow, row.RowId);
            craft.NextPopularityPercent = PopularityRatio(popNext, row.RowId);
            snap.Crafts.Add(craft);
        }
    }

    private static int PopularityRatio(MJICraftworksPopularity? pop, uint craftId)
    {
        if (pop == null || craftId >= pop.Value.Popularity.Count) return 0;
        return pop.Value.Popularity[(int)craftId].ValueNullable?.Ratio ?? 0;
    }

    private static string Popularity(MJICraftworksPopularity? pop, uint craftId)
    {
        if (pop == null || craftId >= pop.Value.Popularity.Count) return string.Empty;
        var type = pop.Value.Popularity[(int)craftId];
        var label = type.RowId switch { 1 => "Very high", 2 => "High", 3 => "Average", 4 => "Low", _ => $"#{type.RowId}" };
        var ratio = type.ValueNullable?.Ratio ?? 0;
        return ratio > 0 ? $"{label} ({ratio}%)" : label;
    }

    private void ReadIsleventory(MJIManager* mgr, IslandSnapshot snap)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<MJIItemPouch>();
        Dictionary<uint, int>? live = null;

        var agent = AgentMJIPouch.Instance();
        if (agent != null && agent->InventoryData != null)
        {
            live = [];
            foreach (var item in agent->InventoryData->Inventory.AsSpan())
                live[item.RowId] = item.StackSize;
            if (live.Count == 0) live = null;
        }

        if (live != null)
        {
            var changed = live.Count != config.LastIsleventory.Count
                          || live.Any(kv => !config.LastIsleventory.TryGetValue(kv.Key, out var old) || old != kv.Value);
            config.LastIsleventoryUtc = DateTime.UtcNow;
            if (changed)
            {
                config.LastIsleventory = live;
                config.Save();
            }
            snap.IsleventorySource = "live";
        }
        else if (config.LastIsleventory.Count > 0)
        {
            snap.IsleventorySource = $"cached from {config.LastIsleventoryUtc.ToLocalTime():g} (open your Isleventory to refresh)";
        }
        else
        {
            snap.IsleventorySource = "unavailable: open your Isleventory once";
            snap.Problems.Add("Isleventory counts aren't available yet: open your Isleventory once.");
        }

        var counts = live ?? config.LastIsleventory;
        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Item.RowId == 0) continue;
            snap.Isleventory.Add(new PouchItem
            {
                PouchId = row.RowId,
                ItemId = row.Item.RowId,
                Name = Name(row.Item.ValueNullable),
                Category = row.Category.ValueNullable?.Singular.ExtractText() ?? string.Empty,
                Count = counts.TryGetValue(row.RowId, out var c) ? c : 0,
                Discovered = !mgr->IsPouchItemLocked((ushort)row.RowId),
            });
        }
    }

    private void ReadExpeditions(MJIManager* mgr, IslandSnapshot snap)
    {
        var state = mgr->GranariesState;
        if (state == null)
        {
            snap.Problems.Add("Granary data isn't loaded (usually only on the island).");
            return;
        }

        var areas = Plugin.DataManager.GetExcelSheet<MJIStockyardManagementArea>();
        for (var i = 0; i < MJIGranariesState.MaxGranaries; i++)
        {
            var g = state->Granary[i];
            var exp = new Expedition
            {
                Granary = i + 1,
                ExpeditionId = g.ActiveExpeditionId,
                DaysRemaining = g.RemainingDays,
                RareMaterial = g.RareResourcePouchId == 0 ? string.Empty : PouchName(g.RareResourcePouchId),
                RareCount = g.RareResourceCount,
                FinishesUtc = g.FinishTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(g.FinishTime).UtcDateTime : null,
            };
            var waiting = g.RareResourceCount;
            foreach (var n in g.NormalResourceCounts) waiting += n;
            exp.WaitingToCollect = waiting;
            if (areas.GetRowOrDefault(g.ActiveExpeditionId) is { } area)
                exp.Destination = area.Area.ValueNullable?.Text.ExtractText() ?? string.Empty;
            snap.Expeditions.Add(exp);
        }
    }

    private void ReadFarm(MJIManager* mgr, IslandSnapshot snap)
    {
        var farm = mgr->FarmState;
        if (farm == null)
        {
            snap.Problems.Add("Cropland data isn't loaded (usually only on the island).");
            return;
        }

        var pouch = Plugin.DataManager.GetExcelSheet<MJIItemPouch>();
        var slots = Math.Min((int)mgr->GetFarmSlotCount(), farm->SeedType.Length);
        for (var i = 0; i < slots; i++)
        {
            var seed = farm->SeedType[i];
            var crop = string.Empty;
            if (seed != 0)
            {
                var produce = pouch.FirstOrDefault(p => p.Crop.RowId == seed && p.Item.RowId != 0);
                crop = produce.RowId != 0 ? Name(produce.Item.ValueNullable) : $"seed #{seed}";
            }

            snap.FarmPlots.Add(new FarmPlot
            {
                Slot = i + 1,
                SeedRow = seed,
                Crop = crop,
                Growth = farm->GrowthLevel[i],
                Water = farm->WaterLevel[i],
                Yield = farm->GardenerYield[i],
                UnderCare = farm->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare),
            });
        }
    }

    private void ReadPasture(MJIManager* mgr, IslandSnapshot snap)
    {
        var pasture = mgr->PastureHandler;
        if (pasture == null)
        {
            snap.Problems.Add("Pasture data isn't loaded (usually only on the island).");
            return;
        }

        var animals = Plugin.DataManager.GetExcelSheet<MJIAnimals>();
        var names = Plugin.DataManager.GetExcelSheet<BNpcName>();
        foreach (var a in pasture->MJIAnimals)
        {
            if (a.AnimalType == 0 && a.BNPCNameId == 0) continue;
            var animal = new Animal
            {
                Slot = a.SlotId,
                Species = names.GetRowOrDefault(a.BNPCNameId)?.Singular.ExtractText() ?? $"#{a.BNPCNameId}",
                Nickname = a.NicknameString,
                Food = a.FoodLevel,
                Mood = a.Mood,
                UnderCare = a.UnderCare,
            };
            if (animals.GetRowOrDefault(a.AnimalType) is { } kind)
                foreach (var r in kind.Reward)
                    if (r.RowId != 0) animal.Leavings.Add(Name(r.ValueNullable));
            snap.Animals.Add(animal);
        }
    }

    private void ReadFavors(MJIManager* mgr, IslandSnapshot snap)
    {
        var favors = mgr->FavorState;
        if (favors == null)
        {
            snap.Problems.Add("Felicitous Favors aren't loaded: open the Felicitous Favors window once.");
            return;
        }

        var crafts = Plugin.DataManager.GetExcelSheet<MJICraftworksObject>();
        for (var i = 0; i < favors->CraftObjectIds.Length; i++)
        {
            var id = favors->CraftObjectIds[i];
            if (id == 0) continue;
            var hours = crafts.GetRowOrDefault(id)?.CraftingTime ?? 0;
            snap.Favors.Add(new Favor
            {
                Hours = hours,
                Required = hours == 6 ? 6 : 8,
                Index = i,
                CraftId = id,
                Name = crafts.GetRowOrDefault(id) is { } c ? Name(c.Item.ValueNullable) : $"#{id}",
                Delivered = favors->NumDelivered[i],
                Scheduled = favors->NumScheduled[i],
                Shipped = favors->Shipped(i),
                Bonus = favors->Bonus(i),
            });
        }
    }

    private void ReadSchedule(IslandSnapshot snap)
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null || !agent->IsAgentActive())
        {
            snap.ScheduleSource = "open the Isleworks agenda to read it";
            return;
        }

        var data = agent->Data;
        snap.ScheduleSource = $"live (showing cycle {data->CycleDisplayed + 1}, in progress: cycle {data->CycleInProgress + 1})";
        var crafts = Plugin.DataManager.GetExcelSheet<MJICraftworksObject>();
        var w = 0;
        foreach (var ws in data->WorkshopSchedules)
        {
            w++;
            var agenda = new WorkshopAgenda { Workshop = w };
            for (var i = 0; i < ws.NumScheduleEntries && i < ws.EntryData.Length; i++)
            {
                var e = ws.EntryData[i];
                agenda.Entries.Add(new AgendaEntry
                {
                    CraftId = e.CraftObjectId,
                    Name = crafts.GetRowOrDefault(e.CraftObjectId) is { } c ? Name(c.Item.ValueNullable) : $"#{e.CraftObjectId}",
                    StartHour = e.StartingSlot,
                    Hours = e.Duration,
                    Efficient = e.Efficient,
                });
            }

            snap.Schedule.Add(agenda);
        }
    }
}
