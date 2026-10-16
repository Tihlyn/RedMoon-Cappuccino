using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Pictomancer, level 100 (Dawntrail). Fire → Aero → Water in Red/Green/Blue; Subtractive Palette at
/// 50 gauge (Blizzard → Stone → Thunder, Comet in Black from Monochrome Tones); Holy in White only to
/// avoid capping White Paint. Starry Muse and the potion every two minutes (Inspiration for five
/// spells, then Rainbow Drip and Star Prism), Striking Muse into the three hammers, Living Muse on
/// charge through Pom → Wing → Claw → Fang with Mog of the Ages and Retribution of the Madeen.
/// All motifs start painted; afterwards each is hardcast (3s, 4s recast) when its canvas is empty
/// and outside Starry Muse.
/// </summary>
public sealed class PctRotation : Rotation
{
    public override string Job => "PCT";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;

    // GCDs
    private readonly SimAbility fireInRed = Gcd("Fire in Red", 34650, 490, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility aeroInGreen = Gcd("Aero in Green", 34651, 530, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility waterInBlue = Gcd("Water in Blue", 34652, 570, type: AttackType.Spell, castMs: 1500);
    private readonly SimAbility blizzardInCyan = Gcd("Blizzard in Cyan", 34653, 860, type: AttackType.Spell, castMs: 2300, recastMs: 3300);
    private readonly SimAbility stoneInYellow = Gcd("Stone in Yellow", 34654, 900, type: AttackType.Spell, castMs: 2300, recastMs: 3300);
    private readonly SimAbility thunderInMagenta = Gcd("Thunder in Magenta", 34655, 940, type: AttackType.Spell, castMs: 2300, recastMs: 3300);
    private readonly SimAbility holyInWhite = Gcd("Holy in White", 34662, 570, type: AttackType.Spell);
    private readonly SimAbility cometInBlack = Gcd("Comet in Black", 34663, 940, type: AttackType.Spell, recastMs: 3300);
    private readonly SimAbility hammerStamp = Gcd("Hammer Stamp", 34678, 560, type: AttackType.Spell, autoCrit: true, autoDh: true);
    private readonly SimAbility hammerBrush = Gcd("Hammer Brush", 34679, 580, type: AttackType.Spell, autoCrit: true, autoDh: true);
    private readonly SimAbility polishingHammer = Gcd("Polishing Hammer", 34680, 600, type: AttackType.Spell, autoCrit: true, autoDh: true);
    private readonly SimAbility starPrism = Gcd("Star Prism", 34681, 1100, type: AttackType.Spell);
    private readonly SimAbility rainbowDrip = Gcd("Rainbow Drip", 34688, 1000, type: AttackType.Spell);
    private readonly SimAbility creatureMotif = Gcd("Creature Motif", 34689, 0, "manual", AttackType.Spell, castMs: 3000, recastMs: 4000);
    private readonly SimAbility weaponMotif = Gcd("Weapon Motif", 34690, 0, "manual", AttackType.Spell, castMs: 3000, recastMs: 4000);
    private readonly SimAbility landscapeMotif = Gcd("Landscape Motif", 34691, 0, "manual", AttackType.Spell, castMs: 3000, recastMs: 4000);

    // oGCDs
    private readonly SimAbility starryMuse = Ogcd("Starry Muse", 34675, 0, 120_000,
        grants: ["Starry Muse", "Inspiration", "Starstruck", "Subtractive Spectrum"]);
    private readonly SimAbility strikingMuse = Ogcd("Striking Muse", 34674, 0, 60_000, charges: 2);
    private readonly SimAbility[] livingMuses =
    [
        Ogcd("Pom Muse", 34670, 800, 40_000, charges: 3, group: "Living Muse"),
        Ogcd("Winged Muse", 34671, 800, 40_000, charges: 3, group: "Living Muse"),
        Ogcd("Clawed Muse", 34672, 800, 40_000, charges: 3, group: "Living Muse"),
        Ogcd("Fanged Muse", 34673, 800, 40_000, charges: 3, group: "Living Muse"),
    ];
    private readonly SimAbility mogOfTheAges = Ogcd("Mog of the Ages", 34676, 1000, 30_000, group: "Portrait");
    private readonly SimAbility madeen = Ogcd("Retribution of the Madeen", 34677, 1100, 30_000, group: "Portrait");
    private readonly SimAbility subtractivePalette = Ogcd("Subtractive Palette", 34683, 0, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private int comboStep, subtractiveStep, hammerStep;
    private int gauge, whitePaint, blackPaint;
    private int creature;           // 0 Pom .. 3 Fang: the next depiction to render
    private bool creaturePainted, weaponPainted, landscapePainted, hammerTime;
    private string? portrait;
    private int inspiration;

    public PctRotation()
    {
        abilities =
        [
            fireInRed, aeroInGreen, waterInBlue, blizzardInCyan, stoneInYellow, thunderInMagenta, holyInWhite, cometInBlack,
            hammerStamp, hammerBrush, polishingHammer, starPrism, rainbowDrip, creatureMotif, weaponMotif, landscapeMotif,
            starryMuse, strikingMuse, .. livingMuses, mogOfTheAges, madeen, subtractivePalette,
        ];
        buffs =
        [
            new() { Name = "Starry Muse", DurationMs = 20_000, DamagePercent = 5 },
            // Hyperphantasia: 25% off the next five aetherhue, Holy, Comet and Star Prism casts.
            new() { Name = "Inspiration", DurationMs = 30_000, HastePercent = 25 },
            new() { Name = "Starstruck", DurationMs = 20_000 },
            new() { Name = "Subtractive Spectrum", DurationMs = 30_000 },
            new() { Name = "Rainbow Bright", DurationMs = 30_000 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = subtractiveStep = hammerStep = 0;
        gauge = whitePaint = blackPaint = 0;
        creature = 0;
        creaturePainted = weaponPainted = landscapePainted = true;
        hammerTime = false;
        portrait = null;
        inspiration = 0;
    }

    private bool InStarry(SimState s) => s.HasBuff("Starry Muse");

    public override SimAbility? NextGcd(SimState s)
    {
        if (s.HasBuff("Rainbow Bright")) return rainbowDrip;
        if (s.HasBuff("Starstruck")) return starPrism;
        if (hammerTime)
            return hammerStep switch { 0 => hammerStamp, 1 => hammerBrush, _ => polishingHammer };
        if (blackPaint > 0 && (InStarry(s) || subtractiveStep == 0 && s.CooldownRemaining(starryMuse) > 30_000))
            return cometInBlack;
        if (subtractiveStep > 0)
            return subtractiveStep switch { 3 => blizzardInCyan, 2 => stoneInYellow, _ => thunderInMagenta };
        // Repaint empty canvases outside the burst.
        if (!InStarry(s))
        {
            if (!landscapePainted && s.CooldownRemaining(starryMuse) < 30_000) return landscapeMotif;
            if (!weaponPainted && s.CooldownRemaining(strikingMuse) < 20_000) return weaponMotif;
            if (!creaturePainted && (s.Charges(livingMuses[0]) < 3 || s.CooldownRemaining(livingMuses[0]) < 15_000)) return creatureMotif;
        }
        if (whitePaint >= 5 && comboStep == 2) return holyInWhite;
        return comboStep switch { 1 => aeroInGreen, 2 => waterInBlue, _ => fireInRed };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && landscapePainted && s.IsReady(starryMuse) && s.Now > 2000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(starryMuse) && landscapePainted && s.Now > 2000)
            return starryMuse;
        if (portrait == "Madeen" && s.IsReady(madeen)) return madeen;
        if (portrait == "Moogle" && s.IsReady(mogOfTheAges)) return mogOfTheAges;
        if (creaturePainted && s.IsReady(livingMuses[0]) && (InStarry(s) || s.Charges(livingMuses[0]) == 3 || s.CooldownRemaining(starryMuse) > 40_000))
            return livingMuses[creature];
        if (weaponPainted && !hammerTime && s.IsReady(strikingMuse) && (InStarry(s) || s.Charges(strikingMuse) == 2 || s.CooldownRemaining(starryMuse) > 30_000))
            return strikingMuse;
        if (subtractiveStep == 0 && blackPaint == 0 && (gauge >= 50 || s.HasBuff("Subtractive Spectrum"))
            && (InStarry(s) || gauge >= 100 || s.CooldownRemaining(starryMuse) > 20_000 || s.HasBuff("Subtractive Spectrum")))
            return subtractivePalette;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        // Hyperphantasia: five qualifying casts under Inspiration, then Rainbow Bright.
        if (s.HasBuff("Inspiration") && (a == fireInRed || a == aeroInGreen || a == waterInBlue || a == blizzardInCyan
            || a == stoneInYellow || a == thunderInMagenta || a == holyInWhite || a == cometInBlack || a == starPrism))
        {
            if (++inspiration >= 5)
            {
                s.Remove("Inspiration");
                s.Grant("Rainbow Bright");
            }
        }

        if (a == fireInRed) comboStep = 1;
        else if (a == aeroInGreen) comboStep = 2;
        else if (a == waterInBlue) { comboStep = 0; gauge = Math.Min(100, gauge + 25); whitePaint = Math.Min(5, whitePaint + 1); }
        else if (a == blizzardInCyan || a == stoneInYellow || a == thunderInMagenta) subtractiveStep--;
        else if (a == holyInWhite) whitePaint--;
        else if (a == cometInBlack) blackPaint--;
        else if (a == hammerStamp || a == hammerBrush || a == polishingHammer)
        {
            hammerStep++;
            if (hammerStep == 3) { hammerTime = false; hammerStep = 0; }
        }
        else if (a == starPrism) s.Remove("Starstruck");
        else if (a == rainbowDrip) { s.Remove("Rainbow Bright"); whitePaint = Math.Min(5, whitePaint + 1); }
        else if (a == creatureMotif) creaturePainted = true;
        else if (a == weaponMotif) weaponPainted = true;
        else if (a == landscapeMotif) landscapePainted = true;
        else if (a == starryMuse) { landscapePainted = false; inspiration = 0; }
        else if (a == strikingMuse) { weaponPainted = false; hammerTime = true; hammerStep = 0; }
        else if (Array.IndexOf(livingMuses, a) is var i and >= 0)
        {
            creaturePainted = false;
            if (i == 1) portrait = "Moogle";
            else if (i == 3) portrait = "Madeen";
            creature = (creature + 1) % 4;
        }
        else if (a == mogOfTheAges || a == madeen) portrait = null;
        else if (a == subtractivePalette)
        {
            if (s.HasBuff("Subtractive Spectrum")) s.Remove("Subtractive Spectrum");
            else gauge -= 50;
            subtractiveStep = 3;
            if (whitePaint > 0) { whitePaint--; blackPaint++; }
        }
    }
}
