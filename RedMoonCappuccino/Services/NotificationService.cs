using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Watches event data and raises Dalamud toast notifications (bottom-right,
/// like the plugin updater) when a new event is detected and when an event
/// with a reminder enabled is about to start.
/// </summary>
public class NotificationService : IDisposable
{
    private static readonly TimeSpan CheckInterval    = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReminderLeadTime = TimeSpan.FromMinutes(10);

    /// <summary>Above this many new events in one snapshot, collapse into a single summary toast.</summary>
    private const int MaxIndividualNewEventToasts = 3;

    private readonly Configuration config;
    private readonly DataService dataService;
    private readonly INotificationManager notificationManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly HashSet<string> knownEventIds;
    private readonly HashSet<string> firedReminders = new();
    private DateTime lastCheck = DateTime.MinValue;

    public NotificationService(
        Configuration config,
        DataService dataService,
        INotificationManager notificationManager,
        IFramework framework,
        IPluginLog log)
    {
        this.config              = config;
        this.dataService         = dataService;
        this.notificationManager = notificationManager;
        this.framework           = framework;
        this.log                 = log;

        knownEventIds = new HashSet<string>(config.KnownEventIds);
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now - lastCheck < CheckInterval) return;
        lastCheck = now;

        // No snapshot received yet — nothing to compare against.
        if (dataService.LastUpdated == default) return;

        var upcoming = dataService.GetUpcomingEvents();
        var changed  = CheckNewEvents(upcoming);
        CheckReminders(upcoming, now);
        changed |= PruneStaleIds(upcoming);

        if (changed)
        {
            config.KnownEventIds = knownEventIds.ToList();
            config.Save();
        }
    }

    /// <summary>
    /// Detects events not seen before. IDs are tracked even while notifications
    /// are opted out, so enabling them later does not flood with old events.
    /// </summary>
    private bool CheckNewEvents(List<EventSummary> upcoming)
    {
        var newEvents = upcoming.Where(e => knownEventIds.Add(e.Id)).ToList();
        if (newEvents.Count == 0) return false;

        if (config.EnableEventNotifications)
        {
            if (newEvents.Count > MaxIndividualNewEventToasts)
            {
                Notify($"{newEvents.Count} new events available");
            }
            else
            {
                foreach (var ev in newEvents)
                    Notify($"New '{ev.Type}' event available");
            }
        }

        return true;
    }

    private void CheckReminders(List<EventSummary> upcoming, DateTime now)
    {
        if (!config.EnableEventNotifications) return;

        foreach (var ev in upcoming)
        {
            if (!config.EventReminderIds.Contains(ev.Id) || firedReminders.Contains(ev.Id))
                continue;

            var untilStart = ev.Date - now;
            if (untilStart > TimeSpan.Zero && untilStart <= ReminderLeadTime)
            {
                firedReminders.Add(ev.Id);
                Notify($"Event '{ev.Type}' is about to start");
            }
        }
    }

    /// <summary>Drops tracked IDs for events no longer present (started or removed server-side).</summary>
    private bool PruneStaleIds(List<EventSummary> upcoming)
    {
        var live = new HashSet<string>(upcoming.Select(e => e.Id));
        live.UnionWith(dataService.GetPastEvents().Select(e => e.Id));

        var removedKnown = knownEventIds.RemoveWhere(id => !live.Contains(id)) > 0;
        var removedReminders = config.EventReminderIds.RemoveAll(id => !live.Contains(id)) > 0;
        firedReminders.RemoveWhere(id => !live.Contains(id));

        return removedKnown || removedReminders;
    }

    /// <summary>How long toasts stay on screen. The Dalamud default is short and easy to miss.</summary>
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(10);

    /// <summary>Longest DM preview shown in a toast before truncation.</summary>
    private const int DmPreviewMaxLength = 180;

    /// <summary>
    /// Pop-up for a direct message received while the chat window is closed.
    /// Shows the sender and a preview of the message text.
    /// </summary>
    public void NotifyDm(string from, string text)
    {
        var preview = text.Length > DmPreviewMaxLength
            ? text[..DmPreviewMaxLength] + "…"
            : text;
        Notify($"{from}: {preview}", "Live Chat — New Direct Message");
    }

    private void Notify(string content) => Notify(content, "Red Moon Cappuccino");

    /// <summary>
    /// Shows a toast in the expanded (non-minimized) style. Dalamud's default is a
    /// single collapsed line, which is too small to reliably catch the eye.
    /// </summary>
    private void Notify(string content, string title)
    {
        try
        {
            notificationManager.AddNotification(new Notification
            {
                Content         = content,
                Title           = title,
                Type            = NotificationType.Info,
                Minimized       = false,
                InitialDuration = ToastDuration,
            });
        }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Failed to show notification: {ex.Message}");
        }
    }
}
