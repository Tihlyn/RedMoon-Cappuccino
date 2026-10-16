using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Black Mage, level 100 (Dawntrail). The standard loop, scripted rather than tracked through MP:
/// Astral Fire is Fire III, Fire IV ×3, Paradox, Fire IV ×3, Despair, Flare Star (Manafont, when up,
/// adds Paradox, six more Fire IVs, Despair and Flare Star); Umbral Ice is Blizzard III, Blizzard IV,
/// Paradox. High Thunder whenever Thunderhead is up and the damage over time is low, Xenoglossy for
/// every Polyglot (one per 30s, plus Amplifier), Ley Lines on charge.
/// Multipliers: Enochian 27% on everything; Astral Fire III 80% on Fire IV, Despair and Flare Star;
/// Fire III and Blizzard III at xivgear's effective 1.35 and 0.72 (cast across the element switch).
/// Opposite-element casts at three stacks take half the cast time.
/// </summary>
public sealed class BlmRotation : Rotation
{
    private const int PolyglotIntervalMs = 30_000;

    public override string Job => "BLM";
    public override string SpeedStat => Rmc.GameData.Stats.SpellSpeed;
    public override int TraitPercent => 130;

    private readonly SimAbility fire4 = Gcd("Fire IV", 3577, 300, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility despair = Gcd("Despair", 16505, 350, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility flareStar = Gcd("Flare Star", 36989, 500, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility paradox = Gcd("Paradox", 25797, 540, type: AttackType.Spell);
    private readonly SimAbility fire3 = new()
    {
        Name = "Fire III", ActionId = 152, Potency = 290, Type = AttackType.Spell, Gcd = true, CastMs = 1750, DamageMultiplier = 1.35,
    };
    private readonly SimAbility blizzard3 = new()
    {
        Name = "Blizzard III", ActionId = 154, Potency = 290, Type = AttackType.Spell, Gcd = true, CastMs = 1750, DamageMultiplier = 0.72,
    };
    private readonly SimAbility blizzard4 = Gcd("Blizzard IV", 3576, 300, type: AttackType.Spell, castMs: 2000);
    private readonly SimAbility highThunder = Gcd("High Thunder", 36986, 150, type: AttackType.Spell,
        dot: new SimDot { Name = "High Thunder (DoT)", ActionId = 36986, Potency = 60, DurationMs = 30_000 });
    private readonly SimAbility xenoglossy = Gcd("Xenoglossy", 16507, 890, type: AttackType.Spell);

    private readonly SimAbility manafont = Ogcd("Manafont", 158, 0, 120_000);
    private readonly SimAbility leyLines = Ogcd("Ley Lines", 3573, 0, 120_000, charges: 2, grants: ["Ley Lines"]);
    private readonly SimAbility amplifier = Ogcd("Amplifier", 25796, 0, 120_000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs;

    private readonly Queue<SimAbility> phase = new();
    private bool thunderhead, inFire;
    private int polyglotsUsed, amplified;
    private bool manafontQueued;

    public BlmRotation()
    {
        abilities = [fire4, despair, flareStar, paradox, fire3, blizzard3, blizzard4, highThunder, xenoglossy, manafont, leyLines, amplifier];
        buffs =
        [
            new() { Name = "Enochian", DurationMs = int.MaxValue / 2, DamagePercent = 27 },
            new() { Name = "Astral Fire III", DurationMs = int.MaxValue / 2, DamagePercent = 80, AppliesTo = a => a == fire4 || a == despair || a == flareStar },
            new() { Name = "Ley Lines", DurationMs = 20_000, HastePercent = 15 },
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        phase.Clear();
        thunderhead = true;
        inFire = false;
        polyglotsUsed = amplified = 0;
        manafontQueued = false;
    }

    private int Polyglots(SimState s) => Math.Min(3, s.Now / PolyglotIntervalMs + amplified - polyglotsUsed);

    private void QueueFire()
    {
        foreach (var a in new[] { fire3, fire4, fire4, fire4, paradox, fire4, fire4, fire4, despair, flareStar })
            phase.Enqueue(a);
    }

    private void QueueIce()
    {
        foreach (var a in new[] { blizzard3, blizzard4, paradox })
            phase.Enqueue(a);
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (!s.HasBuff("Enochian"))
            s.Grant("Enochian");
        if (thunderhead && s.DotRemaining("High Thunder (DoT)") < 5000 && s.FightEndMs - s.Now > 15_000)
            return highThunder;
        // Polyglot: spend in Umbral Ice, or anywhere before the third one overcaps.
        if (Polyglots(s) > 0 && (!inFire && phase.Count > 0 && phase.Peek() == fire3 || Polyglots(s) >= 2))
            return xenoglossy;
        if (phase.Count == 0)
        {
            manafontQueued = false;
            if (inFire) QueueIce(); else QueueFire();
            inFire = !inFire;
            thunderhead = true;
        }
        return phase.Peek();
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (s.IsReady(potion) && s.Now > 2000 && inFire && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(leyLines) && s.Now > 2000 && (s.Charges(leyLines) == 2 || !s.HasBuff("Ley Lines")))
            return leyLines;
        if (s.IsReady(amplifier) && Polyglots(s) < 3)
            return amplifier;
        // Manafont in Astral Fire (in practice after an instant: casts leave no weave room); its
        // Paradox, six Fire IVs, Despair and Flare Star follow this phase's Flare Star.
        if (s.IsReady(manafont) && inFire && !manafontQueued)
            return manafont;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (phase.Count > 0 && phase.Peek() == a)
            phase.Dequeue();
        if (a == fire3) s.Grant("Astral Fire III");
        else if (a == blizzard3) s.Remove("Astral Fire III");
        else if (a == highThunder) thunderhead = false;
        else if (a == xenoglossy) polyglotsUsed++;
        else if (a == amplifier) amplified++;
        else if (a == manafont)
        {
            manafontQueued = true;
            foreach (var x in new[] { paradox, fire4, fire4, fire4, fire4, fire4, fire4, despair, flareStar })
                phase.Enqueue(x);
        }
    }
}
