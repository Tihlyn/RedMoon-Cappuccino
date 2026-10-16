using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Ninja, level 100 (Dawntrail). Spinning Edge → Gust Slash → Aeolian Edge (Armor Crush whenever
/// Kazematoi runs out); Raiton on mudra charges, each followed by Fleeting Raiju; Suiton ahead of
/// every Kunai's Bane; Kassatsu into Hyosho Ranryu; Dokumori, Ten Chi Jin (Fuma → Raiton → Suiton,
/// then Tenri Jindo), Meisui, Bunshin and Phantom Kamaitachi, Dream Within a Dream, and Ninki into
/// Bhavacakra or Zesho Meppo. Mudras are 0.5s and ninjutsu 1.5s fixed GCDs (1.0s under Ten Chi Jin);
/// Bunshin's shadow adds its 160 potency to each weaponskill it follows.
/// </summary>
public sealed class NinRotation : Rotation
{
    private const int MudraMs = 500;
    private const int NinjutsuMs = 1500;
    private const int TcjMs = 1000;
    private const int ShadowPotency = 160;

    public override string Job => "NIN";
    public override int TraitHastePercent => 15;

    // GCDs
    private readonly SimAbility spinningEdge = Gcd("Spinning Edge", 2240, 300);
    private readonly SimAbility gustSlash = Gcd("Gust Slash", 2242, 400, "Combo Potency");
    private readonly SimAbility aeolianEdge = Gcd("Aeolian Edge", 2255, 560, "manual");
    private readonly SimAbility armorCrush = Gcd("Armor Crush", 3563, 500, "Flank Combo Potency");
    private readonly SimAbility fleetingRaiju = Gcd("Fleeting Raiju", 25778, 700);
    private readonly SimAbility phantomKamaitachi = Gcd("Phantom Kamaitachi", 25774, 700);
    private readonly SimAbility mudraFirst = Gcd("Ten", 2259, 0, "manual", AttackType.Ability, recastMs: MudraMs, fixedRecast: true, cooldownMs: 20_000, charges: 2);
    private readonly SimAbility mudra = Gcd("Chi", 2261, 0, "manual", AttackType.Ability, recastMs: MudraMs, fixedRecast: true);
    private readonly SimAbility raiton = Gcd("Raiton", 2267, 740, type: AttackType.Ability, recastMs: NinjutsuMs, fixedRecast: true);
    private readonly SimAbility suiton = Gcd("Suiton", 2271, 580, type: AttackType.Ability, recastMs: NinjutsuMs, fixedRecast: true);
    private readonly SimAbility hyoshoRanryu = Gcd("Hyosho Ranryu", 16492, 1300, type: AttackType.Ability, recastMs: NinjutsuMs, fixedRecast: true);
    private readonly SimAbility tcjFuma = Gcd("Fuma Shuriken", 18873, 500, type: AttackType.Ability, recastMs: TcjMs, fixedRecast: true);
    private readonly SimAbility tcjRaiton = Gcd("Raiton", 18877, 740, type: AttackType.Ability, recastMs: TcjMs, fixedRecast: true);
    private readonly SimAbility tcjSuiton = Gcd("Suiton", 18881, 580, type: AttackType.Ability, recastMs: TcjMs, fixedRecast: true);

    // oGCDs
    private readonly SimAbility kunaisBane = Ogcd("Kunai's Bane", 36958, 700, 60_000, grants: ["Kunai's Bane"]);
    private readonly SimAbility dokumori = Ogcd("Dokumori", 36957, 400, 120_000, grants: ["Dokumori", "Higi"]);
    private readonly SimAbility kassatsu = Ogcd("Kassatsu", 2264, 0, 60_000, grants: ["Kassatsu"]);
    private readonly SimAbility tenChiJin = Ogcd("Ten Chi Jin", 7403, 0, 120_000, grants: ["Tenri Jindo Ready"]);
    private readonly SimAbility tenriJindo = Ogcd("Tenri Jindo", 36961, 1100, 1000);
    private readonly SimAbility meisui = Ogcd("Meisui", 16489, 0, 120_000, grants: ["Meisui"]);
    private readonly SimAbility bunshin = Ogcd("Bunshin", 16493, 0, 90_000);
    private readonly SimAbility dreamWithinADream = Ogcd("Dream Within a Dream", 3566, 540, 60_000, "manual");
    private readonly SimAbility bhavacakra = Ogcd("Bhavacakra", 7402, 400, 1000);
    private readonly SimAbility zeshoMeppo = Ogcd("Zesho Meppo", 36960, 700, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private readonly Queue<SimAbility> sequence = new();
    private int comboStep, kazematoi, ninki, raiju, bunshinStacks;
    private bool shadowWalker, kamaitachiReady;

    public NinRotation()
    {
        abilities =
        [
            spinningEdge, gustSlash, aeolianEdge, armorCrush, fleetingRaiju, phantomKamaitachi, mudraFirst, mudra, raiton, suiton, hyoshoRanryu,
            tcjFuma, tcjRaiton, tcjSuiton, kunaisBane, dokumori, kassatsu, tenChiJin, tenriJindo, meisui, bunshin, dreamWithinADream,
            bhavacakra, zeshoMeppo,
        ];
        buffs =
        [
            new() { Name = "Kunai's Bane", DurationMs = 15_000, DamagePercent = 10 },
            new() { Name = "Dokumori", DurationMs = 20_000, DamagePercent = 5 },
            new() { Name = "Higi", DurationMs = 30_000 },
            new() { Name = "Kassatsu", DurationMs = 15_000, DamagePercent = 30, AppliesTo = a => a == hyoshoRanryu },
            new() { Name = "Tenri Jindo Ready", DurationMs = 30_000 },
            new() { Name = "Meisui", DurationMs = 30_000 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        sequence.Clear();
        comboStep = kazematoi = ninki = raiju = bunshinStacks = 0;
        shadowWalker = kamaitachiReady = false;
    }

    private bool Burst(SimState s) => s.HasBuff("Kunai's Bane") || s.HasBuff("Dokumori");

    public override SimAbility? NextGcd(SimState s)
    {
        if (sequence.Count > 0)
            return sequence.Peek();
        if (raiju > 0)
            return fleetingRaiju;
        if (kamaitachiReady && Burst(s))
            return phantomKamaitachi;

        // Ninjutsu: Hyosho Ranryu under Kassatsu, Suiton before Kunai's Bane, Raiton otherwise.
        if (s.HasBuff("Kassatsu"))
            return Queue(mudra, mudra, hyoshoRanryu);
        if (!shadowWalker && s.CooldownRemaining(kunaisBane) < 10_000 && s.IsReady(mudraFirst))
            return Queue(mudraFirst, mudra, mudra, suiton);
        if (s.IsReady(mudraFirst) && (s.Charges(mudraFirst) == 2 || Burst(s) || s.CooldownRemaining(mudraFirst) < 5000)
            && (shadowWalker || s.CooldownRemaining(kunaisBane) > 25_000))
            return Queue(mudraFirst, mudra, raiton);

        return comboStep switch
        {
            1 => gustSlash,
            2 => kazematoi == 0 ? armorCrush : aeolianEdge,
            _ => spinningEdge,
        };
    }

    private SimAbility Queue(params SimAbility[] actions)
    {
        foreach (var a in actions)
            sequence.Enqueue(a);
        return sequence.Peek();
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (sequence.Count > 0)
            return null;
        if (s.IsReady(potion) && s.CooldownRemaining(dokumori) < 3000 && shadowWalker && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (shadowWalker && s.IsReady(kunaisBane))
            return kunaisBane;
        if (s.IsReady(dokumori) && (s.HasBuff("Kunai's Bane") || s.CooldownRemaining(kunaisBane) > 30_000))
            return dokumori;
        if (s.IsReady(bunshin) && ninki >= 50)
            return bunshin;
        if (s.IsReady(kassatsu) && (s.HasBuff("Kunai's Bane") || s.CooldownRemaining(kunaisBane) > 30_000))
            return kassatsu;
        if (s.IsReady(tenChiJin) && s.HasBuff("Kunai's Bane") && !s.HasBuff("Kassatsu") && raiju == 0)
            return tenChiJin;
        if (s.HasBuff("Tenri Jindo Ready"))
            return tenriJindo;
        if (shadowWalker && s.IsReady(meisui) && !s.IsReady(kunaisBane) && s.CooldownRemaining(kunaisBane) > 20_000)
            return meisui;
        if (s.IsReady(dreamWithinADream) && (Burst(s) || s.CooldownRemaining(kunaisBane) > 20_000))
            return dreamWithinADream;
        if (ninki >= 50 && (Burst(s) || ninki >= 85 || s.HasBuff("Meisui")) && (!s.IsReady(bunshin) || ninki >= 100))
            return s.HasBuff("Higi") ? zeshoMeppo : bhavacakra;
        return null;
    }

    public override int PotencyBonus(SimAbility a, SimState s)
    {
        var bonus = 0;
        // Bunshin's shadow joins melee weaponskills.
        if (bunshinStacks > 0 && a.Type == AttackType.Weaponskill && a != phantomKamaitachi)
            bonus += ShadowPotency;
        if ((a == bhavacakra || a == zeshoMeppo) && s.HasBuff("Meisui"))
            bonus += 150;
        return bonus;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (sequence.Count > 0 && sequence.Peek() == a)
            sequence.Dequeue();

        if (a.Type == AttackType.Weaponskill && bunshinStacks > 0 && a != phantomKamaitachi)
        {
            bunshinStacks--;
            ninki += 5;
        }

        if (a == spinningEdge) { comboStep = 1; ninki += 5; }
        else if (a == gustSlash) { comboStep = 2; ninki += 5; }
        else if (a == aeolianEdge) { comboStep = 0; ninki += 15; kazematoi--; }
        else if (a == armorCrush) { comboStep = 0; ninki += 15; kazematoi = Math.Min(5, kazematoi + 2); }
        else if (a == fleetingRaiju) { raiju--; ninki += 5; }
        else if (a == phantomKamaitachi) { kamaitachiReady = false; ninki += 10; }
        else if (a == raiton || a == tcjRaiton) raiju = Math.Min(3, raiju + 1);
        else if (a == suiton || a == tcjSuiton) shadowWalker = true;
        else if (a == hyoshoRanryu) s.Remove("Kassatsu");
        else if (a == kunaisBane) shadowWalker = false;
        else if (a == dokumori) ninki += 40;
        else if (a == meisui) { shadowWalker = false; ninki += 50; }
        else if (a == bunshin) { ninki -= 50; bunshinStacks = 5; kamaitachiReady = true; }
        else if (a == bhavacakra) ninki -= 50;
        else if (a == zeshoMeppo) { ninki -= 50; s.Remove("Higi"); }
        else if (a == tenriJindo) s.Remove("Tenri Jindo Ready");
        else if (a == tenChiJin)
        {
            sequence.Enqueue(tcjFuma);
            sequence.Enqueue(tcjRaiton);
            sequence.Enqueue(tcjSuiton);
        }
        ninki = Math.Min(100, ninki);
    }
}
