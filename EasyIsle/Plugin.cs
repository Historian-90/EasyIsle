using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EasyIsle.Windows;
using Lumina.Excel.Sheets;

namespace EasyIsle;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    private const string Command = "/easyisle";
    private const string ShortCommand = "/ei";

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Configuration Configuration { get; }
    public IslandReader Reader { get; }
    public IslandSnapshot? Snapshot { get; private set; }
    public string? ReadError { get; private set; }

    private HashSet<uint>? gatherable;

    /// <summary>MJIItemPouch rows you can gather yourself (from the MJIGatheringItem sheet).</summary>
    public HashSet<uint> Gatherable
    {
        get
        {
            if (gatherable != null) return gatherable;
            var items = DataManager.GetExcelSheet<MJIGatheringItem>().Select(g => g.Item.RowId).Where(i => i != 0).ToHashSet();
            gatherable = DataManager.GetExcelSheet<MJIItemPouch>().Where(p => items.Contains(p.Item.RowId)).Select(p => p.RowId).ToHashSet();
            return gatherable;
        }
    }

    private const uint DefaultIslandTerritory = 1055;
    private List<WeatherChance>? islandWeather;
    private uint islandMap;

    private uint IslandTerritory => Configuration.IslandTerritory != 0 ? Configuration.IslandTerritory : DefaultIslandTerritory;

    /// <summary>The island's weathers and their chances (from its WeatherRate row).</summary>
    public IReadOnlyList<WeatherChance> IslandWeather
    {
        get
        {
            if (islandWeather != null) return islandWeather;
            var list = new List<WeatherChance>();
            try
            {
                if (DataManager.GetExcelSheet<TerritoryType>().TryGetRow(IslandTerritory, out var t))
                {
                    islandMap = t.Map.RowId;
                    var english = DataManager.GetExcelSheet<Weather>(ClientLanguage.English);
                    var rate = t.WeatherRate.Value;
                    for (var i = 0; i < rate.Weather.Count && i < rate.Rate.Count; i++)
                    {
                        var w = rate.Weather[i];
                        if (w.RowId == 0 || rate.Rate[i] == 0) continue;
                        var en = english.TryGetRow(w.RowId, out var e) ? e.Name.ExtractText() : string.Empty;
                        list.Add(new WeatherChance(w.RowId, w.Value.Name.ExtractText(), en, rate.Rate[i], w.Value.Icon));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Couldn't read the island's weather");
            }

            islandWeather = list;
            return list;
        }
    }

    /// <summary>
    /// Puts the game's map flag on a spot of the island and opens the map. Nothing else.
    /// Cave spots go on the Cave System map once it's been seen (see LearnCaveMap); until then the
    /// surface map, and the returned note says how to fix that.
    /// </summary>
    public string? FlagOnMap(float x, float y, bool cave = false)
    {
        try
        {
            _ = IslandWeather;
            if (islandMap == 0) return "The island map isn't loaded.";
            var map = islandMap;
            string? note = null;
            if (cave)
            {
                if (Configuration.IslandCaveMap != 0) map = Configuration.IslandCaveMap;
                else note = "Flagged on the surface map: open the map and switch it to Cave System once, then press Map again.";
            }

            GameGui.OpenMapWithMapLink(new MapLinkPayload(IslandTerritory, map, x, y));
            return note;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't open the map");
            return "Couldn't open the map: " + ex.Message;
        }
    }

    /// <summary>
    /// The island has two maps in the same zone (Surface Terrain and Cave System). The game's data
    /// doesn't say which row is the cave, so watch the map window: any other island map shown there
    /// (switching floors, or the map following you into the cave) is the cave map.
    /// </summary>
    private unsafe void LearnCaveMap()
    {
        _ = IslandWeather;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
        if (agent == null || islandMap == 0) return;
        foreach (var (territory, map) in new[] { (agent->SelectedTerritoryId, agent->SelectedMapId), (agent->CurrentTerritoryId, agent->CurrentMapId) })
        {
            if (territory != IslandTerritory || map == 0 || map == islandMap || map == Configuration.IslandCaveMap) continue;
            Configuration.IslandCaveMap = map;
            Configuration.Save();
            Log.Information($"Island cave map is {map}");
        }
    }

    private readonly WindowSystem windowSystem = new("EasyIsle");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Reader = new IslandReader(Configuration);

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open EasyIsle, the Island Sanctuary workshop planner. Short form: /ei",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { ShowInHelp = false });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(ShortCommand);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    /// <summary>Reads the island now. Called from the UI (game thread).</summary>
    public void Refresh()
    {
        try
        {
            Snapshot = Reader.Read();
            ReadError = null;
            if (Snapshot.OnIsland && ClientState.TerritoryType != 0 && ClientState.TerritoryType != Configuration.IslandTerritory)
            {
                Configuration.IslandTerritory = ClientState.TerritoryType;
                islandWeather = null;
                Configuration.Save();
            }

            if (Snapshot.OnIsland) LearnCaveMap();

            Replan();
        }
        catch (Exception ex)
        {
            ReadError = ex.Message;
            Log.Error(ex, "Couldn't read island data");
        }
    }

    public WeekPlan? Week { get; private set; }
    public (List<GranaryAdvice> advice, List<RareStock> rares)? Granary { get; private set; }
    public CropPlan? Crops { get; private set; }
    public PastureAdvice? Pasture { get; private set; }
    public Dictionary<uint, Forecast> Forecasts { get; private set; } = [];
    public bool Planning { get; private set; }

    private string planSignature = string.Empty;
    private bool replanQueued;

    /// <summary>Re-plans the week in the background when anything that matters has changed.</summary>
    public void Replan(bool force = false)
    {
        var snap = Snapshot;
        if (snap == null) return;
        var signature = Signature(snap);
        if (!force && signature == planSignature) return;
        if (Planning)
        {
            replanQueued = true;
            return;
        }

        planSignature = signature;
        Planning = true;
        replanQueued = false;

        // Work on a private copy: the live snapshot shares dictionaries with the configuration.
        var copy = JsonSerializer.Deserialize<IslandSnapshot>(JsonSerializer.Serialize(snap))!;
        var options = new PlanOptions
        {
            AllowGathering = Configuration.AllowGathering,
            PrioritizeFavors = Configuration.PrioritizeFavors,
        };
        var gather = Gatherable;
        var feedChoice = Configuration.FeedPouchId != 0 ? Configuration.FeedPouchId : Configuration.FeedInUse;
        var savedWeek = Configuration.WeekLock;
        var savedCrops = new Dictionary<int, uint>(Configuration.CropLock);
        var savedPasture = new Dictionary<string, int>(Configuration.PastureLock);
        var season = IslandReader.SeasonStartUtc(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var optionKey = $"{Configuration.AllowGathering}|{Configuration.PrioritizeFavors}";
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var forecasts = WeekPlanner.Forecasts(copy);
                var savedOk = savedWeek != null && savedWeek.Season == season && savedWeek.CycleDay == copy.CycleDay && savedWeek.Options == optionKey;
                var week = new WeekPlanner(copy, options, gather, forecasts) { Saved = savedOk ? savedWeek : null }.Make();
                if (Planner.LastAnomaly is { } odd)
                {
                    Log.Warning($"Impossible craft value, counted as 0: {odd}");
                    week.Notes.Add("A craft's value came out impossible and was counted as 0 (details in /xllog). Please send a snapshot from Settings > Troubleshooting.");
                    Planner.LastAnomaly = null;
                }
                var crops = CropAdvisor.Advise(copy, week, feedChoice);
                var cropLock = CropAdvisor.ApplyLock(crops, savedCrops);
                var pasture = AnimalAdvisor.Advise(copy, week, savedPasture);
                Forecasts = forecasts;
                Week = week;
                Granary = GranaryAdvisor.Advise(copy, week);
                Crops = crops;
                Pasture = pasture;

                // Keep what was decided so the advice sits still.
                Framework.RunOnFrameworkThread(() =>
                {
                    if (!week.Kept && week.Days.Count > 0)
                        Configuration.WeekLock = new WeekLock { Season = season, CycleDay = copy.CycleDay, Options = optionKey, Cycles = week.Days.Select(d => d.Cycle).ToList(), Chains = week.Chains, RestAdvice = week.RestAdvice };
                    Configuration.CropLock = cropLock;
                    Configuration.PastureLock = pasture.Capture.Count + pasture.Release.Count > 0 ? pasture.Target : [];
                    Configuration.Save();
                });
            }
            catch (Exception ex)
            {
                Week = null;
                Log.Error(ex, "Week planner failed");
            }
            finally
            {
                Planning = false;
            }
        }).ContinueWith(_ =>
        {
            if (replanQueued) Framework.RunOnFrameworkThread(() => Replan());
        });
    }

    /// <summary>Drops the kept week plan and plans again from scratch.</summary>
    public void NewWeekPlan()
    {
        Configuration.WeekLock = null;
        Configuration.Save();
        Replan(true);
    }

    /// <summary>Drops the kept pasture advice and works out new advice.</summary>
    public void NewPasturePlan()
    {
        Configuration.PastureLock = [];
        Configuration.Save();
        Replan(true);
    }

    /// <summary>Drops the kept planting list and works out a new one.</summary>
    public void NewCropPlan()
    {
        Configuration.CropLock = [];
        Configuration.Save();
        Replan(true);
    }

    private string Signature(IslandSnapshot s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.DemandValid).Append('|').Append(s.CycleDay).Append('|').Append(s.Groove).Append('|').Append(s.Rank).Append('|');
        sb.Append(string.Join(",", s.RestDays)).Append('|').Append(Configuration.AllowGathering).Append(Configuration.PrioritizeFavors).Append('|');
        foreach (var w in s.Workshops) sb.Append(w.Level).Append(w.UnderConstruction ? 'u' : 'b');
        foreach (var c in s.Crafts) sb.Append(c.SupplyLevel).Append(c.PopularityPercent);
        foreach (var p in s.Isleventory) sb.Append(p.Count).Append(',');
        foreach (var f in s.Favors) sb.Append(f.Delivered).Append(f.Scheduled);
        foreach (var e in s.Expeditions) sb.Append(e.ExpeditionId).Append(e.DaysRemaining);
        foreach (var d in s.Destinations) sb.Append(d.Unlocked ? 'y' : 'n');
        foreach (var f in s.FarmPlots) sb.Append(f.SeedRow).Append(':').Append(f.Yield).Append(f.CareHalted ? 'h' : '-').Append(f.UnderCare ? 'c' : '-');
        foreach (var a in s.Animals) sb.Append(a.Row).Append(',');
        sb.Append('|').Append(s.Animals.Count).Append('|').Append(Configuration.FeedPouchId).Append(Configuration.FeedInUse);
        foreach (var season in s.SupplyHistory) sb.Append(season.Key).Append(season.Value.Count);
        return sb.ToString();
    }

    public string SnapshotFolder => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "snapshots");

    public string SnapshotJson() => Snapshot == null ? "{}" : JsonSerializer.Serialize(Snapshot, JsonOptions);

    /// <summary>Saves the current snapshot as a JSON file and returns its path.</summary>
    public string SaveSnapshot()
    {
        Directory.CreateDirectory(SnapshotFolder);
        var path = Path.Combine(SnapshotFolder, $"island-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, SnapshotJson());
        return path;
    }
}
