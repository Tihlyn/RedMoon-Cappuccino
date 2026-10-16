using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Dragoon, level 100 (Dawntrail). Two alternating 5-GCD combos; Spiral Blow → Chaotic Spring
/// whenever Power Surge or the damage over time is about to run out. Everything else on cooldown,
/// with Life Surge on Heavens' Thrust / Drakesbane and the potion with the 2-minute buffs.
/// Only Raiden Thrust builds Firstminds' Focus (two per Wyrmwind Thrust).
/// </summary>
public sealed class DrgRotation : Rotation
{
    public override string Job => "DRG";

    // GCDs
    private readonly SimAbility trueThrust = Gcd("True Thrust", 75, 230);
    private readonly SimAbility raidenThrust = Gcd("Raiden Thrust", 16479, 320);
    private readonly SimAbility lanceBarrage = Gcd("Lance Barrage", 36954, 340, "Combo Potency");
    private readonly SimAbility heavensThrust = Gcd("Heavens' Thrust", 25771, 460, "Combo Potency");
    private readonly SimAbility fangAndClaw = Gcd("Fang and Claw", 3554, 340, "Flank Combo Potency");
    private readonly SimAbility drakesbane = Gcd("Drakesbane", 36952, 460);
    private readonly SimAbility spiralBlow = Gcd("Spiral Blow", 36955, 300, "Combo Potency", grants: ["Power Surge"]);
    private readonly SimAbility chaoticSpring = Gcd("Chaotic Spring", 25772, 340, "Rear Combo Potency",
        dot: new SimDot { Name = "Chaotic Spring (DoT)", ActionId = 25772, Potency = 45, DurationMs = 24_000 });
    private readonly SimAbility wheelingThrust = Gcd("Wheeling Thrust", 3556, 340, "Rear Combo Potency");

    // oGCDs
    private readonly SimAbility lanceCharge = Ogcd("Lance Charge", 85, 0, 60_000, grants: ["Lance Charge"]);
    private readonly SimAbility battleLitany = Ogcd("Battle Litany", 3557, 0, 120_000, grants: ["Battle Litany"]);
    private readonly SimAbility lifeSurge = Ogcd("Life Surge", 83, 0, 40_000, charges: 2, grants: ["Life Surge"]);
    private readonly SimAbility geirskogul = Ogcd("Geirskogul", 3555, 280, 60_000, grants: ["Life of the Dragon", "Nastrond Ready"]);
    private readonly SimAbility nastrond = Ogcd("Nastrond", 7400, 720, 0);
    private readonly SimAbility stardiver = Ogcd("Stardiver", 16480, 840, 30_000, lockMs: 1500, grants: ["Starcross Ready"]);
    private readonly SimAbility starcross = Ogcd("Starcross", 36956, 1000, 0);
    private readonly SimAbility highJump = Ogcd("High Jump", 16478, 400, 30_000, lockMs: 800, grants: ["Dive Ready"]);
    private readonly SimAbility mirageDive = Ogcd("Mirage Dive", 7399, 380, 0);
    private readonly SimAbility dragonfireDive = Ogcd("Dragonfire Dive", 96, 500, 120_000, lockMs: 800, grants: ["Dragon's Flight"]);
    private readonly SimAbility riseOfTheDragon = Ogcd("Rise of the Dragon", 36953, 550, 0);
    private readonly SimAbility wyrmwindThrust = Ogcd("Wyrmwind Thrust", 25773, 440, 10_000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Power Surge", DurationMs = 30_000, DamagePercent = 10 },
        new() { Name = "Lance Charge", DurationMs = 20_000, DamagePercent = 10 },
        new() { Name = "Battle Litany", DurationMs = 20_000, CritRate = 10 },
        new() { Name = "Life of the Dragon", DurationMs = 20_000, DamagePercent = 15 },
        new() { Name = "Life Surge", DurationMs = 5_000, ForceCrit = true, AppliesTo = a => a.Type == AttackType.Weaponskill },
        new() { Name = "Nastrond Ready", DurationMs = 20_000 },
        new() { Name = "Starcross Ready", DurationMs = 20_000 },
        new() { Name = "Dive Ready", DurationMs = 15_000 },
        new() { Name = "Dragon's Flight", DurationMs = 30_000 },
    ];

