namespace Rmc.GameData;

/// <summary>
/// One patch's worth of game data the server computes against. Produced by the extractor from
/// the local game files; never edited by hand.
/// </summary>
public sealed class GameDataSnapshot
{
    public SnapshotMeta Meta { get; set; } = new();
    public List<LevelInfo> Levels { get; set; } = [];
    public List<ClassJobInfo> ClassJobs { get; set; } = [];
    public List<ClanInfo> Clans { get; set; } = [];
    public List<BaseParamInfo> BaseParams { get; set; } = [];
    public List<ItemLevelInfo> ItemLevels { get; set; } = [];
    public List<MateriaInfo> Materia { get; set; } = [];
    public List<FoodInfo> Foods { get; set; } = [];
    /// <summary>Main-stat medicine (Gemdraughts): same shape as food, 30s duration.</summary>
    public List<FoodInfo> Medicines { get; set; } = [];
    public List<GearItem> Items { get; set; } = [];
    /// <summary>Every token, book, tomestone and augment material the items' costs name, with what buys it.</summary>
    public List<CurrencyInfo> Currencies { get; set; } = [];
}

public sealed class SnapshotMeta
{
    public int SchemaVersion { get; set; } = GameDataJson.SchemaVersion;
    /// <summary>Patch label given to the extractor, e.g. "7.5". The game files don't carry it.</summary>
    public string Patch { get; set; } = string.Empty;
    /// <summary>Contents of ffxivgame.ver at extraction time.</summary>
    public string GameVersion { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public string Generator { get; set; } = string.Empty;
    public int MaxLevel { get; set; }
    public int MinItemLevel { get; set; }
    /// <summary>Shop currencies no rule in overlays/currencies.json matched; the checks fail while any remain.</summary>
    public List<string> UnclassifiedCurrencies { get; set; } = [];
}

/// <summary>
/// Level scaling. <see cref="LevelDiv"/> and <see cref="BaseSpeed"/> come from ParamGrow; the
/// rest is not in any sheet and comes from overlays/levels.json (community-derived constants,
/// as in xivgear's xivconstants and Allagan Studies).
/// </summary>
public sealed class LevelInfo
{
    public int Level { get; set; }
    public int LevelDiv { get; set; }
    public int BaseSpeed { get; set; }
    public int BaseMainStat { get; set; }
    public int BaseSubStat { get; set; }
    /// <summary>Base HP before the job modifier.</summary>
    public int Hp { get; set; }
    /// <summary>HP per vitality point above base, in tenths (30.1 → 301).</summary>
    public int HpScalarTank { get; set; }
    public int HpScalarOther { get; set; }
    /// <summary>Main stat → damage scaling (apMod) for tanks and everyone else.</summary>
    public int MainStatPowerModTank { get; set; }
    public int MainStatPowerModOther { get; set; }
}

public sealed class ClassJobInfo
{
    public int Id { get; set; }
    public string Abbreviation { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Tank, Healer, Melee, Ranged or Caster.</summary>
    public string Role { get; set; } = string.Empty;
    public string MainStat { get; set; } = string.Empty;
    /// <summary>Limited jobs (BLU, BST) have their own gear rules and no BiS.</summary>
    public bool Limited { get; set; }
    /// <summary>Job modifiers in percent (100 = 1x), keyed by stat key plus "hp" and "mp".</summary>
    public Dictionary<string, int> Modifiers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ClanInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Additive base-stat modifiers keyed by stat key.</summary>
    public Dictionary<string, int> Modifiers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class BaseParamInfo
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Per-slot-shape percentages, keyed by <see cref="CapSlots"/> names.</summary>
    public Dictionary<string, int> SlotPercent { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Indexed by an item's <see cref="GearItem.MeldParamIndex"/>.</summary>
    public List<int> MeldParam { get; set; } = [];
}

/// <summary>ItemLevel row: the per-stat modifier the meld cap formula scales.</summary>
public sealed class ItemLevelInfo
{
    public int ItemLevel { get; set; }
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MateriaInfo
{
    public int Id { get; set; }
    public string Stat { get; set; } = string.Empty;
    public List<MateriaGrade> Grades { get; set; } = [];
}

public sealed class MateriaGrade
{
    /// <summary>1-based, as shown in game (XII = 12).</summary>
    public int Grade { get; set; }
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public sealed class FoodInfo
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ItemLevel { get; set; }
    public bool CanBeHq { get; set; }
    /// <summary>Effect duration (Well Fed: 30 min = 1800s; medicine: 30s).</summary>
    public int DurationSeconds { get; set; }
    public List<FoodBonus> Bonuses { get; set; } = [];
}

public sealed class FoodBonus
{
    public string Stat { get; set; } = string.Empty;
    /// <summary>When true the bonus is a percentage of the stat, capped at Max; otherwise a flat value.</summary>
    public bool Relative { get; set; }
    public int Value { get; set; }
    public int Max { get; set; }
    public int ValueHq { get; set; }
    public int MaxHq { get; set; }
}

public sealed class GearItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ItemLevel { get; set; }
    public int EquipLevel { get; set; }
    /// <summary>One of <see cref="Slots"/>; rings report <see cref="Slots.Ring"/>.</summary>
    public string Slot { get; set; } = string.Empty;
    /// <summary>BaseParam column the meld caps use for this item's slot shape (see <see cref="CapSlots"/>).</summary>
    public string CapSlot { get; set; } = string.Empty;
    public List<string> Jobs { get; set; } = [];
    public bool CanBeHq { get; set; }
    public bool Unique { get; set; }
    public bool Crafted { get; set; }
    public int MateriaSlots { get; set; }
    /// <summary>Overmelding allowed, up to <see cref="GearRules.MaxMateriaSlots"/> slots total.</summary>
    public bool AdvancedMelding { get; set; }
    public int MeldParamIndex { get; set; }
    /// <summary>Normal-quality stats.</summary>
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    /// <summary>High-quality stats (base + HQ bonus); equal to <see cref="Stats"/> when the item can't be HQ.</summary>
    public Dictionary<string, int> StatsHq { get; set; } = new(StringComparer.Ordinal);
    public WeaponInfo? Weapon { get; set; }
    public AcquisitionSource Source { get; set; } = new();
}

public sealed class WeaponInfo
{
    public int PhysicalDamage { get; set; }
    public int MagicalDamage { get; set; }
    public int PhysicalDamageHq { get; set; }
    public int MagicalDamageHq { get; set; }
    public int DelayMs { get; set; }
}

/// <summary>
/// Where an item comes from, derived from the game files: recipes mark crafted gear, and the
/// exchange shops say which currency buys everything else. overlays/currencies.json names the
/// currencies; overlays/overrides.json pins the rare item no shop or rule covers.
/// </summary>
public sealed class AcquisitionSource
{
    /// <summary>One of <see cref="SourceTypes"/>. For an augmented item, the base item's type.</summary>
    public string Type { get; set; } = SourceTypes.Unknown;
    /// <summary>Shop or currency the type was derived from, for display and debugging.</summary>
    public string? Detail { get; set; }
    /// <summary>What the exchange costs, when the item is bought (tomestones, books, tokens).</summary>
    public List<CurrencyCost>? Costs { get; set; }
    /// <summary>Set when the item is an augmented version of another item.</summary>
    public AugmentInfo? Augment { get; set; }
}

public sealed class CurrencyCost
{
    /// <summary>Item id of the currency; 0 for tomestones and scrips, which shops reference by index.</summary>
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Amount { get; set; }
    /// <summary>Source type of the currency itself (e.g. books come from Savage).</summary>
    public string Type { get; set; } = SourceTypes.Unknown;
}

/// <summary>A currency or material gear costs reference, and the exchanges that give it.</summary>
public sealed class CurrencyInfo
{
    /// <summary>Item id; 0 for tomestones and scrips (named only).</summary>
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = SourceTypes.Unknown;
    /// <summary>Each way to get one by exchange (e.g. Thundersteeped Twine for 4 books III, or for nuts).</summary>
    public List<List<CurrencyCost>> Exchanges { get; set; } = [];
}

public sealed class AugmentInfo
{
    public int BaseItemId { get; set; }
    public List<CurrencyCost> Materials { get; set; } = [];
}

public static class SourceTypes
{
    public const string Crafted = "Crafted";
    public const string Tome = "Tome";
    public const string Scrip = "Scrip";
    public const string Savage = "Savage";
    public const string Raid = "Raid";
    public const string AllianceRaid = "AllianceRaid";
    public const string Trial = "Trial";
    public const string Dungeon = "Dungeon";
    public const string Ultimate = "Ultimate";
    public const string Relic = "Relic";
    public const string FieldOperation = "FieldOperation";
    public const string WondrousTails = "WondrousTails";
    public const string Other = "Other";
    public const string Unknown = "Unknown";
}

public static class GearRules
{
    public const int MaxMateriaSlots = 5;
    public const int MaxGrade = 12;

    /// <summary>
    /// Even grades (the "full" ones: VI, VIII, X, XII) can only go in the first overmeld slot;
    /// later overmeld slots take the odd grade below.
    /// </summary>
    public static bool GradeAllowedInSlot(int grade, int slotIndex, int guaranteedSlots)
    {
        var overmeldIndex = slotIndex - guaranteedSlots;
        if (overmeldIndex <= 0)
            return true;
        return grade % 2 == 1 || grade < 6;
    }
}

/// <summary>BaseParam slot-percentage columns, named after the game's slot shapes.</summary>
public static class CapSlots
{
    public const string OneHandWeapon = "OneHandWeapon";
    public const string TwoHandWeapon = "TwoHandWeapon";
    public const string OffHand = "OffHand";
    public const string Head = "Head";
    public const string Chest = "Chest";
    public const string Hands = "Hands";
    public const string Legs = "Legs";
    public const string Feet = "Feet";
    public const string Earring = "Earring";
    public const string Necklace = "Necklace";
    public const string Bracelet = "Bracelet";
    public const string Ring = "Ring";
    public const string ChestHead = "ChestHead";
    public const string ChestHeadLegsFeet = "ChestHeadLegsFeet";
    public const string LegsFeet = "LegsFeet";
    public const string HeadChestHandsLegsFeet = "HeadChestHandsLegsFeet";
    public const string ChestLegsGloves = "ChestLegsGloves";
    public const string ChestLegsFeet = "ChestLegsFeet";
}
