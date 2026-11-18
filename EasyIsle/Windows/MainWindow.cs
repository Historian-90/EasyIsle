using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EasyIsle.Windows;

/// <summary>
/// For now: a view of everything EasyIsle can read about the island, so we can check each data
/// source in game before the planner is built on top of it.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Good = new(0.55f, 0.9f, 0.55f, 1f);

    private readonly Plugin plugin;
    private DateTime lastRead = DateTime.MinValue;
    private bool autoRefresh = true;
    private bool availableOnly = true;
    private bool discoveredOnly = true;
    private string status = string.Empty;

    public MainWindow(Plugin plugin) : base("EasyIsle###EasyIsleMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620, 400) };
        Size = new Vector2(860, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => plugin.Refresh();

    public override void Draw()
    {
        if (autoRefresh && DateTime.UtcNow - lastRead > TimeSpan.FromSeconds(1))
        {
            plugin.Refresh();
            lastRead = DateTime.UtcNow;
        }

        if (ImGui.Button("Refresh")) plugin.Refresh();
        ImGui.SameLine();
        ImGui.Checkbox("Auto", ref autoRefresh);
        ImGui.SameLine();
        if (ImGui.Button("Copy snapshot"))
        {
            ImGui.SetClipboardText(plugin.SnapshotJson());
            status = "Snapshot copied to the clipboard.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Save snapshot"))
        {
            try { status = "Saved " + plugin.SaveSnapshot(); }
            catch (Exception ex) { status = "Couldn't save: " + ex.Message; }
        }

        ImGui.SameLine();
        if (ImGui.Button("Open snapshots folder"))
        {
            try
            {
                System.IO.Directory.CreateDirectory(plugin.SnapshotFolder);
                Process.Start(new ProcessStartInfo { FileName = plugin.SnapshotFolder, UseShellExecute = true });
            }
            catch (Exception ex) { status = "Couldn't open folder: " + ex.Message; }
        }

        if (status.Length > 0) ImGui.TextColored(Muted, status);
        if (plugin.ReadError != null) ImGui.TextColored(Warn, "Read error: " + plugin.ReadError);

        var s = plugin.Snapshot;
        if (s == null)
        {
            ImGui.TextUnformatted("No data yet.");
            return;
        }

        ImGui.Separator();
        if (!ImGui.BeginTabBar("##tabs")) return;
        Tab("Island", () => DrawIsland(s));
        Tab($"Crafts ({s.Crafts.Count(c => c.Available)})", () => DrawCrafts(s));
        Tab("Isleventory", () => DrawIsleventory(s));
        Tab("Granary, crops, animals", () => DrawProduce(s));
        Tab($"Favors ({s.Favors.Count})", () => DrawFavors(s));
        Tab("Agenda", () => DrawAgenda(s));
        ImGui.EndTabBar();
    }

    private static void Tab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        if (ImGui.BeginChild("##tabbody")) draw();
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    private static void DrawIsland(IslandSnapshot s)
    {
        if (s.Problems.Count > 0)
        {
            ImGui.TextColored(Warn, "Missing data:");
            foreach (var p in s.Problems) ImGui.BulletText(p);
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(Good, "Everything EasyIsle needs is loaded.");
        }

        Row("On the island", s.OnIsland ? "yes" : "no");
        Row("Island rank", $"{s.Rank} ({s.Xp:N0} xp)");
        Row("Groove", s.Groove.ToString());
        Row("Cycle day", $"{s.CycleDay + 1} of 7 ({DayName(s.CycleDay)})");
        Row("Rest days", s.RestDays.Count == 0 ? "not loaded" : string.Join(", ", s.RestDays.Select(d => d < 7 ? $"{DayName(d)} (this week)" : $"{DayName(d - 7)} (next week)")));
        Row("Supply & Demand", s.DemandValid ? $"loaded (popularity set {s.PopularityRow}, next {s.NextPopularityRow})" : "not loaded");
        Row("Cabin level", s.CabinLevel.ToString());
        for (var i = 0; i < s.Workshops.Count; i++) Row($"Workshop {i + 1}", Describe(s.Workshops[i]));
        for (var i = 0; i < s.GranaryBuildings.Count; i++) Row($"Granary {i + 1}", Describe(s.GranaryBuildings[i]));
        Row("Cropland", Describe(s.Farm));
        Row("Pasture", Describe(s.Pasture));
    }

    private static string DayName(int cycleDay) =>
        new[] { "Tue", "Wed", "Thu", "Fri", "Sat", "Sun", "Mon" }[((cycleDay % 7) + 7) % 7];

    private static string Describe(Building b)
    {
        if (!b.Placed) return "not built";
        var text = $"level {b.Level}";
        if (b.UnderConstruction) text += $", under construction ({b.HoursToCompletion} h left)";
        return text;
    }

    private static void Row(string label, string value)
    {
        ImGui.TextColored(Muted, label + ":");
        ImGui.SameLine(170);
        ImGui.TextUnformatted(value);
    }

    private void DrawCrafts(IslandSnapshot s)
    {
        ImGui.Checkbox("Only crafts my rank allows", ref availableOnly);
        var crafts = s.Crafts.Where(c => !availableOnly || c.Available).ToList();
        if (!ImGui.BeginTable("##crafts", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var h in new[] { "Item", "Rank", "Hours", "Value", "Themes", "Materials", "Supply / demand", "Popularity (next)" })
            ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();
        foreach (var c in crafts)
        {
            ImGui.TableNextRow();
            Cell(c.Name);
            Cell(c.RankRequired.ToString());
            Cell(c.Hours.ToString());
            Cell(c.Value.ToString());
            Cell(string.Join(", ", c.Themes));
            Cell(string.Join(", ", c.Materials.Select(m => $"{m.Amount}x {m.Name}")));
            Cell(c.Supply.Length == 0 ? "-" : $"{c.Supply} / {c.DemandShift}");
            Cell(c.Popularity.Length == 0 ? "-" : $"{c.Popularity} ({c.NextPopularity})");
        }

        ImGui.EndTable();
    }

    private void DrawIsleventory(IslandSnapshot s)
    {
        ImGui.TextColored(Muted, "Counts: " + s.IsleventorySource);
        ImGui.Checkbox("Only items I've discovered", ref discoveredOnly);
        if (!ImGui.BeginTable("##pouch", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Category");
        ImGui.TableSetupColumn("Count");
        ImGui.TableHeadersRow();
        foreach (var p in s.Isleventory.Where(p => !discoveredOnly || p.Discovered))
        {
            ImGui.TableNextRow();
            Cell(p.Name);
            Cell(p.Category);
            Cell(p.Count.ToString());
        }

        ImGui.EndTable();
    }

    private static void DrawProduce(IslandSnapshot s)
    {
        ImGui.TextUnformatted("Granary expeditions");
        if (s.Expeditions.Count == 0) ImGui.TextColored(Muted, "  not loaded");
        foreach (var e in s.Expeditions)
        {
            var text = e.ExpeditionId == 0
                ? "idle"
                : $"{e.Destination}, {e.DaysRemaining} day(s) left, rare: {e.RareMaterial}" + (e.FinishesUtc is { } f ? $", ends {f.ToLocalTime():g}" : string.Empty);
            if (e.WaitingToCollect > 0) text += $", {e.WaitingToCollect} waiting to collect";
            Row($"  Granary {e.Granary}", text);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Cropland");
        if (s.FarmPlots.Count == 0) ImGui.TextColored(Muted, "  not loaded");
        foreach (var p in s.FarmPlots)
        {
            Row($"  Plot {p.Slot}", p.SeedRow == 0
                ? "empty"
                : $"{p.Crop}, growth {p.Growth}, water {p.Water}, yield {p.Yield}" + (p.UnderCare ? ", under care" : string.Empty));
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Pasture");
        if (s.Animals.Count == 0) ImGui.TextColored(Muted, "  not loaded or empty");
        foreach (var a in s.Animals)
        {
            var name = a.Nickname.Length > 0 ? $"{a.Nickname} ({a.Species})" : a.Species;
            Row($"  Slot {a.Slot}", $"{name}, food {a.Food}, mood {a.Mood}" + (a.UnderCare ? ", under care" : string.Empty)
                                     + (a.Leavings.Count > 0 ? $", leaves {string.Join(" / ", a.Leavings)}" : string.Empty));
        }
    }

    private static void DrawFavors(IslandSnapshot s)
    {
        if (s.Favors.Count == 0)
        {
            ImGui.TextColored(Muted, "Not loaded: open the Felicitous Favors window once.");
            return;
        }

        foreach (var f in s.Favors)
        {
            Row($"Favor {f.Index + 1}", $"{f.Name}: delivered {f.Delivered}, scheduled {f.Scheduled}"
                                       + (f.Shipped ? ", shipped" : string.Empty) + (f.Bonus ? ", bonus" : string.Empty));
        }
    }

    private static void DrawAgenda(IslandSnapshot s)
    {
        ImGui.TextColored(Muted, "Agenda: " + s.ScheduleSource);
        foreach (var w in s.Schedule)
        {
            Row($"Workshop {w.Workshop}", w.Entries.Count == 0
                ? "nothing scheduled"
                : string.Join("  >  ", w.Entries.Select(e => $"{e.Name} ({e.StartHour}h+{e.Hours}{(e.Efficient ? ", efficient" : string.Empty)})")));
        }
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }
}
