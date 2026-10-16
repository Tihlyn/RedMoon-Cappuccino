using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Samurai, level 100 (Dawntrail). Gyofu combos build the three Sen (keeping Fugetsu and Fuka up),
/// Midare Setsugekka at three Sen followed by its Kaeshi, Higanbana with one Sen when the damage
/// over time is about to fall off. Meikyo Shisui on charge (Tendo Setsugekka after it), Ikishoten
/// every two minutes with the potion, Ogi Namikiri and Zanshin; Kenki into Senei, then Shinten.
/// Positionals always land.
/// </summary>
public sealed class SamRotation : Rotation
{
    private const int IaijutsuCastMs = 1300;

    public override string Job => "SAM";

    // GCDs
    private readonly SimAbility gyofu = Gcd("Gyofu", 36963, 240);
    private readonly SimAbility jinpu = Gcd("Jinpu", 7478, 300, "Combo Potency", grants: ["Fugetsu"]);
    private readonly SimAbility shifu = Gcd("Shifu", 7479, 300, "Combo Potency", grants: ["Fuka"]);
    private readonly SimAbility yukikaze = Gcd("Yukikaze", 7480, 340, "Combo Potency");
    private readonly SimAbility gekko = Gcd("Gekko", 7481, 420, "Rear Combo Potency");
    private readonly SimAbility kasha = Gcd("Kasha", 7482, 420, "Flank Combo Potency");
    private readonly SimAbility higanbana = Gcd("Higanbana", 7489, 200, castMs: IaijutsuCastMs,
        dot: new SimDot { Name = "Higanbana (DoT)", ActionId = 7489, Potency = 50, DurationMs = 60_000 });
    private readonly SimAbility midare = Gcd("Midare Setsugekka", 7487, 680, castMs: IaijutsuCastMs, autoCrit: true, grants: ["Tsubame-gaeshi Ready"]);
    private readonly SimAbility kaeshiSetsugekka = Gcd("Kaeshi: Setsugekka", 16486, 680, autoCrit: true);
    private readonly SimAbility tendoSetsugekka = Gcd("Tendo Setsugekka", 36966, 1100, castMs: IaijutsuCastMs, autoCrit: true, grants: ["Tsubame-gaeshi Ready"]);
    private readonly SimAbility tendoKaeshi = Gcd("Tendo Kaeshi Setsugekka", 36968, 1100, autoCrit: true);
    private readonly SimAbility ogiNamikiri = Gcd("Ogi Namikiri", 25781, 1000, castMs: IaijutsuCastMs, autoCrit: true, grants: ["Kaeshi: Namikiri Ready"]);
    private readonly SimAbility kaeshiNamikiri = Gcd("Kaeshi: Namikiri", 25782, 1000, autoCrit: true);

