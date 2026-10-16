using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim;

/// <summary>An action a rotation can use. Potencies come from the game tooltips (see PotencyTable).</summary>
public sealed class SimAbility
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Action row id + tooltip label: where the patch-day refresh reads <see cref="Potency"/>.</summary>
    public uint ActionId { get; init; }
    public string PotencyLabel { get; init; } = "base";
    public int Potency { get; set; }
    public AttackType Type { get; init; } = AttackType.Weaponskill;

    public bool Gcd { get; init; }
    /// <summary>Base cast time; 0 = instant. Scaled by speed and haste like the GCD.</summary>
    public int CastMs { get; init; }
    /// <summary>Base recast for GCDs (2500 for most; 1500 for some). Scaled by speed and haste.</summary>
    public int RecastMs { get; init; } = 2500;
    /// <summary>The recast ignores speed and haste (dance steps and finishes).</summary>
    public bool FixedRecast { get; init; }
    /// <summary>For oGCDs (and GCDs with their own timer): cooldown per charge. 0 = none.</summary>
    public int CooldownMs { get; init; }
    public int Charges { get; init; } = 1;
    /// <summary>Abilities sharing a cooldown name share charges (e.g. a skill and its replacement).</summary>
    public string? CooldownGroup { get; init; }
    public int AnimationLockMs { get; init; } = 600;

    public bool AutoCrit { get; init; }
    public bool AutoDirectHit { get; init; }
    public bool CannotCritOrDh { get; init; }
    /// <summary>Scales this action's damage (pets hit for less than their owner per potency).</summary>
    public double DamageMultiplier { get; init; } = 1;

    /// <summary>Damage over time this action applies (replaces an existing one of the same name).</summary>
    public SimDot? Dot { get; init; }
    /// <summary>Buffs granted on use (names from the rotation's buff list).</summary>
    public IReadOnlyList<string> Grants { get; init; } = [];
    /// <summary>Uses a potion: grants Medicated with the potted stats.</summary>
    public bool IsPotion { get; init; }

    public override string ToString() => Name;
}

public sealed class SimDot
{
    public string Name { get; init; } = string.Empty;
    public uint ActionId { get; init; }
    public string PotencyLabel { get; init; } = "Potency";
    public int Potency { get; set; }
    public int DurationMs { get; init; }
}

/// <summary>A buff or debuff. Effects apply to hits that <see cref="AppliesTo"/> accepts (all by default).</summary>
public sealed class SimBuff
{
    public string Name { get; init; } = string.Empty;
    public int DurationMs { get; init; }
    public double DamagePercent { get; init; }
    public double CritRate { get; init; }
    public double DirectHitRate { get; init; }
    /// <summary>Haste on GCD recast and casts while active, percent.</summary>
    public int HastePercent { get; init; }
    /// <summary>Haste on auto-attack delay only (Riddle of Wind), percent.</summary>
    public int AutoAttackHastePercent { get; init; }
    /// <summary>Next qualifying hit is a guaranteed crit / direct hit (Life Surge, Reassemble).</summary>
    public bool ForceCrit { get; init; }
    public bool ForceDirectHit { get; init; }
    /// <summary>Stacks granted; each qualifying use consumes one (0 = not consumed by use).</summary>
    public int Stacks { get; init; }
    public Func<SimAbility, bool>? AppliesTo { get; init; }

    public bool Affects(SimAbility a) => AppliesTo == null || AppliesTo(a);
}

public sealed class SimSettings
{
    public double FightSeconds { get; set; } = 510;
    /// <summary>Allowed GCD delay when weaving an oGCD (xivgear "acceptable clip").</summary>
    public int AcceptableClipMs { get; set; } = 100;
    public bool UsePotion { get; set; } = true;
    /// <summary>Other party members' raid buffs, applied in 20s windows every 120s starting at <see cref="PartyBurstStartMs"/>.</summary>
    public List<PartyBuff> PartyBuffs { get; set; } = [];
    public int PartyBurstStartMs { get; set; } = 7000;
    /// <summary>Record every hit (for debugging and the timeline view).</summary>
    public bool RecordTimeline { get; set; }
    /// <summary>Collect each hit's stat-independent context (<see cref="SimResult.Hits"/>) for the fitted profiles.</summary>
    public bool RecordHits { get; set; }
}

public sealed class SimResult
{
    public double TotalDamage { get; set; }
    public double Dps { get; set; }
    public int GcdCount { get; set; }
    public Dictionary<string, AbilityTotal> ByAbility { get; } = new(StringComparer.Ordinal);
    public List<SimEvent> Timeline { get; } = [];
    /// <summary>With <see cref="SimSettings.RecordHits"/>: total buff weight per hit context.</summary>
    public Dictionary<HitKey, double> Hits { get; } = [];
}

/// <summary>
/// Everything about a hit except the gear set's stats: given these, one hit's expected damage is
/// BaseDamage(stats or potted stats, Potency, Type, AutoDirectHit, IsDot) × crit/DH factor at the
/// bonus rates × the hit's buff multiplier. Rates are in ten-thousandths.
/// </summary>
public readonly record struct HitKey(int Potency, AttackType Type, bool AutoCrit, bool AutoDirectHit, bool IsDot, bool CannotCritOrDh,
    bool Potted, int CritBonus, int DirectHitBonus);

public sealed class AbilityTotal
{
    public int Uses { get; set; }
    public double Damage { get; set; }
}

public sealed record SimEvent(int TimeMs, string Name, double Damage);
