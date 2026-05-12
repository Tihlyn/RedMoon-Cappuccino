using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;

namespace RedMoonCappuccino.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly DataService dataService;
    private readonly List<PatchCalendarEntry> upcomingPatches;
    private readonly List<MountGuideEntry> mountGuides;

    public MainWindow(Plugin plugin, DataService dataService)
        : base("Red Moon Cappuccino##MainWindow",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin      = plugin;
        this.dataService = dataService;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        upcomingPatches = LoadPatchCalendar();
        mountGuides     = LoadMountGuides();
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##mainTabs"))
        {
            DrawOverviewTab();
            DrawUsefulLinksTab();
            DrawEventsTab();
            DrawPastEventsTab();
            ImGui.EndTabBar();
        }
    }

    // ── Overview ─────────────────────────────────────────────────────────────

    private void DrawOverviewTab()
    {
        using var tab = ImRaii.TabItem("Overview");
        if (!tab) return;

        ImGui.Spacing();

        // Connection status badge
        if (plugin.WsService.IsConnected)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00CC00u))
                ImGui.TextUnformatted("● Connected");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFF0044FFu))
                ImGui.TextUnformatted("● Disconnected – reconnecting...");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var taxInfo = dataService.Tax;
        if (taxInfo == null)
        {
            ImGui.TextUnformatted("Waiting for server data...");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
                ImGui.TextUnformatted("Market Tax");

            ImGui.Spacing();

            using var table = ImRaii.Table("##taxTable", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (table)
            {
                ImGui.TableSetupColumn("Field",    ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Value",    ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted("Lowest Tax City");
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
                    ImGui.TextUnformatted(taxInfo.Location);

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted("Tax Rate");
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
                    ImGui.TextUnformatted($"{taxInfo.Rate}%");
            }
        }

        ImGui.Spacing();

        var lastUpdated = dataService.LastUpdated;
        if (lastUpdated != default)
            ImGui.TextUnformatted($"Last updated: {lastUpdated.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawUpcomingPatchesSection();
    }

    private void DrawUpcomingPatchesSection()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
            ImGui.TextUnformatted("Upcoming Patches (7.4 – 8.0)");

        ImGui.Spacing();

        if (upcomingPatches.Count == 0)
        {
            ImGui.TextUnformatted("No upcoming patch data available.");
            return;
        }

        using var table = ImRaii.Table("##upcomingPatchesTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("Patch", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Release", ImGuiTableColumnFlags.WidthFixed, 95f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var patch in upcomingPatches)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(patch.Version);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(patch.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(patch.Type.Replace('_', ' '));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(patch.ReleaseDate);

            ImGui.TableNextColumn();
            var note = patch.ReleaseIsProjected ? "Projected date" : "-";
            if (!string.IsNullOrWhiteSpace(patch.Note))
                note = patch.Note;
            ImGui.TextWrapped(note);
        }
    }

    private void DrawUsefulLinksTab()
    {
        using var tab = ImRaii.TabItem("Useful Links");
        if (!tab) return;

        using var child = ImRaii.Child("##usefulLinksScroll", new Vector2(0, 0), false);
        if (!child) return;

        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
            ImGui.TextUnformatted("Mount Guides");

        ImGui.Spacing();

        using (var table = ImRaii.Table("##mountGuidesTable", 6,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Mount", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Source / Fight", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Creator", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                if (mountGuides.Count == 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("-");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("No guide data available.");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("-");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("-");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("-");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted("-");
                }
                else
                {
                    for (var i = 0; i < mountGuides.Count; i++)
                    {
                        var guide = mountGuides[i];

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(guide.Mount);
                        ImGui.TableNextColumn(); ImGui.TextWrapped(guide.Source);
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(guide.Creator);
                        ImGui.TableNextColumn(); ImGui.TextWrapped(guide.VideoTitle);

                        ImGui.TableNextColumn();
                        if (ImGui.Selectable($"{guide.Link}##guide_link_{i}", false))
                            OpenExternalLink(guide.Link);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Open link");

                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(string.IsNullOrWhiteSpace(guide.Note) ? "-" : guide.Note);
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
            ImGui.TextUnformatted("Visual Plans");
        ImGui.Spacing();

        const string visualPlanUrl = "https://wtfdig.info";
        if (ImGui.Selectable($"{visualPlanUrl}##visual_plans_link", false))
            OpenExternalLink(visualPlanUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open link");
    }

    // ── Events ───────────────────────────────────────────────────────────────

    private void DrawEventsTab()
    {
        using var tab = ImRaii.TabItem("Events");
        if (!tab) return;

        ImGui.Spacing();

        var upcomingEvents = dataService.GetUpcomingEvents();

        if (upcomingEvents.Count == 0)
        {
            ImGui.TextUnformatted("No upcoming events.");
            return;
        }

        using var child = ImRaii.Child("##eventsScroll", new Vector2(0, 0), false);
        if (!child) return;

        foreach (var ev in upcomingEvents)
            DrawEventEntry(ev, $"ev_{ev.Id}");
    }

    // ── Past Events ──────────────────────────────────────────────────────────

    private void DrawPastEventsTab()
    {
        using var tab = ImRaii.TabItem("Past Events");
        if (!tab) return;

        ImGui.Spacing();

        var pastEvents = dataService.GetPastEvents();

        if (pastEvents.Count == 0)
        {
            ImGui.TextUnformatted("No recent past events (within the last 24 hours).");
            return;
        }

        using var child = ImRaii.Child("##pastScroll", new Vector2(0, 0), false);
        if (!child) return;

        foreach (var ev in pastEvents)
            DrawEventEntry(ev, $"past_{ev.Id}");
    }

    // ── Shared event entry ───────────────────────────────────────────────────

    private void DrawEventEntry(EventSummary ev, string uniqueId)
    {
        var headerLabel = $"[{ev.Date.ToLocalTime():MM/dd HH:mm}]  {ev.Type}  —  {ev.Description}##{uniqueId}";
        var expanded = ImGui.CollapsingHeader(headerLabel);
        if (!expanded) return;

        using var indent = ImRaii.PushIndent(14f);

        ImGui.TextUnformatted($"Organizer:    {ev.Organizer}");
        ImGui.TextUnformatted($"Group type:   {ev.GroupType}");
        ImGui.TextUnformatted($"Date:         {ev.Date.ToLocalTime():yyyy-MM-dd HH:mm}");

        if (ev.Participants.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"Participants ({ev.Participants.Count}):");

            using var ptable = ImRaii.Table($"##pt_{uniqueId}", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (ptable)
            {
                ImGui.TableSetupColumn("Role",  ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                foreach (var p in ev.Participants)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Role);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Class);
                }
            }
        }
        else
        {
            ImGui.TextUnformatted("Participants: (none)");
        }

        ImGui.Spacing();
        DrawEventImage(ev.Id, uniqueId);
        ImGui.Spacing();
    }

    private void DrawEventImage(string eventId, string uniqueId)
    {
        var imagePath = dataService.GetCachedImagePath(eventId);

        if (imagePath != null)
        {
            if (Plugin.TextureProvider.GetFromFile(imagePath).TryGetWrap(out var tex, out _) && tex != null)
            {
                var availableWidth = ImGui.GetContentRegionAvail().X;
                var displayWidth   = Math.Min(availableWidth, tex.Width);
                var aspectRatio    = (float)tex.Height / tex.Width;
                ImGui.Image(tex.Handle, new Vector2(displayWidth, displayWidth * aspectRatio));
            }
            else
            {
                ImGui.TextUnformatted("(Loading image...)");
            }
        }
        else if (dataService.IsImagePending(eventId))
        {
            ImGui.TextUnformatted("(Downloading image...)");
        }
        else if (dataService.HasImageManifest(eventId))
        {
            // Trigger lazy download — safe to call every frame, no-ops once a request is in flight.
            dataService.RequestImageIfNeeded(eventId);
            ImGui.TextUnformatted("(Downloading image...)");
        }
        else
        {
            ImGui.TextUnformatted("(No image for this event)");
        }
    }

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to open URL: {Url}", url);
        }
    }

    private static List<PatchCalendarEntry> LoadPatchCalendar()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "patch-calendar.json");
        if (!File.Exists(path)) return [];

        try
        {
            var root = JsonSerializer.Deserialize<PatchCalendarRoot>(File.ReadAllText(path));
            return root?.Patches?
                .Where(p => p.Version is "7.4" or "7.5" or "8.0")
                .ToList()
                ?? [];
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load patch calendar data.");
            return [];
        }
    }

    private static List<MountGuideEntry> LoadMountGuides()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "links.json");
        if (!File.Exists(path)) return [];

        try
        {
            var root = JsonSerializer.Deserialize<LinksRoot>(File.ReadAllText(path));
            return root?.MountGuides ?? [];
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load links data.");
            return [];
        }
    }

    private sealed class PatchCalendarRoot
    {
        [JsonPropertyName("patches")]
        public List<PatchCalendarEntry>? Patches { get; set; }
    }

    private sealed class PatchCalendarEntry
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("releaseIsProjected")]
        public bool ReleaseIsProjected { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    private sealed class LinksRoot
    {
        [JsonPropertyName("mount_guides")]
        public List<MountGuideEntry>? MountGuides { get; set; }
    }

    private sealed class MountGuideEntry
    {
        [JsonPropertyName("mount")]
        public string Mount { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("creator")]
        public string Creator { get; set; } = string.Empty;

        [JsonPropertyName("video_title")]
        public string VideoTitle { get; set; } = string.Empty;

        [JsonPropertyName("link")]
        public string Link { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
