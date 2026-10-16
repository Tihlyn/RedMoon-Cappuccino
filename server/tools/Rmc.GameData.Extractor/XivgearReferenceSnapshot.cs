using System.Text.Json.Nodes;

namespace Rmc.GameData.Extractor;

/// <summary>
/// Pulls the xivgear data API rows our checks compare against and stores them in our own
/// model shapes under data/reference/xivgear. Run by hand while developing; the checks only
/// read the committed files, so they never depend on xivgear being up.
/// </summary>
public sealed class XivgearReferenceSnapshot(GameDataIndex data, TextWriter log)
{
    private const string Api = "https://data.xivgear.app/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly (string Key, string Field)[] ItemLevelFields =
    [
        (Stats.Strength, "strength"), (Stats.Dexterity, "dexterity"), (Stats.Vitality, "vitality"),
        (Stats.Intelligence, "intelligence"), (Stats.Mind, "mind"), (Stats.Piety, "piety"),
        (Stats.Tenacity, "tenacity"), (Stats.DirectHit, "directHitRate"), (Stats.Critical, "criticalHit"),
        (Stats.Determination, "determination"), (Stats.SkillSpeed, "skillSpeed"), (Stats.SpellSpeed, "spellSpeed"),
        ("hp", "HP"), ("wdPhys", "physicalDamage"), ("wdMag", "magicalDamage"),
    ];

    private static readonly (string CapSlot, string Field)[] SlotFields =
    [
        (CapSlots.OneHandWeapon, "oneHandWeaponPercent"), (CapSlots.TwoHandWeapon, "twoHandWeaponPercent"),
        (CapSlots.OffHand, "offHandPercent"), (CapSlots.Head, "headPercent"), (CapSlots.Chest, "chestPercent"),
        (CapSlots.Hands, "handsPercent"), (CapSlots.Legs, "legsPercent"), (CapSlots.Feet, "feetPercent"),
        (CapSlots.Earring, "earringPercent"), (CapSlots.Necklace, "necklacePercent"), (CapSlots.Bracelet, "braceletPercent"),
        (CapSlots.Ring, "ringPercent"), (CapSlots.ChestHead, "chestHeadPercent"),
        (CapSlots.ChestHeadLegsFeet, "chestHeadLegsFeetPercent"), (CapSlots.LegsFeet, "legsFeetPercent"),
        (CapSlots.HeadChestHandsLegsFeet, "headChestHandsLegsFeetPercent"),
        (CapSlots.ChestLegsGloves, "chestLegsGlovesPercent"), (CapSlots.ChestLegsFeet, "chestLegsFeetPercent"),
    ];

