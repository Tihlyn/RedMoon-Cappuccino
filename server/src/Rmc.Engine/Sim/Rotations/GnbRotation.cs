using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Gunbreaker, level 100 (Dawntrail). One-minute bursts: No Mercy, then Bloodfest, Double Down,
/// Gnashing Fang, Sonic Break, Bow Shock, the Reign of Beasts combo and Burst Strikes inside it;
/// Gnashing Fang's second charge and Blasting Zone on cooldown; Keen Edge combo otherwise. Every
/// cartridge weaponskill is followed by its Continuation. The potion goes with No Mercy.
/// </summary>
public sealed class GnbRotation : Rotation
{
    public override string Job => "GNB";

    // GCDs
    private readonly SimAbility keenEdge = Gcd("Keen Edge", 16137, 300);
    private readonly SimAbility brutalShell = Gcd("Brutal Shell", 16139, 380, "Combo Potency");
    private readonly SimAbility solidBarrel = Gcd("Solid Barrel", 16145, 460, "Combo Potency");
    private readonly SimAbility burstStrike = Gcd("Burst Strike", 16162, 420);
    private readonly SimAbility gnashingFang = Gcd("Gnashing Fang", 16146, 440, cooldownMs: 30_000, charges: 2);
    private readonly SimAbility savageClaw = Gcd("Savage Claw", 16147, 500);
    private readonly SimAbility wickedTalon = Gcd("Wicked Talon", 16150, 560);
    private readonly SimAbility doubleDown = Gcd("Double Down", 25760, 1000, cooldownMs: 60_000);
    private readonly SimAbility sonicBreak = Gcd("Sonic Break", 16153, 340,
        dot: new SimDot { Name = "Sonic Break (DoT)", ActionId = 16153, Potency = 120, DurationMs = 15_000 });
    private readonly SimAbility reignOfBeasts = Gcd("Reign of Beasts", 36937, 800);
    private readonly SimAbility nobleBlood = Gcd("Noble Blood", 36938, 900);
    private readonly SimAbility lionHeart = Gcd("Lion Heart", 36939, 1000);

