using System;
using System.Collections.Generic;
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
        Tab("Cropland", DrawCropland);
        Tab("Pasture", DrawPasture);
        Tab("Find", () => DrawFind(s));
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
        for (var i = 0; i < s.GranaryBuildings.Count; i++)
        {
            var b = s.GranaryBuildings[i];
            var e = s.Expeditions.FirstOrDefault(x => x.Granary == i + 1);
            var text = !b.Placed ? "not built" : $"Granary {Roman(b.Level + 1)}" + (b.UnderConstruction ? $", under construction ({b.HoursToCompletion} h left)" : string.Empty);
            if (b.Placed && e != null) text += e.ExpeditionId == 0 ? ", idle" : $", on an expedition to {e.Destination} ({e.DaysRemaining} day(s) left)";
            Row($"Granary {i + 1}", text);
        }
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
        ImGui.TextColored(Muted, "This plan stays put for today; it's re-planned once a day with the new Supply & Demand reading.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Re-plan now")) plugin.NewWeekPlan();
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

        DrawGranaryAdvice();

        if (plan.Shortfalls.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warn, "Gather for this week's plan:");
            foreach (var sf in plan.Shortfalls)
                ImGui.BulletText($"{sf.Need - sf.Have} {sf.Name} (have {sf.Have}, plan uses {sf.Need})" + (sf.Gatherable ? string.Empty : " - not gatherable!"));
        }
    }

    private bool showAllCrops;
    private uint highlightCrop;
    private uint nextHighlight;

    private static ImTextureID GameIcon(uint icon) =>
        Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(icon)).GetWrapOrEmpty().Handle;

    private static void Icon(uint icon, float size)
    {
        if (icon == 0)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(icon)).GetWrapOrEmpty();
        ImGui.Image(tex.Handle, new Vector2(size));
    }

    private void DrawCropland()
    {
        var plan = plugin.Crops;
        var snap = plugin.Snapshot;
        if (plan == null || snap == null || snap.FarmPlots.Count == 0)
        {
            ImGui.TextColored(Muted, plugin.Planning ? "Working it out..." : "Cropland data loads on your island. Open your Isleventory once too, so EasyIsle knows your produce and seeds.");
            return;
        }

        foreach (var a in plan.Alerts) ImGui.TextColored(Warn, a);
        if (plan.Alerts.Count > 0) ImGui.Spacing();

        highlightCrop = nextHighlight;
        nextHighlight = 0;
        DrawPlotGrid(plan, snap);

        var changes = plan.Plots.Where(p => p.Change).ToList();
        ImGui.Spacing();
        ImGui.TextColored(Muted, "The planting list stays put until those plots are planted.");
        ImGui.SameLine();
        if (ImGui.SmallButton("New planting list")) plugin.NewCropPlan();
        if (changes.Count == 0)
        {
            ImGui.TextColored(Good, plan.Needs.Count == 0
                ? "Open your Isleventory once so EasyIsle knows your produce and seeds."
                : "Every plot is growing what it should. Nothing to replant.");
        }
        else
        {
            var empty = changes.Count(c => c.Plot.CropPouchId == 0);
            ImGui.TextUnformatted(empty == changes.Count
                ? $"Plant {changes.Count} plot(s):"
                : $"Plant or replant {changes.Count} plot(s) (if the gardener tends one, dismiss it first, then plant and entrust again):");
            ImGui.TextColored(Muted, "Crops your workshops or feed will run short of get plots first; the rest go to whatever you have least of.");
            // One seed at a time: group plots by what to plant. Hovering a group lights its plots up.
            foreach (var g in changes.GroupBy(c => c.Recommended!.PouchId).OrderBy(g => g.Min(c => PlotLayout.WalkOrder(c.Slot))))
            {
                var crop = g.First().Recommended!;
                ImGui.TextUnformatted("  ");
                ImGui.SameLine();
                Icon(crop.SeedIcon != 0 ? crop.SeedIcon : crop.Icon, ImGui.GetTextLineHeight());
                ImGui.SameLine();
                ImGui.TextUnformatted($"{crop.Name} ({g.Count()}):");
                ImGui.SameLine();
                ImGui.TextUnformatted("plots " + string.Join(", ", g.Select(c => c.Slot).OrderBy(PlotLayout.WalkOrder)));
                if (ImGui.IsItemHovered()) nextHighlight = crop.PouchId;
                var replacing = g.Where(c => c.Plot.CropPouchId != 0).ToList();
                if (replacing.Count > 0)
                    ImGui.TextColored(Muted, "      replaces " + string.Join(", ", replacing.Select(c => $"{c.Plot.Crop} on {c.Slot}")));
            }
        }

        ImGui.Spacing();
        DrawFeed(plan, snap);

        ImGui.Spacing();
        ImGui.Checkbox("Show every crop", ref showAllCrops);
        if (!ImGui.BeginTable("##crops", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders)) return;
        ImGui.TableSetupColumn("Crop");
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("With gardener", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Workshops / wk", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Feed / wk", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Lasts", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Plots now > best", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Seeds", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();
        foreach (var n in plan.Needs.OrderBy(n => n.WeeksLeft ?? double.MaxValue))
        {
            if (!showAllCrops && n.PlotsNow == 0 && n.PlotsRecommended == 0 && n.PerWeek < 0.5) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Icon(n.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            ImGui.TextUnformatted(n.Name);
            Cell(n.Stock.ToString());
            Cell(n.Waiting > 0 ? n.Waiting.ToString() : "-");
            Cell(n.WorkshopPerWeek >= 0.5 ? n.WorkshopPerWeek.ToString("0") : "-");
            Cell(n.FeedPerWeek >= 0.5 ? n.FeedPerWeek.ToString("0") : "-");
            Cell(n.WeeksLeft is { } w ? (w < 1 ? "under a week" : $"{w:0.#} weeks") : "-");
            Cell(n.PlotsNow == n.PlotsRecommended ? n.PlotsNow.ToString() : $"{n.PlotsNow} > {n.PlotsRecommended}");
            Cell(n.Seeds.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawPlotGrid(CropPlan plan, IslandSnapshot snap)
    {
        const float cell = 52f;
        const float gap = 6f;
        var plots = plan.Plots;

        // Plot positions as checked in game (see PlotLayout).
        const int cols = 5;
        var rowCount = (plots.Count + cols - 1) / cols;
        var spots = new Dictionary<int, Vector2>();
        foreach (var p in plots)
        {
            var row = PlotLayout.Row(p.Slot);
            var col = PlotLayout.Column(p.Slot);
            spots[p.Slot] = new Vector2(col * (cell + gap), (rowCount - 1 - row) * (cell + gap));
        }

        var area = new Vector2(cols * (cell + gap), rowCount * (cell + gap));
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        foreach (var a in plots)
        {
            var min = origin + spots[a.Slot];
            var max = min + new Vector2(cell);
            var bg = a.Plot.CropPouchId == 0 ? new Vector4(0.25f, 0.2f, 0.15f, 1f)
                : a.Change ? new Vector4(0.45f, 0.3f, 0.1f, 1f)
                : new Vector4(0.15f, 0.3f, 0.15f, 1f);
            draw.AddRectFilled(min, max, ImGui.GetColorU32(bg), 4f);
            // Growing: the crop. To plant: the seed (small in the corner if something else grows there now).
            var cropIcon = snap.Isleventory.FirstOrDefault(p => p.PouchId == a.Plot.CropPouchId)?.Icon ?? 0;
            var seedIcon = a.Change ? a.Recommended!.SeedIcon : 0u;
            if (cropIcon != 0)
                draw.AddImage(GameIcon(cropIcon), min + new Vector2(8, 8), max - new Vector2(8, 8));
            if (seedIcon != 0)
            {
                if (cropIcon == 0) draw.AddImage(GameIcon(seedIcon), min + new Vector2(8, 8), max - new Vector2(8, 8));
                else draw.AddImage(GameIcon(seedIcon), max - new Vector2(24, 24), max - new Vector2(2, 2));
            }

            draw.AddText(min + new Vector2(3, 1), ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), a.Slot.ToString());
            if (a.Plot.Yield > 0)
            {
                var t = a.Plot.Yield.ToString();
                var size = ImGui.CalcTextSize(t);
                draw.AddText(new Vector2(max.X - size.X - 3, min.Y + 1), ImGui.GetColorU32(a.Plot.Yield >= 50 ? Warn : Good), t);
            }

            if (a.Change && highlightCrop != 0 && a.Recommended!.PouchId == highlightCrop)
                draw.AddRect(min - new Vector2(2), max + new Vector2(2), ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), 4f, ImDrawFlags.None, 3f);
            else if (a.Change)
                draw.AddRect(min, max, ImGui.GetColorU32(Warn), 4f, ImDrawFlags.None, 2f);

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##plot{a.Slot}", new Vector2(cell));
            if (ImGui.IsItemHovered())
            {
                var text = $"Plot {a.Slot}: {(a.Plot.CropPouchId == 0 ? "empty" : a.Plot.Crop)}";
                if (a.Plot.CropPouchId != 0) text += $"\nGrowth {a.Plot.Growth}, water {a.Plot.Water}";
                text += a.Plot.UnderCare ? "\nGardener: tending" : a.Plot.CareHalted ? "\nGardener: stopped" : "\nGardener: not entrusted";
                if (a.Plot.Yield > 0) text += $"\nWaiting with the gardener: {a.Plot.Yield}";
                if (a.Change) text += a.Plot.CropPouchId == 0 ? $"\nPlant {a.Recommended!.Name}" : $"\nReplant with {a.Recommended!.Name}";
                ImGui.SetTooltip(text);
            }
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0, area.Y + gap));
        ImGui.TextColored(Muted, "Seed icon = plant that seed here. Crop icon = growing. Orange border = still to plant (hover a crop below to light up its plots). Top-right number = produce waiting with the gardener.");
    }

    private void DrawFeed(CropPlan plan, IslandSnapshot snap)
    {
        ImGui.TextUnformatted($"Pasture: {plan.Animals} animal(s).");
        if (snap.FeedRecipes.Count == 0) return;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        var current = plan.Feed?.OutputName ?? "-";
        if (ImGui.BeginCombo("Feed##feed", current))
        {
            foreach (var r in snap.FeedRecipes.OrderByDescending(r => r.Order))
            {
                if (ImGui.Selectable(r.OutputName, r.OutputPouchId == plan.Feed?.OutputPouchId))
                {
                    plugin.Configuration.FeedPouchId = r.OutputPouchId;
                    plugin.NewCropPlan();
                }
            }

            ImGui.EndCombo();
        }

        if (plan.Feed != null && plan.Animals > 0)
        {
            var crafts = plan.FeedPerWeek / plan.Feed.Yield;
            var mats = string.Join(", ", plan.Feed.Materials.Select(m => $"{m.Amount * crafts:0} {m.Name}"));
            ImGui.TextColored(Muted, $"About {plan.FeedPerWeek:0} {plan.Feed.OutputName} a week (one per animal per day, {plan.Feed.Yield} per craft) takes {mats}.");
            if (plan.AnyProducePerWeek > 0)
            {
                var weeks = plan.SpareProduce / plan.AnyProducePerWeek;
                ImGui.TextColored(Muted, weeks >= 6
                    ? $"Any produce works, and you have about {plan.SpareProduce:N0} spare (about {weeks:0} weeks of feed), so no plots are set aside for it. Make feed from whatever you have most of."
                    : $"Spare produce covers only about {weeks:0.#} weeks of feed, so some plots grow extra for it (Feed / wk column).");
            }
        }
    }

    private void DrawPasture()
    {
        var p = plugin.Pasture;
        var snap = plugin.Snapshot;
        if (p == null || snap == null || snap.AnimalKinds.Count == 0)
        {
            ImGui.TextColored(Muted, plugin.Planning ? "Working it out..." : "Pasture data loads on your island.");
            return;
        }

        var names = snap.Isleventory.ToDictionary(x => x.PouchId, x => x.Name);
        string Leaves(AnimalKind k) => $"{names.GetValueOrDefault(k.CommonPouchId)} + {names.GetValueOrDefault(k.BonusPouchId)}";

        var now = DateTimeOffset.UtcNow;
        ImGui.TextUnformatted($"Pasture: {p.Owned}/{p.Capacity} animals.");
        DrawWeather(now);
        if (p.Capture.Count == 0 && p.Release.Count == 0)
        {
            ImGui.TextColored(Good, "Your animals cover what your workshops need. Nothing to catch or release.");
        }
        else
        {
            ImGui.TextColored(Muted, "This advice stays put until you've made these catches and releases.");
            ImGui.SameLine();
            if (ImGui.SmallButton("New pasture advice")) plugin.NewPasturePlan();
        }

        foreach (var r in p.Release)
        {
            ImGui.Spacing();
            var who = string.Join(", ", r.Animals.Select(a => a.Nickname.Length > 0 ? $"{a.Nickname} ({a.Species})" : a.Species));
            ImGui.TextColored(Warn, $"Release {r.Count}: {who}");
            ImGui.TextColored(Muted, $"    {Leaves(r.Kind)}: {r.Why}.");
        }

        foreach (var c in p.Capture)
        {
            ImGui.Spacing();
            Icon(c.Kind.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            var main = c.Spots.FirstOrDefault();
            ImGui.TextUnformatted($"Catch {c.Count}: {(main != null ? main.Name : $"an animal leaving {Leaves(c.Kind)}")}  ({c.Kind.Size}, {AnimalAdvisor.Restraint(c.Kind.Size)})");
            ImGui.TextColored(Muted, $"    {Leaves(c.Kind)} {c.Why}.");
            if (main != null) DrawSpot(main, "Found", now);
            foreach (var alt in c.Spots.Skip(1)) DrawSpot(alt, "Or " + alt.Name, now);
        }

        ImGui.Spacing();
        if (ImGui.BeginTable("##leavings", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("Leaving");
            ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Workshops / wk", ImGuiTableColumnFlags.WidthFixed, 105);
            ImGui.TableSetupColumn("Animals give / wk", ImGuiTableColumnFlags.WidthFixed, 125);
            ImGui.TableSetupColumn("After advice", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Lasts", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var l in p.Leavings.OrderBy(l => l.WeeksLeft ?? double.MaxValue))
            {
                ImGui.TableNextRow();
                Cell(l.Name);
                ImGui.TableNextColumn();
                if (l.AtCap) ImGui.TextColored(Warn, $"{l.Stock} (cap)");
                else ImGui.TextUnformatted(l.Stock.ToString());
                Cell(l.WorkshopPerWeek >= 0.5 ? l.WorkshopPerWeek.ToString("0") : "-");
                Cell(l.SupplyNow.ToString("0"));
                Cell(Math.Abs(l.SupplyAfter - l.SupplyNow) < 0.5 ? "-" : l.SupplyAfter.ToString("0"));
                Cell(l.WeeksLeft is { } w ? (w < 1 ? "under a week" : $"{w:0.#} weeks") : "-");
            }

            ImGui.EndTable();
        }

        ImGui.TextColored(Muted, "Each animal leaves its common leaving daily and its bonus leaving 80% of days when Gleeful (Premium Island Greenfeed): about 7 + 5.6 a week. The isleventory holds 999 of each.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"Your animals ({snap.Animals.Count})###animals"))
        {
            foreach (var a in snap.Animals.OrderBy(a => a.Species))
            {
                var name = a.Nickname.Length > 0 ? $"{a.Nickname} ({a.Species})" : a.Species;
                ImGui.BulletText($"{name}: {string.Join(" + ", a.Leavings)}" + (a.UnderCare ? ", with the caretaker" : string.Empty));
            }
        }
    }

    private readonly Dictionary<string, (long Hour, SpawnWindow? Window)> windowCache = [];
    private string findFilter = string.Empty;

    /// <summary>When a spot's time and weather next hold; cached per Eorzea hour.</summary>
    private SpawnWindow? WindowFor(SpeciesSpot spot, DateTimeOffset now)
    {
        var hour = (long)Math.Floor(IslandClock.EorzeaHours(now));
        if (windowCache.TryGetValue(spot.Name, out var c) && c.Hour == hour) return c.Window;
        var w = IslandClock.NextWindow(spot, plugin.IslandWeather, now);
        if (w != null || plugin.IslandWeather.Count > 0) windowCache[spot.Name] = (hour, w);
        return w;
    }

    private void DrawSpot(SpeciesSpot spot, string label, DateTimeOffset now)
    {
        ImGui.TextColored(Muted, $"    {label} at ({spot.X:0.#}, {spot.Y:0.#}), {spot.When}.");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Map##{label}{spot.Name}")) plugin.FlagOnMap(spot.X, spot.Y);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Puts a flag on this spot and opens the map.");
        if (!spot.Rare) return;
        var w = WindowFor(spot, now);
        ImGui.TextColored(w != null && w.OpenAt(now) ? Good : Muted, "      " + IslandClock.Describe(w, now, true));
    }

    private void DrawWeather(DateTimeOffset now)
    {
        var rates = plugin.IslandWeather;
        if (rates.Count == 0)
        {
            ImGui.TextColored(Muted, $"Eorzea time {IslandClock.EorzeaClock(now)}. Island weather isn't available.");
            return;
        }

        var f = IslandClock.Forecast(rates, now, 8);
        var changes = new List<(DateTimeOffset Start, WeatherChance Weather)>();
        for (var i = 1; i < f.Count && changes.Count < 4; i++)
            if (f[i].Weather.Id != f[i - 1].Weather.Id) changes.Add(f[i]);
        var next = changes.Count == 0 ? "no change for a while" : string.Join(", ", changes.Select(x => $"{x.Weather.Name} in {IslandClock.Span(x.Start - now)}"));
        ImGui.TextUnformatted($"Eorzea time {IslandClock.EorzeaClock(now)}, {f[0].Weather.Name}.");
        ImGui.TextColored(Muted, $"Coming up: {next}.");
    }

    private void DrawFind(IslandSnapshot s)
    {
        var now = DateTimeOffset.UtcNow;
        DrawWeather(now);
        ImGui.TextColored(Muted, "Animals for now; materials and seeds come next. \"Map\" puts the game's flag on the spot.");
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##findfilter", "Search name or leaving...", ref findFilter, 64);

        var owned = s.Animals.GroupBy(a => a.Species).ToDictionary(g => g.Key, g => g.Count());
        var rows = SpeciesTable.All
            .Where(a => findFilter.Length == 0
                        || a.Name.Contains(findFilter, StringComparison.OrdinalIgnoreCase)
                        || a.Common.Contains(findFilter, StringComparison.OrdinalIgnoreCase)
                        || a.Bonus.Contains(findFilter, StringComparison.OrdinalIgnoreCase))
            .Select(a => (Spot: a, Window: a.Rare ? WindowFor(a, now) : null))
            .OrderBy(r => !r.Spot.Rare ? 2 : r.Window == null ? 3 : r.Window.OpenAt(now) ? 0 : 1)
            .ThenBy(r => r.Window?.Start ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Spot.Name)
            .ToList();

        if (!ImGui.BeginTable("##find", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Animal");
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 35);
        ImGui.TableSetupColumn("Leaves", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Needs", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Chance", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("You have", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableHeadersRow();
        foreach (var (spot, w) in rows)
        {
            ImGui.TableNextRow();
            Cell(spot.Name);
            Cell(spot.Size.ToString());
            Cell($"{spot.Common} + {spot.Bonus}");
            Cell(spot.Rare ? spot.When : "-");
            ImGui.TableNextColumn();
            if (!spot.Rare) ImGui.TextColored(Muted, "always");
            else if (w == null) ImGui.TextColored(Muted, "unknown");
            else if (w.OpenAt(now)) ImGui.TextColored(Good, $"now, {IslandClock.Span(w.End - now)} left");
            else ImGui.TextUnformatted($"in {IslandClock.Span(w.Start - now)}");
            Cell(owned.TryGetValue(spot.Name, out var n) ? n.ToString() : "");
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Map##find{spot.Name}")) plugin.FlagOnMap(spot.X, spot.Y);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"({spot.X:0.#}, {spot.Y:0.#})");
        }

        ImGui.EndTable();
    }

    private void DrawGranaryAdvice()
    {
        if (plugin.Granary is not { } g) return;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Granary expeditions###granary", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (g.advice.Count == 0)
        {
            ImGui.TextColored(Muted, "Open a granary's expedition window once (Select Terrain) so EasyIsle can see your expedition areas.");
            return;
        }

        foreach (var a in g.advice)
        {
            ImGui.TextUnformatted($"Granary {a.Granary}: {a.Action}");
            ImGui.TextColored(Muted, "    " + a.Reason);
        }


        if (!ImGui.BeginTable("##rares", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders)) return;
        ImGui.TableSetupColumn("Rare material");
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Plan uses / week", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Lasts", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();
        foreach (var r in g.rares.OrderBy(r => r.WeeksLeft ?? double.MaxValue))
        {
            ImGui.TableNextRow();
            Cell(r.Name);
            Cell(r.Have.ToString());
            Cell(r.WeeklyUse > 0 ? r.WeeklyUse.ToString() : "-");
            Cell(r.WeeksLeft is { } w ? (w < 1 ? "under a week" : $"about {w:0.#} weeks") : "-");
        }

        ImGui.EndTable();
    }

    private void DrawDay(DayPlan day)
    {
        // Workshops with the same agenda are shown once.
        foreach (var g in day.Workshops.GroupBy(w => string.Join("|", w.Crafts.Select(c => c.Craft.Id))))
        {
            var list = g.ToList();
            var first = list[0];
            var label = list.Count == 1 ? $"Workshop {first.Workshop}" : "Workshops " + string.Join(", ", list.Select(w => w.Workshop));
            ImGui.TextUnformatted($"{label}  ({first.Hours} h, about {list.Sum(w => w.Total):N0} cowries)");
            ImGui.SameLine();
            var canCopy = AgendaClipboard.Available;
            if (!canCopy) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Copy for the game##copy{day.Cycle}_{first.Workshop}"))
            {
                var error = AgendaClipboard.Copy(first.Crafts);
                status = error ?? $"Copied. In the agenda, right-click {(list.Count == 1 ? $"workshop {first.Workshop}" : "each of workshops " + string.Join(", ", list.Select(w => w.Workshop)))} on cycle {day.Cycle + 1} and choose Apply Copied Agenda.";
            }

            if (!canCopy) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canCopy ? "Puts this agenda in the game's agenda clipboard. Paste it with right-click > Apply Copied Agenda." : "Open the Isleworks agenda to copy.");
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

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Agenda clipboard readout (for debugging)"))
        {
            var text = AgendaReadout();
            ImGui.TextUnformatted(text);
            if (ImGui.Button("Copy readout")) ImGui.SetClipboardText(text);
        }
    }

    /// <summary>Raw view of the game's copied agenda and the displayed cycle's workshops.</summary>
    private static unsafe string AgendaReadout()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null || !agent->IsAgentActive()) return "Open the Isleworks agenda first.";
        var data = agent->Data;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"cycle displayed {data->CycleDisplayed}, in progress {data->CycleInProgress}, hour {data->HourSinceCycleStart}, flags {(byte)data->Flags1:X2}/{(byte)data->Flags2:X2}");
        Dump(sb, "copied", &data->CopiedSchedule);
        for (var i = 0; i < data->WorkshopSchedules.Length; i++)
            fixed (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule.WorkshopData* w = &data->WorkshopSchedules[i])
                Dump(sb, $"workshop {i + 1}", w);
        return sb.ToString();
    }

    private static unsafe void Dump(System.Text.StringBuilder sb, string label, FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule.WorkshopData* w)
    {
        sb.AppendLine($"{label}: entries {w->NumScheduleEntries}, efficient {w->NumEfficientCrafts}, slots {w->UsedTimeSlots:X8}");
        for (var i = 0; i < w->EntryData.Length; i++)
        {
            var e = w->EntryData[i];
            if (e.CraftObjectId == 0 && e.Duration == 0) continue;
            sb.AppendLine($"  [{i}] craft {e.CraftObjectId} flags {(uint)e.Flags:X} start {e.StartingSlot} dur {e.Duration} started {e.Started} eff {e.Efficient}");
        }

        var raw = new ReadOnlySpan<byte>(w, 84);
        sb.AppendLine("  raw " + Convert.ToHexString(raw));
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }
}
