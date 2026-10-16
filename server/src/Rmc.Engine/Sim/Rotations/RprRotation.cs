using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Reaper, level 100 (Dawntrail). Death's Design kept up with Shadow of Death; Soul Slice and the
/// Slice combo build Soul, spent through Gluttony (two Executioner's) and Blood Stalk / Unveiled
/// Gibbet/Gallows (one Gibbet or Gallows); those build Shroud for Enshroud: four Reapings, two
/// Lemure's Slices, Sacrificium and Communio. Arcane Circle, the potion and Plentiful Harvest
/// (with a full eight Immortal Sacrifice) every two minutes give the free Enshroud and Perfectio.
/// Positionals always land; Gibbet and Gallows (and the Reapings) alternate so each is enhanced.
/// </summary>
public sealed class RprRotation : Rotation
{
    private const int EnshroudGcdMs = 1500;

    public override string Job => "RPR";

    // GCDs
    private readonly SimAbility slice = Gcd("Slice", 24373, 420);
    private readonly SimAbility waxingSlice = Gcd("Waxing Slice", 24374, 500, "Combo Potency");
    private readonly SimAbility infernalSlice = Gcd("Infernal Slice", 24375, 600, "Combo Potency");
    private readonly SimAbility shadowOfDeath = Gcd("Shadow of Death", 24378, 300);
    private readonly SimAbility soulSlice = Gcd("Soul Slice", 24380, 520, cooldownMs: 30_000, charges: 2);
    private readonly SimAbility gibbet = Gcd("Gibbet", 24382, 620, "Flank Enhanced Potency");
    private readonly SimAbility gallows = Gcd("Gallows", 24383, 620, "Rear Enhanced Potency");
    private readonly SimAbility executionersGibbet = Gcd("Executioner's Gibbet", 36970, 820, "Flank Enhanced Potency");
    private readonly SimAbility executionersGallows = Gcd("Executioner's Gallows", 36971, 820, "Rear Enhanced Potency");
    private readonly SimAbility voidReaping = Gcd("Void Reaping", 24395, 640, "Enhanced Void Reaping Potency", recastMs: EnshroudGcdMs, fixedRecast: true);
    private readonly SimAbility crossReaping = Gcd("Cross Reaping", 24396, 640, "Enhanced Cross Reaping Potency", recastMs: EnshroudGcdMs, fixedRecast: true);
    private readonly SimAbility communio = Gcd("Communio", 24398, 1100, type: AttackType.Spell, castMs: 1300);
    private readonly SimAbility perfectio = Gcd("Perfectio", 36973, 1300);
    private readonly SimAbility plentifulHarvest = Gcd("Plentiful Harvest", 24385, 1000, "manual");

    // oGCDs
    private readonly SimAbility arcaneCircle = Ogcd("Arcane Circle", 24405, 0, 120_000, grants: ["Arcane Circle", "Bloodsown Circle"]);
    private readonly SimAbility gluttony = Ogcd("Gluttony", 24393, 560, 60_000);
    private readonly SimAbility bloodStalk = Ogcd("Unveiled Gibbet", 24390, 440, 1000, group: "Avatar");
    private readonly SimAbility enshroud = Ogcd("Enshroud", 24394, 0, 5000, grants: ["Enshrouded", "Oblatio"]);
    private readonly SimAbility lemuresSlice = Ogcd("Lemure's Slice", 24399, 280, 1000);
    private readonly SimAbility sacrificium = Ogcd("Sacrificium", 36969, 700, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Death's Design", DurationMs = 60_000, DamagePercent = 10 },
        new() { Name = "Arcane Circle", DurationMs = 20_000, DamagePercent = 3 },
        new() { Name = "Bloodsown Circle", DurationMs = 6_000 },
        new() { Name = "Enshrouded", DurationMs = 30_000 },
        new() { Name = "Oblatio", DurationMs = 30_000 },
        new() { Name = "Ideal Host", DurationMs = 30_000 },
        new() { Name = "Perfectio Occulta", DurationMs = 30_000 },
        new() { Name = "Perfectio Parata", DurationMs = 30_000 },
    ];

    private int comboStep;
    private int soul, shroud;
    private int lemure, voidShroud;
    private int reavers, executioners;
    private bool gibbetNext, voidNext;
    private bool immortalSacrifice;

