using System;
using System.Collections.Generic;

namespace EasyIsle;

/// <summary>
/// Everything EasyIsle knows about the player's island at one moment. Plain data, so it can be
/// saved as JSON and fed to the planner without the game running.
/// </summary>
[Serializable]
public class IslandSnapshot
{
    public DateTime TakenUtc { get; set; } = DateTime.UtcNow;
    public bool OnIsland { get; set; }
    public bool ManagerFound { get; set; }

    public int Rank { get; set; }
    public uint Xp { get; set; }
    public uint Groove { get; set; }

    /// <summary>Groove cap from built landmarks (15/20/25/35/45).</summary>
    public int GrooveMax { get; set; }

    public int LandmarksBuilt { get; set; }

    /// <summary>0-6, 0 = reset day (Tuesday).</summary>
    public int CycleDay { get; set; }

    /// <summary>0-13; 0 = this week's reset day, 7 = next reset.</summary>
    public List<int> RestDays { get; set; } = [];

    public int PopularityRow { get; set; }
    public int NextPopularityRow { get; set; }

    /// <summary>False while the game hasn't loaded Supply &amp; Demand yet (open the Isleworks once).</summary>
    public bool DemandValid { get; set; }

    public List<Building> Workshops { get; set; } = [];
    public List<Building> GranaryBuildings { get; set; } = [];
    public Building Farm { get; set; } = new();
    public Building Pasture { get; set; } = new();
    public int CabinLevel { get; set; }

    public List<Craft> Crafts { get; set; } = [];

    /// <summary>Where the isleventory counts came from ("live", "cached ...", or "unavailable").</summary>
    public string IsleventorySource { get; set; } = "unavailable";

    public List<PouchItem> Isleventory { get; set; } = [];
    public List<Expedition> Expeditions { get; set; } = [];

    /// <summary>Granary expedition areas from the game data.</summary>
    public List<Destination> Destinations { get; set; } = [];

    /// <summary>True when the unlocked areas were read from the game (granary window opened at least once).</summary>
    public bool DestinationsUnlockKnown { get; set; }
    public List<FarmPlot> FarmPlots { get; set; } = [];

    /// <summary>Animal kinds from the game data (MJIAnimals), with their leavings.</summary>
    public List<AnimalKind> AnimalKinds { get; set; } = [];

    /// <summary>How many animals the pasture holds (5 / 10 / 20).</summary>
    public int PastureCapacity { get; set; }

    /// <summary>Recipes that turn produce into animal feed.</summary>
    public List<FeedRecipe> FeedRecipes { get; set; } = [];
    public List<Animal> Animals { get; set; } = [];
    public List<Favor> Favors { get; set; } = [];

    /// <summary>The agenda as the game shows it; only filled while the Isleworks window is open.</summary>
    public string ScheduleSource { get; set; } = "unavailable";

    public List<WorkshopAgenda> Schedule { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    /// <summary>Supply &amp; demand bytes recorded at the first read of each cycle: season start (yyyy-MM-dd) -> cycle (0-6) -> craft -> byte.</summary>
    public Dictionary<string, Dictionary<int, Dictionary<uint, int>>> SupplyHistory { get; set; } = [];

    /// <summary>Hex dump of IslandState (offset 0x00 onward), for working out fields the struct map gets wrong.</summary>
    public string RawIslandState { get; set; } = string.Empty;
}

[Serializable]
public class Building
{
    public bool Placed { get; set; }
    public int Level { get; set; }
    public bool UnderConstruction { get; set; }
    public int HoursToCompletion { get; set; }
}

[Serializable]
public class Craft
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RankRequired { get; set; }
    public bool Available { get; set; }
    public int Hours { get; set; }
    public int Value { get; set; }
    public List<string> Themes { get; set; } = [];
    public List<Ingredient> Materials { get; set; } = [];
    public string Supply { get; set; } = string.Empty;
    public string DemandShift { get; set; } = string.Empty;
    public string Popularity { get; set; } = string.Empty;
    public string NextPopularity { get; set; } = string.Empty;

