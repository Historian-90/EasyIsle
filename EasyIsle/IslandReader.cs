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

        // ClientStructs maps MJIWorkshops/MJIGranaries without the 8-byte pointer each one starts with,
        // so every field reads 8 bytes early (granaries came out "under construction, 247 h").
        // Real layout, checked against a fully built island: pointer, then PlaceId, GlamourLevel,
        // HoursToCompletion, BuildingLevel, UnderConstruction (one byte per building each).
        var raw = (byte*)island;
        snap.Workshops.AddRange(ReadBuildings(raw + 0xB0, 4));
        snap.GranaryBuildings.AddRange(ReadBuildings(raw + 0xD0, 2));

        snap.RawIslandState = Convert.ToHexString(new ReadOnlySpan<byte>(island, IslandState.StructSize));
        snap.Farm = FromFarmPasture(island->Farm);
        snap.Pasture = FromFarmPasture(island->Pasture);

        ReadCrafts(mgr, snap);
        ReadIsleventory(mgr, snap);
        ReadExpeditions(mgr, snap);
        ReadDestinations(snap);
        ReadFarm(mgr, snap);
        ReadFeedRecipes(snap);
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

    private static List<Building> ReadBuildings(byte* fields, int count)
    {
        var list = new List<Building>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new Building
            {
                Placed = fields[i] != 0,
                Level = fields[3 * count + i],
                HoursToCompletion = fields[2 * count + i],
                UnderConstruction = fields[4 * count + i] != 0,
            });
        }

        return list;
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
            // Match by game item id. RowId isn't an MJIItemPouch row for every entry: the Tools tab
            // numbers its own rows from 1, which overwrote the first materials with a count of 1.
            var byItem = new Dictionary<uint, int>();
            foreach (var item in agent->InventoryData->Inventory.AsSpan())
                if (item.ItemId != 0) byItem[item.ItemId] = item.StackSize;
            live = [];
            foreach (var row in sheet)
                if (row.RowId != 0 && row.Item.RowId != 0 && byItem.TryGetValue(row.Item.RowId, out var count))
                    live[row.RowId] = count;
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
        var categoryOf = new Dictionary<uint, uint>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Item.RowId == 0) continue;
            snap.Isleventory.Add(new PouchItem
            {
                PouchId = row.RowId,
                ItemId = row.Item.RowId,
                Name = Name(row.Item.ValueNullable),
                Category = row.Category.ValueNullable?.Singular.ExtractText() ?? string.Empty,
                Icon = row.Item.ValueNullable?.Icon ?? 0,
                CropRow = row.Crop.RowId,
                Count = counts.TryGetValue(row.RowId, out var c) ? c : 0,
                Discovered = !mgr->IsPouchItemLocked((ushort)row.RowId),
            });
            categoryOf[row.RowId] = row.Category.RowId;
        }

        LinkProduce(snap, categoryOf);
    }

    /// <summary>
    /// Only seed rows point at their crop (MJIItemPouch.Crop), so produce has to be matched up:
    /// produce is the category with one item per crop and no crop links, in the same order as the
    /// crop rows. What the Cropland Management window shows (seed -> yield) wins when it's known.
    /// </summary>
    private void LinkProduce(IslandSnapshot snap, Dictionary<uint, uint> categoryOf)
    {
        var crops = Plugin.DataManager.GetExcelSheet<MJICropSeed>()
            .Where(c => c.RowId != 0 && c.Item.RowId != 0).Select(c => c.RowId).OrderBy(r => r).ToList();
        var produceCategory = snap.Isleventory
            .GroupBy(p => categoryOf.GetValueOrDefault(p.PouchId))
            .FirstOrDefault(g => g.Key != 0 && g.Count() == crops.Count && g.All(p => p.CropRow == 0));
        if (produceCategory != null)
        {
            var ordered = produceCategory.OrderBy(p => p.PouchId).ToList();
            for (var k = 0; k < ordered.Count; k++)
            {
                ordered[k].CropRow = crops[k];
                ordered[k].IsProduce = true;
            }
        }

        // Learn seed -> produce from the Cropland Management window when it's open.
        var agent = AgentMJIFarmManagement.Instance();
        if (agent != null && agent->IsAgentActive())
        {
            var changed = false;
            for (var i = 0; i < agent->NumSlots && i < agent->Slots.Length; i++)
            {
                var slot = agent->Slots[i];
                if (slot.SeedItemId == 0 || slot.YieldItemId == 0) continue;
                if (config.CropYields.TryGetValue(slot.SeedItemId, out var known) && known == slot.YieldItemId) continue;
                config.CropYields[slot.SeedItemId] = slot.YieldItemId;
                changed = true;
            }

            if (changed) config.Save();
        }

        foreach (var (seedItem, yieldItem) in config.CropYields)
        {
            var seed = snap.Isleventory.FirstOrDefault(p => p.ItemId == seedItem && !p.IsProduce);
            var produce = snap.Isleventory.FirstOrDefault(p => p.ItemId == yieldItem);
            if (seed == null || produce == null || seed.CropRow == 0) continue;
            foreach (var other in snap.Isleventory.Where(p => p.IsProduce && p.CropRow == seed.CropRow && p != produce))
                other.CropRow = 0;
            produce.CropRow = seed.CropRow;
            produce.IsProduce = true;
        }
    }

    /// <summary>
    /// Expedition areas as the granary window lists them (only unlocked ones, names in the game's
    /// language). Remembered once seen, since the window has to be open for the game to load them.
    /// </summary>
    private void ReadDestinations(IslandSnapshot snap)
    {
        var agent = AgentMJIGatheringHouse.Instance();
        if (agent != null && agent->Data != null && agent->Data->Initialized && agent->Data->Expeditions.Count > 0)
        {
            var pouchByItem = new Dictionary<uint, uint>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<MJIItemPouch>())
                if (row.RowId != 0 && row.Item.RowId != 0) pouchByItem.TryAdd(row.Item.RowId, row.RowId);

            var seen = new List<Destination>();
            foreach (var e in agent->Data->Expeditions.AsSpan())
            {
                if (e.ExpeditionId == 0) continue;
                var d = new Destination
                {
                    Id = e.ExpeditionId,
                    Name = e.Name.ToString(),
                    RarePouchId = pouchByItem.GetValueOrDefault(e.RareItemId),
                };
                d.RareName = d.RarePouchId != 0 ? PouchName(d.RarePouchId) : string.Empty;
                for (var i = 0; i < e.NumNormalItems && i < e.NormalItemIds.Length; i++)
                    if (pouchByItem.TryGetValue(e.NormalItemIds[i], out var pouch) && !d.CommonPouchIds.Contains(pouch))
                        d.CommonPouchIds.Add(pouch);
                seen.Add(d);
            }

            var changed = seen.Count != config.KnownDestinations.Count
                          || seen.Zip(config.KnownDestinations).Any(p => p.First.Id != p.Second.Id || p.First.Name != p.Second.Name
                                                                        || p.First.RarePouchId != p.Second.RarePouchId
                                                                        || !p.First.CommonPouchIds.SequenceEqual(p.Second.CommonPouchIds));
            if (changed && seen.All(d => d.Name.Length > 0))
            {
                config.KnownDestinations = seen;
                config.Save();
            }
        }

        snap.DestinationsUnlockKnown = config.KnownDestinations.Count > 0;
        snap.Destinations = config.KnownDestinations
            .Select(d => new Destination { Id = d.Id, Name = d.Name, RarePouchId = d.RarePouchId, RareName = d.RareName, CommonPouchIds = [.. d.CommonPouchIds] })
            .ToList();
        foreach (var e in snap.Expeditions)
            if (snap.Destinations.FirstOrDefault(d => d.Id == e.ExpeditionId) is { } d) e.Destination = d.Name;
    }

    private void ReadExpeditions(MJIManager* mgr, IslandSnapshot snap)
    {
        var state = mgr->GranariesState;
        if (state == null)
        {
            snap.Problems.Add("Granary data isn't loaded (usually only on the island).");
            return;
        }

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
            exp.Destination = g.ActiveExpeditionId == 0 ? string.Empty : $"area {g.ActiveExpeditionId}"; // named in ReadDestinations
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

        var slots = Math.Min((int)mgr->GetFarmSlotCount(), farm->SeedType.Length);
        for (var i = 0; i < slots; i++)
        {
            var seed = farm->SeedType[i];
            var plot = new FarmPlot
            {
                Slot = i + 1,
                SeedRow = seed,
                Growth = farm->GrowthLevel[i],
                Water = farm->WaterLevel[i],
                Yield = farm->GardenerYield[i],
                UnderCare = farm->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare),
                CareHalted = farm->FarmSlotFlags[i].HasFlag(FarmSlotFlags.CareHalted),
            };
            if (seed != 0)
            {
                var produce = snap.Isleventory.FirstOrDefault(p => p.CropRow == seed && p.IsProduce);
                var seeds = snap.Isleventory.FirstOrDefault(p => p.CropRow == seed && !p.IsProduce);
                plot.CropPouchId = produce?.PouchId ?? 0;
                plot.SeedPouchId = seeds?.PouchId ?? 0;
                plot.Crop = produce?.Name ?? $"seed #{seed}";
            }

            snap.FarmPlots.Add(plot);
        }

    }

    /// <summary>
    /// Feed recipes. Greenfeed takes "any produce" (2 for Island Greenfeed; 2 + 2 different kinds for
    /// Premium), which the recipe data doesn't point at a single item, so such ingredients are kept as
    /// "any produce" (PouchId 0).
    /// </summary>
    private void ReadFeedRecipes(IslandSnapshot snap)
    {
        var byPouch = snap.Isleventory.ToDictionary(p => p.PouchId);
        foreach (var r in Plugin.DataManager.GetExcelSheet<MJIRecipe>())
        {
            if (r.RowId == 0 || r.ItemPouch.RowId == 0) continue;
            var recipe = new FeedRecipe
            {
                OutputPouchId = r.ItemPouch.RowId,
                OutputName = byPouch.TryGetValue(r.ItemPouch.RowId, out var o) ? o.Name : PouchName(r.ItemPouch.RowId),
                Yield = Math.Max(1, (int)r.Yield),
                Order = r.Order,
            };
            var anyProduce = false;
            for (var i = 0; i < r.Material.Count; i++)
            {
                var amount = i < r.Amount.Count ? r.Amount[i] : 0;
                if (r.Material[i].RowId == 0 || amount == 0) continue;
                var mat = r.Material[i].ValueNullable?.ItemPouch.RowId ?? 0;
                if (mat == 0 || !byPouch.ContainsKey(mat))
                {
                    anyProduce = true;
                    recipe.Materials.Add(new Ingredient { PouchId = 0, Name = "any produce", Amount = amount });
                }
                else
                {
                    recipe.Materials.Add(new Ingredient { PouchId = mat, Name = byPouch[mat].Name, Amount = amount });
                }
            }

            if (anyProduce || recipe.Materials.Any(m => byPouch.TryGetValue(m.PouchId, out var p) && p.IsProduce))
                snap.FeedRecipes.Add(recipe);
        }

        // If the data didn't show them, fall back to the known greenfeed recipes (rows 62/63 in the
        // isleventory: Island Greenfeed = 2 produce -> 3, Premium = 2 + 2 different produce -> 3).
        if (snap.FeedRecipes.Count == 0)
        {
            foreach (var (row, amount, order) in new[] { (62u, 2, 1), (63u, 4, 2) })
            {
                if (!byPouch.TryGetValue(row, out var item)) continue;
                snap.FeedRecipes.Add(new FeedRecipe
                {
                    OutputPouchId = row,
                    OutputName = item.Name,
                    Yield = 3,
                    Order = order,
                    Materials = [new Ingredient { PouchId = 0, Name = "any produce", Amount = amount }],
                });
            }
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
                Season = (i / 3) switch { 0 => "Last", 1 => "Current", _ => "Next" },
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
