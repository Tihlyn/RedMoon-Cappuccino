using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Astrologian, level 100 (Dawntrail). Fall Malefic with Combust III kept up; Divination every two
/// minutes and Oracle after it; Earthly Star on cooldown, left to mature into a Stellar Explosion;
/// Astral/Umbral Draw on cooldown with Lord of Crowns from every Astral Draw. Cards go to party
/// members, so they don't raise the astrologian's own damage. Like xivgear's healer sims: no
/// potion, no auto-attacks.
/// </summary>
public sealed class AstRotation : Rotation
{
    private const int StarMaturityMs = 20_000;

    public override string Job => "AST";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;
    public override int AutoAttackPotency => 0;
    public override bool UsesPotion => false;

    private readonly SimAbility fallMalefic = Gcd("Fall Malefic", 25871, 270, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility combust = Gcd("Combust III", 16554, 0, type: AttackType.Spell,
        dot: new SimDot { Name = "Combust III (DoT)", ActionId = 16554, Potency = 70, DurationMs = 30_000 });
    private readonly SimAbility divination = Ogcd("Divination", 16552, 0, 120_000, grants: ["Divination", "Divining"]);
    private readonly SimAbility oracle = Ogcd("Oracle", 37029, 860, 1000);
    private readonly SimAbility earthlyStar = Ogcd("Earthly Star", 7439, 0, 60_000);
    private readonly SimAbility stellarExplosion = Ogcd("Stellar Explosion", 8324, 310, 1000, "base2");
    private readonly SimAbility astralDraw = Ogcd("Astral Draw", 37017, 0, 55_000, group: "Draw");
    private readonly SimAbility umbralDraw = Ogcd("Umbral Draw", 37018, 0, 55_000, group: "Draw");
    private readonly SimAbility lordOfCrowns = Ogcd("Lord of Crowns", 7444, 400, 1000);

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Divination", DurationMs = 20_000, DamagePercent = 6 },
        new() { Name = "Divining", DurationMs = 30_000 },
    ];

    private int starPlacedAt = -1;
    private bool astralNext, lordReady;

    public AstRotation()
    {
        abilities = [fallMalefic, combust, divination, oracle, earthlyStar, stellarExplosion, astralDraw, umbralDraw, lordOfCrowns];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        starPlacedAt = -1;
        astralNext = true;
        lordReady = false;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.DotRemaining("Combust III (DoT)") < 3000 && s.FightEndMs - s.Now > 15_000)
            return combust;
        return fallMalefic;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (starPlacedAt >= 0 && s.Now - starPlacedAt >= StarMaturityMs)
            return stellarExplosion;
        if (s.IsReady(divination) && s.Now > 2000)
            return divination;
        if (s.HasBuff("Divining"))
            return oracle;
        if (lordReady && (s.HasBuff("Divination") || s.CooldownRemaining(divination) > 30_000))
            return lordOfCrowns;
        if (s.IsReady(astralDraw) && !lordReady)
            return astralNext ? astralDraw : umbralDraw;
        if (s.IsReady(earthlyStar) && starPlacedAt < 0)
            return earthlyStar;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == earthlyStar) starPlacedAt = s.Now;
        else if (a == stellarExplosion) starPlacedAt = -1;
        else if (a == oracle) s.Remove("Divining");
        else if (a == astralDraw) { astralNext = false; lordReady = true; }
        else if (a == umbralDraw) astralNext = true;
        else if (a == lordOfCrowns) lordReady = false;
    }
}
