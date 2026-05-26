using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;

namespace RedMoonCappuccino.Windows;

public class AcquisitionWindow : Window, IDisposable
{
    private readonly WebSocketService wsService;
    private uint currentItemId;
    private volatile bool isLoading;
    private AcqResultData? result;

    public AcquisitionWindow(WebSocketService wsService)
        : base("Where do I find that?##AcqWindow")
    {
        this.wsService = wsService;
        wsService.OnAcqResult += HandleAcqResult;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 220),
            MaximumSize = new Vector2(900, 800),
        };
        Size          = new Vector2(540, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void ShowForItem(uint itemId)
    {
        currentItemId = itemId;
        result        = null;
        isLoading     = true;
        IsOpen        = true;
    }

    private void HandleAcqResult(AcqResultMessage msg)
    {
        if (msg.Data == null || (uint)msg.Data.ItemId != currentItemId) return;
        result    = msg.Data;
        isLoading = false;
    }

    public void Dispose()
    {
        wsService.OnAcqResult -= HandleAcqResult;
    }

    public override void Draw()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
            ImGui.TextUnformatted($"Item ID:  {currentItemId}");
        ImGui.Separator();
        ImGui.Spacing();

        if (isLoading)
        {
            ImGui.TextUnformatted("Querying server...");
            return;
        }

        if (result == null)
        {
            ImGui.TextUnformatted("No response received.");
            return;
        }

        if (result.Sources.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
                ImGui.TextUnformatted("No acquisition sources found for this item.");
            return;
        }

        // Scrollable area that fills remaining window space
        using var scroll = ImRaii.Child("##acqScroll", new Vector2(0, 0), true);
        if (!scroll) return;

        ImGui.Spacing();

        var grouped = result.Sources
            .GroupBy(s => s.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var g = 0; g < grouped.Count; g++)
        {
            var group   = grouped[g];
            var entries = group.ToList();
            var label   = FormatSourceType(group.Key);

            // ── Section header ────────────────────────────────────────────────
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
                ImGui.TextUnformatted(entries.Count == 1 ? label : $"{label}  \u00d7{entries.Count}");

            ImGui.Spacing();

            for (var i = 0; i < entries.Count; i++)
            {
                var src = entries[i];

                // Entry index badge when there are multiple of same type
                if (entries.Count > 1)
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
                        ImGui.TextUnformatted($"  Entry {i + 1}");
                    ImGui.Spacing();
                }

                if (src.Extra is { Count: > 0 })
                {
                    using var indent = ImRaii.PushIndent(12f);
                    using var table  = ImRaii.Table(
                        $"##t_{g}_{i}", 2,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);

                    if (table)
                    {
                        ImGui.TableSetupColumn("K", ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
                        ImGui.TableSetupColumn("V", ImGuiTableColumnFlags.WidthStretch);

                        foreach (var kv in src.Extra)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFCCCCCCu))
                                ImGui.TextUnformatted(FormatKey(kv.Key));
                            ImGui.TableNextColumn();
                            ImGui.TextWrapped(FormatJsonElement(kv.Value));
                        }
                    }
                }
                else
                {
                    using var indent = ImRaii.PushIndent(12f);
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
                        ImGui.TextUnformatted("(no additional data)");
                }

                if (i < entries.Count - 1)
                {
                    ImGui.Spacing();
                    ImGui.Spacing();
                }
            }

            if (g < grouped.Count - 1)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }

        ImGui.Spacing();
    }

    private static string FormatSourceType(string type) => type switch
    {
        "drop"          => "Drop",
        "item_ref"      => "Item Reference",
        "craft"         => "Crafting",
        "gathering"     => "Gathering",
        "fishing"       => "Fishing",
        "shop"          => "Shop / Vendor",
        "venture"       => "Retainer Venture",
        "fate"          => "FATE",
        "desynth"       => "Desynthesis",
        "gardening"     => "Gardening",
        "extractable"   => "Materia Extraction",
        "fish"          => "Fish",
        "food"          => "Food",
        "gatherableItem"=> "Gatherable Item",
        "eventItem"     => "Event Item",
        _               => type,
    };

    // Converts camelCase / snake_case keys into readable "Title Case" labels.
    private static string FormatKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;

        if (key.Contains('_'))
        {
            return string.Join(" ", key.Split('_')
                .Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
        }

        var sb = new StringBuilder();
        sb.Append(char.ToUpperInvariant(key[0]));
        for (var i = 1; i < key.Length; i++)
        {
            if (char.IsUpper(key[i])) sb.Append(' ');
            sb.Append(key[i]);
        }
        return sb.ToString();
    }

    private static string FormatJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True   => "Yes",
        JsonValueKind.False  => "No",
        JsonValueKind.Null   => "(none)",
        JsonValueKind.Array  => string.Join(", ", el.EnumerateArray().Select(FormatJsonElement)),
        _                    => el.GetRawText(),
    };
}