    // oGCDs
    private readonly SimAbility noMercy = Ogcd("No Mercy", 16138, 0, 60_000, grants: ["No Mercy", "Ready to Break"]);
    private readonly SimAbility bloodfest = Ogcd("Bloodfest", 16164, 0, 60_000, grants: ["Ready to Reign"]);
    private readonly SimAbility blastingZone = Ogcd("Blasting Zone", 16165, 800, 30_000);
    private readonly SimAbility bowShock = Ogcd("Bow Shock", 16159, 150, 60_000,
        dot: new SimDot { Name = "Bow Shock (DoT)", ActionId = 16159, Potency = 60, DurationMs = 15_000 });
    private readonly SimAbility hypervelocity = Ogcd("Hypervelocity", 25759, 180, 1000);
    private readonly SimAbility jugularRip = Ogcd("Jugular Rip", 16156, 220, 1000);
    private readonly SimAbility abdomenTear = Ogcd("Abdomen Tear", 16157, 260, 1000);
    private readonly SimAbility eyeGouge = Ogcd("Eye Gouge", 16158, 300, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "No Mercy", DurationMs = 20_000, DamagePercent = 20 },
        new() { Name = "Ready to Break", DurationMs = 30_000 },
        new() { Name = "Ready to Reign", DurationMs = 30_000 },
    ];

    private int comboStep;          // Keen Edge combo
    private int fangStep;           // 1 after Gnashing Fang, 2 after Savage Claw
    private int reignStep;          // 1 after Reign of Beasts, 2 after Noble Blood
    private int cartridges;
    private SimAbility? continuation;

    public GnbRotation()
    {
        abilities =
        [
            keenEdge, brutalShell, solidBarrel, burstStrike, gnashingFang, savageClaw, wickedTalon, doubleDown, sonicBreak,
            reignOfBeasts, nobleBlood, lionHeart,
            noMercy, bloodfest, blastingZone, bowShock, hypervelocity, jugularRip, abdomenTear, eyeGouge,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = fangStep = reignStep = 0;
        cartridges = 0;
        continuation = null;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (fangStep == 1) return savageClaw;
        if (fangStep == 2) return wickedTalon;
        if (reignStep == 1) return nobleBlood;
        if (reignStep == 2) return lionHeart;

        if (s.HasBuff("No Mercy"))
        {
            if (s.IsReady(doubleDown) && cartridges >= 2) return doubleDown;
            if (s.HasBuff("Ready to Reign")) return reignOfBeasts;
            if (s.IsReady(gnashingFang) && cartridges >= 1) return gnashingFang;
            if (s.HasBuff("Ready to Break")) return sonicBreak;
            if (cartridges >= 1 && (s.CooldownRemaining(doubleDown) > s.BuffRemaining("No Mercy") || cartridges >= 3)) return burstStrike;
        }
        else
        {
            var mercySoon = s.CooldownRemaining(noMercy) < 5000;
            // Don't sit on two Gnashing Fang charges; don't overcap cartridges with Solid Barrel.
            if (!mercySoon && s.Charges(gnashingFang) == 2 && cartridges >= 1) return gnashingFang;
            if (!mercySoon && s.IsReady(gnashingFang) && cartridges >= 1 && s.CooldownRemaining(noMercy) > 17_000 && s.Charges(gnashingFang) == 1
                && s.CooldownRemaining(gnashingFang) < s.CooldownRemaining(noMercy) - 17_000)
                return gnashingFang;
            if (s.HasBuff("Ready to Reign") && s.BuffRemaining("Ready to Reign") < 8000) return reignOfBeasts;
            // Bloodfest leftovers above the normal cap of three, or a full gauge before Solid Barrel.
            if (cartridges > 3 || (comboStep == 2 && cartridges >= 3)) return burstStrike;
        }
        return comboStep switch { 1 => brutalShell, 2 => solidBarrel, _ => keenEdge };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (continuation != null)
            return continuation;
        if (s.IsReady(potion) && s.CooldownRemaining(noMercy) < 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(noMercy) && (cartridges >= 1 || s.Now > 10_000) && s.TimeUntilGcd > 0)
            return noMercy;
        if (s.HasBuff("No Mercy") && s.IsReady(bloodfest) && cartridges <= 3)
            return bloodfest;
        if (s.HasBuff("No Mercy") && s.IsReady(bowShock))
            return bowShock;
        if (s.IsReady(blastingZone) && (s.HasBuff("No Mercy") || s.CooldownRemaining(noMercy) > 15_000))
            return blastingZone;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        // A weaponskill ends any unused Continuation.
        if (a.Gcd) continuation = null;

        if (a == keenEdge) comboStep = 1;
        else if (a == brutalShell) comboStep = 2;
        else if (a == solidBarrel) { comboStep = 0; cartridges = Math.Min(MaxCartridges(s), cartridges + 1); }
        else if (a == burstStrike) { cartridges--; continuation = hypervelocity; }
        else if (a == gnashingFang) { cartridges--; fangStep = 1; continuation = jugularRip; }
        else if (a == savageClaw) { fangStep = 2; continuation = abdomenTear; }
        else if (a == wickedTalon) { fangStep = 0; continuation = eyeGouge; }
        else if (a == doubleDown) cartridges -= 2;
        else if (a == sonicBreak) s.Remove("Ready to Break");
        else if (a == reignOfBeasts) { reignStep = 1; s.Remove("Ready to Reign"); }
        else if (a == nobleBlood) reignStep = 2;
        else if (a == lionHeart) reignStep = 0;
        else if (a == bloodfest) cartridges = Math.Min(6, cartridges + 3);
        else if (a == hypervelocity || a == jugularRip || a == abdomenTear || a == eyeGouge) continuation = null;
    }

    // Bloodfest raises the cap to six while it lasts; spending brings it back under three soon after.
    private int MaxCartridges(SimState s) => cartridges > 3 ? 6 : 3;
}