    /// <summary>CraftworkSupply as a number (0 = Nonexistent ... 4 = Overflowing), -1 when unknown.</summary>
    public int SupplyLevel { get; set; } = -1;

    /// <summary>Popularity multiplier in percent (80-140), 0 when unknown.</summary>
    public int PopularityPercent { get; set; }

    public int NextPopularityPercent { get; set; }
}

[Serializable]
public class Ingredient
{
    public uint PouchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Amount { get; set; }
}

[Serializable]
public class PouchItem
{
    public uint PouchId { get; set; }
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Discovered { get; set; }
    public uint Icon { get; set; }

    /// <summary>MJICropSeed row for produce and seeds (0 for everything else).</summary>
    public uint CropRow { get; set; }

    /// <summary>True for produce (grown on the cropland), false for its seeds.</summary>
    public bool IsProduce { get; set; }
}

[Serializable]
public class Expedition
{
    public int Granary { get; set; }
    public int ExpeditionId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public int DaysRemaining { get; set; }
    public string RareMaterial { get; set; } = string.Empty;
    public int RareCount { get; set; }
    public int WaitingToCollect { get; set; }
    public DateTime? FinishesUtc { get; set; }
}

[Serializable]
public class FarmPlot
{
    public int Slot { get; set; }
    public uint SeedRow { get; set; }
    public string Crop { get; set; } = string.Empty;

    /// <summary>Isleventory row of the produce this plot grows (0 = empty).</summary>
    public uint CropPouchId { get; set; }

    /// <summary>Isleventory row of the seeds it uses.</summary>
    public uint SeedPouchId { get; set; }

    public int Growth { get; set; }
    public int Water { get; set; }

    /// <summary>Produce the gardener is holding for this plot (it stops at 50).</summary>
    public int Yield { get; set; }

    public bool UnderCare { get; set; }
    public bool CareHalted { get; set; }
}

[Serializable]
public class FeedRecipe
{
    public uint OutputPouchId { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public int Yield { get; set; }
    public int Order { get; set; }
    public List<Ingredient> Materials { get; set; } = [];
}

[Serializable]
public class Animal
{
    public int Slot { get; set; }

    /// <summary>MJIAnimals row (the kind of animal).</summary>
    public uint Row { get; set; }

    public uint CommonPouchId { get; set; }
    public uint BonusPouchId { get; set; }
    public string Species { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public int Food { get; set; }
    public int Mood { get; set; }
    public bool UnderCare { get; set; }
    public List<string> Leavings { get; set; } = [];
}

[Serializable]
public class Favor
{
    public int Index { get; set; }
    public uint CraftId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Delivered { get; set; }
    public int Scheduled { get; set; }
    public bool Shipped { get; set; }
    public bool Bonus { get; set; }
    public int Hours { get; set; }

    /// <summary>"Last", "Current" or "Next" season: the 9 slots are 3 seasons of 3 requests.</summary>
    public string Season { get; set; } = string.Empty;

    /// <summary>Items the request asks for: 8 for 4 h and 8 h crafts, 6 for 6 h crafts.</summary>
    public int Required { get; set; }
}

[Serializable]
public class WorkshopAgenda
{
    public int Workshop { get; set; }
    public List<AgendaEntry> Entries { get; set; } = [];
}

[Serializable]
public class AgendaEntry
{
    public uint CraftId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int StartHour { get; set; }
    public int Hours { get; set; }
    public bool Efficient { get; set; }
}

[Serializable]
public class Destination
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint RarePouchId { get; set; }
    public string RareName { get; set; } = string.Empty;
    public List<uint> CommonPouchIds { get; set; } = [];
    public bool Unlocked { get; set; } = true;
}

[Serializable]
public class AnimalKind
{
    public uint Row { get; set; }

    /// <summary>'S', 'M' or 'L' (net, restraint or soporific to catch).</summary>
    public char Size { get; set; }

    public uint CommonPouchId { get; set; }
    public uint BonusPouchId { get; set; }
    public uint Icon { get; set; }
}
