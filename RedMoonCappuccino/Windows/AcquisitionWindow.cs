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
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;

namespace RedMoonCappuccino.Windows;

public class AcquisitionWindow : Window, IDisposable
{
    private readonly WebSocketService wsService;
    private readonly IDataManager dataManager;
    private uint currentItemId;
    private volatile bool isLoading;
    private AcqResultData? result;

    public AcquisitionWindow(WebSocketService wsService, IDataManager dataManager)
        : base("Where do I find that?##AcqWindow")
    {
        this.wsService   = wsService;
        this.dataManager = dataManager;
        wsService.OnAcqResult += HandleAcqResult;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 180),
            MaximumSize = new Vector2(900, 700),
        };
        Size          = new Vector2(540, 345);
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
        // Show item name (from server response) or fall back to "Item #ID" while loading
        var headerName = result?.Name is { Length: > 0 } n ? n : $"Item #{currentItemId}";
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
            ImGui.TextUnformatted(headerName);
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
            DrawReducesInto();
            return;
        }

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

            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFFFD700u))
                ImGui.TextUnformatted(entries.Count == 1 ? label : $"{label}  ×{entries.Count}");

            ImGui.Spacing();

            for (var i = 0; i < entries.Count; i++)
            {
                var src = entries[i];

                if (entries.Count > 1)
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
                        ImGui.TextUnformatted($"  Entry {i + 1}");
                    ImGui.Spacing();
                }

                ImGui.PushID(g * 100 + i);
                using var indent = ImRaii.PushIndent(12f);
                DrawSourceContent(src);
                ImGui.PopID();

                if (i < entries.Count - 1) { ImGui.Spacing(); ImGui.Spacing(); }
            }

            if (g < grouped.Count - 1)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }

        DrawReducesInto();
        ImGui.Spacing();
    }

    private void DrawReducesInto()
    {
        if (result?.ReducesInto is not { } ri || ri.ValueKind != JsonValueKind.Array) return;
        var items = ri.EnumerateArray().ToList();
        if (items.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
            ImGui.TextUnformatted("Aetherial Reduction yields:");
        using var indent = ImRaii.PushIndent(12f);
        foreach (var item in items)
            ImGui.BulletText(GetStr(item, "name") ?? $"Item #{GetInt(item, "itemId")}");
    }

    // ── Source dispatch ───────────────────────────────────────────────────────

    private void DrawSourceContent(AcqSource src)
    {
        if (src.Extra == null || src.Extra.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAAAAAAu))
                ImGui.TextUnformatted("(no additional data)");
            return;
        }

        switch (src.Type?.ToLowerInvariant())
        {
            case "shop":                DrawShopSource(src.Extra);               break;
            case "gathering":           DrawGatheringSource(src.Extra);          break;
            case "aetherial_reduction": DrawAetherialReductionSource(src.Extra); break;
            case "crafting":            DrawCraftingSource(src.Extra);           break;
            case "drop":                DrawDropSource(src.Extra, dataManager);  break;
            case "instance":            DrawInstanceSource(src.Extra);           break;
            case "fate":                DrawFateSource(src.Extra);               break;
            case "venture":             DrawVentureSource(src.Extra);            break;
            case "gardening":           DrawGardeningSource(src.Extra);          break;
            case "desynthesis":         DrawDesynthesisSource(src.Extra);        break;
            case "fishing":             DrawFishingSource(src.Extra);            break;
            case "quest":               DrawQuestSource(src.Extra);              break;
            case "achievement":         DrawAchievementSource(src.Extra);        break;
            default:                    DrawGenericSource(src.Extra);            break;
        }
    }

    // ── Shop ──────────────────────────────────────────────────────────────────

    private static void DrawShopSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##shop");
        if (!tbl) return;

        if (extra.TryGetValue("currencies", out var currEl) && currEl.ValueKind == JsonValueKind.Array)
        {
            var parts = currEl.EnumerateArray()
                .Select(c => $"{GetInt(c, "amount") ?? 1}× {GetStr(c, "name") ?? "Unknown Currency"}")
                .ToList();
            TR("Cost", string.Join("  +  ", parts));
        }

        if (extra.TryGetValue("npcs", out var npcsEl) && npcsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var npc in npcsEl.EnumerateArray())
            {
                var name   = GetStr(npc, "name")   ?? "Unknown NPC";
                var region = GetStr(npc, "region");
                var zone   = GetStr(npc, "zone");
                var coords = FormatCoords(npc);
                var loc    = string.Join(" › ", new[] { region, zone }.Where(s => !string.IsNullOrEmpty(s)));
                var val    = name;
                if (!string.IsNullOrEmpty(loc))    val += $"  ·  {loc}";
                if (!string.IsNullOrEmpty(coords)) val += $"  ({coords})";
                TR("Vendor", val);
            }
        }
    }

    // ── Gathering ─────────────────────────────────────────────────────────────

    private static void DrawGatheringSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##gath");
        if (!tbl) return;

        if (extra.TryGetValue("gatheringTypes", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array)
        {
            var types = string.Join(", ", typesEl.EnumerateArray()
                .Select(t => FormatGatheringType(t.GetString())).Where(s => s != null));
            if (!string.IsNullOrEmpty(types)) TR("Method", types);
        }

        AddNodeRows(extra);
    }

    // ── Aetherial Reduction ───────────────────────────────────────────────────

    private static void DrawAetherialReductionSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##ar");
        if (!tbl) return;

        if (extra.TryGetValue("sourceItem", out var si) && si.ValueKind == JsonValueKind.Object)
            TR("Reduce", GetStr(si, "name") ?? $"Item #{GetInt(si, "itemId")}");

        AddNodeRows(extra);
    }

    // ── Crafting ─────────────────────────────────────────────────────────────

    private static void DrawCraftingSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##craft");
        if (!tbl) return;

        var job    = extra.TryGetValue("job",   out var jobEl)                          ? jobEl.GetString() : null;
        var level  = extra.TryGetValue("level", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.Number ? lvlEl.GetInt32().ToString() : null;
        var stars  = extra.TryGetValue("stars", out var stEl)  && stEl.ValueKind  == JsonValueKind.Number ? stEl.GetInt32()  : 0;
        var starStr = stars > 0 ? "  " + new string('★', stars) : string.Empty;
        TR("Recipe", $"{job ?? "?"}  Lv.{level ?? "?"}{starStr}");

        var yields = extra.TryGetValue("yields", out var yEl) && yEl.ValueKind == JsonValueKind.Number ? yEl.GetInt32() : 1;
        if (yields > 1) TR("Yields", $"×{yields}");

        if (extra.TryGetValue("expert", out var exEl) && exEl.ValueKind == JsonValueKind.True)
            TR("Expert", "Yes");
    }

    // ── Drop ──────────────────────────────────────────────────────────────────

    private static void DrawDropSource(Dictionary<string, JsonElement> extra, IDataManager dm)
    {
        using var tbl = MakeSourceTable("##drop");
        if (!tbl) return;

        var name = extra.TryGetValue("mobName", out var nEl) && nEl.ValueKind == JsonValueKind.String
            ? nEl.GetString() : null;
        var id   = extra.TryGetValue("mobId", out var iEl) && iEl.ValueKind == JsonValueKind.Number
            ? iEl.GetInt32() : (int?)null;

        // Server may omit the name; resolve it locally via Lumina BNpcName sheet
        if (name == null && id.HasValue)
        {
            var row = dm.GetExcelSheet<BNpcName>()?.GetRow((uint)id.Value);
            if (row.HasValue) name = row.Value.Singular.ExtractText();
        }

        TR("Mob", name ?? (id.HasValue ? $"Mob #{id}" : "Unknown"));

        if (extra.TryGetValue("instanceName", out var instEl) && instEl.ValueKind == JsonValueKind.String)
        {
            var inst = instEl.GetString();
            if (!string.IsNullOrEmpty(inst)) TR("Instance", inst);
        }

        // Spawn positions — group by zone to avoid listing every individual spawn point
        if (extra.TryGetValue("positions", out var posEl) && posEl.ValueKind == JsonValueKind.Array)
        {
            var zones = posEl.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .GroupBy(p => GetStr(p, "zone") ?? string.Empty)
                .ToList();

            foreach (var zoneGroup in zones)
            {
                var first = zoneGroup.First();
                var area  = GetStr(first, "area");
                var zone  = zoneGroup.Key;
                var level = GetInt(first, "level");
                var loc   = string.Join(" › ", new[] { area, zone }.Where(s => !string.IsNullOrEmpty(s)));
                if (level.HasValue) loc += $"  (Lv.{level})";
                TR("Location", loc);
            }
        }
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    private static void DrawInstanceSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##inst");
        if (!tbl) return;

        if (!extra.TryGetValue("instances", out var instEl) || instEl.ValueKind != JsonValueKind.Array) return;

        foreach (var inst in instEl.EnumerateArray())
        {
            var name  = GetStr(inst, "name")        ?? "Unknown";
            var ctRaw = GetStr(inst, "contentType") ?? string.Empty;
            var level = GetInt(inst, "levelRequired");
            var ilvl  = GetInt(inst, "ilvlRequired");

            var val  = name;
            var reqs = new System.Collections.Generic.List<string>();
            if (level is > 1) reqs.Add($"Lv.{level}");
            if (ilvl  is > 0) reqs.Add($"iLvl {ilvl}");
            if (reqs.Count > 0) val += $"  ({string.Join(" · ", reqs)})";

            TR(FormatContentType(ctRaw), val);
        }
    }

    private static string FormatContentType(string ct) => ct switch
    {
        "Raid"         => "Raid",
        "TreasureHunt" => "Treasure Hunt",
        "Trial"        => "Trial",
        "Dungeon"      => "Dungeon",
        "AllianceRaid" => "Alliance Raid",
        "Ultimate"     => "Ultimate",
        "DeepDungeon"  => "Deep Dungeon",
        "Variant"      => "Variant",
        "Criterion"    => "Criterion",
        _              => string.IsNullOrEmpty(ct) ? "Instance" : ct,
    };

    // ── FATE ──────────────────────────────────────────────────────────────────

    private static void DrawFateSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##fate");
        if (!tbl) return;

        var name  = extra.TryGetValue("fateName", out var nEl) ? nEl.GetString() : null;
        var level = extra.TryGetValue("level",    out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : (int?)null;
        var val   = name ?? "Unknown FATE";
        if (level.HasValue) val += $"  (Lv.{level})";
        TR("FATE", val);
    }

    // ── Venture ───────────────────────────────────────────────────────────────

    private static void DrawVentureSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##vent");
        if (!tbl) return;

        TR("Venture", extra.TryGetValue("ventureName", out var nEl) ? nEl.GetString() ?? "Unknown" : "Unknown");
    }

    // ── Gardening ─────────────────────────────────────────────────────────────

    private static void DrawGardeningSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##gard");
        if (!tbl) return;

        if (extra.TryGetValue("seeds", out var seedsEl) && seedsEl.ValueKind == JsonValueKind.Array)
        {
            var seeds = string.Join(", ", seedsEl.EnumerateArray()
                .Select(s => GetStr(s, "name") ?? $"Item #{GetInt(s, "itemId")}"));
            TR("Seeds", seeds);
        }
    }

    // ── Desynthesis ───────────────────────────────────────────────────────────

    private static void DrawDesynthesisSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##des");
        if (!tbl) return;

        if (extra.TryGetValue("sources", out var srcEl) && srcEl.ValueKind == JsonValueKind.Array)
        {
            var items = string.Join(", ", srcEl.EnumerateArray()
                .Select(s => GetStr(s, "name") ?? $"Item #{GetInt(s, "itemId")}"));
            TR("Desynth", items);
        }
    }

    // ── Fishing ───────────────────────────────────────────────────────────────

    private static void DrawFishingSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##fish");
        if (!tbl) return;

        var region = extra.TryGetValue("region", out var rEl) ? rEl.GetString() : null;
        var area   = extra.TryGetValue("area",   out var aEl) ? aEl.GetString() : null;
        var zone   = extra.TryGetValue("zone",   out var zEl) ? zEl.GetString() : null;
        var level  = extra.TryGetValue("level",  out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : (int?)null;
        var coords = FormatCoordsFromDict(extra);

        var loc = string.Join(" › ", new[] { region, area, zone }.Where(s => !string.IsNullOrEmpty(s)));
        if (!string.IsNullOrEmpty(coords)) loc = string.IsNullOrEmpty(loc) ? coords : $"{loc}  ({coords})";
        TR("Spot", loc);
        if (level.HasValue) TR("Level", $"Lv.{level}");
    }

    // ── Quest ─────────────────────────────────────────────────────────────────

    private static void DrawQuestSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##quest");
        if (!tbl) return;

        TR("Quest", extra.TryGetValue("questName", out var nEl) ? nEl.GetString() ?? "Unknown Quest" : "Unknown Quest");
    }

    // ── Achievement ───────────────────────────────────────────────────────────

    private static void DrawAchievementSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##ach");
        if (!tbl) return;

        TR("Achievement", extra.TryGetValue("achievementName", out var nEl) ? nEl.GetString() ?? "Unknown" : "Unknown");
    }

    // ── Generic fallback ──────────────────────────────────────────────────────

    private static void DrawGenericSource(Dictionary<string, JsonElement> extra)
    {
        using var tbl = MakeSourceTable("##gen");
        if (!tbl) return;

        foreach (var kv in extra)
            TR(FormatKey(kv.Key), FormatJsonElement(kv.Value));
    }

    // ── Node row helper (must be called within an open table context) ──────────

    private static void AddNodeRows(Dictionary<string, JsonElement> extra)
    {
        if (extra.TryGetValue("nodesMissing", out var missingEl) && missingEl.ValueKind == JsonValueKind.True &&
            (!extra.TryGetValue("nodes", out var check) || check.ValueKind == JsonValueKind.Null))
        {
            TR("Nodes", "(data not yet available)");
            return;
        }

        if (!extra.TryGetValue("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array) return;

        var nodes = nodesEl.EnumerateArray().ToList();
        for (var n = 0; n < nodes.Count; n++)
        {
            var node      = nodes[n];
            var gtRaw     = GetStr(node, "gatherType");
            var gt        = gtRaw != null ? FormatGatheringType(gtRaw) : null;
            var level     = GetInt(node, "level");
            var area      = GetStr(node, "area");
            var zone      = GetStr(node, "zone");
            var coords    = FormatCoords(node);
            var legendary = GetBool(node, "legendary") == true;
            var ephemeral = GetBool(node, "ephemeral") == true;

            // If node has no gatherType, omit prefix (Method row already shows it)
            var typeLevel = gt != null ? $"{gt} Lv.{level?.ToString() ?? "?"}" : $"Lv.{level?.ToString() ?? "?"}";
            if (legendary) typeLevel += " ★";
            if (ephemeral) typeLevel += " (Ephemeral)";

            var loc   = string.Join(" › ", new[] { area, zone }.Where(s => !string.IsNullOrEmpty(s)));
            var val   = typeLevel;
            if (!string.IsNullOrEmpty(loc))    val += $"  |  {loc}";
            if (!string.IsNullOrEmpty(coords)) val += $"  ({coords})";
            TR(nodes.Count > 1 ? $"Node {n + 1}" : "Node", val);

            // Folklore tome requirement
            if (node.TryGetProperty("folklore", out var folkEl) && folkEl.ValueKind == JsonValueKind.Object)
            {
                var folkName = GetStr(folkEl, "name");
                if (!string.IsNullOrEmpty(folkName)) TR("Folklore", folkName);
            }

            // Timed spawn windows (hours, e.g. [4, 16] → "4:00  ·  16:00")
            if (node.TryGetProperty("spawns", out var spawnsEl) && spawnsEl.ValueKind == JsonValueKind.Array)
            {
                var times = string.Join("  ·  ", spawnsEl.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.Number)
                    .Select(s => $"{s.GetInt32()}:00"));
                if (!string.IsNullOrEmpty(times)) TR("Spawns", times);
            }

            // Active duration in minutes (skip if 0 = no timer data)
            if (node.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number && durEl.GetInt32() > 0)
                TR("Duration", $"{durEl.GetInt32()} min");
        }
    }

    private static string? FormatGatheringType(string? raw) => raw switch
    {
        "type_0" or "Mining"       => "Mining",
        "type_1" or "Quarrying"    => "Quarrying",
        "type_2" or "Logging"      => "Logging",
        "type_3" or "Harvesting"   => "Harvesting",
        "type_4" or "Spearfishing" => "Spearfishing",
        "type_5" or "Fishing"      => "Fishing",
        null or ""                 => null,
        _                          => raw,
    };

    private static string FormatSourceType(string type) => type switch
    {
        "drop"               => "Drop",
        "instance"           => "Instance Drop",
        "fate"               => "FATE",
        "venture"            => "Retainer Venture",
        "gardening"          => "Gardening",
        "desynthesis"        => "Desynthesis",
        "desynth"            => "Desynthesis",
        "aetherial_reduction"=> "Aetherial Reduction",
        "gathering"          => "Gathering",
        "fishing"            => "Fishing",
        "shop"               => "Shop / Vendor",
        "crafting"           => "Crafting",
        "craft"              => "Crafting",
        "quest"              => "Quest",
        "achievement"        => "Achievement",
        "item_ref"           => "Item Reference",
        "extractable"        => "Materia Extraction",
        "fish"               => "Fish",
        "food"               => "Food",
        "gatherableItem"     => "Gatherable Item",
        "eventItem"          => "Event Item",
        _                    => type,
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

    // ── Table helpers ─────────────────────────────────────────────────────────

    private readonly struct TableScope : IDisposable
    {
        private readonly bool opened;
        public TableScope(string id)
        {
            opened = ImGui.BeginTable(id, 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, 0));
            if (opened)
            {
                ImGui.TableSetupColumn("K", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("V", ImGuiTableColumnFlags.WidthStretch);
            }
        }
        public static implicit operator bool(TableScope t) => t.opened;
        public void Dispose() { if (opened) ImGui.EndTable(); }
    }

    private static TableScope MakeSourceTable(string id) => new(id);

    private static void TR(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFCCCCCCu))
            ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }

    // ── JsonElement accessors ─────────────────────────────────────────────────

    private static string? GetStr(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(key, out var p) &&
        p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(key, out var p) &&
        p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static bool? GetBool(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var p)) return null;
        return p.ValueKind == JsonValueKind.True ? true : p.ValueKind == JsonValueKind.False ? false : null;
    }

    private static string? FormatCoords(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("coords", out var coords) ||
            coords.ValueKind != JsonValueKind.Object) return null;
        if (!coords.TryGetProperty("x", out var xEl) || !coords.TryGetProperty("y", out var yEl)) return null;
        if (xEl.ValueKind == JsonValueKind.Null || yEl.ValueKind == JsonValueKind.Null) return null;
        return $"X:{xEl.GetDouble():F1} Y:{yEl.GetDouble():F1}";
    }

    private static string? FormatCoordsFromDict(Dictionary<string, JsonElement> extra)
    {
        if (!extra.TryGetValue("coords", out var coords) || coords.ValueKind != JsonValueKind.Object) return null;
        if (!coords.TryGetProperty("x", out var xEl) || !coords.TryGetProperty("y", out var yEl)) return null;
        if (xEl.ValueKind == JsonValueKind.Null || yEl.ValueKind == JsonValueKind.Null) return null;
        return $"X:{xEl.GetDouble():F1} Y:{yEl.GetDouble():F1}";
    }
}
