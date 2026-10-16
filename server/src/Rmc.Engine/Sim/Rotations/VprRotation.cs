using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Viper, level 100 (Dawntrail). Dual-wield combos alternating Hunter's Sting / Swiftskin's Sting
/// (keeping Hunter's Instinct and Swiftscaled up) with venom-chained finishers; Vicewinder into
/// both coils on charge; Uncoiled Fury for every Rattling Coil; Reawaken (four Generations with
/// their Legacies, then Ouroboros) at 50 Serpent Offerings and free from Serpent's Ire every two
/// minutes, with the potion. Positionals and the venom order always land; twin bites at base
/// potency, as xivgear counts them.
/// </summary>
public sealed class VprRotation : Rotation
{
    public override string Job => "VPR";

    // GCDs
    private readonly SimAbility steelFangs = Gcd("Steel Fangs", 34606, 300, "Honed Steel Potency");
    private readonly SimAbility reavingFangs = Gcd("Reaving Fangs", 34607, 300, "Honed Reavers Potency");
    private readonly SimAbility huntersSting = Gcd("Hunter's Sting", 34608, 300, grants: ["Hunter's Instinct"]);
    private readonly SimAbility swiftskinsSting = Gcd("Swiftskin's Sting", 34609, 300, grants: ["Swiftscaled"]);
    // Positional potency plus the venom's 100.
    private readonly SimAbility flankFinisher = Gcd("Flanksting Strike", 34610, 500, "manual");
    private readonly SimAbility rearFinisher = Gcd("Hindsting Strike", 34612, 500, "manual");
    private readonly SimAbility vicewinder = Gcd("Vicewinder", 34620, 540, recastMs: 3000, cooldownMs: 40_000, charges: 2);
    private readonly SimAbility huntersCoil = Gcd("Hunter's Coil", 34621, 680, "flank", recastMs: 3000, grants: ["Hunter's Instinct"]);
    private readonly SimAbility swiftskinsCoil = Gcd("Swiftskin's Coil", 34622, 680, "rear", recastMs: 3000, grants: ["Swiftscaled"]);
    private readonly SimAbility uncoiledFury = Gcd("Uncoiled Fury", 34633, 680, recastMs: 3500);
    private readonly SimAbility reawaken = Gcd("Reawaken", 34626, 750, recastMs: 2200);
    private readonly SimAbility[] generations =
    [
        Gcd("First Generation", 34627, 680, "manual", recastMs: 2000),
        Gcd("Second Generation", 34628, 680, "manual", recastMs: 2000),
        Gcd("Third Generation", 34629, 680, "manual", recastMs: 2000),
        Gcd("Fourth Generation", 34630, 680, "manual", recastMs: 2000),
    ];
    private readonly SimAbility ouroboros = Gcd("Ouroboros", 34631, 1150, recastMs: 2000);

