using System.Reflection;
using Lumina;
using LuminaGameData = Lumina.GameData;
using Lumina.Excel.Sheets;

namespace Rmc.GameData.Extractor;

public sealed class ExtractOptions
{
    public required string SqpackPath { get; init; }
    public required string Patch { get; init; }
    public required string OverlayDirectory { get; init; }
    /// <summary>Equip level of the gear to export; 0 means the highest equip level found.</summary>
    public int Level { get; init; }
    public int MinItemLevel { get; init; }
    public int MinFoodItemLevel { get; init; }
}

/// <summary>Builds a <see cref="GameDataSnapshot"/> from the local game files.</summary>
public sealed class LuminaExtractor(ExtractOptions options, TextWriter log)
{
    private const uint MealUiCategory = 46;

    /// <summary>Currencies the last extraction couldn't classify; recorded in the snapshot meta.</summary>
    public IReadOnlyList<string> UnclassifiedCurrencies { get; private set; } = [];
    private List<CurrencyInfo> Currencies { get; set; } = [];
    private const uint WellFedStatus = 48;
    private const uint MedicineUiCategory = 44;
    private const uint MedicatedStatus = 49;

    private static readonly IReadOnlyDictionary<byte, string> RoleById = new Dictionary<byte, string>
    {
        [1] = "Tank", [2] = "Melee", [3] = "Ranged", [4] = "Healer",
    };

