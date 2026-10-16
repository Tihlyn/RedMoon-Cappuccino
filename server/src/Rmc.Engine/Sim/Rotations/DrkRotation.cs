using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Dark Knight, level 100 (Dawntrail). Hard Slash combo; Bloodspiller at 50 Blood; Delirium every
/// minute into Scarlet Delirium → Comeuppance → Torcleaver; Living Shadow (one pet hit for its five
/// attacks) every two minutes with the potion, Disesteem, The Blackest Night for a free Edge of
/// Shadow, Shadowbringer, Carve and Spit, Salted Earth and Salt and Darkness. Edge of Shadow keeps
/// Darkside up and spends MP (3,000 each), which comes back from the server tick, Syphon Strike,
/// Carve and Spit and Blood Weapon.
/// </summary>
public sealed class DrkRotation : Rotation
{
    private const int MaxMp = 10_000;
    /// <summary>Living Shadow: Abyssal Drain, Shadowbringer, Edge of Shadow, Bloodspiller, Disesteem.</summary>
    private const int ShadowPotency = 420 + 570 + 420 + 420 + 620;
    /// <summary>The shadow's damage per potency relative to the knight's own (under Darkside), measured
    /// on xivgear's sim: it doesn't take Darkside but hits harder per potency than a tank does.</summary>
    private const double ShadowDamageMultiplier = 1.058;

    public override string Job => "DRK";

    // GCDs
    private readonly SimAbility hardSlash = Gcd("Hard Slash", 3617, 300);
    private readonly SimAbility syphonStrike = Gcd("Syphon Strike", 3623, 380, "Combo Potency");
    private readonly SimAbility souleater = Gcd("Souleater", 3632, 480, "Combo Potency");
    private readonly SimAbility bloodspiller = Gcd("Bloodspiller", 7392, 600);
    private readonly SimAbility scarletDelirium = Gcd("Scarlet Delirium", 36928, 620);
    private readonly SimAbility comeuppance = Gcd("Comeuppance", 36929, 720);
    private readonly SimAbility torcleaver = Gcd("Torcleaver", 36930, 820);
    private readonly SimAbility disesteem = Gcd("Disesteem", 36932, 1000);

    // oGCDs
    private readonly SimAbility delirium = Ogcd("Delirium", 7390, 0, 60_000, grants: ["Blood Weapon"]);
    private readonly SimAbility edgeOfShadow = Ogcd("Edge of Shadow", 16470, 460, 1000);
    private readonly SimAbility shadowbringer = Ogcd("Shadowbringer", 25757, 600, 60_000, charges: 2);
    private readonly SimAbility carveAndSpit = Ogcd("Carve and Spit", 3643, 540, 60_000);
    private readonly SimAbility saltedEarth = Ogcd("Salted Earth", 3639, 0, 90_000,
        dot: new SimDot { Name = "Salted Earth (DoT)", ActionId = 3639, PotencyLabel = "base", Potency = 50, DurationMs = 15_000 });
    private readonly SimAbility saltAndDarkness = Ogcd("Salt and Darkness", 25755, 500, 20_000);
    private readonly SimAbility livingShadow = new()
    {
        Name = "Living Shadow", ActionId = 16472, PotencyLabel = "manual", Potency = ShadowPotency, Type = AttackType.Ability,
        CooldownMs = 120_000, Grants = ["Scorn"], DamageMultiplier = ShadowDamageMultiplier,
    };
    private readonly SimAbility blackestNight = Ogcd("The Blackest Night", 7393, 0, 15_000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Darkside", DurationMs = 30_000, DamagePercent = 10 },
        new() { Name = "Blood Weapon", DurationMs = 15_000 },
        new() { Name = "Scorn", DurationMs = 30_000 },
    ];

    private int comboStep, deliriumStep;
    private int blood, mp;
    private int bloodWeaponStacks;
    private bool darkArts, saltReady;
    private int lastTick;