    // oGCDs
    private readonly SimAbility serpentsIre = Ogcd("Serpent's Ire", 34647, 0, 120_000, grants: ["Ready to Reawaken"]);
    private readonly SimAbility deathRattle = Ogcd("Death Rattle", 34634, 280, 1000);
    private readonly SimAbility twinfang = Ogcd("Twinfang Bite", 34636, 120, 1000);
    private readonly SimAbility twinblood = Ogcd("Twinblood Bite", 34637, 120, 1000);
    private readonly SimAbility uncoiledTwinfang = Ogcd("Uncoiled Twinfang", 34644, 120, 1000);
    private readonly SimAbility uncoiledTwinblood = Ogcd("Uncoiled Twinblood", 34645, 120, 1000);
    private readonly SimAbility[] legacies =
    [
        Ogcd("First Legacy", 34640, 320, 1000),
        Ogcd("Second Legacy", 34641, 320, 1000),
        Ogcd("Third Legacy", 34642, 320, 1000),
        Ogcd("Fourth Legacy", 34643, 320, 1000),
    ];
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Hunter's Instinct", DurationMs = 40_000, DamagePercent = 10 },
        new() { Name = "Swiftscaled", DurationMs = 40_000, HastePercent = 15 },
        new() { Name = "Ready to Reawaken", DurationMs = 30_000 },
    ];

    private int comboStep;          // 0 fangs, 1 sting, 2 finisher
    private bool steelNext, huntersNext, flankNext;
    private int coilStep;           // 0 none, 1 after Vicewinder, 2 after first coil
    private bool huntersCoilFirst;
    private int offerings, rattlingCoils;
    private int tribute, generation;
    private int pendingDeathRattle, pendingTwinfang, pendingTwinblood, pendingUncoiledFang, pendingUncoiledBlood;
    private int pendingLegacy = -1;

    public VprRotation()
    {
        abilities =
        [
            steelFangs, reavingFangs, huntersSting, swiftskinsSting, flankFinisher, rearFinisher, vicewinder, huntersCoil, swiftskinsCoil,
            uncoiledFury, reawaken, .. generations, ouroboros,
            serpentsIre, deathRattle, twinfang, twinblood, uncoiledTwinfang, uncoiledTwinblood, .. legacies,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = 0;
        steelNext = huntersNext = flankNext = true;
        coilStep = 0;
        huntersCoilFirst = true;
        offerings = rattlingCoils = 0;
        tribute = generation = 0;
        pendingDeathRattle = pendingTwinfang = pendingTwinblood = pendingUncoiledFang = pendingUncoiledBlood = 0;
        pendingLegacy = -1;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (tribute > 0)
            return generation < 4 ? generations[generation] : ouroboros;
        if (coilStep == 1)
            return huntersCoilFirst ? huntersCoil : swiftskinsCoil;
        if (coilStep == 2)
            return huntersCoilFirst ? swiftskinsCoil : huntersCoil;
        if (comboStep == 1)
            return huntersNext ? huntersSting : swiftskinsSting;
        if (comboStep == 2)
            return flankNext ? flankFinisher : rearFinisher;

        // Between combos: keep both buffs up before spending.
        var buffsUp = s.BuffRemaining("Hunter's Instinct") > 10_000 && s.BuffRemaining("Swiftscaled") > 10_000;
        if (buffsUp && s.HasBuff("Ready to Reawaken"))
            return reawaken;
        if (buffsUp && offerings >= 50 && (s.CooldownRemaining(serpentsIre) > 30_000 || offerings >= 90))
            return reawaken;
        if (s.IsReady(vicewinder) && (s.Charges(vicewinder) == 2 || s.BuffRemaining("Hunter's Instinct") > 5000))
            return vicewinder;
        if (rattlingCoils > 0 && buffsUp)
            return uncoiledFury;
        return steelNext ? steelFangs : reavingFangs;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (pendingLegacy >= 0)
            return legacies[pendingLegacy];
        if (pendingTwinfang > 0) return twinfang;
        if (pendingTwinblood > 0) return twinblood;
        if (pendingUncoiledFang > 0) return uncoiledTwinfang;
        if (pendingUncoiledBlood > 0) return uncoiledTwinblood;
        if (pendingDeathRattle > 0) return deathRattle;
        if (s.IsReady(potion) && s.CooldownRemaining(serpentsIre) < 3000 && s.Now > 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(serpentsIre) && s.Now > 3000 && tribute == 0)
            return serpentsIre;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == steelFangs || a == reavingFangs) { comboStep = 1; steelNext = a == reavingFangs; }
        else if (a == huntersSting || a == swiftskinsSting)
        {
            comboStep = 2;
            huntersNext = a == swiftskinsSting;
        }
        else if (a == flankFinisher || a == rearFinisher)
        {
            comboStep = 0;
            flankNext = !flankNext;
            offerings += 10;
            pendingDeathRattle = 1;
        }
        else if (a == vicewinder) { coilStep = 1; rattlingCoils = Math.Min(3, rattlingCoils + 1); huntersCoilFirst = s.BuffRemaining("Hunter's Instinct") <= s.BuffRemaining("Swiftscaled"); }
        else if (a == huntersCoil || a == swiftskinsCoil)
        {
            coilStep = coilStep == 1 ? 2 : 0;
            offerings += 5;
            pendingTwinfang = pendingTwinblood = 1;
        }
        else if (a == uncoiledFury)
        {
            rattlingCoils--;
            pendingUncoiledFang = pendingUncoiledBlood = 1;
        }
        else if (a == reawaken)
        {
            if (s.HasBuff("Ready to Reawaken")) s.Remove("Ready to Reawaken");
            else offerings -= 50;
            tribute = 5;
            generation = 0;
        }
        else if (Array.IndexOf(generations, a) is var g and >= 0)
        {
            tribute--;
            generation = g + 1;
            pendingLegacy = g;
        }
        else if (a == ouroboros) { tribute = 0; generation = 0; }
        else if (a == serpentsIre) rattlingCoils = Math.Min(3, rattlingCoils + 1);
        else if (a == deathRattle) pendingDeathRattle = 0;
        else if (a == twinfang) pendingTwinfang = 0;
        else if (a == twinblood) pendingTwinblood = 0;
        else if (a == uncoiledTwinfang) pendingUncoiledFang = 0;
        else if (a == uncoiledTwinblood) pendingUncoiledBlood = 0;
        else if (Array.IndexOf(legacies, a) >= 0) pendingLegacy = -1;
        offerings = Math.Min(100, offerings);
    }
}
