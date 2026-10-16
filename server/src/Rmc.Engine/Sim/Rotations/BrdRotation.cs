using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Bard, level 100 (Dawntrail). Songs in the standard order, each 45s on a two-minute cycle
/// (the Wanderer's Minuet, Mage's Ballad, Army's Paeon cut short when the Minuet returns);
/// Raging Strikes, Battle Voice, Radiant Finale (three Coda) and the potion with the Minuet,
/// Barrage into a triple Refulgent Arrow and Resonant Arrow, Radiant Encore. Caustic Bite and
/// Stormbite refreshed by Iron Jaws; Apex Arrow at full Soul Voice and Blast Arrow after it;
/// Empyreal Arrow, Sidewinder and Heartbreak Shot on cooldown, Pitch Perfect at three Repertoire.
/// Random procs count as their expected value: Repertoire 80% per 3s song tick (Pitch Perfect
/// stacks, 7.5s off Heartbreak Shot, or 4% Paeon haste), Soul Voice 5 per Repertoire, Hawk's Eye
/// 35% per Burst Shot, bite or Iron Jaws.
/// </summary>
public sealed class BrdRotation : Rotation
{
    private const int SongMs = 45_000;
    private const double RepertoirePerTick = 0.8;
    private const double HawksEyeChance = 0.35;

    public override string Job => "BRD";
    public override int TraitPercent => 120;
    public override int AutoAttackPotency => 80;

    private enum Song { None, Minuet, Ballad, Paeon }

    // GCDs
    private readonly SimAbility burstShot = Gcd("Burst Shot", 16495, 220);
    private readonly SimAbility refulgentArrow = Gcd("Refulgent Arrow", 7409, 280);
    private readonly SimAbility barragedRefulgent = Gcd("Refulgent Arrow", 7409, 840, "manual");
    private readonly SimAbility causticBite = Gcd("Caustic Bite", 7406, 150,
        dot: new SimDot { Name = "Caustic Bite (DoT)", ActionId = 7406, Potency = 20, DurationMs = 45_000 });
    private readonly SimAbility stormbite = Gcd("Stormbite", 7407, 100,
        dot: new SimDot { Name = "Stormbite (DoT)", ActionId = 7407, Potency = 25, DurationMs = 45_000 });
    private readonly SimAbility ironJaws = Gcd("Iron Jaws", 3560, 100);
    private readonly SimAbility apexArrow = Gcd("Apex Arrow", 16496, 140);
    private readonly SimAbility blastArrow = Gcd("Blast Arrow", 25784, 700);
    private readonly SimAbility radiantEncore = Gcd("Radiant Encore", 36977, 900, "manual");
    private readonly SimAbility resonantArrow = Gcd("Resonant Arrow", 36976, 640);

