using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim;

/// <summary>
/// A job's rotation: its abilities and buffs, and the choice of what to press next. One class per
/// job (Sim/Rotations). The engine owns time, cooldowns, buffs, damage over time and auto-attacks;
/// the rotation owns combos and gauges.
/// </summary>
public abstract class Rotation
{
    public abstract string Job { get; }
    /// <summary>"sks" or "sps": the stat that sets this job's GCD.</summary>
    public virtual string SpeedStat => Rmc.GameData.Stats.SkillSpeed;
    /// <summary>Role trait damage multiplier (Magic Mastery 130, ranged 120), percent.</summary>
    public virtual int TraitPercent => 100;
    /// <summary>Permanent haste from traits, applied to GCD recast, casts and auto-attacks.</summary>
    public virtual int TraitHastePercent => 0;
    /// <summary>Auto-attack potency; 0 for jobs that don't auto-attack.</summary>
    public virtual int AutoAttackPotency => 90;
    /// <summary>Whether the job's standard sim drinks a potion (xivgear's healer sims don't).</summary>
    public virtual bool UsesPotion => true;

    public abstract IReadOnlyList<SimAbility> Abilities { get; }
    public abstract IReadOnlyList<SimBuff> Buffs { get; }

    /// <summary>Clears combo and gauge state before a run.</summary>
    public abstract void Reset();
    /// <summary>The GCD to use now, or null to wait.</summary>
    public abstract SimAbility? NextGcd(SimState s);
    /// <summary>An oGCD to weave now, or null. Called again after each weave until null.</summary>
    public abstract SimAbility? NextOgcd(SimState s);
    /// <summary>Update combo/gauge state after an action resolves.</summary>
    public virtual void OnUse(SimAbility a, SimState s) { }
    /// <summary>Potency added to <paramref name="a"/>'s for this use (gauge-scaled or buff-raised potencies).</summary>
    public virtual int PotencyBonus(SimAbility a, SimState s) => 0;

    protected static SimAbility Gcd(string name, uint id, int potency, string label = "base", AttackType type = AttackType.Weaponskill,
        int recastMs = 2500, int castMs = 0, bool autoCrit = false, bool autoDh = false, SimDot? dot = null, string[]? grants = null,
        int cooldownMs = 0, int charges = 1, bool fixedRecast = false, string? group = null)
        => new()
        {
            Name = name, ActionId = id, Potency = potency, PotencyLabel = label, Type = type, Gcd = true, RecastMs = recastMs,
            CastMs = castMs, AutoCrit = autoCrit, AutoDirectHit = autoDh, Dot = dot, Grants = grants ?? [],
            CooldownMs = cooldownMs, Charges = charges, FixedRecast = fixedRecast, CooldownGroup = group,
        };

    protected static SimAbility Ogcd(string name, uint id, int potency, int cooldownMs, string label = "base", int charges = 1,
        int lockMs = 600, string[]? grants = null, AttackType type = AttackType.Ability, string? group = null, SimDot? dot = null)
        => new()
        {
            Name = name, ActionId = id, Potency = potency, PotencyLabel = label, Type = type, Gcd = false, CooldownMs = cooldownMs,
            Charges = charges, AnimationLockMs = lockMs, Grants = grants ?? [], CooldownGroup = group, Dot = dot,
        };

    protected static SimAbility Potion() => new() { Name = "Potion", Gcd = false, CooldownMs = 270_000, AnimationLockMs = 1100, IsPotion = true };
}
