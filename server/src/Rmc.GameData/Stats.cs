namespace Rmc.GameData;

/// <summary>
/// Stat keys used across the snapshot. They match the plugin's gear DB keys so results can be
/// handed back without translation.
/// </summary>
public static class Stats
{
    public const string Strength = "str";
    public const string Dexterity = "dex";
    public const string Vitality = "vit";
    public const string Intelligence = "int";
    public const string Mind = "mnd";
    public const string Piety = "pie";
    public const string Tenacity = "ten";
    public const string DirectHit = "dh";
    public const string Critical = "crit";
    public const string Determination = "det";
    public const string SkillSpeed = "sks";
    public const string SpellSpeed = "sps";

    /// <summary>BaseParam row id → stat key, for every stat the planner tracks.</summary>
    public static readonly IReadOnlyDictionary<uint, string> ByBaseParamId = new Dictionary<uint, string>
    {
        [1] = Strength, [2] = Dexterity, [3] = Vitality, [4] = Intelligence, [5] = Mind, [6] = Piety,
        [19] = Tenacity, [22] = DirectHit, [27] = Critical, [44] = Determination, [45] = SkillSpeed, [46] = SpellSpeed,
    };

    public static readonly IReadOnlyDictionary<string, uint> BaseParamIdByKey =
        ByBaseParamId.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>Weapon damage bonuses carried as HQ "special" params on weapons.</summary>
    public const uint PhysicalDamageParam = 12;
    public const uint MagicalDamageParam = 13;

    /// <summary>Substats a materia can carry for combat jobs.</summary>
    public static readonly IReadOnlySet<string> CombatSubstats = new HashSet<string>(StringComparer.Ordinal)
    {
        Critical, Determination, DirectHit, SkillSpeed, SpellSpeed, Tenacity, Piety,
    };

    public static readonly IReadOnlySet<string> MainStats = new HashSet<string>(StringComparer.Ordinal)
    {
        Strength, Dexterity, Vitality, Intelligence, Mind,
    };
}

/// <summary>Equipment slot keys, matching the plugin's BiS JSON.</summary>
public static class Slots
{
    public const string Weapon = "WEAPON";
    public const string OffHand = "OFFHAND";
    public const string Head = "HEAD";
    public const string Body = "BODY";
    public const string Hands = "HANDS";
    public const string Legs = "LEGS";
    public const string Feet = "FEET";
    public const string Ears = "EARS";
    public const string Neck = "NECK";
    public const string Wrists = "WRISTS";
    public const string RingLeft = "RING_L";
    public const string RingRight = "RING_R";

    /// <summary>Slot a ring item reports; it can go in either ring slot.</summary>
    public const string Ring = "RING";

    public static readonly IReadOnlyList<string> All =
    [
        Weapon, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RingLeft, RingRight,
    ];

    /// <summary>True when an item reporting <paramref name="itemSlot"/> can be worn in <paramref name="setSlot"/>.</summary>
    public static bool Fits(string itemSlot, string setSlot)
        => itemSlot == setSlot || (itemSlot == Ring && setSlot is RingLeft or RingRight);
}
