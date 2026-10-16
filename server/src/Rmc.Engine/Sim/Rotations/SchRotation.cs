using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Scholar, level 100 (Dawntrail). Broil IV with Biolysis kept up; Chain Stratagem every two minutes
/// and Baneful Impaction after it; every Aetherflow stack (Aetherflow each minute, Dissipation every
/// three) spent on Energy Drain. Like xivgear's SCH sim: no potion, auto-attacks on.
/// </summary>
public sealed class SchRotation : Rotation
{
    public override string Job => "SCH";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;
    public override bool UsesPotion => false;

    private readonly SimAbility broil = Gcd("Broil IV", 25865, 320, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility biolysis = Gcd("Biolysis", 16540, 0, type: AttackType.Spell,
        dot: new SimDot { Name = "Biolysis (DoT)", ActionId = 16540, Potency = 85, DurationMs = 30_000 });
    private readonly SimAbility chainStratagem = Ogcd("Chain Stratagem", 7436, 0, 120_000, grants: ["Chain Stratagem", "Impact Imminent"]);
    private readonly SimAbility banefulImpaction = Ogcd("Baneful Impaction", 37012, 0, 1000,
        dot: new SimDot { Name = "Baneful Impaction (DoT)", ActionId = 37012, Potency = 140, DurationMs = 15_000 });
    private readonly SimAbility aetherflow = Ogcd("Aetherflow", 166, 0, 60_000);
    private readonly SimAbility dissipation = Ogcd("Dissipation", 3587, 0, 180_000);
    private readonly SimAbility energyDrain = Ogcd("Energy Drain", 167, 100, 1000);

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Chain Stratagem", DurationMs = 20_000, CritRate = 10 },
        new() { Name = "Impact Imminent", DurationMs = 30_000 },
    ];

    private int stacks;

    public SchRotation()
    {
        abilities = [broil, biolysis, chainStratagem, banefulImpaction, aetherflow, dissipation, energyDrain];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset() => stacks = 0;

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.DotRemaining("Biolysis (DoT)") < 3000 && s.FightEndMs - s.Now > 15_000)
            return biolysis;
        return broil;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(chainStratagem) && s.Now > 2000)
            return chainStratagem;
        if (s.HasBuff("Impact Imminent"))
            return banefulImpaction;
        if (stacks == 0 && s.IsReady(aetherflow))
            return aetherflow;
        if (stacks == 0 && s.IsReady(dissipation) && s.CooldownRemaining(aetherflow) > 10_000)
            return dissipation;
        // Spend stacks before the next refill, and inside Chain Stratagem.
        if (stacks > 0 && (s.HasBuff("Chain Stratagem") || s.CooldownRemaining(aetherflow) < stacks * 2500 + 2500
                           || s.CooldownRemaining(chainStratagem) > s.CooldownRemaining(aetherflow)))
            return energyDrain;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == aetherflow || a == dissipation) stacks = 3;
        else if (a == energyDrain) stacks--;
        else if (a == banefulImpaction) s.Remove("Impact Imminent");
    }
}
