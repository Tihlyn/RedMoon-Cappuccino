using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Warrior, level 100 (Dawntrail). Heavy Swing → Maim → Storm's Path, or Storm's Eye whenever Surging
/// Tempest needs topping up; Fell Cleave spends the Beast Gauge (Enhanced Infuriate: 5s off Infuriate each),
/// Infuriate into Inner Chaos. Inner Release every minute: three free Fell Cleaves, Primal Rend,
/// Primal Ruination and Primal Wrath, with the potion every six minutes. Upheaval and Onslaught on
/// cooldown under Surging Tempest.
/// </summary>
public sealed class WarRotation : Rotation
{
    public override string Job => "WAR";

    // GCDs
    private readonly SimAbility heavySwing = Gcd("Heavy Swing", 31, 240);
    private readonly SimAbility maim = Gcd("Maim", 37, 340, "Combo Potency");
    private readonly SimAbility stormsPath = Gcd("Storm's Path", 42, 500, "Combo Potency");
    private readonly SimAbility stormsEye = Gcd("Storm's Eye", 45, 500, "Combo Potency");
    private readonly SimAbility fellCleave = Gcd("Fell Cleave", 3549, 580);
    private readonly SimAbility innerChaos = Gcd("Inner Chaos", 16465, 700, autoCrit: true, autoDh: true);
    private readonly SimAbility primalRend = Gcd("Primal Rend", 25753, 720, autoCrit: true, autoDh: true);
    private readonly SimAbility primalRuination = Gcd("Primal Ruination", 36925, 800, autoCrit: true, autoDh: true);

    // oGCDs
    private readonly SimAbility innerRelease = Ogcd("Inner Release", 7389, 0, 60_000, grants: ["Inner Release", "Primal Rend Ready"]);
    private readonly SimAbility infuriate = Ogcd("Infuriate", 52, 0, 60_000, charges: 2, grants: ["Nascent Chaos"]);
    private readonly SimAbility primalWrath = Ogcd("Primal Wrath", 36924, 700, 1000);
    private readonly SimAbility upheaval = Ogcd("Upheaval", 7387, 420, 30_000);
    private readonly SimAbility onslaught = Ogcd("Onslaught", 7386, 150, 30_000, charges: 3);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private int comboStep;
    private int gauge;
    private int burgeoning;

    public WarRotation()
    {
        abilities =
        [
            heavySwing, maim, stormsPath, stormsEye, fellCleave, innerChaos, primalRend, primalRuination,
            innerRelease, infuriate, primalWrath, upheaval, onslaught,
        ];
        buffs =
        [
            new() { Name = "Surging Tempest", DurationMs = 30_000, DamagePercent = 10 },
            // Three free Fell Cleaves (releasedFellCleave); each use takes a stack.
            new() { Name = "Inner Release", DurationMs = 15_000, Stacks = 3, AppliesTo = a => a == releasedFellCleave },
            new() { Name = "Primal Rend Ready", DurationMs = 30_000 },
            new() { Name = "Primal Ruination Ready", DurationMs = 20_000 },
            new() { Name = "Wrathful", DurationMs = 30_000 },
            new() { Name = "Nascent Chaos", DurationMs = 30_000 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = 0;
        gauge = 0;
        burgeoning = 0;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        var surging = s.BuffRemaining("Surging Tempest");
        if (s.HasBuff("Inner Release"))
            return releasedFellCleave;
        if (s.HasBuff("Nascent Chaos") && gauge >= 50)
            return innerChaos;
        if (s.HasBuff("Primal Rend Ready"))
            return primalRend;
        if (s.HasBuff("Primal Ruination Ready"))
            return primalRuination;
        // Spend before the next combo action would overcap, or while Infuriate is about to cap.
        var nextGain = comboStep switch { 1 => 10, 2 => surging < 30_000 ? 10 : 20, _ => 10 };
        if (gauge >= 50 && surging > 0 && (gauge + nextGain > 100 || s.Charges(infuriate) == 2 || s.CooldownRemaining(infuriate) < 10_000))
            return fellCleave;
        return comboStep switch
        {
            1 => maim,
            2 => surging < 30_000 ? stormsEye : stormsPath,
            _ => heavySwing,
        };
    }

    // Fell Cleave under Inner Release: free and a guaranteed critical direct hit.
    private readonly SimAbility releasedFellCleave = Gcd("Fell Cleave", 3549, 580, autoCrit: true, autoDh: true);

    public override SimAbility? NextOgcd(SimState s)
    {
        var surging = s.HasBuff("Surging Tempest");
        if (s.IsReady(potion) && s.CooldownRemaining(innerRelease) < 3000 && surging && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(innerRelease) && surging)
            return innerRelease;
        if (s.HasBuff("Wrathful"))
            return primalWrath;
        if (s.IsReady(upheaval) && surging)
            return upheaval;
        if (s.IsReady(infuriate) && gauge <= 50 && !s.HasBuff("Nascent Chaos") && surging && !s.HasBuff("Inner Release"))
            return infuriate;
        if (s.IsReady(onslaught) && surging && (s.Charges(onslaught) == 3 || s.HasBuff("Inner Release") || s.CooldownRemaining(innerRelease) > 40_000))
            return onslaught;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == heavySwing) { gauge += 10; comboStep = 1; }
        else if (a == maim) { gauge += 10; comboStep = 2; }
        else if (a == stormsPath) { gauge += 20; comboStep = 0; }
        else if (a == stormsEye)
        {
            gauge += 10;
            comboStep = 0;
            s.Grant("Surging Tempest", Math.Min(60_000, s.BuffRemaining("Surging Tempest") + 30_000));
        }
        else if (a == fellCleave)
        {
            gauge -= 50;
            s.ReduceCooldown(infuriate, 5_000);
        }
        else if (a == releasedFellCleave)
        {
            s.ReduceCooldown(infuriate, 5_000);
            if (++burgeoning == 3)
            {
                burgeoning = 0;
                s.Grant("Wrathful");
            }
        }
        else if (a == innerChaos)
        {
            gauge -= 50;
            s.Remove("Nascent Chaos");
            s.ReduceCooldown(infuriate, 5_000);
        }
        else if (a == primalRend) { s.Remove("Primal Rend Ready"); s.Grant("Primal Ruination Ready"); }
        else if (a == primalRuination) s.Remove("Primal Ruination Ready");
        else if (a == primalWrath) s.Remove("Wrathful");
        else if (a == infuriate) gauge += 50;
        else if (a == innerRelease)
            s.Grant("Surging Tempest", Math.Min(60_000, s.BuffRemaining("Surging Tempest") + 10_000));
        gauge = Math.Min(100, gauge);
    }
}
