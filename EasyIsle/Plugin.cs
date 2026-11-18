using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using Dalamud.Game.Command;
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
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var forecasts = WeekPlanner.Forecasts(copy);
                var week = new WeekPlanner(copy, options, gather, forecasts).Make();
                Forecasts = forecasts;
                Week = week;
                Granary = GranaryAdvisor.Advise(copy, week);
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