    public RprRotation()
    {
        abilities =
        [
            slice, waxingSlice, infernalSlice, shadowOfDeath, soulSlice, gibbet, gallows, executionersGibbet, executionersGallows,
            voidReaping, crossReaping, communio, perfectio, plentifulHarvest,
            arcaneCircle, gluttony, bloodStalk, enshroud, lemuresSlice, sacrificium,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = 0;
        soul = shroud = 0;
        lemure = voidShroud = 0;
        reavers = executioners = 0;
        gibbetNext = voidNext = true;
        immortalSacrifice = false;
    }

    // Death's Design is a debuff on the target: tracked as a buff so it raises everything.
    private int DesignRemaining(SimState s) => s.BuffRemaining("Death's Design");

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.HasBuff("Perfectio Parata"))
            return perfectio;
        if (lemure > 0)
        {
            if (lemure == 1)
                return communio;
            return voidNext ? voidReaping : crossReaping;
        }
        if (executioners > 0)
            return gibbetNext ? executionersGibbet : executionersGallows;
        if (reavers > 0)
            return gibbetNext ? gibbet : gallows;
        // Keep Death's Design up; extend it before the two-minute burst.
        var burstSoon = s.CooldownRemaining(arcaneCircle) < 5000;
        if (DesignRemaining(s) < 5000 || (burstSoon && DesignRemaining(s) < 30_000))
            return shadowOfDeath;
        if (immortalSacrifice && !s.HasBuff("Bloodsown Circle"))
            return plentifulHarvest;
        if (s.IsReady(soulSlice) && soul <= 50)
            return soulSlice;
        return comboStep switch { 1 => waxingSlice, 2 => infernalSlice, _ => slice };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && s.CooldownRemaining(arcaneCircle) < 3000 && s.Now > 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(arcaneCircle) && s.Now > 3000 && s.HasBuff("Death's Design"))
            return arcaneCircle;

        if (lemure > 0)
        {
            if (s.HasBuff("Oblatio"))
                return sacrificium;
            if (voidShroud >= 2)
                return lemuresSlice;
            return null;
        }

        var busy = reavers > 0 || executioners > 0;
        if (!busy && (shroud >= 50 || s.HasBuff("Ideal Host")) && s.IsReady(enshroud)
            && (s.HasBuff("Ideal Host") || s.HasBuff("Arcane Circle") || s.CooldownRemaining(arcaneCircle) > 15_000 || shroud >= 90))
            return enshroud;
        if (!busy && soul >= 50 && s.IsReady(gluttony))
            return gluttony;
        if (!busy && soul >= 50 && s.CooldownRemaining(gluttony) > 10_000 && s.IsReady(bloodStalk) && shroud < 100)
            return bloodStalk;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == slice) { soul += 10; comboStep = 1; }
        else if (a == waxingSlice) { soul += 10; comboStep = 2; }
        else if (a == infernalSlice) { soul += 10; comboStep = 0; }
        else if (a == soulSlice) soul += 50;
        else if (a == shadowOfDeath)
        {
            // Death's Design stacks 30s per application up to 60s.
            s.Grant("Death's Design", Math.Min(60_000, DesignRemaining(s) + 30_000));
        }
        else if (a == gibbet || a == gallows) { reavers--; shroud += 10; gibbetNext = a == gallows; }
        else if (a == executionersGibbet || a == executionersGallows) { executioners--; shroud += 10; gibbetNext = a == executionersGallows; }
        else if (a == gluttony) { soul -= 50; executioners = 2; }
        else if (a == bloodStalk) { soul -= 50; reavers = 1; }
        else if (a == enshroud)
        {
            if (s.HasBuff("Ideal Host")) s.Remove("Ideal Host");
            else shroud -= 50;
            lemure = 5;
            voidShroud = 0;
        }
        else if (a == voidReaping || a == crossReaping) { lemure--; voidShroud++; voidNext = a == crossReaping; }
        else if (a == lemuresSlice) voidShroud -= 2;
        else if (a == sacrificium) s.Remove("Oblatio");
        else if (a == communio)
        {
            lemure = 0;
            voidShroud = 0;
            s.Remove("Enshrouded");
            s.Remove("Oblatio");
            if (s.HasBuff("Perfectio Occulta"))
            {
                s.Remove("Perfectio Occulta");
                s.Grant("Perfectio Parata");
            }
        }
        else if (a == perfectio) s.Remove("Perfectio Parata");
        else if (a == arcaneCircle) immortalSacrifice = true;
        else if (a == plentifulHarvest)
        {
            immortalSacrifice = false;
            s.Grant("Ideal Host");
            s.Grant("Perfectio Occulta");
        }
        soul = Math.Min(100, soul);
        shroud = Math.Min(100, shroud);
    }
}