    public DrkRotation()
    {
        abilities =
        [
            hardSlash, syphonStrike, souleater, bloodspiller, scarletDelirium, comeuppance, torcleaver, disesteem,
            delirium, edgeOfShadow, shadowbringer, carveAndSpit, saltedEarth, saltAndDarkness, livingShadow, blackestNight,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = deliriumStep = 0;
        blood = 0;
        mp = MaxMp;
        bloodWeaponStacks = 0;
        darkArts = saltReady = false;
        lastTick = 0;
    }

    // MP server ticks: 200 every 3s in combat.
    private void Regen(SimState s)
    {
        var ticks = s.Now / 3000 - lastTick;
        if (ticks <= 0) return;
        mp = Math.Min(MaxMp, mp + 200 * ticks);
        lastTick = s.Now / 3000;
    }

    private bool Burst(SimState s) => s.HasBuff("Scorn") || s.CooldownRemaining(livingShadow) > 100_000;

    public override SimAbility? NextGcd(SimState s)
    {
        if (deliriumStep > 0)
            return deliriumStep switch { 1 => scarletDelirium, 2 => comeuppance, _ => torcleaver };
        if (s.HasBuff("Scorn"))
            return disesteem;
        var nextBlood = comboStep == 2 ? 20 : 0;
        if (blood >= 50 && (blood + nextBlood > 100 || Burst(s) || s.CooldownRemaining(delirium) > 10_000))
            return bloodspiller;
        return comboStep switch { 1 => syphonStrike, 2 => souleater, _ => hardSlash };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        Regen(s);
        var darkside = s.BuffRemaining("Darkside");
        if (darkside == 0 && (mp >= 3000 || darkArts))
            return edgeOfShadow;
        if (s.IsReady(potion) && s.CooldownRemaining(livingShadow) < 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(livingShadow) && darkside > 0)
            return livingShadow;
        if (s.IsReady(delirium) && darkside > 0 && deliriumStep == 0)
            return delirium;
        if (s.HasBuff("Scorn") && s.IsReady(blackestNight) && !darkArts && mp >= 3000)
            return blackestNight;
        if (s.IsReady(saltedEarth))
            return saltedEarth;
        if (saltReady && s.IsReady(saltAndDarkness))
            return saltAndDarkness;
        if (s.IsReady(carveAndSpit) && (Burst(s) || s.CooldownRemaining(livingShadow) > 30_000))
            return carveAndSpit;
        if (s.IsReady(shadowbringer) && (Burst(s) || s.Charges(shadowbringer) == 2 && s.CooldownRemaining(livingShadow) > 30_000))
            return shadowbringer;
        // Edge of Shadow: keep Darkside, spend in the burst, never sit near full MP.
        if (darkArts || (mp >= 3000 && (darkside < 10_000 || Burst(s) || mp >= MaxMp - 1200)))
            return edgeOfShadow;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        Regen(s);
        if (a.Gcd && bloodWeaponStacks > 0)
        {
            bloodWeaponStacks--;
            blood += 10;
            mp += 600;
        }
        if (a == hardSlash) comboStep = 1;
        else if (a == syphonStrike) { comboStep = 2; mp += 600; }
        else if (a == souleater) { comboStep = 0; blood += 20; }
        else if (a == bloodspiller) blood -= 50;
        else if (a == scarletDelirium) { deliriumStep = 2; mp += 200; }
        else if (a == comeuppance) { deliriumStep = 3; mp += 200; }
        else if (a == torcleaver) { deliriumStep = 0; mp += 200; }
        else if (a == disesteem) s.Remove("Scorn");
        else if (a == delirium) { deliriumStep = 1; bloodWeaponStacks = 3; }
        else if (a == edgeOfShadow)
        {
            if (darkArts) darkArts = false;
            else mp -= 3000;
            s.Grant("Darkside", Math.Min(60_000, s.BuffRemaining("Darkside") + 30_000));
        }
        else if (a == blackestNight) { mp -= 3000; darkArts = true; }
        else if (a == carveAndSpit) mp += 600;
        else if (a == saltedEarth) saltReady = true;
        else if (a == saltAndDarkness) saltReady = false;
        blood = Math.Min(100, blood);
        mp = Math.Min(MaxMp, mp);
    }
}