    // Combo state: the next GCD in the current combo (null = start a new one).
    private SimAbility? nextInCombo;
    private int firstmindsFocus;
    private bool started;

    public DrgRotation()
    {
        abilities =
        [
            trueThrust, raidenThrust, lanceBarrage, heavensThrust, fangAndClaw, drakesbane, spiralBlow, chaoticSpring, wheelingThrust,
            lanceCharge, battleLitany, lifeSurge, geirskogul, nastrond, stardiver, starcross, highJump, mirageDive, dragonfireDive,
            riseOfTheDragon, wyrmwindThrust,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        nextInCombo = null;
        firstmindsFocus = 0;
        started = false;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (nextInCombo != null)
            return nextInCombo;
        return started ? raidenThrust : trueThrust;
    }

    private SimAbility PredictNextGcd(SimState s) => NextGcd(s)!;

    public override SimAbility? NextOgcd(SimState s)
    {
        var burstSoon = s.CooldownRemaining(battleLitany) < 3000;
        if (s.IsReady(potion) && burstSoon && s.Now + 30_000 < s.FightEndMs + 15_000)
            return potion;
        if (s.IsReady(lanceCharge))
            return lanceCharge;
        if (s.IsReady(battleLitany))
            return battleLitany;
        if (s.IsReady(geirskogul))
            return geirskogul;
        var upcoming = PredictNextGcd(s);
        if (s.IsReady(lifeSurge) && !s.HasBuff("Life Surge") && (upcoming == heavensThrust || upcoming == drakesbane))
            return lifeSurge;
        if (s.IsReady(dragonfireDive))
            return dragonfireDive;
        if (s.IsReady(highJump))
            return highJump;
        if (s.HasBuff("Nastrond Ready"))
            return nastrond;
        if (s.HasBuff("Life of the Dragon") && s.IsReady(stardiver) && s.TimeUntilGcd >= 1500)
            return stardiver;
        if (s.HasBuff("Starcross Ready"))
            return starcross;
        if (s.HasBuff("Dragon's Flight"))
            return riseOfTheDragon;
        if (s.HasBuff("Dive Ready"))
            return mirageDive;
        if (firstmindsFocus >= 2 && s.IsReady(wyrmwindThrust))
            return wyrmwindThrust;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == trueThrust || a == raidenThrust)
        {
            started = true;
            if (a == raidenThrust)
                firstmindsFocus = Math.Min(2, firstmindsFocus + 1);
            // Refresh Power Surge / the damage over time before they run out.
            var refresh = s.BuffRemaining("Power Surge") < 12_000 || s.DotRemaining("Chaotic Spring (DoT)") < 12_000;
            nextInCombo = refresh ? spiralBlow : lanceBarrage;
        }
        else if (a == lanceBarrage) nextInCombo = heavensThrust;
        else if (a == heavensThrust) nextInCombo = fangAndClaw;
        else if (a == spiralBlow) nextInCombo = chaoticSpring;
        else if (a == chaoticSpring) nextInCombo = wheelingThrust;
        else if (a == fangAndClaw || a == wheelingThrust) nextInCombo = drakesbane;
        else if (a == drakesbane) nextInCombo = null;
        else if (a == nastrond) s.Remove("Nastrond Ready");
        else if (a == starcross) s.Remove("Starcross Ready");
        else if (a == mirageDive) s.Remove("Dive Ready");
        else if (a == riseOfTheDragon) s.Remove("Dragon's Flight");
        else if (a == wyrmwindThrust) firstmindsFocus = 0;
    }
}
