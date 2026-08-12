using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

/// <summary>
/// Submersible route planner: pick a material, get the sectors and the voyage
/// that bring back the most of it for the submarine you actually own.
/// </summary>
public class RouteWindow : ThemedWindow, IDisposable
{
    private static readonly Vector4 Gold = new(0.898f, 0.757f, 0.420f, 1f);

    private static readonly Vector4[] TierColors =
    {
        new(0.545f, 0.702f, 0.898f, 1f), // T1
        new(0.647f, 0.573f, 0.906f, 1f), // T2
        new(0.906f, 0.639f, 0.427f, 1f), // T3
    };

    private readonly SubmarineRouteService service;
    private readonly SubmarineGameData gameData;

    // Build state
    private readonly int[] partIndices = new int[4];
    private int rank = 130;
    private string? activePreset;
    private string? loadedSubmarine;

    // Selection state
    private int selectedItem = -1;
    private string search = string.Empty;
    private readonly HashSet<int> disabled = new();
    private int disabledVersion;

    // Filtered material list, rebuilt only when the filter text or game data changes.
    private readonly List<(string Display, int Index)> materials = new();
    private string? materialFilter;
    private int materialRevision = -1;

    // Derived state, refreshed whenever an input changes
    private bool dirty = true;
    private int[] stats = new int[5];
    private int minimumRank = 1;
    private List<SectorEstimate> rows = new();
    private List<MapRoute> routes = new();
    private BuildSuggestion? suggestion;
    private float currentScore;

    // The optimiser is the one expensive step and only depends on these three
    // inputs, so swapping parts never re-runs it.
    private (int Item, int Rank, int Disabled) suggestionKey = (-1, -1, -1);

    private int sortColumn = 3;
    private bool sortAscending;

    /// <summary>Whether voyage distances were available on the previous frame.</summary>
    private bool voyageDataSeen;

