using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;

namespace EasyIsle.Windows;

/// <summary>
/// EasyIsle's window, laid out like Outer Voice: a status header, an icon rail on the left and one
/// page at a time on the right.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.55f, 0.9f, 0.55f, 1f);
    private static readonly Vector4 Accent = new(0.55f, 0.42f, 0.85f, 1f);
    private const float RailWidth = 46f;

    private enum Page { Plan, Cropland, Pasture, Granary, Find, Island, Settings }

    private readonly Plugin plugin;
    private Page page = Page.Plan;
    private DateTime lastRead = DateTime.MinValue;
    private bool availableOnly = true;
    private bool discoveredOnly = true;
    private string status = string.Empty;

    public MainWindow(Plugin plugin) : base("EasyIsle###EasyIsleMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 420) };
        Size = new Vector2(920, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => plugin.Refresh();

    public override void Draw()
    {
        if (plugin.Configuration.AutoRefresh && DateTime.UtcNow - lastRead > TimeSpan.FromSeconds(3))
        {
            plugin.Refresh();
            lastRead = DateTime.UtcNow;
        }

        DrawHeader();
        ImGui.Separator();

        ImGui.BeginChild("##rail", new Vector2(RailWidth, 0));
        RailButton(Page.Plan, FontAwesomeIcon.ClipboardList, "Plan\nThis week's workshop agendas, ready to paste into the game.");
        RailButton(Page.Cropland, FontAwesomeIcon.Seedling, "Cropland\nWhat to plant where, and feed.");
        RailButton(Page.Pasture, FontAwesomeIcon.Paw, "Pasture\nWhich animals to keep, catch and release.");
        RailButton(Page.Granary, FontAwesomeIcon.Ship, "Granary\nWhere to send your expeditions.");
        RailButton(Page.Find, FontAwesomeIcon.MapMarkedAlt, "Find\nWhere to gather materials and seeds and catch animals, with map flags.");
        RailButton(Page.Island, FontAwesomeIcon.Home, "Island\nEverything EasyIsle reads about your island.");
        RailButton(Page.Settings, FontAwesomeIcon.Cog, "Settings\nRefreshing and troubleshooting.");
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##page", Vector2.Zero);
        var s = plugin.Snapshot;
        if (s == null && page != Page.Settings)
            Note("Nothing read yet. Head to your island and open this window there.");
        else
            switch (page)
            {
                case Page.Plan: DrawPlan(); break;
                case Page.Cropland: DrawCropland(); break;
                case Page.Pasture: DrawPasture(); break;
                case Page.Granary: DrawGranaryAdvice(); break;
                case Page.Find: DrawFind(s!); break;
                case Page.Island: DrawIslandPage(s!); break;
                case Page.Settings: DrawSettings(); break;
            }

        ImGui.EndChild();
    }

    private void RailButton(Page target, FontAwesomeIcon icon, string tooltip)
    {
        var active = page == target;
        var size = new Vector2(RailWidth - 8, RailWidth - 8);
        var clicked = active
            ? ImGuiComponents.IconButton((int)target, icon, Accent, Accent, Accent, size)
            : ImGuiComponents.IconButton((int)target, icon, new Vector4(0, 0, 0, 0), null, null, size);
        if (clicked) page = target;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.Spacing();
    }

    // ---- Header: the island at a glance ---------------------------------------------------------

    private void DrawHeader()
    {
        var s = plugin.Snapshot;
        if (ImGuiComponents.IconButton("##refresh", FontAwesomeIcon.Sync)) plugin.Refresh();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Read the island again now.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (s == null)
            ImGui.TextColored(Bad, "No island data yet");
        else if (!s.OnIsland)
            ImGui.TextColored(Muted, $"Rank {s.Rank}  ·  not on your island (showing the last reading)");
        else
            ImGui.TextColored(Good, $"Rank {s.Rank}  ·  Cycle {s.CycleDay + 1} ({DayName(s.CycleDay)})  ·  Groove {s.Groove}/{s.GrooveMax}");

        if (s != null && s.Problems.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Warn, $"  {s.Problems.Count} thing(s) to load");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", s.Problems));
        }

        if (plugin.Planning)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "  planning...");
        }

        // Right side: Eorzea time and the island's weather.
        var now = DateTimeOffset.UtcNow;
        var weather = IslandClock.WeatherAt(plugin.IslandWeather, now);
        var right = $"ET {IslandClock.EorzeaClock(now)}" + (weather != null ? $"  ·  {weather.Name}" : string.Empty);
        var width = ImGui.CalcTextSize(right).X;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 10, ImGui.GetWindowContentRegionMax().X - width));
        ImGui.TextColored(Muted, right);

        if (plugin.ReadError != null) Colored(Bad, "Couldn't read the island: " + plugin.ReadError);
        if (status.Length > 0) Colored(Muted, status);
    }

    // ---- Settings -------------------------------------------------------------------------------

    private void DrawSettings()
    {
        var cfg = plugin.Configuration;
        if (Group("Refreshing"))
        {
            var auto = cfg.AutoRefresh;
            if (ImGui.Checkbox("Keep reading the island while this window is open", ref auto))
            {
                cfg.AutoRefresh = auto;
                cfg.Save();
            }

            HelpMarker("Every few seconds. Off: only when the window opens or you press the refresh button in the header.");
        }

        if (Group("Troubleshooting", false))
        {
            Note("A snapshot is everything EasyIsle read about your island, as text. Handy when something looks wrong.");
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

            if (plugin.Snapshot is { } s && ImGui.CollapsingHeader("Raw island data"))
                DrawRaw(s);
        }
    }

    // ---- Island page: what EasyIsle reads ------------------------------------------------------

    private void DrawIslandPage(IslandSnapshot s)
    {
        if (!ImGui.BeginTabBar("##islandtabs")) return;
        SubTab("Overview", () => DrawIsland(s));
        SubTab($"Crafts ({s.Crafts.Count(c => c.Available)})", () => DrawCrafts(s));
        SubTab("Isleventory", () => DrawIsleventory(s));
        SubTab("Granary, crops, animals", () => DrawProduce(s));
        SubTab("Favors", () => DrawFavors(s));
        SubTab("Agenda", () => DrawAgenda(s));
        ImGui.EndTabBar();
    }

    private static void SubTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        if (ImGui.BeginChild("##tabbody")) draw();
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    // ---- Shared look ----------------------------------------------------------------------------

    /// <summary>Wrapped, coloured text (wraps at the window or table cell edge).</summary>
    private static void Colored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private static void Plain(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void Note(string text) => Colored(Muted, text);

    /// <summary>A muted capitals heading with a rule under it (and an optional (?) beside it).</summary>
    private static void Section(string title, string? help = null)
    {
        ImGui.Spacing();
        ImGui.TextColored(Muted, title.ToUpperInvariant());
        if (help != null) HelpMarker(help);
        ImGui.Separator();
    }

    /// <summary>A collapsible group tinted with the accent colour.</summary>
    private static bool Group(string title, bool openByDefault = true)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.35f));
        var open = ImGui.CollapsingHeader(title, openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ImGui.PopStyleColor();
        return open;
    }

    /// <summary>A small map-pin button (the game's flag goes on the spot).</summary>
    private static bool MapButton(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 1));
        var clicked = ImGuiComponents.IconButton($"##map{id}", FontAwesomeIcon.MapMarkerAlt);
        ImGui.PopStyleVar();
        return clicked;
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextColored(Muted, "(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static void DrawIsland(IslandSnapshot s)
    {
        if (s.Problems.Count > 0)
        {
            Colored(Warn, "Missing data:");
            foreach (var p in s.Problems) ImGui.BulletText(p);
            ImGui.Spacing();
        }
        else
        {
            Colored(Good, "Everything EasyIsle needs is loaded.");
        }

        Section("Island");
        Row("On the island", s.OnIsland ? "yes" : "no");
        Row("Island rank", $"{s.Rank} ({s.Xp:N0} xp)");
        Row("Groove", $"{s.Groove} / {s.GrooveMax}");
        Row("Cycle day", $"{s.CycleDay + 1} of 7 ({DayName(s.CycleDay)})");
        Row("Rest days", s.RestDays.Count == 0 ? "not loaded" : string.Join(", ", s.RestDays.Select(d => d < 7 ? $"{DayName(d)} (this week)" : $"{DayName(d - 7)} (next week)")));
        Row("Supply & Demand", s.DemandValid ? $"loaded (popularity set {s.PopularityRow}, next {s.NextPopularityRow})" : "not loaded");
        Section("Buildings");
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

    }

    private static void DrawRaw(IslandSnapshot s)
    {
        Colored(Muted, "Offset  bytes");
        for (var i = 0; i < s.RawIslandState.Length; i += 32)
        {
            var line = s.RawIslandState.Substring(i, Math.Min(32, s.RawIslandState.Length - i));
            var spaced = string.Join(" ", Enumerable.Range(0, line.Length / 2).Select(b => line.Substring(b * 2, 2)));
            Plain($"{i / 2:X4}    {spaced}");
        }

        if (ImGui.Button("Copy raw data")) ImGui.SetClipboardText(s.RawIslandState);
    }

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };

    private void DrawPlan()
    {
        var cfg = plugin.Configuration;
        var changed = false;
        var gather = cfg.AllowGathering;
        if (ImGui.Checkbox("Count materials I can still gather", ref gather)) { cfg.AllowGathering = gather; changed = true; }
        HelpMarker("On: the plan may use gatherable materials you're short on, and lists what to gather.\nOff: only what's in your isleventory right now.");
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12, 0));
        ImGui.SameLine();
        var favors = cfg.PrioritizeFavors;
        if (ImGui.Checkbox("Prioritize Felicitous Favors", ref favors)) { cfg.PrioritizeFavors = favors; changed = true; }
        HelpMarker("Fits the Felicitous Favors items into the plan even when something else would earn a few more cowries.");
        if (changed)
        {
            cfg.Save();
            plugin.Replan(true);
        }

        var plan = plugin.Week;
        if (plan == null)
        {
            Note(plugin.Planning ? "Planning the week..." : "No plan yet.");
            return;
        }

        foreach (var n in plan.Notes) Colored(Warn, n);
        if (plan.Days.Count == 0) return;

        var range = plan.Days.Count == 1 ? $"cycle {plan.Days[0].Cycle + 1}" : $"cycles {string.Join(", ", plan.Days.Select(d => d.Cycle + 1))}";
        Section("This week",
            "The plan stays put for today and is re-planned once a day with the new Supply & Demand reading.\n\n" +
            $"Peaks: {plan.PeaksKnown} known, {plan.PeaksPartly} narrowed down, {plan.PeaksUnknown} unknown. Open the Isleworks agenda once each cycle to sharpen the forecast.");
        Colored(Good, $"About {plan.Total:N0} cowries over {range}. Groove {plugin.Snapshot?.Groove} > {plan.GrooveAfter}.");
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Re-plan now")) plugin.NewWeekPlan();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Throw away today's plan and work out a fresh one.");
        if (plan.RestAdvice != null) Colored(Warn, plan.RestAdvice);
        if (plan.FavorItemsBoosted.Count > 0) Note("Felicitous Favors: " + string.Join(", ", plan.FavorItemsBoosted));

        for (var i = 0; i < plan.Days.Count; i++)
        {
            var day = plan.Days[i];
            var title = $"Cycle {day.Cycle + 1} ({DayName(day.Cycle)}): about {day.Total:N0} cowries" + (i == 0 ? "  -  set this one now" : "  -  preview");
            if (!Group($"{title}###day{day.Cycle}", i == 0)) continue;
            if (i > 0) Note("Later cycles get re-planned as each item's peak is confirmed, so check back before setting them.");
            DrawDay(day);
        }

        if (plan.Shortfalls.Count > 0)
        {
            Section("Gather for this week");
            foreach (var sf in plan.Shortfalls)
                Colored(sf.Gatherable ? new Vector4(1, 1, 1, 1) : Warn, $"  {sf.Need - sf.Have} {sf.Name}  (have {sf.Have}, plan uses {sf.Need})" + (sf.Gatherable ? string.Empty : "  -  not gatherable!"));
            Note("The Find page shows where to gather each one.");
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
            Colored(Muted, plugin.Planning ? "Working it out..." : "Cropland data loads on your island. Open your Isleventory once too, so EasyIsle knows your produce and seeds.");
            return;
        }

        foreach (var a in plan.Alerts) Colored(Warn, a);
        if (plan.Alerts.Count > 0) ImGui.Spacing();

        highlightCrop = nextHighlight;
        nextHighlight = 0;
        Section("Plots", "Seed icon: plant that seed here. Crop icon: growing.\nOrange border: still to plant (hover a crop in the planting list to light up its plots).\nTop-right number: produce waiting with the gardener.\nLaid out like your cropland, plot 1 bottom right.");
        DrawPlotGrid(plan, snap);

        var changes = plan.Plots.Where(p => p.Change).ToList();
        Section("Planting list", "The list stays put until those plots are planted.\nCrops your workshops or feed will run short of get plots first; the rest go to whatever you have least of.");
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "New planting list")) plugin.NewCropPlan();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Throw away this list and work out a fresh one.");
        if (changes.Count == 0)
        {
            Colored(Good, plan.Needs.Count == 0
                ? "Open your Isleventory once so EasyIsle knows your produce and seeds."
                : "Every plot is growing what it should. Nothing to replant.");
        }
        else
        {
            var empty = changes.Count(c => c.Plot.CropPouchId == 0);
            Plain(empty == changes.Count
                ? $"Plant {changes.Count} plot(s):"
                : $"Plant or replant {changes.Count} plot(s) (if the gardener tends one, dismiss it first, then plant and entrust again):");
            // One seed at a time: group plots by what to plant. Hovering a group lights its plots up.
            foreach (var g in changes.GroupBy(c => c.Recommended!.PouchId).OrderBy(g => g.Min(c => PlotLayout.WalkOrder(c.Slot))))
            {
                var crop = g.First().Recommended!;
                Plain("  ");
                ImGui.SameLine();
                Icon(crop.SeedIcon != 0 ? crop.SeedIcon : crop.Icon, ImGui.GetTextLineHeight());
                ImGui.SameLine();
                Plain($"{crop.Name} ({g.Count()}):");
                ImGui.SameLine();
                Plain("plots " + string.Join(", ", g.Select(c => c.Slot).OrderBy(PlotLayout.WalkOrder)));
                if (ImGui.IsItemHovered()) nextHighlight = crop.PouchId;
                var replacing = g.Where(c => c.Plot.CropPouchId != 0).ToList();
                if (replacing.Count > 0)
                    Colored(Muted, "      replaces " + string.Join(", ", replacing.Select(c => $"{c.Plot.Crop} on {c.Slot}")));
            }
        }

        Section("Feed");
        DrawFeed(plan, snap);

        Section("Crops");
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
            Plain(n.Name);
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

        ImGui.SetCursorScreenPos(origin + new Vector2(0, area.Y));
        ImGui.Dummy(new Vector2(area.X, 0));
    }

    private void DrawFeed(CropPlan plan, IslandSnapshot snap)
    {
        Plain($"Pasture: {plan.Animals} animal(s).");
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
            Colored(Muted, $"About {plan.FeedPerWeek:0} {plan.Feed.OutputName} a week (one per animal per day, {plan.Feed.Yield} per craft) takes {mats}.");
            if (plan.AnyProducePerWeek > 0)
            {
                var weeks = plan.SpareProduce / plan.AnyProducePerWeek;
                Colored(Muted, weeks >= 6
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
            Colored(Muted, plugin.Planning ? "Working it out..." : "Pasture data loads on your island.");
            return;
        }

        var names = snap.Isleventory.ToDictionary(x => x.PouchId, x => x.Name);
        string Leaves(AnimalKind k) => $"{names.GetValueOrDefault(k.CommonPouchId)} + {names.GetValueOrDefault(k.BonusPouchId)}";

        var now = DateTimeOffset.UtcNow;
        Section($"Pasture: {p.Owned}/{p.Capacity} animals", "Which animals to keep for the leavings this week's agendas use. The pasture only holds so many, so it may suggest swapping one kind for another.\nThis advice stays put until you've made these catches and releases.");
        if (p.Capture.Count == 0 && p.Release.Count == 0)
        {
            Colored(Good, "Your animals cover what your workshops need. Nothing to catch or release.");
        }
        else
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "New pasture advice")) plugin.NewPasturePlan();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Throw away this advice and work out a fresh one.");
        }

        foreach (var r in p.Release)
        {
            ImGui.Spacing();
            var who = string.Join(", ", r.Animals.Select(a => a.Nickname.Length > 0 ? $"{a.Nickname} ({a.Species})" : a.Species));
            Colored(Warn, $"Release {r.Count}: {who}");
            Colored(Muted, $"    {Leaves(r.Kind)}: {r.Why}.");
        }

        foreach (var c in p.Capture)
        {
            ImGui.Spacing();
            Icon(c.Kind.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            var main = c.Spots.FirstOrDefault();
            Plain($"Catch {c.Count}: {(main != null ? main.Name : $"an animal leaving {Leaves(c.Kind)}")}  ({c.Kind.Size}, {AnimalAdvisor.Restraint(c.Kind.Size)})");
            Colored(Muted, $"    {Leaves(c.Kind)} {c.Why}.");
            if (main != null) DrawSpot(main, "Found", now);
            foreach (var alt in c.Spots.Skip(1)) DrawSpot(alt, "Or " + alt.Name, now);
        }

        Section("Leavings", "Each animal leaves its common leaving every day, and its bonus leaving 80% of days when Gleeful (fed Premium Island Greenfeed): about 7 + 5.6 a week. The isleventory holds 999 of each.");
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
                if (l.AtCap) Colored(Warn, $"{l.Stock} (cap)");
                else Plain(l.Stock.ToString());
                Cell(l.WorkshopPerWeek >= 0.5 ? l.WorkshopPerWeek.ToString("0") : "-");
                Cell(l.SupplyNow.ToString("0"));
                Cell(Math.Abs(l.SupplyAfter - l.SupplyNow) < 0.5 ? "-" : l.SupplyAfter.ToString("0"));
                Cell(l.WeeksLeft is { } w ? (w < 1 ? "under a week" : $"{w:0.#} weeks") : "-");
            }

            ImGui.EndTable();
        }

        if (Group($"Your animals ({snap.Animals.Count})###animals", false))
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
        Colored(Muted, $"    {label} at ({spot.X:0.#}, {spot.Y:0.#}), {spot.When}.");
        ImGui.SameLine();
        if (MapButton($"{label}{spot.Name}")) status = plugin.FlagOnMap(spot.X, spot.Y) ?? string.Empty;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flag this spot on the map.");
        if (!spot.Rare) return;
        var w = WindowFor(spot, now);
        Colored(w != null && w.OpenAt(now) ? Good : Muted, "      " + IslandClock.Describe(w, now, true));
    }

    private void DrawWeather(DateTimeOffset now)
    {
        var rates = plugin.IslandWeather;
        if (rates.Count == 0)
        {
            Note("The island's weather isn't available.");
            return;
        }

        var f = IslandClock.Forecast(rates, now, 8);
        var changes = new List<(DateTimeOffset Start, WeatherChance Weather)>();
        for (var i = 1; i < f.Count && changes.Count < 4; i++)
            if (f[i].Weather.Id != f[i - 1].Weather.Id) changes.Add(f[i]);
        var next = changes.Count == 0 ? "no change for a while" : string.Join(", ", changes.Select(x => $"{x.Weather.Name} in {IslandClock.Span(x.Start - now)}"));
        Note($"Island weather: {f[0].Weather.Name} now; {next}.");
    }

    private void DrawFind(IslandSnapshot s)
    {
        var now = DateTimeOffset.UtcNow;
        DrawWeather(now);
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##findfilter", "Search...", ref findFilter, 64);
        HelpMarker("\"Map\" puts the game's flag on the spot and opens the map. It never moves you.");
        if (!ImGui.BeginTabBar("##findtabs")) return;
        if (ImGui.BeginTabItem("Materials"))
        {
            DrawFindMaterials(s);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Seeds"))
        {
            DrawFindSeeds(s);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Animals"))
        {
            DrawFindAnimals(s, now);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private bool Matches(string text) => findFilter.Length == 0 || text.Contains(findFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Workshop use per full week, from the kept week plan.</summary>
    private Dictionary<uint, double> WeeklyUse()
    {
        var use = new Dictionary<uint, double>();
        var week = plugin.Week;
        if (week == null || week.Days.Count == 0) return use;
        foreach (var (id, amount) in week.MaterialUse) use[id] = amount * 5.0 / week.Days.Count;
        return use;
    }

    private static string Tool(int tier) => tier switch
    {
        0 => "bare hands",
        1 => "Islekeep's Stone Hatchet",
        2 => "Islekeep's Stone Hammer",
        3 => "Islekeep's Shovel",
        4 => "Islekeep's Copper Scythe",
        6 => "Islekeep's Bronze Beakaxe",
        _ => "a later island tool",
    };

    private void DrawGatherSpot(GatherSpot g, string id)
    {
        if (MapButton($"{id}{g.PouchId}")) status = plugin.FlagOnMap(g.X, g.Y, g.Cave) ?? string.Empty;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"({g.X:0.0}, {g.Y:0.0}){(g.Cave ? " in the cave system" : string.Empty)}, search within about {g.Radius:0.#} of it.\nNeeds {Tool(g.Tier)}.");
    }

    private void DrawFindMaterials(IslandSnapshot s)
    {
        var use = WeeklyUse();
        var spots = s.GatherSpots.Where(g => s.Isleventory.Any(p => p.PouchId == g.PouchId && p.CropRow == 0)).ToList();
        var gathered = spots.Select(g => g.PouchId).ToHashSet();
        var fromGranary = new Dictionary<uint, List<string>>();
        foreach (var d in s.Destinations)
        {
            foreach (var id in d.CommonPouchIds.Append(d.RarePouchId))
            {
                if (id == 0 || gathered.Contains(id)) continue;
                if (!fromGranary.TryGetValue(id, out var l)) fromGranary[id] = l = [];
                if (!l.Contains(d.Name)) l.Add(d.Name);
            }
        }

        var items = s.Isleventory.ToDictionary(p => p.PouchId);
        var rows = spots.Select(g => (Id: g.PouchId, Spot: (GatherSpot?)g, Sort: g.Sort))
            .Concat(fromGranary.Keys.Select(id => (Id: id, Spot: (GatherSpot?)null, Sort: 1000 + (int)id)))
            .Where(r => items.ContainsKey(r.Id) && Matches(items[r.Id].Name))
            .Select(r =>
            {
                var item = items[r.Id];
                var perWeek = use.GetValueOrDefault(r.Id);
                double? weeks = perWeek >= 0.5 ? item.Count / perWeek : null;
                return (r.Spot, Item: item, PerWeek: perWeek, Weeks: weeks, r.Sort);
            })
            .OrderBy(r => r.Weeks ?? double.MaxValue)
            .ThenBy(r => r.Sort)
            .ToList();

        Colored(Muted, "Sorted by how soon this week's agendas run each one out. Hover a Map button for the tool it needs.");
        if (!ImGui.BeginTable("##findmat", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Material");
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Workshops / wk", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Lasts", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 260);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableHeadersRow();
        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Icon(r.Item.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            Plain(r.Item.Name);
            ImGui.TableNextColumn();
            if (r.Item.Count >= 999) Colored(Warn, "999 (cap)");
            else Plain(r.Item.Count.ToString());
            Cell(r.PerWeek >= 0.5 ? r.PerWeek.ToString("0") : "-");
            ImGui.TableNextColumn();
            if (r.Weeks is { } w) Colored(w < 2 ? Warn : w < 6 ? new Vector4(1, 1, 1, 1) : Muted, w < 1 ? "under a week" : $"{w:0.#} weeks");
            else Colored(Muted, "-");
            ImGui.TableNextColumn();
            if (r.Spot is { } g)
            {
                var where = $"({g.X:0.0}, {g.Y:0.0}){(g.Cave ? " cave" : string.Empty)}";
                if (r.Item.Discovered) Plain(where);
                else Colored(Muted, $"{where}, not in your log yet");
                if (!r.Item.Discovered && ImGui.IsItemHovered())
                    ImGui.SetTooltip($"You haven't gathered this yet. It needs {Tool(g.Tier)}, which comes with island progress.");
                ImGui.TableNextColumn();
                DrawGatherSpot(g, "mat");
            }
            else
            {
                Colored(Muted, "Granary: " + string.Join(", ", fromGranary[r.Item.PouchId]));
                ImGui.TableNextColumn();
            }
        }

        ImGui.EndTable();
    }

    private void DrawFindSeeds(IslandSnapshot s)
    {
        var spots = s.GatherSpots.GroupBy(g => g.PouchId).ToDictionary(g => g.Key, g => g.First());
        var planted = s.FarmPlots.Where(f => f.SeedPouchId != 0).GroupBy(f => f.SeedPouchId).ToDictionary(g => g.Key, g => g.Count());
        var produce = s.Isleventory.Where(p => p.IsProduce).GroupBy(p => p.CropRow).ToDictionary(g => g.Key, g => g.First());
        var seeds = s.Isleventory.Where(p => !p.IsProduce && p.CropRow != 0 && Matches(p.Name)).OrderBy(p => p.CropRow).ToList();

        Colored(Muted, "A few seeds grow wild and can be gathered from the start; the cropland mammet sells more as your island ranks up.");
        if (!ImGui.BeginTable("##findseed", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Seed");
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Planted", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 300);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableHeadersRow();
        foreach (var seed in seeds)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Icon(seed.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            Plain(seed.Name);
            Cell(seed.Count.ToString());
            Cell(planted.TryGetValue(seed.PouchId, out var n) ? n.ToString() : "");
            ImGui.TableNextColumn();
            if (spots.TryGetValue(seed.PouchId, out var g))
            {
                Plain($"Grows wild at ({g.X:0.0}, {g.Y:0.0}), or from the mammet");
                ImGui.TableNextColumn();
                DrawGatherSpot(g, "seed");
            }
            else
            {
                var grown = produce.TryGetValue(seed.CropRow, out var pr) && pr.Discovered;
                if (seed.Count > 0 || grown) Plain("Cropland mammet");
                else Colored(Muted, "Cropland mammet, once your island rank unlocks it");
                ImGui.TableNextColumn();
            }
        }

        ImGui.EndTable();
    }

    private void DrawFindAnimals(IslandSnapshot s, DateTimeOffset now)
    {
        var owned = s.Animals.GroupBy(a => a.Species).ToDictionary(g => g.Key, g => g.Count());
        var rows = SpeciesTable.All
            .Where(a => Matches(a.Name) || Matches(a.Common) || Matches(a.Bonus))
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
            if (!spot.Rare) Colored(Muted, "always");
            else if (w == null) Colored(Muted, "unknown");
            else if (w.OpenAt(now)) Colored(Good, $"now, {IslandClock.Span(w.End - now)} left");
            else Plain($"in {IslandClock.Span(w.Start - now)}");
            Cell(owned.TryGetValue(spot.Name, out var n) ? n.ToString() : "");
            ImGui.TableNextColumn();
            if (MapButton($"find{spot.Name}")) status = plugin.FlagOnMap(spot.X, spot.Y) ?? string.Empty;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"({spot.X:0.#}, {spot.Y:0.#})");
        }

        ImGui.EndTable();
    }

    private void DrawGranaryAdvice()
    {
        var snap = plugin.Snapshot;
        if (plugin.Granary is not { } g)
        {
            Note(plugin.Planning ? "Working it out..." : "Granary advice follows the week plan; it appears once the plan is ready.");
            return;
        }

        Section("Send your granaries", "Picked from what this week's plan uses and how long your stock of each rare material lasts. Each expedition runs 7 days.");
        if (g.advice.Count == 0)
        {
            Note("Open a granary's expedition window once (Select Terrain) so EasyIsle can see your expedition areas.");
        }

        foreach (var a in g.advice)
        {
            Plain($"Granary {a.Granary}: {a.Action}");
            Note("    " + a.Reason);
            if (snap?.Expeditions.FirstOrDefault(e => e.Granary == a.Granary) is { } e)
                Note(e.ExpeditionId == 0 ? "    Idle right now." : $"    Now: {e.Destination}, {e.DaysRemaining} day(s) left" + (e.WaitingToCollect > 0 ? $", {e.WaitingToCollect} item(s) to collect." : "."));
        }

        Section("Rare materials");
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
            Plain($"{label}  ({first.Hours} h, about {list.Sum(w => w.Total):N0} cowries)");
            ImGui.SameLine();
            var canCopy = AgendaClipboard.Available;
            if (!canCopy) ImGui.BeginDisabled();
            ImGui.PushID($"copy{day.Cycle}_{first.Workshop}");
            var copy = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "Copy for the game");
            ImGui.PopID();
            if (copy)
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
        Colored(Muted, label + ":");
        ImGui.SameLine(170);
        Plain(value);
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
        Colored(Muted, "Counts: " + s.IsleventorySource);
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
        Plain("Granary expeditions");
        if (s.Expeditions.Count == 0) Colored(Muted, "  not loaded");
        foreach (var e in s.Expeditions)
        {
            var text = e.ExpeditionId == 0
                ? "idle"
                : $"{e.Destination}, {e.DaysRemaining} day(s) left, rare: {e.RareMaterial}" + (e.FinishesUtc is { } f ? $", ends {f.ToLocalTime():g}" : string.Empty);
            if (e.WaitingToCollect > 0) text += $", {e.WaitingToCollect} waiting to collect";
            Row($"  Granary {e.Granary}", text);
        }

        ImGui.Spacing();
        Plain("Cropland");
        if (s.FarmPlots.Count == 0) Colored(Muted, "  not loaded");
        foreach (var p in s.FarmPlots)
        {
            Row($"  Plot {p.Slot}", p.SeedRow == 0
                ? "empty"
                : $"{p.Crop}, growth {p.Growth}, water {p.Water}, yield {p.Yield}" + (p.UnderCare ? ", under care" : string.Empty));
        }

        ImGui.Spacing();
        Plain("Pasture");
        if (s.Animals.Count == 0) Colored(Muted, "  not loaded or empty");
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
            Colored(Muted, "Not loaded: open the Felicitous Favors window once.");
            return;
        }

        foreach (var season in new[] { "Current", "Next", "Last" })
        {
            var list = s.Favors.Where(f => f.Season == season).ToList();
            if (list.Count == 0) continue;
            ImGui.Spacing();
            Plain(season + " season");
            foreach (var f in list)
            {
                ImGui.BulletText($"{f.Name}: shipped {f.Delivered}/{f.Required}, scheduled {f.Scheduled}"
                                   + (f.Shipped ? ", done" : string.Empty) + (f.Bonus ? ", bonus" : string.Empty));
            }
        }
    }

    private static void DrawAgenda(IslandSnapshot s)
    {
        Colored(Muted, "Agenda: " + s.ScheduleSource);
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
            Plain(text);
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
        Plain(text);
    }
}
