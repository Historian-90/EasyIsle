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
        if (autoRefresh && DateTime.UtcNow - lastRead > TimeSpan.FromSeconds(3))
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
        Tab("Plan", DrawPlan);
        Tab("Island", () => DrawIsland(s));
        Tab($"Crafts ({s.Crafts.Count(c => c.Available)})", () => DrawCrafts(s));
        Tab("Isleventory", () => DrawIsleventory(s));
        Tab("Granary, crops, animals", () => DrawProduce(s));
        Tab("Favors", () => DrawFavors(s));
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
        Row("Groove", $"{s.Groove} / {s.GrooveMax}");
        Row("Cycle day", $"{s.CycleDay + 1} of 7 ({DayName(s.CycleDay)})");
        Row("Rest days", s.RestDays.Count == 0 ? "not loaded" : string.Join(", ", s.RestDays.Select(d => d < 7 ? $"{DayName(d)} (this week)" : $"{DayName(d - 7)} (next week)")));
        Row("Supply & Demand", s.DemandValid ? $"loaded (popularity set {s.PopularityRow}, next {s.NextPopularityRow})" : "not loaded");
        Row("Cabin level", s.CabinLevel.ToString());
        for (var i = 0; i < s.Workshops.Count; i++)
            Row($"Workshop {i + 1}", s.Workshops[i].Placed ? $"Workshop {Roman(s.Workshops[i].Level + 1)}" + (s.Workshops[i].UnderConstruction ? ", under construction" : string.Empty) : "not built");
        foreach (var e in s.Expeditions)
            Row($"Granary {e.Granary}", e.ExpeditionId == 0 ? "idle" : $"on an expedition to {e.Destination} ({e.DaysRemaining} day(s) left)");
        Row("Cropland", Describe(s.Farm));
        Row("Pasture", Describe(s.Pasture));

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Raw island data (for debugging)"))
        {
            ImGui.TextColored(Muted, "Offset  bytes");
            for (var i = 0; i < s.RawIslandState.Length; i += 32)
            {
                var line = s.RawIslandState.Substring(i, Math.Min(32, s.RawIslandState.Length - i));
                var spaced = string.Join(" ", Enumerable.Range(0, line.Length / 2).Select(b => line.Substring(b * 2, 2)));
                ImGui.TextUnformatted($"{i / 2:X4}    {spaced}");
            }

            if (ImGui.Button("Copy raw data")) ImGui.SetClipboardText(s.RawIslandState);
        }
    }

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };

    private void DrawPlan()
    {
        var cfg = plugin.Configuration;
        var changed = false;
        var gather = cfg.AllowGathering;
        if (ImGui.Checkbox("Count materials I can still gather", ref gather)) { cfg.AllowGathering = gather; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("On: the plan may use gatherable materials you're short on, and lists what to gather.\nOff: only what's in your isleventory right now.");
        ImGui.SameLine();
        var favors = cfg.PrioritizeFavors;
        if (ImGui.Checkbox("Prioritize Felicitous Favors", ref favors)) { cfg.PrioritizeFavors = favors; changed = true; }
        if (changed)
        {
            cfg.Save();
            plugin.Replan(true);
        }

        var plan = plugin.Week;
        if (plan == null)
        {
            ImGui.TextColored(Muted, plugin.Planning ? "Planning the week..." : "No plan yet.");
            return;
        }

        if (plugin.Planning) { ImGui.SameLine(); ImGui.TextColored(Muted, "(updating...)"); }

        foreach (var n in plan.Notes) ImGui.TextColored(Warn, n);
        if (plan.Days.Count == 0) return;

        var range = plan.Days.Count == 1 ? $"cycle {plan.Days[0].Cycle + 1}" : $"cycles {string.Join(", ", plan.Days.Select(d => d.Cycle + 1))}";
        ImGui.TextColored(Good, $"This week: about {plan.Total:N0} cowries over {range}. Groove {plugin.Snapshot?.Groove} -> {plan.GrooveAfter}.");
        ImGui.TextColored(Muted, $"Peaks: {plan.PeaksKnown} known, {plan.PeaksPartly} narrowed down, {plan.PeaksUnknown} unknown. Open the Isleworks agenda once each cycle to sharpen the forecast.");
        if (plan.RestAdvice != null) ImGui.TextColored(Warn, plan.RestAdvice);
        if (plan.FavorItemsBoosted.Count > 0)
            ImGui.TextColored(Muted, "Felicitous Favors: " + string.Join(", ", plan.FavorItemsBoosted));

        for (var i = 0; i < plan.Days.Count; i++)
        {
            var day = plan.Days[i];
            ImGui.Spacing();
            var title = $"Cycle {day.Cycle + 1} ({DayName(day.Cycle)}): about {day.Total:N0} cowries" + (i == 0 ? "  - set this one now" : "  - preview");
            if (!ImGui.CollapsingHeader($"{title}###day{day.Cycle}", i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) continue;
            if (i > 0) ImGui.TextColored(Muted, "Later cycles get re-planned as each item's peak is confirmed, so check back before setting them.");
            DrawDay(day);
        }

        if (plan.Shortfalls.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warn, "Gather for this week's plan:");
            foreach (var sf in plan.Shortfalls)
                ImGui.BulletText($"{sf.Need - sf.Have} {sf.Name} (have {sf.Have}, plan uses {sf.Need})" + (sf.Gatherable ? string.Empty : " - not gatherable!"));
        }
    }

    private static void DrawDay(DayPlan day)
    {
        // Workshops with the same agenda are shown once.
        foreach (var g in day.Workshops.GroupBy(w => string.Join("|", w.Crafts.Select(c => c.Craft.Id))))
        {
            var list = g.ToList();
            var first = list[0];
            var label = list.Count == 1 ? $"Workshop {first.Workshop}" : "Workshops " + string.Join(", ", list.Select(w => w.Workshop));
            ImGui.TextUnformatted($"{label}  ({first.Hours} h, about {list.Sum(w => w.Total):N0} cowries)");
            if (!ImGui.BeginTable($"##plan{day.Cycle}_{first.Workshop}", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders)) continue;
            ImGui.TableSetupColumn("Start", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Hours", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Bonus", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Cowries", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var c in first.Crafts)
            {
                ImGui.TableNextRow();
                Cell($"+{c.StartHour} h");
                Cell(c.Craft.Name);
                Cell(c.Craft.Hours.ToString());
                Cell(c.Efficient ? "efficient x2" : "-");
                Cell($"{c.ValueEach} x{c.Count} = {c.Total}");
            }

            ImGui.EndTable();
        }
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
        if (!ImGui.BeginTable("##crafts", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var h in new[] { "Item", "Rank", "Hours", "Value", "Themes", "Materials", "Supply / demand", "Popularity (next)", "Peak" })
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
            Cell(plugin.Forecasts.TryGetValue(c.Id, out var f) ? PeakModel.Describe(f) : "-");
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

        foreach (var season in new[] { "Current", "Next", "Last" })
        {
            var list = s.Favors.Where(f => f.Season == season).ToList();
            if (list.Count == 0) continue;
            ImGui.Spacing();
            ImGui.TextUnformatted(season + " season");
            foreach (var f in list)
            {
                ImGui.BulletText($"{f.Name}: shipped {f.Delivered}/{f.Required}, scheduled {f.Scheduled}"
                                   + (f.Shipped ? ", done" : string.Empty) + (f.Bonus ? ", bonus" : string.Empty));
            }
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
