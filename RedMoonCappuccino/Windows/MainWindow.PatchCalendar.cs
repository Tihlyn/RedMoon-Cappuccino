using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

// Overview tab, patch calendar: a countdown card for the next patch above a timeline of the
// patch cycle, both read once from Resources/patch-calendar.json.
public partial class MainWindow
{
    private enum PatchState { Released, Today, Next, Upcoming }

    private void DrawPatchCalendarSection()
    {
        RmcTheme.SectionHeader("Patch Calendar");
        ImGui.Spacing();

        if (upcomingPatches.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No patch data available.");
            return;
        }

        var today     = DateOnly.FromDateTime(DateTime.Now);
        var nextIndex = upcomingPatches.FindIndex(p => p.Date >= today);

        if (nextIndex >= 0)
        {
            DrawNextPatchCard(upcomingPatches[nextIndex], nextIndex > 0 ? upcomingPatches[nextIndex - 1] : null, today);
            ImGui.Spacing();
        }

        DrawPatchTimeline(today, nextIndex);
    }

    /// <summary>The next patch with a countdown and a bar running from the previous release to it.</summary>
    private static void DrawNextPatchCard(PatchCalendarEntry next, PatchCalendarEntry? previous, DateOnly today)
    {
        var s        = ImGuiHelpers.GlobalScale;
        var draw     = ImGui.GetWindowDrawList();
        var lineH    = ImGui.GetTextLineHeight();
        var padding  = new Vector2(14f, 9f) * s;
        var barH     = 5f * s;
        var gap      = 7f * s;
        var origin   = ImGui.GetCursorScreenPos();
        var size     = new Vector2(ImGui.GetContentRegionAvail().X, padding.Y * 2f + lineH * 2f + gap * 2f + barH);

        var date    = next.Date!.Value;
        var days    = date.DayNumber - today.DayNumber;
        var isToday = days == 0;
        var accent  = isToday ? RmcTheme.Success : RmcTheme.Cornflower;

        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(RmcTheme.Fade(RmcTheme.SlateBlue, 0.14f)), 8f * s);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(RmcTheme.Fade(accent, 0.22f)), 8f * s);
        draw.AddRectFilled(new Vector2(origin.X + 5f * s, origin.Y + 8f * s),
                           new Vector2(origin.X + 8f * s, origin.Y + size.Y - 8f * s),
                           ImGui.GetColorU32(accent), 1.5f * s);

        var left  = origin.X + padding.X;
        var right = origin.X + size.X - padding.X;
        var y     = origin.Y + padding.Y;

        // Title line: "Next patch  8.0  Evercold" ........ "in 100 days"
        var x = left;
        x = DrawTextAt(draw, new Vector2(x, y), isToday ? "Patch day" : "Next patch", RmcTheme.TextMuted) + 8f * s;
        x = DrawTextAt(draw, new Vector2(x, y), next.Version, accent) + 8f * s;
        var title = PatchTitle(next);
        if (title.Length > 0)
            DrawTextAt(draw, new Vector2(x, y), title, RmcTheme.Text);

        var countdown = isToday ? "out today" : RelativeDays(days);
        DrawTextAt(draw, new Vector2(right - ImGui.CalcTextSize(countdown).X, y), countdown, accent);

        // Progress since the previous release.
        y += lineH + gap;
        var fraction = 1f;
        var elapsed  = 0;
        var total    = 0;
        if (previous?.Date is { } previousDate)
        {
            total    = date.DayNumber - previousDate.DayNumber;
            elapsed  = today.DayNumber - previousDate.DayNumber;
            fraction = total > 0 ? Math.Clamp(elapsed / (float)total, 0f, 1f) : 1f;
        }

        var barMin = new Vector2(left, y);
        var barMax = new Vector2(right, y + barH);
        draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(RmcTheme.Fade(RmcTheme.LightSteel, 0.12f)), barH / 2f);
        var fillX = barMin.X + (barMax.X - barMin.X) * fraction;
        if (fraction > 0f)
        {
            draw.AddRectFilled(barMin, new Vector2(Math.Max(fillX, barMin.X + barH), barMax.Y),
                               ImGui.GetColorU32(accent), barH / 2f);
            var knob = new Vector2(Math.Max(barMin.X + barH, Math.Min(fillX, barMax.X - barH / 2f)), y + barH / 2f);
            draw.AddCircleFilled(knob, barH * 1.5f, ImGui.GetColorU32(RmcTheme.Fade(accent, 0.30f)));
            draw.AddCircleFilled(knob, barH * 0.9f, ImGui.GetColorU32(accent));
        }

        // End points under the bar.
        y += barH + gap;
        if (previous != null)
            DrawTextAt(draw, new Vector2(left, y), $"{previous.Version} · {FormatPatchDate(previous)}", RmcTheme.TextMuted);
        var nextDate = FormatPatchDate(next);
        DrawTextAt(draw, new Vector2(right - ImGui.CalcTextSize(nextDate).X, y), nextDate, RmcTheme.TextMuted);

        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
        {
            var tooltip = BuildPatchTooltip(next, today);
            if (previous != null && total > 0)
                tooltip += $"\n{Math.Clamp(elapsed, 0, total)} of {total} days since {previous.Version}";
            ImGui.SetTooltip(tooltip);
        }
    }

    /// <summary>One row per patch on a vertical rail; the travelled part of the rail is lit.</summary>
    private void DrawPatchTimeline(DateOnly today, int nextIndex)
    {
        var s        = ImGuiHelpers.GlobalScale;
        var draw     = ImGui.GetWindowDrawList();
        var lineH    = ImGui.GetTextLineHeight();
        var rowH     = lineH + 9f * s;
        var gutter   = 26f * s;
        var width    = ImGui.GetContentRegionAvail().X;
        var versionW = upcomingPatches.Max(p => ImGui.CalcTextSize(p.Version).X) + 14f * s;
        var canHover = ImGui.IsWindowHovered();

        var states = new PatchState[upcomingPatches.Count];
        for (var i = 0; i < states.Length; i++)
            states[i] = GetPatchState(upcomingPatches[i], i, nextIndex, today);

        // Rows touch so the rail runs unbroken from one to the next.
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { Y = 0f });

        for (var i = 0; i < upcomingPatches.Count; i++)
        {
            var patch    = upcomingPatches[i];
            var state    = states[i];
            var released = state == PatchState.Released;
            var min      = ImGui.GetCursorScreenPos();
            var max      = min + new Vector2(width, rowH);
            var textY    = min.Y + (rowH - lineH) / 2f;
            var center   = new Vector2(min.X + gutter / 2f, min.Y + rowH / 2f);
            var radius   = NodeRadius(patch.Type) * s;
            var railGap  = radius + 3f * s;

            if (canHover && ImGui.IsMouseHoveringRect(min, max))
                draw.AddRectFilled(min, max, ImGui.GetColorU32(RmcTheme.Fade(RmcTheme.SlateBlue, 0.16f)), 5f * s);

            if (i > 0)
                draw.AddLine(new Vector2(center.X, min.Y), new Vector2(center.X, center.Y - railGap),
                             RailColor(states[i]), 2f * s);
            if (i < upcomingPatches.Count - 1)
                draw.AddLine(new Vector2(center.X, center.Y + railGap), new Vector2(center.X, max.Y),
                             RailColor(states[i + 1]), 2f * s);

            var nodeColor = NodeColor(state);
            switch (state)
            {
                case PatchState.Released:
                    draw.AddCircleFilled(center, radius, ImGui.GetColorU32(nodeColor));
                    break;
                case PatchState.Today:
                case PatchState.Next:
                    draw.AddCircleFilled(center, radius + 3f * s, ImGui.GetColorU32(RmcTheme.Fade(nodeColor, 0.25f)));
                    draw.AddCircleFilled(center, radius, ImGui.GetColorU32(nodeColor));
                    break;
                default:
                    draw.AddCircle(center, radius - 0.75f * s, ImGui.GetColorU32(nodeColor), 0, 1.5f * s);
                    break;
            }

            var x = min.X + gutter + 4f * s;
            DrawTextAt(draw, new Vector2(x, textY), patch.Version,
                       state switch
                       {
                           PatchState.Released => RmcTheme.TextMuted,
                           PatchState.Today    => RmcTheme.Success,
                           PatchState.Next     => RmcTheme.Cornflower,
                           _                   => RmcTheme.Text,
                       });
            x += versionW;

            // Right side first: the date, and how far away it is for anything not yet out.
            var right    = max.X - 8f * s;
            var dateText = FormatPatchDate(patch);
            right -= ImGui.CalcTextSize(dateText).X;
            DrawTextAt(draw, new Vector2(right, textY), dateText, released ? RmcTheme.TextMuted : RmcTheme.Text);

            if (!released && patch.Date is { } date)
            {
                var relative = RelativeDays(date.DayNumber - today.DayNumber);
                right -= ImGui.CalcTextSize(relative).X + 12f * s;
                DrawTextAt(draw, new Vector2(right, textY), relative,
                           state == PatchState.Today ? RmcTheme.Success : RmcTheme.Fade(RmcTheme.Cornflower, 0.85f));
            }

            // Title and type badge, clipped short of the right side on narrow windows.
            draw.PushClipRect(new Vector2(x, min.Y), new Vector2(right - 10f * s, max.Y), true);
            var title = PatchTitle(patch);
            if (title.Length > 0)
            {
                var titleColor = released ? RmcTheme.TextMuted : patch.IsMinor ? RmcTheme.LightSteel : RmcTheme.Text;
                x = DrawTextAt(draw, new Vector2(x, textY), title, titleColor) + 8f * s;
            }
            if (PatchBadge(patch.Type) is { } badge)
                DrawBadge(draw, new Vector2(x, textY), badge.Label, released ? RmcTheme.Fade(badge.Color, 0.6f) : badge.Color);
            draw.PopClipRect();

            ImGui.Dummy(new Vector2(width, rowH));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BuildPatchTooltip(patch, today));
        }
    }

    private static PatchState GetPatchState(PatchCalendarEntry patch, int index, int nextIndex, DateOnly today)
    {
        if (patch.Date < today) return PatchState.Released;
        if (patch.Date == today) return PatchState.Today;
        return index == nextIndex ? PatchState.Next : PatchState.Upcoming;
    }

    private static Vector4 NodeColor(PatchState state) => state switch
    {
        PatchState.Released => RmcTheme.Fade(RmcTheme.LightSteel, 0.55f),
        PatchState.Today    => RmcTheme.Success,
        PatchState.Next     => RmcTheme.Cornflower,
        _                   => RmcTheme.Fade(RmcTheme.Cornflower, 0.75f),
    };

    /// <summary>A rail segment is lit once the patch it leads to is out.</summary>
    private static uint RailColor(PatchState to) => ImGui.GetColorU32(
        to is PatchState.Released or PatchState.Today
            ? RmcTheme.Fade(RmcTheme.SlateBlue, 0.9f)
            : RmcTheme.Fade(RmcTheme.LightSteel, 0.18f));

    private static float NodeRadius(string type) => type switch
    {
        "expansion"              => 6.5f,
        "major" or "final_major" => 5f,
        _                        => 3.5f,
    };

    private static (string Label, Vector4 Color)? PatchBadge(string type) => type switch
    {
        "expansion"   => ("Expansion", RmcTheme.Warning),
        "major"       => ("Major", RmcTheme.Cornflower),
        "final_major" => ("Final major", RmcTheme.Lighten(RmcTheme.SlateBlue, 0.35f)),
        _             => null,
    };

    /// <summary>Major patches go by their name; point patches by their note, if any.</summary>
    private static string PatchTitle(PatchCalendarEntry patch) =>
        (patch.IsMinor ? patch.Note : patch.Name)?.Trim() ?? string.Empty;

    private static string FormatPatchDate(PatchCalendarEntry patch)
    {
        if (patch.Date is not { } date)
            return string.IsNullOrWhiteSpace(patch.ReleaseDate) ? "TBA" : patch.ReleaseDate;
        var text = date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return patch.ReleaseIsProjected ? $"~{text}" : text;
    }

    private static string RelativeDays(int days) => days switch
    {
        0    => "today",
        1    => "tomorrow",
        -1   => "yesterday",
        > 0  => $"in {days} days",
        _    => $"{-days} days ago",
    };

    private static string BuildPatchTooltip(PatchCalendarEntry patch, DateOnly today)
    {
        var lines = new List<string>
        {
            patch.IsMinor || string.IsNullOrWhiteSpace(patch.Name)
                ? $"Patch {patch.Version}"
                : $"Patch {patch.Version} — {patch.Name}",
        };

        if (patch.IsMinor && !string.IsNullOrWhiteSpace(patch.Note))
            lines.Add(patch.Note.Trim());
        else if (PatchBadge(patch.Type) is { } badge)
            lines.Add(badge.Label + (patch.Type == "expansion" ? "" : " patch"));

        if (patch.Date is { } date)
            lines.Add($"{date.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)} · " +
                      RelativeDays(date.DayNumber - today.DayNumber));
        else
            lines.Add("Release date not announced yet");

        if (patch.ReleaseIsProjected)
            lines.Add("Projected date, not yet announced");

        if (!patch.IsMinor && !string.IsNullOrWhiteSpace(patch.Note))
            lines.Add(patch.Note.Trim());

        return string.Join("\n", lines);
    }

    /// <summary>Draws text at a screen position and returns the x just past it.</summary>
    private static float DrawTextAt(ImDrawListPtr draw, Vector2 pos, string text, Vector4 color)
    {
        draw.AddText(pos, ImGui.GetColorU32(color), text);
        return pos.X + ImGui.CalcTextSize(text).X;
    }

    /// <summary>Rounded pill around one line of text whose top-left is <paramref name="pos"/>.</summary>
    private static void DrawBadge(ImDrawListPtr draw, Vector2 pos, string label, Vector4 color)
    {
        var s       = ImGuiHelpers.GlobalScale;
        var size    = ImGui.CalcTextSize(label);
        var padding = new Vector2(6f, 1f) * s;
        var min     = pos - new Vector2(0f, padding.Y);
        var max     = pos + new Vector2(size.X + padding.X * 2f, size.Y + padding.Y);

        draw.AddRectFilled(min, max, ImGui.GetColorU32(RmcTheme.Fade(color, color.W * 0.18f)), (max.Y - min.Y) / 2f);
        draw.AddText(pos + new Vector2(padding.X, 0f), ImGui.GetColorU32(color), label);
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
                            Type             = "minor",
                            ReleaseDate      = sub.ReleaseDate,
                            ReleaseIsProjected = sub.ReleaseIsProjected,
                            Note             = sub.Note,
                        });
                    }
                }
            }

            foreach (var entry in result)
                entry.Date = DateOnly.TryParseExact(entry.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                    DateTimeStyles.None, out var date)
                    ? date
                    : null;

            // Undated entries go last; the rest in release order, whatever order the file lists them in.
            return result.OrderBy(p => p.Date ?? DateOnly.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load patch calendar data.");
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

        [JsonPropertyName("subPatches")]
        public List<SubPatchEntry>? SubPatches { get; set; }

        /// <summary>Parsed <see cref="ReleaseDate"/>; null when the file's date is missing or malformed.</summary>
        [JsonIgnore]
        public DateOnly? Date { get; set; }

        [JsonIgnore]
        public bool IsMinor => Type == "minor";
    }

    private sealed class SubPatchEntry
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("releaseIsProjected")]
        public bool ReleaseIsProjected { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
