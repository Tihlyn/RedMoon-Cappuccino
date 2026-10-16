using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Machinist, level 100 (Dawntrail). Tools (Drill, Air Anchor, Chain Saw, Excavator) on cooldown
/// with Reassemble, the heated 1-2-3 combo otherwise; Hypercharge into five Blazing Shots whenever
/// no tool comes back during the window. Barrel Stabilizer, Wildfire, Full Metal Field and the
/// potion together every two minutes; Automaton Queen near full battery; Double Check and Checkmate
/// on charge.
/// Wildfire counts as six weaponskills landed (it always covers a Hypercharge window) and the
/// Queen's attacks as one hit whose potency scales with the battery spent.
/// </summary>
public sealed class MchRotation : Rotation
{
    private const int OverheatGcdMs = 1500;
    /// <summary>Automaton Queen's attacks at 50 battery (five Arm Punches, Pile Bunker, Crowned
    /// Collider); they scale linearly up to twice this at 100.</summary>
    private const int QueenPotencyAt50 = 5 * 120 + 340 + 390;
    /// <summary>The Queen's damage per potency relative to the machinist's (measured on xivgear's sim).</summary>
    private const double QueenDamageMultiplier = 0.91;

    public override string Job => "MCH";
    public override int TraitPercent => 120;
    public override int AutoAttackPotency => 80;

    // GCDs
    private readonly SimAbility splitShot = Gcd("Heated Split Shot", 7411, 220);
    private readonly SimAbility slugShot = Gcd("Heated Slug Shot", 7412, 320, "Combo Potency");
    private readonly SimAbility cleanShot = Gcd("Heated Clean Shot", 7413, 420, "Combo Potency");
    private readonly SimAbility drill = Gcd("Drill", 16498, 660, cooldownMs: 20_000, charges: 2);
    private readonly SimAbility airAnchor = Gcd("Air Anchor", 16500, 660, cooldownMs: 40_000);
    private readonly SimAbility chainSaw = Gcd("Chain Saw", 25788, 660, cooldownMs: 60_000, grants: ["Excavator Ready"]);
    private readonly SimAbility excavator = Gcd("Excavator", 36981, 660);
    private readonly SimAbility fullMetalField = Gcd("Full Metal Field", 36982, 900, autoCrit: true, autoDh: true);
    private readonly SimAbility blazingShot = Gcd("Blazing Shot", 36978, 240, recastMs: OverheatGcdMs, fixedRecast: true);

    // oGCDs
    private readonly SimAbility reassemble = Ogcd("Reassemble", 2876, 0, 55_000, charges: 2, grants: ["Reassemble"]);
    private readonly SimAbility barrelStabilizer = Ogcd("Barrel Stabilizer", 7414, 0, 120_000, grants: ["Hypercharged", "Full Metal Machinist"]);
    private readonly SimAbility hypercharge = Ogcd("Hypercharge", 17209, 0, 10_000, grants: ["Overheated"]);
    private readonly SimAbility wildfire = new()
    {
        Name = "Wildfire", ActionId = 2878, PotencyLabel = "manual", Potency = 6 * 240, Type = AttackType.Ability,
        CooldownMs = 120_000, CannotCritOrDh = true,
    };
    private readonly SimAbility doubleCheck = Ogcd("Double Check", 36979, 180, 30_000, charges: 3);
    private readonly SimAbility checkmate = Ogcd("Checkmate", 36980, 180, 30_000, charges: 3);
    private readonly SimAbility queen = new()
    {
        Name = "Automaton Queen", ActionId = 16501, PotencyLabel = "manual", Type = AttackType.Ability, CooldownMs = 6_000,
        DamageMultiplier = QueenDamageMultiplier,
    };
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private int comboStep;
    private int heat, battery;
    private int overheatStacks;

