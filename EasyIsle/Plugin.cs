using System;
using System.IO;
using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EasyIsle.Windows;

namespace EasyIsle;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string Command = "/easyisle";
    private const string ShortCommand = "/ei";

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Configuration Configuration { get; }
    public IslandReader Reader { get; }
    public IslandSnapshot? Snapshot { get; private set; }
    public string? ReadError { get; private set; }

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
        }
        catch (Exception ex)
        {
            ReadError = ex.Message;
            Log.Error(ex, "Couldn't read island data");
        }
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
