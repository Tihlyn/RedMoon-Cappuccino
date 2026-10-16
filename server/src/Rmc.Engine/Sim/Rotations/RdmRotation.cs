using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Red Mage, level 100 (Dawntrail). Dualcast: a hardcast (Jolt III, or Verfire / Verstone on a
/// proc) makes the next Verthunder III / Veraero III instant, keeping Black and White Mana level.
/// At 50|50 the melee combo (Riposte → Zwerchhau → Redoublement → Verflare/Verholy → Scorch →
/// Resolution). Embolden and the potion every two minutes with Manafication (a free combo and
/// Prefulgence), Vice of Thorns; Acceleration into Grand Impact; Fleche, Contre Sixte,
/// Corps-a-corps and Engagement on cooldown. Verfire/Verstone Ready count as their expected value.
/// </summary>
public sealed class RdmRotation : Rotation
{
    public override string Job => "RDM";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;

    // GCDs
    private readonly SimAbility jolt = Gcd("Jolt III", 37004, 360, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility verthunder = Gcd("Verthunder III", 25855, 440, type: AttackType.Spell);
    private readonly SimAbility veraero = Gcd("Veraero III", 25856, 440, type: AttackType.Spell);
    private readonly SimAbility verfire = Gcd("Verfire", 7510, 380, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility verstone = Gcd("Verstone", 7511, 380, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility riposte = Gcd("Enchanted Riposte", 7527, 340, recastMs: 1500);
    private readonly SimAbility zwerchhau = Gcd("Enchanted Zwerchhau", 7528, 380, "Combo Potency", recastMs: 1500);
    private readonly SimAbility redoublement = Gcd("Enchanted Redoublement", 7529, 560, "Combo Potency", recastMs: 2200);
    private readonly SimAbility verflare = Gcd("Verflare", 7525, 650, type: AttackType.Spell);
    private readonly SimAbility verholy = Gcd("Verholy", 7526, 650, type: AttackType.Spell);
    private readonly SimAbility scorch = Gcd("Scorch", 16530, 750, type: AttackType.Spell);
    private readonly SimAbility resolution = Gcd("Resolution", 25858, 850, type: AttackType.Spell);
    private readonly SimAbility grandImpact = Gcd("Grand Impact", 37006, 600, type: AttackType.Spell);

    // oGCDs
    private readonly SimAbility embolden = Ogcd("Embolden", 7520, 0, 120_000, grants: ["Embolden", "Thorned Flourish"]);
    private readonly SimAbility manafication = Ogcd("Manafication", 7521, 0, 110_000, grants: ["Prefulgence Ready"]);
    private readonly SimAbility acceleration = Ogcd("Acceleration", 7518, 0, 55_000, charges: 2, grants: ["Grand Impact Ready"]);
    private readonly SimAbility viceOfThorns = Ogcd("Vice of Thorns", 37005, 950, 1000);
    private readonly SimAbility prefulgence = Ogcd("Prefulgence", 37007, 1200, 1000);
    private readonly SimAbility fleche = Ogcd("Fleche", 7517, 480, 25_000);
    private readonly SimAbility contreSixte = Ogcd("Contre Sixte", 7519, 420, 45_000);
    private readonly SimAbility corpsACorps = Ogcd("Corps-a-corps", 7506, 130, 35_000, charges: 2);
    private readonly SimAbility engagement = Ogcd("Engagement", 16527, 180, 35_000, charges: 2);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        // Own magic damage +10% (the party gets 5%).
        new() { Name = "Embolden", DurationMs = 20_000, DamagePercent = 10 },
        new() { Name = "Thorned Flourish", DurationMs = 30_000 },
        new() { Name = "Prefulgence Ready", DurationMs = 30_000 },
        new() { Name = "Grand Impact Ready", DurationMs = 30_000 },
    ];

    private int black, white, manaStacks, swordplay;
    private int meleeStep; // 1 Zwerchhau, 2 Redoublement, 3 finisher, 4 Scorch, 5 Resolution
    private bool dualcast;
    private double verfireReady, verstoneReady;

    public RdmRotation()
    {
        abilities =
        [
            jolt, verthunder, veraero, verfire, verstone, riposte, zwerchhau, redoublement, verflare, verholy, scorch, resolution, grandImpact,
            embolden, manafication, acceleration, viceOfThorns, prefulgence, fleche, contreSixte, corpsACorps, engagement,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        black = white = manaStacks = swordplay = 0;
        meleeStep = 0;
        dualcast = false;
        verfireReady = verstoneReady = 0;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        switch (meleeStep)
        {
            case 1: return zwerchhau;
            case 2: return redoublement;
            case 3: return black <= white ? verflare : verholy;
            case 4: return scorch;
            case 5: return resolution;
        }
        if (dualcast)
            return black <= white ? verthunder : veraero;
        // The melee combo at 50|50 (free under Magicked Swordplay), held for Embolden when it's close.
        var emboldenSoon = s.CooldownRemaining(embolden) < 10_000 && !s.HasBuff("Embolden");
        if (swordplay > 0 || (Math.Min(black, white) >= 50 && (!emboldenSoon || Math.Max(black, white) >= 90)))
            return riposte;
        if (s.HasBuff("Grand Impact Ready"))
            return grandImpact;
        if (black <= white && verfireReady >= 1) return verfire;
        if (white < black && verstoneReady >= 1) return verstone;
        return jolt;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && s.CooldownRemaining(embolden) < 3000 && s.Now > 2000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(embolden) && s.Now > 2000)
            return embolden;
        if (s.IsReady(manafication) && s.HasBuff("Embolden") && meleeStep == 0 && swordplay == 0)
            return manafication;
        if (s.HasBuff("Thorned Flourish"))
            return viceOfThorns;
        if (s.HasBuff("Prefulgence Ready") && swordplay == 0)
            return prefulgence;
        if (s.IsReady(fleche)) return fleche;
        if (s.IsReady(contreSixte)) return contreSixte;
        if (s.IsReady(acceleration) && !s.HasBuff("Grand Impact Ready") && !dualcast
            && (s.Charges(acceleration) == 2 || s.HasBuff("Embolden")))
            return acceleration;
        if (s.IsReady(corpsACorps) && (s.Charges(corpsACorps) == 2 || s.HasBuff("Embolden"))) return corpsACorps;
        if (s.IsReady(engagement) && (s.Charges(engagement) == 2 || s.HasBuff("Embolden"))) return engagement;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == jolt || a == verfire || a == verstone) dualcast = true;
        else if (a == verthunder || a == veraero) dualcast = false;

        if (a == jolt) { black += 2; white += 2; }
        else if (a == verthunder) { black += 6; verfireReady = Math.Min(1.5, verfireReady + 0.5); }
        else if (a == veraero) { white += 6; verstoneReady = Math.Min(1.5, verstoneReady + 0.5); }
        else if (a == verfire) { black += 5; verfireReady -= 1; }
        else if (a == verstone) { white += 5; verstoneReady -= 1; }
        else if (a == grandImpact) { black += 3; white += 3; s.Remove("Grand Impact Ready"); }
        else if (a == riposte || a == zwerchhau || a == redoublement)
        {
            var cost = a == riposte ? 20 : 15;
            if (swordplay > 0) swordplay--;
            else { black -= cost; white -= cost; }
            manaStacks++;
            meleeStep = a == riposte ? 1 : a == zwerchhau ? 2 : 3;
        }
        else if (a == verflare)
        {
            black += 11; manaStacks = 0; meleeStep = 4;
            // A guaranteed proc when White was the higher side.
            verfireReady = Math.Min(1.5, verfireReady + (white > black - 11 ? 1 : 0.2));
        }
        else if (a == verholy)
        {
            white += 11; manaStacks = 0; meleeStep = 4;
            verstoneReady = Math.Min(1.5, verstoneReady + (black > white - 11 ? 1 : 0.2));
        }
        else if (a == scorch) { black += 4; white += 4; meleeStep = 5; }
        else if (a == resolution) { black += 4; white += 4; meleeStep = 0; }
        else if (a == manafication) { swordplay = 3; meleeStep = 0; }
        else if (a == viceOfThorns) s.Remove("Thorned Flourish");
        else if (a == prefulgence) s.Remove("Prefulgence Ready");
        black = Math.Min(100, black);
        white = Math.Min(100, white);
    }
}