    public MchRotation()
    {
        abilities =
        [
            splitShot, slugShot, cleanShot, drill, airAnchor, chainSaw, excavator, fullMetalField, blazingShot,
            reassemble, barrelStabilizer, hypercharge, wildfire, doubleCheck, checkmate, queen,
        ];
        buffs =
        [
            new() { Name = "Reassemble", DurationMs = 5_000, ForceCrit = true, ForceDirectHit = true,
                AppliesTo = a => a.Type == AttackType.Weaponskill && a != fullMetalField },
            new() { Name = "Hypercharged", DurationMs = 30_000 },
            new() { Name = "Full Metal Machinist", DurationMs = 30_000 },
            new() { Name = "Excavator Ready", DurationMs = 30_000 },
            // Overheated: +20 potency on single-target weaponskills, handled in OnUse via potency.
            new() { Name = "Overheated", DurationMs = 10_000 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = 0;
        heat = battery = 0;
        overheatStacks = 0;
    }

    /// <summary>The tool to press at the next GCD, <paramref name="inMs"/> from now.</summary>
    private SimAbility? ReadyTool(SimState s, int inMs = 0)
    {
        if (s.HasBuff("Excavator Ready")) return excavator;
        if (s.CooldownRemaining(chainSaw) <= inMs) return chainSaw;
        if (s.CooldownRemaining(airAnchor) <= inMs) return airAnchor;
        if (s.CooldownRemaining(drill) <= inMs) return drill;
        return null;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (overheatStacks > 0 && s.HasBuff("Overheated"))
            return blazingShot;
        if (ReadyTool(s) is { } tool)
            return tool;
        if (s.HasBuff("Full Metal Machinist"))
            return fullMetalField;
        return comboStep switch { 1 => slugShot, 2 => cleanShot, _ => splitShot };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (overheatStacks > 0)
        {
            // Inside Hypercharge: Wildfire first, then one weave per Blazing Shot.
            if (s.IsReady(wildfire))
                return wildfire;
            if (doubleCheckNext ? s.IsReady(doubleCheck) : !s.IsReady(checkmate) && s.IsReady(doubleCheck))
                return doubleCheck;
            return s.IsReady(checkmate) ? checkmate : null;
        }

        if (s.IsReady(potion) && s.CooldownRemaining(barrelStabilizer) < 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(barrelStabilizer) && s.Now > 2000)
            return barrelStabilizer;

        // Reassemble the cooldown tools (Drill, Air Anchor, Chain Saw), not Excavator.
        if (s.IsReady(reassemble) && !s.HasBuff("Reassemble") && ReadyTool(s, s.TimeUntilGcd) is { } tool && tool != excavator)
            return reassemble;

        // Hypercharge (Wildfire first when it's up) when no tool comes back during the window.
        var toolsSoon = new[] { drill, airAnchor, chainSaw }.Any(t => s.CooldownRemaining(t) < 8000) || s.HasBuff("Excavator Ready");
        if (s.IsReady(hypercharge) && (heat >= 50 || s.HasBuff("Hypercharged")) && !toolsSoon && !s.HasBuff("Full Metal Machinist"))
            return hypercharge;

        if (s.IsReady(queen) && (battery >= 90 || (battery >= 50 && s.CooldownRemaining(barrelStabilizer) > 100_000 && s.Now > 20_000)))
            return queen;
        if (s.Charges(doubleCheck) >= s.Charges(checkmate) && s.Charges(doubleCheck) >= 2)
            return doubleCheck;
        if (s.Charges(checkmate) >= 2)
            return checkmate;
        return null;
    }

    private bool doubleCheckNext = true;

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == splitShot) { heat += 5; comboStep = 1; }
        else if (a == slugShot) { heat += 5; comboStep = 2; }
        else if (a == cleanShot) { heat += 5; battery += 10; comboStep = 0; }
        else if (a == airAnchor || a == chainSaw) battery += 20;
        else if (a == excavator) { battery += 20; s.Remove("Excavator Ready"); }
        else if (a == fullMetalField) s.Remove("Full Metal Machinist");
        else if (a == hypercharge)
        {
            if (s.HasBuff("Hypercharged")) s.Remove("Hypercharged");
            else heat -= 50;
            overheatStacks = 5;
        }
        else if (a == blazingShot)
        {
            overheatStacks--;
            s.ReduceCooldown(doubleCheck, 15_000);
            s.ReduceCooldown(checkmate, 15_000);
            if (overheatStacks == 0) s.Remove("Overheated");
        }
        else if (a == doubleCheck) doubleCheckNext = false;
        else if (a == checkmate) doubleCheckNext = true;
        else if (a == queen) battery = 0;
        heat = Math.Min(100, heat);
        battery = Math.Min(100, battery);
    }

    public override int PotencyBonus(SimAbility a, SimState s)
    {
        if (a == queen)
            return QueenPotencyAt50 * Math.Clamp(battery, 50, 100) / 50;
        // Overheated: +20 potency on single-target weaponskills.
        if (s.HasBuff("Overheated") && a.Type == AttackType.Weaponskill && a != chainSaw && a != excavator && a != fullMetalField)
            return 20;
        return 0;
    }
}