    public GameDataSnapshot Extract()
    {
        var game = new LuminaGameData(options.SqpackPath, new LuminaOptions { PanicOnSheetChecksumMismatch = true });
        var gameVersionFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.SqpackPath).TrimEnd('\\', '/'))!, "ffxivgame.ver");

        var jobs = ExtractJobs(game);
        var jobAbbreviations = jobs.Select(j => j.Abbreviation).ToArray();
        var level = options.Level > 0 ? options.Level : MaxEquipLevel(game);
        var items = ExtractItems(game, jobAbbreviations, level);

        var snapshot = new GameDataSnapshot
        {
            Meta = new SnapshotMeta
            {
                Patch = options.Patch,
                GameVersion = File.Exists(gameVersionFile) ? File.ReadAllText(gameVersionFile).Trim() : "unknown",
                GeneratedAtUtc = DateTime.UtcNow,
                Generator = "rmc-extract (Lumina)",
                MaxLevel = level,
                MinItemLevel = options.MinItemLevel,
                UnclassifiedCurrencies = UnclassifiedCurrencies.ToList(),
            },
            Levels = ExtractLevels(game),
            ClassJobs = jobs,
            Clans = ExtractClans(game),
            BaseParams = ExtractBaseParams(game),
            ItemLevels = ExtractItemLevels(game, items.Select(i => i.ItemLevel).ToHashSet()),
            Materia = ExtractMateria(game),
            Foods = ExtractConsumables(game, MealUiCategory, WellFedStatus, b => Stats.CombatSubstats.Contains(b.Stat)),
            Medicines = ExtractConsumables(game, MedicineUiCategory, MedicatedStatus, b => Stats.MainStats.Contains(b.Stat)),
            Items = items,
            Currencies = Currencies,
        };

        log.WriteLine($"Extracted {snapshot.Items.Count} items, {snapshot.Foods.Count} foods, {snapshot.Medicines.Count} medicines, {snapshot.Materia.Count} materia types, " +
                      $"{snapshot.ClassJobs.Count} jobs, {snapshot.ItemLevels.Count} item levels (game {snapshot.Meta.GameVersion}).");
        return snapshot;
    }

    private static Lumina.Excel.ExcelSheet<T> Sheet<T>(LuminaGameData game) where T : struct, Lumina.Excel.IExcelRow<T>
        => game.GetExcelSheet<T>() ?? throw new InvalidDataException($"Sheet {typeof(T).Name} is missing from the game data.");

    private static int MaxEquipLevel(LuminaGameData game)
        => Sheet<Item>(game).Where(i => i.EquipSlotCategory.RowId != 0 && !i.IsPvP).Max(i => (int)i.LevelEquip);

    private List<LevelInfo> ExtractLevels(LuminaGameData game)
    {
        var overlay = GameDataJson.Read<List<LevelInfo>>(Path.Combine(options.OverlayDirectory, "levels.json"));
        var grow = Sheet<ParamGrow>(game);
        var result = new List<LevelInfo>();
        foreach (var entry in overlay.OrderBy(l => l.Level))
        {
            var row = grow.GetRowOrDefault((uint)entry.Level)
                      ?? throw new InvalidDataException($"ParamGrow has no row for level {entry.Level}.");
            result.Add(new LevelInfo
            {
                Level = entry.Level,
                LevelDiv = row.LevelModifier,
                BaseSpeed = row.BaseSpeed,
                BaseMainStat = entry.BaseMainStat,
                BaseSubStat = entry.BaseSubStat,
                Hp = entry.Hp,
                HpScalarTank = entry.HpScalarTank,
                HpScalarOther = entry.HpScalarOther,
                MainStatPowerModTank = entry.MainStatPowerModTank,
                MainStatPowerModOther = entry.MainStatPowerModOther,
            });
        }
        return result;
    }

    private static List<ClassJobInfo> ExtractJobs(LuminaGameData game)
    {
        var result = new List<ClassJobInfo>();
        foreach (var row in Sheet<ClassJob>(game))
        {
            // Jobs only (not their base classes), combat only.
            if (row.JobIndex == 0 || !RoleById.TryGetValue(row.Role, out var role))
                continue;
            if (!Stats.ByBaseParamId.TryGetValue(row.PrimaryStat, out var mainStat))
                continue;
            if (role == "Ranged" && mainStat == Stats.Intelligence)
                role = "Caster";

            result.Add(new ClassJobInfo
            {
                Id = (int)row.RowId,
                Abbreviation = row.Abbreviation.ExtractText(),
                Name = row.NameEnglish.ExtractText(),
                Role = role,
                MainStat = mainStat,
                Limited = row.IsLimitedJob,
                Modifiers = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["hp"] = row.ModifierHitPoints,
                    ["mp"] = row.ModifierManaPoints,
                    [Stats.Strength] = row.ModifierStrength,
                    [Stats.Vitality] = row.ModifierVitality,
                    [Stats.Dexterity] = row.ModifierDexterity,
                    [Stats.Intelligence] = row.ModifierIntelligence,
                    [Stats.Mind] = row.ModifierMind,
                    [Stats.Piety] = row.ModifierPiety,
                },
            });
        }
        return result.OrderBy(j => j.Id).ToList();
    }

    private static List<ClanInfo> ExtractClans(LuminaGameData game)
        => Sheet<Tribe>(game)
            .Where(t => t.RowId != 0 && !t.Masculine.IsEmpty)
            .Select(t => new ClanInfo
            {
                Id = (int)t.RowId,
                Name = t.Masculine.ExtractText(),
                Modifiers = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [Stats.Strength] = t.STR,
                    [Stats.Dexterity] = t.DEX,
                    [Stats.Vitality] = t.VIT,
                    [Stats.Intelligence] = t.INT,
                    [Stats.Mind] = t.MND,
                    [Stats.Piety] = t.PIE,
                },
            })
            .ToList();

    private static List<BaseParamInfo> ExtractBaseParams(LuminaGameData game)
    {
        var sheet = Sheet<BaseParam>(game);
        var result = new List<BaseParamInfo>();
        foreach (var (id, key) in Stats.ByBaseParamId.OrderBy(kv => kv.Key))
        {
            var bp = sheet.GetRow(id);
            result.Add(new BaseParamInfo
            {
                Id = (int)id,
                Key = key,
                Name = bp.Name.ExtractText(),
                MeldParam = bp.MeldParam.Select(v => (int)v).ToList(),
                SlotPercent = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [CapSlots.OneHandWeapon] = bp.OneHandWeaponPercent,
                    [CapSlots.TwoHandWeapon] = bp.TwoHandWeaponPercent,
                    [CapSlots.OffHand] = bp.OffHandPercent,
                    [CapSlots.Head] = bp.HeadPercent,
                    [CapSlots.Chest] = bp.ChestPercent,
                    [CapSlots.Hands] = bp.HandsPercent,
                    [CapSlots.Legs] = bp.LegsPercent,
                    [CapSlots.Feet] = bp.FeetPercent,
                    [CapSlots.Earring] = bp.EarringPercent,
                    [CapSlots.Necklace] = bp.NecklacePercent,
                    [CapSlots.Bracelet] = bp.BraceletPercent,
                    [CapSlots.Ring] = bp.RingPercent,
                    [CapSlots.ChestHead] = bp.ChestHeadPercent,
                    [CapSlots.ChestHeadLegsFeet] = bp.ChestHeadLegsFeetPercent,
                    [CapSlots.LegsFeet] = bp.LegsFeetPercent,
                    [CapSlots.HeadChestHandsLegsFeet] = bp.HeadChestHandsLegsFeetPercent,
                    [CapSlots.ChestLegsGloves] = bp.ChestLegsGlovesPercent,
                    [CapSlots.ChestLegsFeet] = bp.ChestLegsFeetPercent,
                },
            });
        }
        return result;
    }

    private static List<ItemLevelInfo> ExtractItemLevels(LuminaGameData game, IReadOnlySet<int> wanted)
    {
        var sheet = Sheet<ItemLevel>(game);
        return wanted.Order()
            .Select(ilvl => sheet.GetRow((uint)ilvl))
            .Select(r => new ItemLevelInfo
            {
                ItemLevel = (int)r.RowId,
                Stats = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [Stats.Strength] = r.Strength,
                    [Stats.Dexterity] = r.Dexterity,
                    [Stats.Vitality] = r.Vitality,
                    [Stats.Intelligence] = r.Intelligence,
                    [Stats.Mind] = r.Mind,
                    [Stats.Piety] = r.Piety,
                    [Stats.Tenacity] = r.Tenacity,
                    [Stats.DirectHit] = r.DirectHitRate,
                    [Stats.Critical] = r.CriticalHit,
                    [Stats.Determination] = r.Determination,
                    [Stats.SkillSpeed] = r.SkillSpeed,
                    [Stats.SpellSpeed] = r.SpellSpeed,
                    ["hp"] = r.HP,
                    ["wdPhys"] = r.PhysicalDamage,
                    ["wdMag"] = r.MagicalDamage,
                },
            })
            .ToList();
    }

    private static List<MateriaInfo> ExtractMateria(LuminaGameData game)
    {
        var result = new List<MateriaInfo>();
        foreach (var row in Sheet<Materia>(game))
        {
            if (!Stats.ByBaseParamId.TryGetValue(row.BaseParam.RowId, out var stat) || !Stats.CombatSubstats.Contains(stat))
                continue;

            var grades = new List<MateriaGrade>();
            for (var i = 0; i < row.Item.Count && i < row.Value.Count; i++)
            {
                var item = row.Item[i];
                if (item.RowId == 0 || row.Value[i] <= 0)
                    continue;
                grades.Add(new MateriaGrade
                {
                    Grade = i + 1,
                    ItemId = (int)item.RowId,
                    Name = item.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    Value = row.Value[i],
                });
            }

            if (grades.Count > 0)
                result.Add(new MateriaInfo { Id = (int)row.RowId, Stat = stat, Grades = grades });
        }
        return result;
    }

    /// <summary>
    /// Meals and medicine share one shape: the item's action applies a status (Well Fed /
    /// Medicated) whose ItemFood row lists the bonuses; Data[2] is the duration in seconds.
    /// </summary>
    private List<FoodInfo> ExtractConsumables(LuminaGameData game, uint uiCategory, uint status, Func<FoodBonus, bool> relevant)
    {
        var foodSheet = Sheet<ItemFood>(game);
        var result = new List<FoodInfo>();
        foreach (var item in Sheet<Item>(game))
        {
            if (item.ItemUICategory.RowId != uiCategory || (int)item.LevelItem.RowId < options.MinFoodItemLevel)
                continue;
            var action = item.ItemAction.ValueNullable;
            if (action is not { } a || a.Data.Count < 3 || a.Data[0] != status)
                continue;
            if (foodSheet.GetRowOrDefault(a.Data[1]) is not { } food)
                continue;

            var bonuses = new List<FoodBonus>();
            foreach (var p in food.Params)
            {
                if (p.BaseParam.RowId == 0)
                    continue;
                // Food also feeds stats the planner doesn't track (e.g. CP); keep only ours.
                if (!Stats.ByBaseParamId.TryGetValue(p.BaseParam.RowId, out var stat))
                    continue;
                bonuses.Add(new FoodBonus
                {
                    Stat = stat,
                    Relative = p.IsRelative,
                    Value = p.Value,
                    Max = p.Max,
                    ValueHq = p.ValueHQ,
                    MaxHq = p.MaxHQ,
                });
            }

            if (bonuses.Count == 0 || !bonuses.Any(relevant))
                continue;

            result.Add(new FoodInfo
            {
                ItemId = (int)item.RowId,
                Name = item.Name.ExtractText(),
                ItemLevel = (int)item.LevelItem.RowId,
                CanBeHq = item.CanBeHq,
                DurationSeconds = a.Data[2],
                Bonuses = bonuses,
            });
        }
        return result.OrderBy(f => f.ItemId).ToList();
    }

    private List<GearItem> ExtractItems(LuminaGameData game, IReadOnlyList<string> jobAbbreviations, int level)
    {
        var crafted = Sheet<Recipe>(game).Select(r => r.ItemResult.RowId).Where(id => id != 0).ToHashSet();
        var acquisition = new AcquisitionResolver(game, crafted, options.OverlayDirectory);
        var jobFlags = jobAbbreviations
            .Select(abbr => (abbr, prop: typeof(ClassJobCategory).GetProperty(abbr, BindingFlags.Public | BindingFlags.Instance)))
            .Where(x => x.prop?.PropertyType == typeof(bool))
            .ToArray();

        var result = new List<GearItem>();
        var unknownShapes = 0;
        foreach (var item in Sheet<Item>(game))
        {
            if (item.EquipSlotCategory.RowId == 0 || item.IsPvP || item.LevelEquip != level)
                continue;
            if ((int)item.LevelItem.RowId < options.MinItemLevel)
                continue;
            if (item.EquipSlotCategory.ValueNullable is not { } esc || esc.SoulCrystal != 0)
                continue;

            object? category = item.ClassJobCategory.ValueNullable;
            if (category == null)
                continue;
            var jobs = jobFlags.Where(j => (bool)j.prop!.GetValue(category)!).Select(j => j.abbr).ToList();
            if (jobs.Count == 0)
                continue;

            var shape = SlotShape(esc);
            if (shape == null)
            {
                unknownShapes++;
                continue;
            }

            var stats = ReadParams(item.BaseParam.Select(p => p.RowId), item.BaseParamValue);
            // Replicas and other glamour-only gear carry no stats.
            if (stats.Count == 0)
                continue;
            var statsHq = new Dictionary<string, int>(stats, StringComparer.Ordinal);
            var weaponHqPhys = 0;
            var weaponHqMag = 0;
            if (item.CanBeHq)
            {
                for (var i = 0; i < item.BaseParamSpecial.Count && i < item.BaseParamValueSpecial.Count; i++)
                {
                    var id = item.BaseParamSpecial[i].RowId;
                    var value = item.BaseParamValueSpecial[i];
                    if (id == Stats.PhysicalDamageParam)
                        weaponHqPhys += value;
                    else if (id == Stats.MagicalDamageParam)
                        weaponHqMag += value;
                    else if (Stats.ByBaseParamId.TryGetValue(id, out var key))
                        statsHq[key] = statsHq.GetValueOrDefault(key) + value;
                }
            }

            var id32 = (int)item.RowId;
            var isCrafted = crafted.Contains(item.RowId);
            var source = acquisition.Resolve(item.RowId);
            result.Add(new GearItem
            {
                Id = id32,
                Name = item.Name.ExtractText(),
                ItemLevel = (int)item.LevelItem.RowId,
                EquipLevel = item.LevelEquip,
                Slot = shape.Value.Slot,
                CapSlot = shape.Value.CapSlot,
                Jobs = jobs,
                CanBeHq = item.CanBeHq,
                Unique = item.IsUnique,
                Crafted = isCrafted,
                MateriaSlots = item.MateriaSlotCount,
                AdvancedMelding = item.IsAdvancedMeldingPermitted,
                MeldParamIndex = item.BaseParamModifier,
                Stats = stats,
                StatsHq = statsHq,
                Weapon = shape.Value.Slot == Slots.Weapon || item.DamagePhys > 0 || item.DamageMag > 0
                    ? new WeaponInfo
                    {
                        PhysicalDamage = item.DamagePhys,
                        MagicalDamage = item.DamageMag,
                        PhysicalDamageHq = item.DamagePhys + weaponHqPhys,
                        MagicalDamageHq = item.DamageMag + weaponHqMag,
                        DelayMs = item.Delayms,
                    }
                    : null,
                Source = source,
            });
        }

        if (acquisition.UnclassifiedCurrencies.Count > 0)
        {
            log.WriteLine($"Currencies no rule in overlays/currencies.json matches (items they buy stay Unknown):");
            foreach (var (name, count) in acquisition.UnclassifiedCurrencies.OrderByDescending(kv => kv.Value))
                log.WriteLine($"  {name} ({count} items)");
        }
        UnclassifiedCurrencies = acquisition.UnclassifiedCurrencies.Keys.Order().ToList();

        // Currencies the costs name, plus what their own exchanges cost (books behind materials).
        var currencies = new Dictionary<string, CurrencyInfo>(StringComparer.Ordinal);
        var pending = new Queue<CurrencyCost>(result.SelectMany(i => (i.Source.Costs ?? []).Concat(i.Source.Augment?.Materials ?? [])));
        while (pending.Count > 0)
        {
            var cost = pending.Dequeue();
            if (currencies.ContainsKey(cost.Name))
                continue;
            var info = acquisition.DescribeCurrency(cost);
            currencies[cost.Name] = info;
            foreach (var c in info.Exchanges.SelectMany(e => e))
                pending.Enqueue(c);
        }
        Currencies = currencies.Values.OrderBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        if (unknownShapes > 0)
            log.WriteLine($"Skipped {unknownShapes} items with an equip slot shape the planner doesn't model.");
        return result.OrderBy(i => i.Id).ToList();
    }

    private static Dictionary<string, int> ReadParams(IEnumerable<uint> ids, IReadOnlyList<short> values)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var id in ids)
        {
            if (i < values.Count && Stats.ByBaseParamId.TryGetValue(id, out var key) && values[i] != 0)
                stats[key] = stats.GetValueOrDefault(key) + values[i];
            i++;
        }
        return stats;
    }

    /// <summary>
    /// Maps an EquipSlotCategory row (1 = occupies, -1 = blocks) to our slot key and the
    /// BaseParam column its meld caps use. Null for shapes we don't model.
    /// </summary>
    private static (string Slot, string CapSlot)? SlotShape(EquipSlotCategory c)
    {
        if (c.MainHand == 1)
            return (Slots.Weapon, c.OffHand == -1 ? CapSlots.TwoHandWeapon : CapSlots.OneHandWeapon);
        if (c.OffHand == 1)
            return (Slots.OffHand, CapSlots.OffHand);
        if (c.Body == 1)
        {
            var head = c.Head == -1;
            var hands = c.Gloves == -1;
            var legs = c.Legs == -1;
            var feet = c.Feet == -1;
            string? cap = (head, hands, legs, feet) switch
            {
                (false, false, false, false) => CapSlots.Chest,
                (true, false, false, false) => CapSlots.ChestHead,
                (true, false, true, true) => CapSlots.ChestHeadLegsFeet,
                (false, true, true, false) => CapSlots.ChestLegsGloves,
                (false, false, true, true) => CapSlots.ChestLegsFeet,
                (true, true, true, true) => CapSlots.HeadChestHandsLegsFeet,
                _ => null,
            };
            return cap == null ? null : (Slots.Body, cap);
        }
        if (c.Head == 1) return (Slots.Head, CapSlots.Head);
        if (c.Gloves == 1) return (Slots.Hands, CapSlots.Hands);
        if (c.Legs == 1) return (Slots.Legs, c.Feet == -1 ? CapSlots.LegsFeet : CapSlots.Legs);
        if (c.Feet == 1) return (Slots.Feet, CapSlots.Feet);
        if (c.Ears == 1) return (Slots.Ears, CapSlots.Earring);
        if (c.Neck == 1) return (Slots.Neck, CapSlots.Necklace);
        if (c.Wrists == 1) return (Slots.Wrists, CapSlots.Bracelet);
        if (c.FingerL == 1 || c.FingerR == 1) return (Slots.Ring, CapSlots.Ring);
        return null;
    }
}
