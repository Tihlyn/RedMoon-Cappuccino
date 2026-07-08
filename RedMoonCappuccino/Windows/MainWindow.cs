using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

public class MainWindow : ThemedWindow, IDisposable
{
    private static readonly string[] ParticipantRoles = { "tank", "healer", "dps", "blue_mage", "rider", "flex" };
    private static readonly IReadOnlyDictionary<string, string[]> RoleJobs =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tank"]   = new[] { "Paladin", "Warrior", "Dark Knight", "Gunbreaker" },
            ["healer"] = new[] { "White Mage", "Scholar", "Astrologian", "Sage" },
            ["dps"]    = new[]
            {
                "Monk", "Dragoon", "Ninja", "Samurai", "Reaper", "Viper",
                "Bard", "Machinist", "Dancer",
                "Black Mage", "Summoner", "Red Mage", "Pictomancer",
            },
        };

    private readonly Plugin plugin;
    private readonly DataService dataService;
    private readonly GearPlannerService gearPlannerService;
    private readonly List<PatchCalendarEntry> upcomingPatches;
    private readonly List<MountGuideEntry> mountGuides;
    private PlannerRunResult? plannerResult;
    private string? plannerSelectedJob;

    // Per-event participation state (keyed by event id)
    private readonly Dictionary<string, int> eventRoleIndex = new();
    private readonly Dictionary<string, int> eventJobIndex = new();

    private enum SubmitState { None, Pending, Confirmed }
    private readonly Dictionary<string, SubmitState> eventSubmitState    = new();
    private readonly Dictionary<string, int>          eventSubmitBaseCount = new();
    private readonly Dictionary<string, DateTime>     eventSubmitPendingTime = new();
    private readonly Dictionary<string, string>       eventSubmitError     = new();

    public MainWindow(Plugin plugin, DataService dataService, GearPlannerService gearPlannerService)
        : base("Red Moon Cappuccino##MainWindow",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin      = plugin;
        this.dataService = dataService;
        this.gearPlannerService = gearPlannerService;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        upcomingPatches = LoadPatchCalendar();
        mountGuides     = LoadMountGuides();
        plannerSelectedJob = gearPlannerService.AvailableJobs.FirstOrDefault();
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##mainTabs"))
        {
            DrawOverviewTab();
            DrawGearPlannerTab();
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
            RmcTheme.StatusDot(RmcTheme.Success, "Connected", RmcTheme.Success);
        else
            RmcTheme.StatusDot(RmcTheme.Error, "Disconnected – reconnecting...", RmcTheme.Error);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var taxInfo = dataService.Tax;
        if (taxInfo == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Waiting for server data...");
        }
        else
        {
            RmcTheme.SectionHeader("Market Tax");

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
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
                    ImGui.TextUnformatted(taxInfo.Location);

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted("Tax Rate");
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
                    ImGui.TextUnformatted($"{taxInfo.Rate}%");
            }
        }

        ImGui.Spacing();

        var lastUpdated = dataService.LastUpdated;
        if (lastUpdated != default)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"Last updated: {lastUpdated.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawUpcomingPatchesSection();
    }

    private void DrawUpcomingPatchesSection()
    {
        RmcTheme.SectionHeader("Upcoming Patches (7.4 – 8.0)");

        ImGui.Spacing();

        if (upcomingPatches.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
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

        RmcTheme.SectionHeader("Mount Guides");

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
                        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
                        {
                            if (ImGui.Selectable($"{guide.Link}##guide_link_{i}", false))
                                OpenExternalLink(guide.Link);
                        }
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

        RmcTheme.SectionHeader("Visual Plans");
        ImGui.Spacing();

        const string visualPlanUrl = "https://wtfdig.info";
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
        {
            if (ImGui.Selectable($"{visualPlanUrl}##visual_plans_link", false))
                OpenExternalLink(visualPlanUrl);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open link");
    }

    private void DrawGearPlannerTab()
    {
        using var tab = ImRaii.TabItem("Gear Planner");
        if (!tab) return;

        ImGui.Spacing();

        var availableJobs = gearPlannerService.AvailableJobs;
        if (availableJobs.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Gear planner data is unavailable. Ensure local JSON planner resources are present.");
            return;
        }

        if (string.IsNullOrWhiteSpace(plannerSelectedJob) ||
            !availableJobs.Contains(plannerSelectedJob, StringComparer.OrdinalIgnoreCase))
            plannerSelectedJob = availableJobs[0];

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
            ImGui.TextWrapped("\u26a0 Make sure your character is on the same job as the one selected below before solving.");
        ImGui.Spacing();

        if (ImGui.BeginCombo("Job", plannerSelectedJob))
        {
            foreach (var job in availableJobs)
            {
                var isSelected = string.Equals(job, plannerSelectedJob, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(job, isSelected))
                {
                    plannerSelectedJob = job;
                    plannerResult = null;
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Auto Detect"))
        {
            var detected = gearPlannerService.GetCurrentJob();
            if (detected != null)
            {
                var match = availableJobs.FirstOrDefault(j =>
                    string.Equals(j, detected, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    plannerSelectedJob = match;
                    plannerResult = null;
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            var detected = gearPlannerService.GetCurrentJob();
            ImGui.SetTooltip(detected != null ? $"Currently playing: {detected}" : "Not logged in or job undetectable");
        }

        ImGui.SameLine();
        if (ImGui.Button("Solve"))
            plannerResult = gearPlannerService.Solve(plannerSelectedJob);

        ImGui.Separator();
        ImGui.Spacing();

        if (plannerResult == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Manual planner is idle. Press Solve to compute progression paths.");
            return;
        }

        if (!plannerResult.IsReady)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Error))
                ImGui.TextUnformatted(plannerResult.Error ?? "Planner is not ready.");
            return;
        }

        ImGui.TextUnformatted($"Job: {plannerResult.SelectedJob}");
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            ImGui.TextUnformatted($"Data version: {plannerResult.DataVersion}  |  Game patch: {plannerResult.GamePatch}  |  BiS patch: {plannerResult.BisPatch}");
            ImGui.TextUnformatted($"Snapshot generated: {plannerResult.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            ImGui.TextUnformatted($"BiS tracked slots: {plannerResult.Snapshot.MatchingSlots}/{plannerResult.Snapshot.TotalTargetSlots}");
            if (!plannerResult.Snapshot.HasKnownCurrentGear)
                ImGui.TextUnformatted("Current gear source: unknown (planning from empty baseline until equipped-gear reader is integrated).");
            ImGui.TextUnformatted(plannerResult.SupportsBranching
                ? "Branching-ready search graph enabled (contingency support planned)."
                : "Single-path mode.");
        }

        var currentTotal = plannerResult.Snapshot.CurrentStats.Values.Sum();
        var targetTotal = plannerResult.Snapshot.TargetStats.Values.Sum();
        ImGui.TextUnformatted($"Current weighted stat sum: {currentTotal} → Target: {targetTotal} (Δ {targetTotal - currentTotal})");

        ImGui.Spacing();

        if (plannerResult.RecommendedPaths.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No upgrade path was generated.");
            return;
        }

        using var child = ImRaii.Child("##gearPlannerResults", new Vector2(0, 0), false);
        if (!child) return;

        foreach (var path in plannerResult.RecommendedPaths)
        {
            var title = $"Path #{path.Rank} · Utility {path.TotalUtility:F1}";
            var open = ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen);
            if (!open) continue;

            using var indent = ImRaii.PushIndent(14f);
            ImGui.TextWrapped(path.Summary);

            for (var i = 0; i < path.Upgrades.Count; i++)
            {
                var step = path.Upgrades[i];
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
                    ImGui.TextUnformatted($"Step {i + 1}: {step.Slot} — {step.TargetItemName} (i{step.TargetItemLevel})");

                ImGui.TextWrapped($"Replaces {step.CurrentItemName} (i{step.CurrentItemLevel}) · Source: {step.SourceType} · Tomes: {step.EstimatedTomeCost} · Books: {step.EstimatedBookCost} · Breakpoint bonus: {step.BreakpointBonus} · Utility: {step.UtilityScore:F1}");
                foreach (var reason in step.Reasons)
                    ImGui.BulletText(reason);
            }
        }
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
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No upcoming events.");
            return;
        }

        using var child = ImRaii.Child("##eventsScroll", new Vector2(0, 0), false);
        if (!child) return;

        foreach (var ev in upcomingEvents)
            DrawEventEntry(ev, $"ev_{ev.Id}", allowParticipate: true);
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
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No recent past events (within the last 24 hours).");
            return;
        }

        using var child = ImRaii.Child("##pastScroll", new Vector2(0, 0), false);
        if (!child) return;

        foreach (var ev in pastEvents)
            DrawEventEntry(ev, $"past_{ev.Id}", allowParticipate: false);
    }

    // ── Shared event entry ───────────────────────────────────────────────────

    private void DrawEventEntry(EventSummary ev, string uniqueId, bool allowParticipate)
    {
        var headerLabel = $"[{ev.Date.ToLocalTime():MM/dd HH:mm}]  {ev.Type}  —  {ev.Description}##{uniqueId}";
        var expanded = ImGui.CollapsingHeader(headerLabel);
        if (!expanded) return;

        using var indent = ImRaii.PushIndent(14f);

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            ImGui.TextUnformatted("Organizer:");
            ImGui.SameLine(120f * ImGuiHelpers.GlobalScale);
        }
        ImGui.TextUnformatted(ev.Organizer);
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            ImGui.TextUnformatted("Group type:");
            ImGui.SameLine(120f * ImGuiHelpers.GlobalScale);
        }
        ImGui.TextUnformatted(ev.GroupType);
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
        {
            ImGui.TextUnformatted("Date:");
            ImGui.SameLine(120f * ImGuiHelpers.GlobalScale);
        }
        ImGui.TextUnformatted($"{ev.Date.ToLocalTime():yyyy-MM-dd HH:mm}");

        if (allowParticipate)
        {
            ImGui.Spacing();

            var notificationsEnabled = plugin.Configuration.EnableEventNotifications;
            var remind = plugin.Configuration.EventReminderIds.Contains(ev.Id);
            using (ImRaii.Disabled(!notificationsEnabled))
            {
                if (ImGui.Checkbox($"Remind me 10 minutes before start##remind_{uniqueId}", ref remind))
                {
                    if (remind)
                        plugin.Configuration.EventReminderIds.Add(ev.Id);
                    else
                        plugin.Configuration.EventReminderIds.Remove(ev.Id);
                    plugin.Configuration.Save();
                }
            }
            if (!notificationsEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Enable event notifications in the plugin settings first.");
        }

        if (ev.Participants.Count > 0)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.LightSteel))
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
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Participants: (none)");
        }

        if (allowParticipate)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            RmcTheme.SectionHeader("Participate");
            ImGui.Spacing();

            if (!eventRoleIndex.ContainsKey(ev.Id)) eventRoleIndex[ev.Id] = 0;
            if (!eventJobIndex.ContainsKey(ev.Id))  eventJobIndex[ev.Id]  = 0;

            var roleIdx = eventRoleIndex[ev.Id];
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo($"Role##role_{uniqueId}", ParticipantRoles[roleIdx]))
            {
                for (var i = 0; i < ParticipantRoles.Length; i++)
                {
                    var selected = roleIdx == i;
                    if (ImGui.Selectable(ParticipantRoles[i], selected))
                    {
                        if (i != roleIdx)
                        {
                            eventRoleIndex[ev.Id] = i;
                            eventJobIndex[ev.Id]  = 0;  // reset job when role changes
                        }
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            var currentRole = ParticipantRoles[roleIdx];
            var needsClass  = RoleJobs.ContainsKey(currentRole);

            if (needsClass)
            {
                var roleJobList = RoleJobs[currentRole];
                ImGui.SameLine();
                // Clamp index in case the role just changed and old index is out of range
                if (eventJobIndex[ev.Id] >= roleJobList.Length)
                    eventJobIndex[ev.Id] = 0;
                var jobIdx = eventJobIndex[ev.Id];
                ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo($"Job##job_{uniqueId}", roleJobList[jobIdx]))
                {
                    for (var i = 0; i < roleJobList.Length; i++)
                    {
                        var selected = jobIdx == i;
                        if (ImGui.Selectable(roleJobList[i], selected))
                            eventJobIndex[ev.Id] = i;
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.Spacing();

            var playerName = (Plugin.ObjectTable.LocalPlayer as ICharacter)?.Name.ToString();
            var canSubmit  = !string.IsNullOrWhiteSpace(playerName) && plugin.WsService.IsConnected;

            // Resolve and auto-advance submit state from latest snapshot data
            if (!eventSubmitState.TryGetValue(ev.Id, out var submitState))
                submitState = SubmitState.None;

            if (submitState == SubmitState.Pending &&
                eventSubmitPendingTime.TryGetValue(ev.Id, out var pendingStart) &&
                (DateTime.UtcNow - pendingStart).TotalSeconds > 5.0)
            {
                eventSubmitState[ev.Id]  = SubmitState.None;
                eventSubmitError[ev.Id]  = "Error : Not an FC member or Already participating.";
                submitState = SubmitState.None;
            }
            else if (submitState == SubmitState.Pending &&
                ev.Participants.Count > eventSubmitBaseCount.GetValueOrDefault(ev.Id))
            {
                eventSubmitState[ev.Id] = SubmitState.Confirmed;
                submitState = SubmitState.Confirmed;
            }

            if (submitState == SubmitState.Confirmed)
            {
                using (RmcTheme.PushButtonColors(RmcTheme.SuccessButton))
                using (ImRaii.Disabled(true))
                {
                    ImGui.Button($"\u2713 Participating##submit_{uniqueId}");
                }
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Success))
                    ImGui.TextUnformatted("Confirmed by server");
            }
            else if (submitState == SubmitState.Pending)
            {
                using (RmcTheme.PushButtonColors(RmcTheme.WarningButton))
                using (ImRaii.Disabled(true))
                {
                    ImGui.Button($"Submitting...##submit_{uniqueId}");
                }
            }
            else
            {
                using (ImRaii.Disabled(!canSubmit))
                {
                    if (ImGui.Button($"Participate##submit_{uniqueId}"))
                    {
                        var roleJobList = needsClass ? RoleJobs[currentRole] : null;
                        var jobClass = roleJobList != null ? roleJobList[eventJobIndex[ev.Id]] : null;
                        eventSubmitState[ev.Id]      = SubmitState.Pending;
                        eventSubmitBaseCount[ev.Id]  = ev.Participants.Count;
                        eventSubmitPendingTime[ev.Id] = DateTime.UtcNow;
                        eventSubmitError.Remove(ev.Id);
                        plugin.WsService.SubmitEventParticipant(ev.Id, playerName!, currentRole, jobClass);
                    }
                }

                if (!plugin.WsService.IsConnected)
                {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                        ImGui.TextUnformatted("(not connected)");
                }
                else if (string.IsNullOrWhiteSpace(playerName))
                {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                        ImGui.TextUnformatted("(log in to participate)");
                }

                if (eventSubmitError.TryGetValue(ev.Id, out var submitError))
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Error))
                        ImGui.TextUnformatted(submitError);
                }
            }

            ImGui.Spacing();
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
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                    ImGui.TextUnformatted("(Loading image...)");
            }
        }
        else if (dataService.IsImagePending(eventId))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("(Downloading image...)");
        }
        else if (dataService.HasImageManifest(eventId))
        {
            // Trigger lazy download — safe to call every frame, no-ops once a request is in flight.
            dataService.RequestImageIfNeeded(eventId);
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("(Downloading image...)");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("(No image for this event)");
        }
    }

    private static void OpenExternalLink(string url)
    {
        if (!TryGetSafeExternalUri(url, out var safeUri))
        {
            Plugin.Log.Warning("Blocked external link with invalid or unsafe URL: {Url}", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = safeUri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to open URL: {Url}", safeUri.AbsoluteUri);
        }
    }

    private static List<PatchCalendarEntry> LoadPatchCalendar()
    {
        var path = GetPluginResourcePath("patch-calendar.json");
        if (path == null) return [];
        if (!File.Exists(path)) return [];

        try
        {
            var root = JsonSerializer.Deserialize<PatchCalendarRoot>(File.ReadAllText(path));
            var patches = root?.Patches ?? [];

            var result = new List<PatchCalendarEntry>();
            foreach (var patch in patches.Where(p => p.Version is "7.4" or "7.5" or "8.0"))
            {
                result.Add(patch);

                if (patch.SubPatches is { Count: > 0 } && patch.Version is "7.5")
                {
                    foreach (var sub in patch.SubPatches)
                    {
                        result.Add(new PatchCalendarEntry
                        {
                            Version          = sub.Version,
                            Name             = "-",
                            Type             = "minor",
                            ReleaseDate      = sub.ReleaseDate,
                            ReleaseIsProjected = sub.ReleaseIsProjected,
                        });
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load patch calendar data.");
            return [];
        }
    }

    private static List<MountGuideEntry> LoadMountGuides()
    {
        var path = GetPluginResourcePath("links.json");
        if (path == null) return [];
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

    private static string? GetPluginResourcePath(string fileName)
    {
        var assemblyLocation = Plugin.PluginInterface.AssemblyLocation.FullName;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
        {
            Plugin.Log.Warning("Plugin assembly location is unavailable.");
            return null;
        }

        var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            Plugin.Log.Warning("Failed to determine plugin directory from assembly location.");
            return null;
        }

        return Path.Combine(pluginDirectory, "Resources", fileName);
    }

    private static bool TryGetSafeExternalUri(string? url, out Uri safeUri)
    {
        safeUri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsedUri)) return false;

        if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        safeUri = parsedUri;
        return true;
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

        [JsonPropertyName("subPatches")]
        public List<SubPatchEntry>? SubPatches { get; set; }
    }

    private sealed class SubPatchEntry
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("releaseIsProjected")]
        public bool ReleaseIsProjected { get; set; }
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