    public RouteWindow(SubmarineRouteService service, SubmarineGameData gameData)
        : base("Submersible Route Planner##RouteWindow")
    {
        this.service  = service;
        this.gameData = gameData;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(1120, 720);
        SizeCondition = ImGuiCond.FirstUseEver;

        if (service.IsReady)
        {
            ApplyPreset(SubmarineRouteService.Presets[0].Name);
            selectedItem = Array.IndexOf(service.Data.Names, "Unaspected Crystal");
            if (selectedItem < 0) selectedItem = 0;
        }
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!service.IsReady)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Error))
                ImGui.TextUnformatted(service.LoadError ?? "Submersible route data is unavailable.");
            return;
        }

        // Game data arrives a few seconds after login, so pick it up as soon as
        // it appears instead of waiting for the next click.
        if (voyageDataSeen != gameData.HasVoyageData)
        {
            voyageDataSeen = gameData.HasVoyageData;
            MarkDirty();
        }

        if (dirty) Recompute();

        var scale     = ImGuiHelpers.GlobalScale;
        var sidebarW  = 310f * scale;

        using (var sidebar = ImRaii.Child("##routeSidebar", new Vector2(sidebarW, 0), true))
        {
            if (sidebar) DrawSidebar();
        }

        ImGui.SameLine();

        using var main = ImRaii.Child("##routeMain", new Vector2(0, 0), false);
        if (main) DrawResults();
    }

    // ── Recompute ─────────────────────────────────────────────────────────────

    private void Recompute()
    {
        dirty = false;

        (stats, minimumRank) = service.ComputeStats(partIndices, rank);

        if (selectedItem < 0 || selectedItem >= service.Data.Names.Length)
        {
            rows = new List<SectorEstimate>();
            routes = new List<MapRoute>();
            suggestion = null;
            return;
        }

        rows = service.BuildRows(selectedItem, stats);

        var options = new RouteOptions
        {
            Disabled = disabled,
            Planner  = gameData.HasVoyageData ? gameData.PlanVoyage : null,
            Range    = gameData.HasVoyageData ? stats[3] : 0,
        };

        routes       = service.BuildRoutes(rows, options);
        currentScore = service.BestMapTotal(service.CandidateSectors(selectedItem, disabled), stats, selectedItem);

        var key = (selectedItem, rank, disabledVersion);
        if (key != suggestionKey)
        {
            suggestion    = service.SuggestBuild(selectedItem, rank, disabled);
            suggestionKey = key;
        }

        SortRows();
    }

    private void SortRows()
    {
        Comparison<SectorEstimate> comparison = sortColumn switch
        {
            1 => (a, b) => (a.Sector.Map * 1000 + SectorOrder(a)).CompareTo(b.Sector.Map * 1000 + SectorOrder(b)),
            2 => (a, b) => a.Tier.CompareTo(b.Tier),
            4 => (a, b) => a.AverageYield.CompareTo(b.AverageYield),
            _ => (a, b) => a.Expected.CompareTo(b.Expected),
        };

        rows.Sort((a, b) => sortAscending ? comparison(a, b) : comparison(b, a));
    }

    private static int SectorOrder(SectorEstimate estimate) =>
        estimate.Sector.Letter.Length * 26 + estimate.Sector.Letter[0];

    private void MarkDirty() => dirty = true;

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void DrawSidebar()
    {
        RmcTheme.SectionHeader("Submarine build");

        DrawPresets();
        DrawKnownSubmarines();

        for (var slot = 0; slot < 4; slot++)
        {
            var parts = service.PartNames(slot);
            ImGui.SetNextItemWidth(-1f);
            using (var combo = ImRaii.Combo($"##slot{slot}", $"{SubmarineRouteService.SlotNames[slot]}  ·  {parts[partIndices[slot]]}"))
            {
                if (combo)
                {
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var partRank = service.PartStats(slot, i)[5];
                        var label    = partRank > rank ? $"{parts[i]}  (rank {partRank})" : parts[i];

                        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted, partRank > rank))
                        {
                            if (ImGui.Selectable(label, i == partIndices[slot]))
                            {
                                partIndices[slot] = i;
                                activePreset      = null;
                                loadedSubmarine   = null;
                                MarkDirty();
                            }
                        }
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Rank {rank}");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderInt("##rank", ref rank, 1, 130, string.Empty))
        {
            loadedSubmarine = null;
            MarkDirty();
        }

        ImGui.Spacing();
        DrawStatRow();

        if (rank < minimumRank)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                ImGui.TextWrapped($"These parts need rank {minimumRank}.");
        }

        DrawLoadedSubmarineCheck();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        RmcTheme.SectionHeader("Target material");

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##materialSearch", "Filter materials…", ref search, 64))
            MarkDirty();

        DrawMaterialList();
    }

    /// <summary>
    /// Warns when the parts of a loaded submarine do not add up to the stats the
    /// game reports for it, which means the offline part table has drifted from
    /// the live game and the yields below are being computed from stale numbers.
    /// </summary>
    private void DrawLoadedSubmarineCheck()
    {
        if (loadedSubmarine == null) return;

        var loaded = gameData.Submarines.FirstOrDefault(s => s.Name == loadedSubmarine);
        if (loaded is not { HasParts: true } || loaded.Stats.SequenceEqual(stats)) return;

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
            ImGui.TextWrapped($"{loadedSubmarine} reports {string.Join(" / ", loaded.Stats)} in game — " +
                              "the planner's part table looks out of date.");
    }

    private void DrawPresets()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;

        for (var i = 0; i < SubmarineRouteService.Presets.Count; i++)
        {
            var preset = SubmarineRouteService.Presets[i];
            using (ImRaii.PushColor(ImGuiCol.Button, RmcTheme.SlateBlue, activePreset == preset.Name))
            {
                if (ImGui.Button(preset.Name, new Vector2(width, 0)))
                    ApplyPreset(preset.Name);
            }

            if (i % 2 == 0) ImGui.SameLine();
        }

        ImGui.Dummy(new Vector2(0, 2f * scale));
    }

    private void ApplyPreset(string name)
    {
        var preset = SubmarineRouteService.Presets.FirstOrDefault(p => p.Name == name);
        if (preset.Parts == null) return;

        for (var slot = 0; slot < 4; slot++)
            partIndices[slot] = service.PartIndex(slot, preset.Parts[slot]);

        activePreset    = name;
        loadedSubmarine = null;
        MarkDirty();
    }

    private void DrawKnownSubmarines()
    {
        var submarines = gameData.Submarines;
        if (submarines.Count == 0) return;

        var scale = ImGuiHelpers.GlobalScale;
        var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted("Load a saved submarine");

        for (var i = 0; i < submarines.Count; i++)
        {
            var sub = submarines[i];
            using (ImRaii.PushColor(ImGuiCol.Button, RmcTheme.SlateBlue, loadedSubmarine == sub.Name))
            {
                if (ImGui.Button($"{Shorten(sub.Name, 10)}  R{sub.Rank}##sub{i}", new Vector2(width, 0)))
                    LoadSubmarine(sub);
            }

            if (ImGui.IsItemHovered()) DrawSubmarineTooltip(sub);
            if (i % 2 == 0) ImGui.SameLine();
        }

        if (submarines.Count % 2 == 1) ImGui.NewLine();
        ImGui.Dummy(new Vector2(0, 2f * scale));
    }

    private void DrawSubmarineTooltip(SavedSubmarine sub)
    {
        using var tooltip = ImRaii.Tooltip();

        ImGui.TextUnformatted($"{sub.Name} — rank {sub.Rank}");
        ImGui.Separator();

        for (var i = 0; i < 5; i++)
            ImGui.TextUnformatted($"{SubmarineRouteService.StatNames[i],-14}{sub.Stats[i]}");

        if (sub.HasParts)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(string.Join(" / ", sub.Parts));
        }

        if (sub.Route.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Current voyage: {string.Join(" › ", sub.Route)}");
        }

        ImGui.Separator();
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            if (sub.Workshop.Length > 0) ImGui.TextUnformatted(sub.Workshop);
            ImGui.TextUnformatted($"Last seen {DescribeAge(sub.SeenUtc)}");
            if (!sub.HasParts) ImGui.TextUnformatted("Parts unrecognised — pick them by hand to plan.");
        }
    }

    private static string DescribeAge(DateTime seenUtc)
    {
        if (seenUtc == default) return "in an earlier session";

        var age = DateTime.UtcNow - seenUtc;
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minutes ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }

    /// <summary>
    /// Loads a live submarine. Its parts and rank drive the planner when they
    /// were recognised; otherwise the stats read from the game are used as they
    /// are, so an unknown part never silently changes the numbers.
    /// </summary>
    private void LoadSubmarine(SavedSubmarine sub)
    {
        rank = Math.Clamp(sub.Rank, 1, 130);

        if (sub.HasParts)
        {
            for (var slot = 0; slot < 4; slot++)
                partIndices[slot] = service.PartIndex(slot, sub.Parts[slot]);
        }

        activePreset    = null;
        loadedSubmarine = sub.Name;
        MarkDirty();
    }

    private void DrawStatRow()
    {
        using var table = ImRaii.Table("##statRow", 5, ImGuiTableFlags.NoSavedSettings);
        if (!table) return;

        ImGui.TableNextRow();
        for (var i = 0; i < 5; i++)
        {
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, i == 3 && !gameData.HasVoyageData ? RmcTheme.TextMuted : Gold))
                ImGui.TextUnformatted(stats[i].ToString());
        }

        ImGui.TableNextRow();
        for (var i = 0; i < 5; i++)
        {
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(SubmarineRouteService.StatShortNames[i]);
        }
    }

    private void DrawMaterialList()
    {
        using var list = ImRaii.Child("##materials", new Vector2(0, 0), true);
        if (!list) return;

        var scale    = ImGuiHelpers.GlobalScale;
        var iconSize = ImGui.GetTextLineHeight();
        var names    = service.Data.Names;

        // Filtering and culture-aware sorting are far too costly to repeat every
        // frame, so the list is only rebuilt when the filter changes — or when
        // logging in swaps the dataset's English names for localised ones.
        if (materialFilter != search || materialRevision != gameData.DataRevision)
        {
            materialFilter   = search;
            materialRevision = gameData.DataRevision;
            materials.Clear();

            for (var i = 0; i < names.Length; i++)
            {
                var display = gameData.GetDisplayName(i, names[i]);
                if (search.Length > 0 &&
                    !display.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !names[i].Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;

                materials.Add((display, i));
            }

            materials.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        }

        var matches = materials;

        foreach (var (display, index) in matches)
        {
            DrawItemIcon(index, names[index], iconSize);
            ImGui.SameLine(0f, 6f * scale);

            if (ImGui.Selectable($"{display}##mat{index}", index == selectedItem))
            {
                selectedItem = index;
                MarkDirty();
            }
        }

        if (matches.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No material matches that filter.");
        }
    }

    private void DrawItemIcon(int itemIndex, string name, float size)
    {
        var iconId = gameData.GetIconId(itemIndex, name);
        if (iconId != 0 &&
            Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(iconId), out var texture) &&
            texture.TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        else
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    // ── Results ───────────────────────────────────────────────────────────────

    private void DrawResults()
    {
        if (selectedItem < 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Pick a material on the left.");
            return;
        }

        var material = gameData.GetDisplayName(selectedItem, service.Data.Names[selectedItem]);

        DrawItemIcon(selectedItem, service.Data.Names[selectedItem], ImGui.GetTextLineHeight() * 1.4f);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Gold))
            ImGui.TextUnformatted(material);

        if (rows.Count == 0)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No sector drops this material.");
            return;
        }

        ImGui.Spacing();
        DrawSuggestion();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawRoutes();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSectorTable();
    }

    private void DrawSuggestion()
    {
        if (suggestion == null || suggestion.Score <= 0) return;

        var scale     = ImGuiHelpers.GlobalScale;
        var sameBuild = true;
        for (var slot = 0; slot < 4 && sameBuild; slot++)
            sameBuild = suggestion.Parts[slot] == service.PartNames(slot)[partIndices[slot]];

        using (ImRaii.Group())
        {
            using (ImRaii.PushColor(ImGuiCol.Text, sameBuild ? RmcTheme.Success : RmcTheme.Cornflower))
                ImGui.TextUnformatted(sameBuild
                    ? "Your build is already the best for this material"
                    : "A better build exists for this material");

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, Gold))
                ImGui.TextUnformatted($"{suggestion.Score:0.00} /voyage");
        }

        DrawSuggestionTooltip();

        for (var slot = 0; slot < 4; slot++)
        {
            var changed = suggestion.Parts[slot] != service.PartNames(slot)[partIndices[slot]];

            using (ImRaii.PushColor(ImGuiCol.Text, changed ? Gold : RmcTheme.TextMuted))
                ImGui.TextUnformatted($"{SubmarineRouteService.SlotNames[slot]} {suggestion.Parts[slot]}");

            ImGui.SameLine(0f, 14f * scale);
        }

        if (sameBuild)
        {
            ImGui.NewLine();
            return;
        }

        if (ImGui.Button("Apply"))
        {
            for (var slot = 0; slot < 4; slot++)
                partIndices[slot] = service.PartIndex(slot, suggestion.Parts[slot]);
            activePreset    = null;
            loadedSubmarine = null;
            MarkDirty();
        }

        ImGui.SameLine();
        var gain = suggestion.Score - currentScore;
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted(gain > 0.005f
                ? $"+{gain:0.00} over your {currentScore:0.00}"
                : "same yield, different parts");
    }

    private void DrawSuggestionTooltip()
    {
        if (suggestion == null || !ImGui.IsItemHovered()) return;

        using var tooltip = ImRaii.Tooltip();

        ImGui.TextUnformatted($"Every part combination buildable at rank {rank} was scored on its best five-sector voyage.");
        ImGui.TextUnformatted("Speed and Range do not change the loot, so ties go to the faster build.");

        ImGui.Separator();
        for (var i = 0; i < 5; i++)
        {
            var delta = suggestion.Stats[i] - stats[i];
            ImGui.TextUnformatted($"{SubmarineRouteService.StatNames[i],-14}{suggestion.Stats[i],4}{(delta == 0 ? string.Empty : $"   ({delta:+#;-#;0})")}");
        }

        if (suggestion.Stats[0] < stats[0] && service.MaxTier(selectedItem) == 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("The lower Surveillance is deliberate: this material only exists in the");
            ImGui.TextUnformatted("first loot tier, and low Surveillance forces every pull into it.");
        }
    }

    private void DrawRoutes()
    {
        if (routes.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No sector on any map drops this for your current build.");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var best  = routes[0];

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
            ImGui.TextUnformatted($"Best voyage — {service.Data.Maps[best.Map]}");

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Gold))
            ImGui.TextUnformatted($"{best.Total:0.00} /voyage");

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy route"))
            ImGui.SetClipboardText(string.Join(" ", best.Sectors.Select(s => s.Sector.Letter)));

        DrawVoyageFacts(best);
        ImGui.Spacing();

        using (var table = ImRaii.Table("##bestRouteSectors", 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings))
        {
            if (table)
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26f * scale);
                ImGui.TableSetupColumn("Sector", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 44f * scale);
                ImGui.TableSetupColumn("Per voyage", ImGuiTableColumnFlags.WidthFixed, 92f * scale);

                for (var i = 0; i < best.Sectors.Count; i++)
                {
                    var row = best.Sectors[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                        ImGui.TextUnformatted((i + 1).ToString());

                    ImGui.TableNextColumn();
                    using (ImRaii.Group())
                    {
                        ImGui.TextUnformatted($"{row.Sector.Letter} · {gameData.GetSectorName(row.Sector)}");
                        DrawLockWarning(row);
                    }
                    DrawSectorTooltip(row);

                    ImGui.TableNextColumn();
                    Pill($"T{row.Tier + 1}", TierColors[row.Tier]);

                    ImGui.TableNextColumn();
                    using (ImRaii.PushColor(ImGuiCol.Text, Gold))
                        ImGui.TextUnformatted($"{row.Expected:0.00}");
                }
            }
        }

        if (routes.Count <= 1) return;

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted("Other maps");

        using var others = ImRaii.Table("##otherRoutes", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings);
        if (!others) return;

        ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 150f * scale);
        ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Per voyage", ImGuiTableColumnFlags.WidthFixed, 92f * scale);

        foreach (var route in routes.Skip(1))
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(service.Data.Maps[route.Map]);

            ImGui.TableNextColumn();
            var path = string.Join(" › ", route.Sectors.Select(s => s.Sector.Letter));
            if (route.OverRange)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                    ImGui.TextUnformatted($"{path}   (out of range)");
            }
            else
            {
                ImGui.TextUnformatted(path);
            }

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Text))
                ImGui.TextUnformatted($"{route.Total:0.00}");
        }
    }

    /// <summary>Range and duration line under the winning voyage.</summary>
    private void DrawVoyageFacts(MapRoute route)
    {
        if (route.Distance <= 0)
        {
            if (!gameData.HasVoyageData)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                    ImGui.TextUnformatted("Log in to check the voyage against your range.");
            }
            return;
        }

        var withinRange = stats[3] >= route.Distance;

        using (ImRaii.PushColor(ImGuiCol.Text, withinRange ? RmcTheme.TextMuted : RmcTheme.Error))
            ImGui.TextUnformatted($"Range {route.Distance} of {stats[3]}");

        var duration = gameData.GetDuration(route.Sectors.Select(s => s.Sector).ToList(), stats[2]);
        if (duration.HasValue)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"·  {(int)duration.Value.TotalHours}h {duration.Value.Minutes:00}m");
        }

        if (route.OverRange)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Error))
                ImGui.TextUnformatted("·  out of range");
        }
        else if (route.Trimmed)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                ImGui.TextUnformatted($"·  {route.Sectors.Count} sectors — more range would fit another");
        }
    }

    private void DrawLockWarning(SectorEstimate row)
    {
        if (gameData.IsUnlocked(row.Sector) == false)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                ImGui.TextUnformatted("(locked)");
        }
    }

    private void DrawSectorTable()
    {
        var scale = ImGuiHelpers.GlobalScale;

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted($"All {rows.Count} sectors that drop this");

        var height = MathF.Max(ImGui.GetContentRegionAvail().Y, 140f * scale);

        using var table = ImRaii.Table("##sectorTable", 7,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.NoSavedSettings,
            new Vector2(0, height));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed, 32f * scale);
        ImGui.TableSetupColumn("Sector", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 46f * scale);
        ImGui.TableSetupColumn("Per voyage",
            ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthFixed,
            92f * scale);
        ImGui.TableSetupColumn("Quantity",
            ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthFixed, 96f * scale);
        // Wide enough for the longest pill trio: High / Optimal / no favor.
        ImGui.TableSetupColumn("Levels", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed, 190f * scale);
        ImGui.TableSetupColumn("What would help", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        ApplySortSpecs();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var included = !disabled.Contains(row.Index);
            if (ImGui.Checkbox($"##use{row.Index}", ref included))
            {
                if (included) disabled.Remove(row.Index); else disabled.Add(row.Index);
                disabledVersion++;
                MarkDirty();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Untick to keep this sector out of the route.");

            ImGui.TableNextColumn();
            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                    ImGui.TextUnformatted(service.Data.Maps[row.Sector.Map]);
                ImGui.SameLine();
                ImGui.TextUnformatted($"{row.Sector.Letter} · {gameData.GetSectorName(row.Sector)}");
                DrawLockWarning(row);
            }
            DrawSectorTooltip(row);

            ImGui.TableNextColumn();
            Pill($"T{row.Tier + 1}", TierColors[row.Tier]);

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, Gold))
                ImGui.TextUnformatted($"{row.Expected:0.00}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.AverageYield:0.0}");
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"({row.MinYield}–{row.MaxYield})");

            ImGui.TableNextColumn();
            Pill(SubmarineRouteService.SurveillanceNames[row.Surveillance], LevelColor(row.Surveillance));
            ImGui.SameLine();
            Pill(SubmarineRouteService.RetrievalNames[row.Retrieval], LevelColor(row.Retrieval));
            ImGui.SameLine();
            Pill(row.Favor ? "Favor" : "No favor", row.Favor ? RmcTheme.Success : RmcTheme.Error);

            ImGui.TableNextColumn();
            DrawUpgradeHints(row);
        }
    }

    private void ApplySortSpecs()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || !specs.SpecsDirty || specs.SpecsCount == 0) return;

        sortColumn    = specs.Specs.ColumnIndex;
        sortAscending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        specs.SpecsDirty = false;

        SortRows();
    }

    private void DrawSectorTooltip(SectorEstimate row)
    {
        if (!ImGui.IsItemHovered() || row.Drop == null) return;

        using var tooltip = ImRaii.Tooltip();

        ImGui.TextUnformatted($"{row.Sector.Letter} · {gameData.GetSectorName(row.Sector)}");
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted(service.Data.Maps[row.Sector.Map]);

        ImGui.Separator();
        ImGui.TextUnformatted($"Chance a voyage brings some back: {row.DropChance * 100f:0.0}%");
        ImGui.TextUnformatted($"Quantity at {SubmarineRouteService.RetrievalNames[row.Retrieval].ToLowerInvariant()} retrieval: {row.MinYield}–{row.MaxYield}, {row.AverageYield:0.0} on average");

        var rankRequirement = gameData.GetRankRequirement(row.Sector);
        if (rankRequirement is > 0)
            ImGui.TextUnformatted($"Sector needs submarine rank {rankRequirement}");

        ImGui.Separator();
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            ImGui.TextUnformatted($"Breakpoints — Surveillance {row.Sector.SurveillanceForMid}/{row.Sector.SurveillanceForHigh}, " +
                                  $"Retrieval {row.Sector.RetrievalForNormal}/{row.Sector.RetrievalForOptimal}, " +
                                  $"Favor {row.Sector.FavorRequired}");
            ImGui.TextUnformatted($"Modelled from {row.Drop.Samples:N0} recorded voyages");
        }
    }

    private void DrawUpgradeHints(SectorEstimate row)
    {
        var wrote = false;

        if (row.SurveillanceUpgrade is { } surveillance)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                ImGui.TextUnformatted($"Surveillance {surveillance} unlocks tier {row.Tier + 1}");
            wrote = true;
        }

        if (row.RetrievalUpgrade is { } retrieval && retrieval.Gain > 0.005f)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Success))
                ImGui.TextUnformatted($"Retrieval {retrieval.Need}: +{retrieval.Gain:0.00}");
            wrote = true;
        }

        if (row.FavorUpgrade is { } favor && favor.Gain > 0.005f)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Success))
                ImGui.TextUnformatted($"Favor {favor.Need}: +{favor.Gain:0.00}");
            wrote = true;
        }

        if (!wrote)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("nothing left to gain");
        }
    }

    // ── Small widgets ─────────────────────────────────────────────────────────

    private static Vector4 LevelColor(int level) => level switch
    {
        2 => RmcTheme.Success,
        1 => RmcTheme.Warning,
        _ => RmcTheme.Error,
    };

    /// <summary>Compact rounded badge, the ImGui counterpart of the web tool's pills.</summary>
    private static void Pill(string text, Vector4 color)
    {
        var scale   = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(5f * scale, 1f * scale);
        var size    = ImGui.CalcTextSize(text);
        var origin  = ImGui.GetCursorScreenPos();
        var extent  = new Vector2(size.X + padding.X * 2f, size.Y + padding.Y * 2f);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + extent, ImGui.GetColorU32(RmcTheme.Fade(color, 0.18f)), 4f * scale);
        draw.AddText(origin + padding, ImGui.GetColorU32(color), text);

        ImGui.Dummy(extent);
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