    // oGCDs
    private readonly SimAbility minuet = Ogcd("the Wanderer's Minuet", 3559, 0, 120_000);
    private readonly SimAbility ballad = Ogcd("Mage's Ballad", 114, 0, 120_000);
    private readonly SimAbility paeon = Ogcd("Army's Paeon", 116, 0, 120_000);
    private readonly SimAbility ragingStrikes = Ogcd("Raging Strikes", 101, 0, 120_000, grants: ["Raging Strikes"]);
    private readonly SimAbility battleVoice = Ogcd("Battle Voice", 118, 0, 120_000, grants: ["Battle Voice"]);
    private readonly SimAbility radiantFinale = Ogcd("Radiant Finale", 25785, 0, 110_000, grants: ["Radiant Finale", "Radiant Encore Ready"]);
    private readonly SimAbility barrage = Ogcd("Barrage", 107, 0, 120_000, grants: ["Barrage", "Resonant Arrow Ready"]);
    private readonly SimAbility empyrealArrow = Ogcd("Empyreal Arrow", 3558, 260, 15_000);
    private readonly SimAbility sidewinder = Ogcd("Sidewinder", 3562, 400, 60_000);
    private readonly SimAbility heartbreakShot = Ogcd("Heartbreak Shot", 36975, 180, 15_000, charges: 3);
    private readonly SimAbility pitchPerfect = Ogcd("Pitch Perfect", 7404, 360, 1000, "3 Repertoire Stacks");
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Raging Strikes", DurationMs = 20_000, DamagePercent = 15 },
        new() { Name = "Battle Voice", DurationMs = 20_000, DirectHitRate = 20 },
        new() { Name = "Radiant Finale", DurationMs = 20_000, DamagePercent = 6 },
        new() { Name = "Radiant Encore Ready", DurationMs = 30_000 },
        new() { Name = "Barrage", DurationMs = 10_000 },
        new() { Name = "Resonant Arrow Ready", DurationMs = 30_000 },
        new() { Name = "Blast Arrow Ready", DurationMs = 10_000 },
        new() { Name = "the Wanderer's Minuet", DurationMs = SongMs, CritRate = 2 },
        new() { Name = "Mage's Ballad", DurationMs = SongMs, DamagePercent = 1 },
        new() { Name = "Army's Paeon", DurationMs = SongMs, DirectHitRate = 3 },
        // Paeon's Repertoire haste, one buff per 4% step.
        new() { Name = "Paeon 1", DurationMs = SongMs, HastePercent = 4 },
        new() { Name = "Paeon 2", DurationMs = SongMs, HastePercent = 4 },
        new() { Name = "Paeon 3", DurationMs = SongMs, HastePercent = 4 },
        new() { Name = "Paeon 4", DurationMs = SongMs, HastePercent = 4 },
    ];

    private Song song;
    private int lastTick;
    private double pitchStacks, paeonStacks, soulVoice, hawksEye;

    public BrdRotation()
    {
        abilities =
        [
            burstShot, refulgentArrow, barragedRefulgent, causticBite, stormbite, ironJaws, apexArrow, blastArrow, radiantEncore, resonantArrow,
            minuet, ballad, paeon, ragingStrikes, battleVoice, radiantFinale, barrage, empyrealArrow, sidewinder, heartbreakShot, pitchPerfect,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        song = Song.None;
        lastTick = 0;
        pitchStacks = paeonStacks = soulVoice = hawksEye = 0;
    }

    private string? SongBuff => song switch
    {
        Song.Minuet => "the Wanderer's Minuet",
        Song.Ballad => "Mage's Ballad",
        Song.Paeon => "Army's Paeon",
        _ => null,
    };

    // Song ticks every 3s: expected Repertoire and Soul Voice.
    private void Tick(SimState s)
    {
        var ticks = s.Now / 3000 - lastTick;
        if (ticks <= 0) return;
        lastTick = s.Now / 3000;
        if (SongBuff is not { } buff || !s.HasBuff(buff))
            return;
        var procs = ticks * RepertoirePerTick;
        soulVoice = Math.Min(100, soulVoice + 5 * procs);
        switch (song)
        {
            case Song.Minuet: pitchStacks = Math.Min(3, pitchStacks + procs); break;
            case Song.Ballad: s.ReduceCooldown(heartbreakShot, (int)(7500 * procs)); break;
            case Song.Paeon:
                var before = (int)paeonStacks;
                paeonStacks = Math.Min(4, paeonStacks + procs);
                for (var i = before + 1; i <= (int)paeonStacks; i++)
                    s.Grant($"Paeon {i}", s.BuffRemaining("Army's Paeon"));
                break;
        }
    }

    private bool Burst(SimState s) => s.HasBuff("Raging Strikes");

    public override SimAbility? NextGcd(SimState s)
    {
        Tick(s);
        if (s.HasBuff("Blast Arrow Ready"))
            return blastArrow;
        if (s.HasBuff("Radiant Encore Ready") && Burst(s))
            return radiantEncore;
        if (s.HasBuff("Resonant Arrow Ready") && Burst(s))
            return resonantArrow;
        if (!s.DotActive("Stormbite (DoT)"))
            return stormbite;
        if (!s.DotActive("Caustic Bite (DoT)"))
            return causticBite;
        // Iron Jaws before the bites fall off, and to snapshot the burst at its end.
        var bites = Math.Min(s.DotRemaining("Stormbite (DoT)"), s.DotRemaining("Caustic Bite (DoT)"));
        if (bites < 5000 || (Burst(s) && s.BuffRemaining("Raging Strikes") < 4000 && bites < 35_000))
            return ironJaws;
        if (s.HasBuff("Barrage"))
            return barragedRefulgent;
        if (soulVoice >= 100 || (soulVoice >= 80 && Burst(s)))
            return apexArrow;
        if (hawksEye >= 1)
            return refulgentArrow;
        return burstShot;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        Tick(s);
        // Songs: the Minuet on cooldown, Ballad and Paeon as the previous song ends.
        if (s.IsReady(minuet) && s.Now > 0 && (song == Song.None || song == Song.Paeon))
            return minuet;
        if (song == Song.Minuet && !s.HasBuff("the Wanderer's Minuet") && s.IsReady(ballad))
            return ballad;
        if (song == Song.Ballad && !s.HasBuff("Mage's Ballad") && s.IsReady(paeon))
            return paeon;
        if (song == Song.Minuet && s.IsReady(potion) && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (song == Song.Minuet && s.IsReady(battleVoice))
            return battleVoice;
        if (song == Song.Minuet && s.IsReady(radiantFinale) && s.HasBuff("Battle Voice"))
            return radiantFinale;
        if (song == Song.Minuet && s.IsReady(ragingStrikes) && s.HasBuff("Battle Voice"))
            return ragingStrikes;
        if (Burst(s) && s.IsReady(barrage))
            return barrage;
        if (s.IsReady(empyrealArrow))
            return empyrealArrow;
        if (s.IsReady(sidewinder) && (Burst(s) || s.CooldownRemaining(ragingStrikes) > 20_000))
            return sidewinder;
        if (song == Song.Minuet && (pitchStacks >= 3 || pitchStacks >= 1 && s.BuffRemaining("the Wanderer's Minuet") < 3000))
            return pitchPerfect;
        if (s.IsReady(heartbreakShot) && (s.Charges(heartbreakShot) == 3 || Burst(s) || song == Song.Ballad))
            return heartbreakShot;
        return null;
    }

    public override int PotencyBonus(SimAbility a, SimState s)
    {
        if (a == pitchPerfect)
        {
            // 100/220/360 for 1/2/3 stacks; expected stacks may be fractional at song end.
            var stacks = (int)Math.Floor(pitchStacks);
            return (stacks switch { >= 3 => 360, 2 => 220, 1 => 100, _ => 0 }) - a.Potency;
        }
        if (a == apexArrow)
            return (int)(Math.Clamp(soulVoice, 20, 100) - 20) * 7;
        return 0;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == burstShot || a == causticBite || a == stormbite || a == ironJaws)
            hawksEye = Math.Min(1.5, hawksEye + HawksEyeChance);
        if (a == ironJaws)
        {
            // Iron Jaws resets both bites with the current buffs.
            s.RefreshDot("Caustic Bite (DoT)", causticBite);
            s.RefreshDot("Stormbite (DoT)", stormbite);
        }
        else if (a == refulgentArrow) hawksEye -= 1;
        else if (a == barragedRefulgent) s.Remove("Barrage");
        else if (a == apexArrow)
        {
            if (soulVoice >= 80) s.Grant("Blast Arrow Ready");
            soulVoice = 0;
        }
        else if (a == blastArrow) s.Remove("Blast Arrow Ready");
        else if (a == radiantEncore) s.Remove("Radiant Encore Ready");
        else if (a == resonantArrow) s.Remove("Resonant Arrow Ready");
        else if (a == empyrealArrow)
        {
            // Empyreal Arrow also triggers the song's Repertoire.
            soulVoice = Math.Min(100, soulVoice + 5);
            if (song == Song.Minuet) pitchStacks = Math.Min(3, pitchStacks + 1);
            else if (song == Song.Ballad) s.ReduceCooldown(heartbreakShot, 7500);
        }
        else if (a == pitchPerfect) pitchStacks = Math.Max(0, pitchStacks - Math.Floor(pitchStacks));
        else if (a == minuet || a == ballad || a == paeon)
        {
            for (var i = 1; i <= 4; i++) s.Remove($"Paeon {i}");
            if (SongBuff is { } old) s.Remove(old);
            song = a == minuet ? Song.Minuet : a == ballad ? Song.Ballad : Song.Paeon;
            s.Grant(SongBuff!);
            pitchStacks = paeonStacks = 0;
        }
    }
}
