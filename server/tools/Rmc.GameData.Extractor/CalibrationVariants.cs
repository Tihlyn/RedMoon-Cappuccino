using System.Text.Json.Nodes;

namespace Rmc.GameData.Extractor;

/// <summary>
/// Builds stat-skewed variants of each job's first BiS set (every meld swapped to one substat,
/// same grade) so the damage model can be checked on stat weights, not just on near-identical
/// BiS sets. Writes our copy (variants/&lt;JOB&gt;.json) and an xivgear sheet export of the same sets
/// (variants/&lt;JOB&gt;.xivgear.json) that xivgear.app opens client-side via ?page=importsheet|…
/// to produce the reference DPS.
/// </summary>
public sealed class CalibrationVariants(GameDataIndex data, TextWriter log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly IReadOnlyDictionary<string, string> SlotToXivgear = new Dictionary<string, string>
    {
        [Slots.Weapon] = "Weapon", [Slots.OffHand] = "OffHand", [Slots.Head] = "Head", [Slots.Body] = "Body",
        [Slots.Hands] = "Hand", [Slots.Legs] = "Legs", [Slots.Feet] = "Feet", [Slots.Ears] = "Ears",
        [Slots.Neck] = "Neck", [Slots.Wrists] = "Wrist", [Slots.RingLeft] = "RingLeft", [Slots.RingRight] = "RingRight",
    };

    private static readonly IReadOnlyDictionary<string, string> RelicToXivgear = new Dictionary<string, string>
    {
        [Stats.Critical] = "crit", [Stats.DirectHit] = "dhit", [Stats.Determination] = "determination",
        [Stats.SkillSpeed] = "skillspeed", [Stats.SpellSpeed] = "spellspeed", [Stats.Tenacity] = "tenacity", [Stats.Piety] = "piety",
    };

    public async Task WriteAsync(string bisDir, string outDir)
    {
        foreach (var file in Directory.EnumerateFiles(bisDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var sheet = GameDataJson.Read<BisSheet>(file);
            var job = data.Job(sheet.Meta.Job)!;
            var baseSet = sheet.Sets[0];

            var stats = new List<string> { Stats.Critical, Stats.Determination, Stats.DirectHit };
            stats.Add(job.Role is "Healer" or "Caster" ? Stats.SpellSpeed : Stats.SkillSpeed);
            if (job.Role == "Tank")
                stats.Add(Stats.Tenacity);

            var variants = new BisSheet { Meta = sheet.Meta, Sets = [baseSet] };
            foreach (var stat in stats)
                variants.Sets.Add(Remeld(baseSet, stat));

            var template = await SheetTemplateAsync(sheet.Meta);
            if (template == null)
            {
                log.WriteLine($"{sheet.Meta.Job}: could not fetch the xivgear sheet to copy its sim settings; skipped.");
                continue;
            }
            template["name"] = $"RMC calibration {sheet.Meta.Job}";
            template["sets"] = new JsonArray(variants.Sets.Select(ToXivgear).ToArray<JsonNode?>());

            GameDataJson.Write(Path.Combine(outDir, $"{sheet.Meta.Job}.json"), variants);
            File.WriteAllText(Path.Combine(outDir, $"{sheet.Meta.Job}.xivgear.json"), template.ToJsonString());
            log.WriteLine($"{sheet.Meta.Job}: {variants.Sets.Count} calibration sets");
        }
    }

    private BisSet Remeld(BisSet set, string stat) => new()
    {
        Name = $"RMC all {stat}",
        FoodItemId = set.FoodItemId,
        ClanId = set.ClanId,
        Slots = set.Slots.ToDictionary(kv => kv.Key, kv => new BisSlot
        {
            ItemId = kv.Value.ItemId,
            RelicStats = kv.Value.RelicStats,
            Materia = kv.Value.Materia.Select(m =>
            {
                var grade = data.Snapshot.Materia.First(x => x.Stat == stat).Grades.First(g => g.Grade == m.Grade);
                return new BisMeld { Stat = stat, Grade = m.Grade, ItemId = grade.ItemId };
            }).ToList(),
        }, StringComparer.Ordinal),
    };

    private JsonObject ToXivgear(BisSet set)
    {
        var items = new JsonObject();
        foreach (var (slot, piece) in set.Slots)
        {
            var node = new JsonObject
            {
                ["id"] = piece.ItemId,
                ["materia"] = new JsonArray(piece.Materia.Select(m => (JsonNode)new JsonObject { ["id"] = m.ItemId, ["locked"] = false }).ToArray()),
            };
            if (piece.RelicStats != null)
                node["relicStats"] = new JsonObject(piece.RelicStats.Select(kv => KeyValuePair.Create(RelicToXivgear[kv.Key], (JsonNode?)kv.Value)));
            items[SlotToXivgear[slot]] = node;
        }
        var result = new JsonObject { ["name"] = set.Name, ["items"] = items };
        if (set.FoodItemId is int food)
            result["food"] = food;
        return result;
    }

    // The sheet export as xivgear serves it, so the variants keep its sim, race and party settings.
    private static async Task<JsonObject?> SheetTemplateAsync(BisMeta meta)
    {
        var src = meta.Source;
        var url = src.Kind switch
        {
            "xivgear-static" => "https://raw.githubusercontent.com/xiv-gear-planner/static-bis-sets/main/" + src.Reference,
            "xivgear-api" => "https://api.xivgear.app/basedata?url=" + Uri.EscapeDataString(src.Reference.Split(',')[0]),
            _ => null,
        };
        if (url == null)
            return null;
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
        if (root == null)
            return null;
        // Single-set exports carry the sheet fields at the top level; keep only sheet-level keys.
        foreach (var key in new[] { "items", "food", "isSeparator", "materiaMemory", "relicStatMemory", "description" })
            root.Remove(key);
        return root;
    }
}
