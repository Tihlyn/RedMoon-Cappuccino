using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// White Mage, level 100 (Dawntrail). Glare III, Dia kept up, Presence of Mind every two minutes
/// (haste and three Glare IVs), Assize on cooldown, every Lily spent on Afflatus Rapture for the
/// Blood Lily and Afflatus Misery at three. Like xivgear's healer sims: no potion, no auto-attacks.
/// </summary>
public sealed class WhmRotation : Rotation
{
    private const int LilyIntervalMs = 20_000;

    public override string Job => "WHM";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;
    public override int AutoAttackPotency => 0;
    public override bool UsesPotion => false;

    private readonly SimAbility glare3 = Gcd("Glare III", 25859, 350, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility glare4 = Gcd("Glare IV", 37009, 640, type: AttackType.Spell);
    private readonly SimAbility dia = Gcd("Dia", 16532, 85, type: AttackType.Spell,
        dot: new SimDot { Name = "Dia (DoT)", ActionId = 16532, Potency = 85, DurationMs = 30_000 });
    private readonly SimAbility misery = Gcd("Afflatus Misery", 16535, 1400, type: AttackType.Spell);
    private readonly SimAbility rapture = Gcd("Afflatus Rapture", 16534, 0, "manual", AttackType.Spell);
    private readonly SimAbility assize = Ogcd("Assize", 3571, 400, 40_000);
    private readonly SimAbility presenceOfMind = Ogcd("Presence of Mind", 136, 0, 120_000, grants: ["Presence of Mind"]);

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Presence of Mind", DurationMs = 15_000, HastePercent = 20 },
    ];

    private int sacredSight, bloodLilies;
    private int liliesUsed;

    public WhmRotation()
    {
        abilities = [glare3, glare4, dia, misery, rapture, assize, presenceOfMind];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        sacredSight = bloodLilies = 0;
        liliesUsed = 0;
    }

    // A Lily every 20s in combat, three at most.
    private int Lilies(SimState s) => Math.Min(3, s.Now / LilyIntervalMs - liliesUsed);

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.DotRemaining("Dia (DoT)") < 3000 && s.FightEndMs - s.Now > 15_000)
            return dia;
        if (bloodLilies >= 3)
            return misery;
        if (sacredSight > 0)
            return glare4;
        if (Lilies(s) > 0 && bloodLilies < 3)
            return rapture;
        return glare3;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(presenceOfMind) && s.Now > 2000)
            return presenceOfMind;
        if (s.IsReady(assize) && s.Now > 2000)
            return assize;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == presenceOfMind) sacredSight = 3;
        else if (a == glare4) sacredSight--;
        else if (a == rapture)
        {
            // Overflowing lilies are lost: catch the counter up so at most three are banked.
            liliesUsed = Math.Max(liliesUsed, s.Now / LilyIntervalMs - 3) + 1;
            bloodLilies++;
        }
        else if (a == misery) bloodLilies = 0;
    }
}