    public async Task WriteAsync(string outDir, string bisDir)
    {
        var baseParams = (await GetItemsAsync("BaseParams"))
            .Where(r => Stats.ByBaseParamId.ContainsKey(Id(r)))
            .Select(r => new BaseParamInfo
            {
                Id = (int)Id(r),
                Key = Stats.ByBaseParamId[Id(r)],
                Name = r["name"]?.GetValue<string>() ?? string.Empty,
                MeldParam = r["meldParam"]!.AsArray().Select(v => v!.GetValue<int>()).ToList(),
                SlotPercent = SlotFields.ToDictionary(f => f.CapSlot, f => r[f.Field]?.GetValue<int>() ?? 0, StringComparer.Ordinal),
            })
            .OrderBy(b => b.Id)
            .ToList();

        var wantedIlvls = data.Snapshot.ItemLevels.Select(l => l.ItemLevel).ToHashSet();
        var itemLevels = (await GetItemsAsync("ItemLevel"))
            .Where(r => wantedIlvls.Contains((int)Id(r)))
            .Select(r => new ItemLevelInfo
            {
                ItemLevel = (int)Id(r),
                Stats = ItemLevelFields.ToDictionary(f => f.Key, f => r[f.Field]?.GetValue<int>() ?? 0, StringComparer.Ordinal),
            })
            .OrderBy(l => l.ItemLevel)
            .ToList();

        var materia = new List<MateriaInfo>();
        foreach (var r in await GetItemsAsync("Materia"))
        {
            var bp = (uint)(r["baseParam"]?.GetValue<int>() ?? 0);
            if (!Stats.ByBaseParamId.TryGetValue(bp, out var stat) || !Stats.CombatSubstats.Contains(stat))
                continue;
            var values = r["value"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            var items = r["item"]!.AsArray().Select(i => i?["rowId"]?.GetValue<int>() ?? 0).ToArray();
            var grades = Enumerable.Range(0, Math.Min(values.Length, items.Length))
                .Where(i => items[i] != 0 && values[i] > 0)
                .Select(i => new MateriaGrade { Grade = i + 1, ItemId = items[i], Value = values[i] })
                .ToList();
            if (grades.Count > 0)
                materia.Add(new MateriaInfo { Id = (int)Id(r), Stat = stat, Grades = grades });
        }

        var items_ = await BisItemsAsync(bisDir);
        var computed = await ComputedStatsAsync(bisDir, Path.Combine(outDir, "computed"));

        GameDataJson.Write(Path.Combine(outDir, "baseparams.json"), baseParams);
        GameDataJson.Write(Path.Combine(outDir, "itemlevels.json"), itemLevels);
        GameDataJson.Write(Path.Combine(outDir, "materia.json"), materia.OrderBy(m => m.Id).ToList());
        GameDataJson.Write(Path.Combine(outDir, "items.json"), items_);
        GameDataJson.Write(Path.Combine(outDir, "_meta.json"), new Dictionary<string, string>
        {
            ["source"] = Api,
            ["fetchedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["snapshotPatch"] = data.Snapshot.Meta.Patch,
        });
        log.WriteLine($"Wrote xivgear reference: {baseParams.Count} base params, {itemLevels.Count} item levels, " +
                      $"{materia.Count} materia, {items_.Count} BiS items, computed stats for {computed} BiS sets -> {outDir}");
    }

    /// <summary>Every item used in our committed BiS sheets, fetched per job (the API requires a job).</summary>
    private async Task<List<GearItem>> BisItemsAsync(string bisDir)
    {
        var byJob = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(bisDir))
        {
            foreach (var file in Directory.EnumerateFiles(bisDir, "*.json", SearchOption.AllDirectories))
            {
                var sheet = GameDataJson.Read<BisSheet>(file);
                var ids = byJob.TryGetValue(sheet.Meta.Job, out var set) ? set : byJob[sheet.Meta.Job] = [];
                foreach (var slot in sheet.Sets.SelectMany(s => s.Slots.Values))
                    ids.Add(slot.ItemId);
            }
        }

        var result = new Dictionary<int, GearItem>();
        foreach (var (job, ids) in byJob)
        {
            var query = $"Items?job={job}&" + string.Join('&', ids.Select(i => $"itemIds={i}"));
            foreach (var r in await GetItemsAsync(query))
            {
                var id = (int)Id(r);
                if (result.ContainsKey(id))
                    continue;
                result[id] = new GearItem
                {
                    Id = id,
                    Name = r["name"]?.GetValue<string>() ?? string.Empty,
                    ItemLevel = r["ilvl"]?.GetValue<int>() ?? 0,
                    CanBeHq = r["canBeHq"]?.GetValue<bool>() ?? false,
                    Unique = r["unique"]?.GetValue<bool>() ?? false,
                    MateriaSlots = r["materiaSlotCount"]?.GetValue<int>() ?? 0,
                    AdvancedMelding = r["advancedMeldingPermitted"]?.GetValue<bool>() ?? false,
                    MeldParamIndex = r["meldParamIndex"]?.GetValue<int>() ?? 0,
                    Stats = ParamMap(r["baseParamMap"]),
                    StatsHq = ParamMap(r["baseParamMapHQ"]),
                    Weapon = (r["damagePhys"]?.GetValue<int>() ?? 0) > 0 || (r["damageMag"]?.GetValue<int>() ?? 0) > 0
                        ? new WeaponInfo
                        {
                            PhysicalDamage = r["damagePhys"]?.GetValue<int>() ?? 0,
                            MagicalDamage = r["damageMag"]?.GetValue<int>() ?? 0,
                            PhysicalDamageHq = r["damagePhysHQ"]?.GetValue<int>() ?? 0,
                            MagicalDamageHq = r["damageMagHQ"]?.GetValue<int>() ?? 0,
                            DelayMs = r["delayMs"]?.GetValue<int>() ?? 0,
                        }
                        : null,
                };
            }
        }
        return result.Values.OrderBy(i => i.Id).ToList();
    }

    /// <summary>Fields of xivgear's computedStats the stat engine checks are compared on.</summary>
    public static readonly string[] ComputedFields =
    [
        "strength", "dexterity", "vitality", "intelligence", "mind", "piety", "crit", "determination", "dhit",
        "skillspeed", "spellspeed", "tenacity", "wdPhys", "wdMag", "weaponDelay", "hp", "mpPerTick",
        "critChance", "critMulti", "dhitChance", "detMulti", "mainStatMulti", "aaStatMulti", "wdMulti", "aaMulti",
        "tncMulti", "tncIncomingMulti", "sksDotMulti", "spsDotMulti", "autoDhBonus",
    ];

    /// <summary>
    /// xivgear's /fulldata for every committed BiS sheet: its computed stats per set, in sheet
    /// order. The stat engine must reproduce these exactly.
    /// </summary>
    private async Task<int> ComputedStatsAsync(string bisDir, string outDir)
    {
        if (!Directory.Exists(bisDir))
            return 0;
        var total = 0;
        foreach (var file in Directory.EnumerateFiles(bisDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var sheet = GameDataJson.Read<BisSheet>(file);
            var queries = sheet.Meta.Source.Kind switch
            {
                "xivgear-static" => [$"page=bis|{sheet.Meta.Source.Reference.Replace(".json", string.Empty).Replace('/', '|')}"],
                "xivgear-api" => sheet.Meta.Source.Reference.Split(',').Select(u => "url=" + Uri.EscapeDataString(u)).ToArray(),
                _ => Array.Empty<string>(),
            };

            var sets = new List<ComputedReference>();
            foreach (var query in queries)
            {
                var json = await Http.GetStringAsync("https://api.xivgear.app/fulldata?" + query);
                foreach (var set in JsonNode.Parse(json)!["sets"]!.AsArray().OfType<JsonObject>())
                {
                    if (set["isSeparator"]?.GetValue<bool>() == true || set["computedStats"] is not JsonObject cs)
                        continue;
                    sets.Add(new ComputedReference
                    {
                        Name = set["name"]?.GetValue<string>() ?? string.Empty,
                        Stats = ComputedFields.Where(f => cs[f] != null).ToDictionary(f => f, f => cs[f]!.GetValue<double>(), StringComparer.Ordinal),
                    });
                }
            }

            if (sets.Count != sheet.Sets.Count)
                log.WriteLine($"{sheet.Meta.Job}: xivgear returned {sets.Count} sets, our sheet has {sheet.Sets.Count}; skipping.");
            else
            {
                GameDataJson.Write(Path.Combine(outDir, $"{sheet.Meta.Job}_{Path.GetFileNameWithoutExtension(file)}.json"), sets);
                total += sets.Count;
            }
        }
        return total;
    }

    private static Dictionary<string, int> ParamMap(JsonNode? node)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (node is JsonObject obj)
        {
            foreach (var (k, v) in obj)
            {
                if (uint.TryParse(k, out var id) && Stats.ByBaseParamId.TryGetValue(id, out var key) && v?.GetValue<int>() is int value and not 0)
                    result[key] = value;
            }
        }
        return result;
    }

    private static uint Id(JsonObject row) => (uint)(row["rowId"]?.GetValue<int>() ?? 0);

    private static async Task<List<JsonObject>> GetItemsAsync(string path)
    {
        var json = await Http.GetStringAsync(Api + path);
        return JsonNode.Parse(json)?["items"]?.AsArray().OfType<JsonObject>().ToList()
               ?? throw new InvalidDataException($"{path}: no 'items' in response.");
    }
}

public sealed class ComputedReference
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, double> Stats { get; set; } = new(StringComparer.Ordinal);
}