    // oGCDs
    private readonly SimAbility meikyo = Ogcd("Meikyo Shisui", 7499, 0, 55_000, charges: 2, grants: ["Meikyo Shisui", "Tendo"]);
    private readonly SimAbility ikishoten = Ogcd("Ikishoten", 16482, 0, 120_000, grants: ["Ogi Namikiri Ready", "Zanshin Ready"]);
    private readonly SimAbility senei = Ogcd("Hissatsu: Senei", 16481, 800, 60_000);
    private readonly SimAbility shinten = Ogcd("Hissatsu: Shinten", 7490, 250, 1000);
    private readonly SimAbility zanshin = Ogcd("Zanshin", 36964, 940, 0);
    private readonly SimAbility shoha = Ogcd("Shoha", 16487, 640, 15_000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private SimAbility? comboFrom;
    private bool getsu, ka, setsu;
    private int kenki, meditation;

    public SamRotation()
    {
        abilities =
        [
            gyofu, jinpu, shifu, yukikaze, gekko, kasha, higanbana, midare, kaeshiSetsugekka, tendoSetsugekka, tendoKaeshi,
            ogiNamikiri, kaeshiNamikiri, meikyo, ikishoten, senei, shinten, zanshin, shoha,
        ];
        buffs =
        [
            new() { Name = "Fugetsu", DurationMs = 40_000, DamagePercent = 13 },
            new() { Name = "Fuka", DurationMs = 40_000, HastePercent = 13 },
            new() { Name = "Meikyo Shisui", DurationMs = 20_000, Stacks = 3, AppliesTo = a => a == gekko || a == kasha || a == yukikaze },
            new() { Name = "Tendo", DurationMs = 30_000 },
            new() { Name = "Tsubame-gaeshi Ready", DurationMs = 30_000 },
            new() { Name = "Ogi Namikiri Ready", DurationMs = 30_000 },
            new() { Name = "Kaeshi: Namikiri Ready", DurationMs = 30_000 },
            new() { Name = "Zanshin Ready", DurationMs = 30_000 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboFrom = null;
        getsu = ka = setsu = false;
        kenki = meditation = 0;
    }

    private int Sen => (getsu ? 1 : 0) + (ka ? 1 : 0) + (setsu ? 1 : 0);

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.HasBuff("Kaeshi: Namikiri Ready"))
            return kaeshiNamikiri;
        if (s.HasBuff("Tsubame-gaeshi Ready"))
            return lastIaijutsuWasTendo ? tendoKaeshi : kaeshiSetsugekka;
        if (s.HasBuff("Ogi Namikiri Ready"))
            return ogiNamikiri;
        if (Sen == 3)
            return s.HasBuff("Tendo") ? tendoSetsugekka : midare;
        // Higanbana on one Sen as the damage over time runs out (on two if it has already dropped).
        var dot = s.DotRemaining("Higanbana (DoT)");
        if ((Sen == 1 && dot < 8000 || Sen == 2 && dot == 0) && s.FightEndMs - s.Now > 30_000 && !s.HasBuff("Meikyo Shisui"))
            return higanbana;

        if (s.HasBuff("Meikyo Shisui"))
        {
            // Finishers without combo; Gekko/Kasha also refresh Fugetsu/Fuka.
            if (!getsu && (ka || s.BuffRemaining("Fugetsu") <= s.BuffRemaining("Fuka")))
                return gekko;
            if (!ka)
                return kasha;
            if (!setsu)
                return yukikaze;
            return gekko;
        }

        if (comboFrom == jinpu)
            return gekko;
        if (comboFrom == shifu)
            return kasha;
        if (comboFrom == gyofu)
        {
            var fugetsu = s.BuffRemaining("Fugetsu");
            var fuka = s.BuffRemaining("Fuka");
            // Setsu first (the two-step combo); Getsu/Ka otherwise, or whichever buff is about to drop.
            // Meikyo Shisui covers most Gekko/Kasha and refreshes both buffs.
            var low = Math.Min(fugetsu, fuka) < 8_000;
            if (!setsu && !low)
                return yukikaze;
            if (low || (!getsu && !ka))
                return fugetsu <= fuka ? (getsu && !ka ? shifu : jinpu) : (ka && !getsu ? jinpu : shifu);
            if (!getsu) return jinpu;
            if (!ka) return shifu;
            return yukikaze;
        }
        return gyofu;
    }

    private bool lastIaijutsuWasTendo;

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && s.CooldownRemaining(ikishoten) < 3000 && s.Now > 0 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(ikishoten) && kenki <= 50 && s.Now > 3000)
            return ikishoten;
        // Not while Higanbana needs refreshing: that takes one Sen from a normal combo first.
        if (s.IsReady(meikyo) && !s.HasBuff("Meikyo Shisui") && !getsu && !ka && comboFrom == null
            && (s.DotRemaining("Higanbana (DoT)") > 12_000 || s.Charges(meikyo) == 2 || s.Now < 5000)
            && !s.HasBuff("Tsubame-gaeshi Ready") && !s.HasBuff("Tendo"))
            return meikyo;
        if (meditation >= 3 && s.IsReady(shoha))
            return shoha;
        if (s.IsReady(senei) && kenki >= 25)
            return senei;
        if (s.HasBuff("Zanshin Ready") && kenki >= 50)
            return zanshin;
        // Keep 25 for Senei and 50 for Zanshin when they're close.
        var reserve = (s.CooldownRemaining(senei) < 10_000 ? 25 : 0) + (s.HasBuff("Zanshin Ready") || s.CooldownRemaining(ikishoten) < 10_000 ? 50 : 0);
        if (kenki >= 25 + reserve && s.IsReady(shinten) && (kenki >= 65 || s.HasBuff("Fugetsu")))
            return shinten;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        var meikyoFinisher = (a == gekko || a == kasha || a == yukikaze) && comboFrom == null;
        if (a == gyofu) { kenki += 5; comboFrom = gyofu; }
        else if (a == jinpu) { kenki += 5; comboFrom = jinpu; }
        else if (a == shifu) { kenki += 5; comboFrom = shifu; }
        else if (a == yukikaze) { kenki += 15; setsu = true; comboFrom = null; }
        else if (a == gekko)
        {
            kenki += 10; getsu = true; comboFrom = null;
            if (meikyoFinisher) s.Grant("Fugetsu");
        }
        else if (a == kasha)
        {
            kenki += 10; ka = true; comboFrom = null;
            if (meikyoFinisher) s.Grant("Fuka");
        }
        else if (a == higanbana || a == midare || a == tendoSetsugekka)
        {
            getsu = ka = setsu = false;
            meditation = Math.Min(3, meditation + 1);
            lastIaijutsuWasTendo = a == tendoSetsugekka;
            if (a == tendoSetsugekka) s.Remove("Tendo");
        }
        else if (a == kaeshiSetsugekka || a == tendoKaeshi) s.Remove("Tsubame-gaeshi Ready");
        else if (a == ogiNamikiri)
        {
            meditation = Math.Min(3, meditation + 1);
            s.Remove("Ogi Namikiri Ready");
        }
        else if (a == kaeshiNamikiri) s.Remove("Kaeshi: Namikiri Ready");
        else if (a == ikishoten) kenki += 50;
        else if (a == senei || a == shinten) kenki -= 25;
        else if (a == zanshin) { kenki -= 50; s.Remove("Zanshin Ready"); }
        else if (a == shoha) meditation = 0;
        kenki = Math.Min(100, kenki);
    }
}
