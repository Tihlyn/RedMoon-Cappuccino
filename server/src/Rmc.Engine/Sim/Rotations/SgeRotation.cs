using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Sage, level 100 (Dawntrail). Dosis III, Eukrasia → Eukrasian Dosis III kept up (1s + 1.5s fixed
/// GCDs), Phlegma III on charge and Psyche on cooldown. Like xivgear's healer sims: no potion, no
/// auto-attacks.
/// </summary>
public sealed class SgeRotation : Rotation
{
    public override string Job => "SGE";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;
    public override int AutoAttackPotency => 0;
    public override bool UsesPotion => false;

    private readonly SimAbility dosis = Gcd("Dosis III", 24312, 380, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility eukrasia = Gcd("Eukrasia", 24290, 0, "manual", AttackType.Spell, recastMs: 1000, fixedRecast: true);
    private readonly SimAbility eukrasianDosis = Gcd("Eukrasian Dosis III", 24314, 0, type: AttackType.Spell, recastMs: 1500, fixedRecast: true,
        dot: new SimDot { Name = "Eukrasian Dosis III (DoT)", ActionId = 24314, Potency = 90, DurationMs = 30_000 });
    private readonly SimAbility phlegma = Gcd("Phlegma III", 24313, 690, type: AttackType.Spell, cooldownMs: 40_000, charges: 2);
    private readonly SimAbility psyche = Ogcd("Psyche", 37033, 690, 60_000);

    private readonly List<SimAbility> abilities;
    private bool eukrasiaUp;

    public SgeRotation()
    {
        abilities = [dosis, eukrasia, eukrasianDosis, phlegma, psyche];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => [];

    public override void Reset() => eukrasiaUp = false;

    public override SimAbility? NextGcd(SimState s)
    {
        if (eukrasiaUp)
            return eukrasianDosis;
        if (s.DotRemaining("Eukrasian Dosis III (DoT)") < 3000 && s.FightEndMs - s.Now > 15_000)
            return eukrasia;
        if (s.IsReady(phlegma) && (s.Charges(phlegma) == 2 || s.CooldownRemaining(phlegma) == 0 && s.Now > 0))
            return phlegma;
        return dosis;
    }

    public override SimAbility? NextOgcd(SimState s)
        => s.IsReady(psyche) && s.Now > 2000 ? psyche : null;

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == eukrasia) eukrasiaUp = true;
        else if (a == eukrasianDosis) eukrasiaUp = false;
    }
}
