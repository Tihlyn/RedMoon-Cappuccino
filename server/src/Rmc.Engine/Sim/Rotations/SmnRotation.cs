using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Summoner, level 100 (Dawntrail). A demi-summon every minute (Solar Bahamut with Searing Light,
/// the potion and Searing Flash every two minutes; Bahamut and Phoenix alternating between): six
/// Impulses or Fountains of Fire, Enkindle and Sunflare/Deathflare. Then the three primals: Ifrit
/// (Summon, two Ruby Rites, Crimson Cyclone and Strike), Titan (Summon, four Topaz Rites with
/// Mountain Buster), Garuda (Summon, four Emerald Rites, Slipstream); Ruin III filler, Ruin IV
/// from Energy Drain, which also gives two Necrotizes.
/// The demi's own attacks (Luxwave, Wyrmwave, Scarlet Flame) ride on each of its GCDs.
/// </summary>
public sealed class SmnRotation : Rotation
{
    private const int DemiMs = 15_000;

    public override string Job => "SMN";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;

    private enum Demi { Solar, Bahamut, Phoenix }

    // GCDs
    private readonly SimAbility ruin3 = Gcd("Ruin III", 3579, 400, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility ruin4 = Gcd("Ruin IV", 7426, 520, type: AttackType.Spell);
    private readonly SimAbility summonSolar = Gcd("Summon Solar Bahamut", 36992, 0, "manual", AttackType.Spell, cooldownMs: 60_000, group: "Demi");
    private readonly SimAbility summonBahamut = Gcd("Summon Bahamut", 7427, 0, "manual", AttackType.Spell, cooldownMs: 60_000, group: "Demi");
    private readonly SimAbility summonPhoenix = Gcd("Summon Phoenix", 25831, 0, "manual", AttackType.Spell, cooldownMs: 60_000, group: "Demi");
    private readonly SimAbility umbralImpulse = Gcd("Umbral Impulse", 36994, 640, type: AttackType.Spell);
    private readonly SimAbility astralImpulse = Gcd("Astral Impulse", 25820, 500, type: AttackType.Spell);
    private readonly SimAbility fountainOfFire = Gcd("Fountain of Fire", 16514, 580, type: AttackType.Spell);
    private readonly SimAbility summonIfrit = Gcd("Summon Ifrit II", 25838, 800, type: AttackType.Spell);
    private readonly SimAbility summonTitan = Gcd("Summon Titan II", 25839, 800, type: AttackType.Spell);
    private readonly SimAbility summonGaruda = Gcd("Summon Garuda II", 25840, 800, type: AttackType.Spell);
    private readonly SimAbility rubyRite = Gcd("Ruby Rite", 25823, 620, type: AttackType.Spell, castMs: 2800, recastMs: 3000);
    private readonly SimAbility crimsonCyclone = Gcd("Crimson Cyclone", 25835, 560, type: AttackType.Spell);
    private readonly SimAbility crimsonStrike = Gcd("Crimson Strike", 25885, 560, type: AttackType.Spell);
    private readonly SimAbility topazRite = Gcd("Topaz Rite", 25824, 340, type: AttackType.Spell);
    private readonly SimAbility emeraldRite = Gcd("Emerald Rite", 25825, 280, type: AttackType.Spell, recastMs: 1500);
    private readonly SimAbility slipstream = Gcd("Slipstream", 25837, 520, type: AttackType.Spell, castMs: 3000, recastMs: 3500,
        dot: new SimDot { Name = "Slipstream (DoT)", ActionId = 25837, Potency = 30, DurationMs = 15_000 });

    // oGCDs
    private readonly SimAbility enkindleSolar = Ogcd("Enkindle Solar Bahamut", 36998, 1500, 20_000);
    private readonly SimAbility enkindleBahamut = Ogcd("Enkindle Bahamut", 7429, 1300, 20_000);
    private readonly SimAbility enkindlePhoenix = Ogcd("Enkindle Phoenix", 16516, 1300, 20_000);
    private readonly SimAbility sunflare = Ogcd("Sunflare", 36996, 1000, 20_000);
    private readonly SimAbility deathflare = Ogcd("Deathflare", 3582, 500, 20_000);
    private readonly SimAbility searingLight = Ogcd("Searing Light", 25801, 0, 120_000, grants: ["Searing Light", "Ruby's Glimmer"]);
    private readonly SimAbility searingFlash = Ogcd("Searing Flash", 36991, 700, 1000);
    private readonly SimAbility energyDrain = Ogcd("Energy Drain", 16508, 200, 60_000);
    private readonly SimAbility necrotize = Ogcd("Necrotize", 36990, 500, 1000);
    private readonly SimAbility mountainBuster = Ogcd("Mountain Buster", 25836, 160, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Searing Light", DurationMs = 20_000, DamagePercent = 5 },
        new() { Name = "Ruby's Glimmer", DurationMs = 30_000 },
    ];

    private readonly Queue<SimAbility> primals = new();
    private int demiCount;
    private Demi? activeDemi;
    private int demiEndsAt;
    private bool enkindled, flared, furtherRuin, mountainBusterReady;
    private int aetherflow;

    public SmnRotation()
    {
        abilities =
        [
            ruin3, ruin4, summonSolar, summonBahamut, summonPhoenix, umbralImpulse, astralImpulse, fountainOfFire, summonIfrit, summonTitan,
            summonGaruda, rubyRite, crimsonCyclone, crimsonStrike, topazRite, emeraldRite, slipstream,
            enkindleSolar, enkindleBahamut, enkindlePhoenix, sunflare, deathflare, searingLight, searingFlash, energyDrain, necrotize,
            mountainBuster,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        primals.Clear();
        demiCount = 0;
        activeDemi = null;
        demiEndsAt = 0;
        enkindled = flared = furtherRuin = mountainBusterReady = false;
        aetherflow = 0;
    }

    // Solar Bahamut every other minute; Bahamut and Phoenix alternate in between.
    private SimAbility NextDemi => (demiCount % 4) switch { 0 or 2 => summonSolar, 1 => summonBahamut, _ => summonPhoenix };

    public override SimAbility? NextGcd(SimState s)
    {
        if (activeDemi is { } demi && s.Now < demiEndsAt)
            return demi switch { Demi.Solar => umbralImpulse, Demi.Bahamut => astralImpulse, _ => fountainOfFire };
        activeDemi = null;
        if (s.IsReady(summonSolar))
            return NextDemi;
        if (primals.Count > 0)
            return primals.Peek();
        if (furtherRuin)
            return ruin4;
        return ruin3;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && activeDemi == Demi.Solar && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(searingLight) && activeDemi == Demi.Solar)
            return searingLight;
        if (activeDemi is { } demi && !enkindled)
            return demi switch { Demi.Solar => enkindleSolar, Demi.Bahamut => enkindleBahamut, _ => enkindlePhoenix };
        if (activeDemi is Demi.Solar && !flared) return sunflare;
        if (activeDemi is Demi.Bahamut && !flared) return deathflare;
        if (s.HasBuff("Ruby's Glimmer")) return searingFlash;
        if (mountainBusterReady) return mountainBuster;
        if (s.IsReady(energyDrain) && aetherflow == 0 && (activeDemi != null || s.CooldownRemaining(searingLight) > 20_000))
            return energyDrain;
        if (aetherflow > 0 && (s.HasBuff("Searing Light") || s.CooldownRemaining(energyDrain) < 10_000))
            return necrotize;
        return null;
    }

    public override int PotencyBonus(SimAbility a, SimState s)
        => a == umbralImpulse ? 160 : a == astralImpulse || a == fountainOfFire ? 150 : 0;

    public override void OnUse(SimAbility a, SimState s)
    {
        if (primals.Count > 0 && primals.Peek() == a)
            primals.Dequeue();
        if (a == summonSolar || a == summonBahamut || a == summonPhoenix)
        {
            activeDemi = a == summonSolar ? Demi.Solar : a == summonBahamut ? Demi.Bahamut : Demi.Phoenix;
            demiEndsAt = s.Now + DemiMs;
            demiCount++;
            enkindled = flared = false;
            primals.Clear();
            foreach (var x in new[] { summonIfrit, rubyRite, rubyRite, crimsonCyclone, crimsonStrike,
                         summonTitan, topazRite, topazRite, topazRite, topazRite,
                         summonGaruda, emeraldRite, emeraldRite, emeraldRite, emeraldRite, slipstream })
                primals.Enqueue(x);
        }
        else if (a == enkindleSolar || a == enkindleBahamut || a == enkindlePhoenix) enkindled = true;
        else if (a == sunflare || a == deathflare) flared = true;
        else if (a == topazRite) mountainBusterReady = true;
        else if (a == mountainBuster) mountainBusterReady = false;
        else if (a == ruin4) furtherRuin = false;
        else if (a == searingFlash) s.Remove("Ruby's Glimmer");
        else if (a == energyDrain) { aetherflow = 2; furtherRuin = true; }
        else if (a == necrotize) aetherflow--;
    }
}
