namespace Rmc.Engine.Damage;

/// <summary>
/// A job's rotation reduced to data: for each GCD tier, every hit its rotation sim landed, grouped
/// by everything except the gear set's stats (potency, attack type, forced crit/DH, potion, crit/DH
/// rate buffs) with the buff multipliers summed. Written by "rmc-extract fit-profiles" from the
/// sims, summed over several fight lengths so where the last burst falls doesn't make one tier look
/// better than its neighbours. <see cref="FittedProfileModel"/> evaluates it for any gear set.
/// </summary>
public sealed class HitProfile
{
    public string Job { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    /// <summary>Game build the potencies were read from (data/potencies).</summary>
    public string GameVersion { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = [];

    /// <summary>"sks" or "sps".</summary>
    public string SpeedStat { get; set; } = "sks";
    public int TraitPercent { get; set; } = 100;
    /// <summary>Permanent trait haste; the tier is the 2.5s GCD with it applied.</summary>
    public int GcdHastePercent { get; set; }
    /// <summary>Weapon delay the auto-attack counts assume.</summary>
    public int WeaponDelayMs { get; set; }
    /// <summary>Fight lengths summed into every tier.</summary>
    public List<double> FightSeconds { get; set; } = [];
    /// <summary>The BiS set the sims ran with (only its speed and weapon delay matter).</summary>
    public string ReferenceSet { get; set; } = string.Empty;
    /// <summary>
    /// [base ms, haste %] for every duration the rotation scales with speed (recasts and casts,
    /// under each combination of its haste buffs). A tier is a run of speed values on which all of
    /// them come out the same, so the rotation's timeline is identical across it.
    /// </summary>
    public List<int[]> ScaledDurations { get; set; } = [];

    /// <summary>The scaled durations at a speed value, in centiseconds: the tier's identity.</summary>
    public int[] Signature(GearsetStats stats, int speed)
        => ScaledDurations.Select(d => stats.GcdFor(speed, d[0], d[1])).ToArray();

    public List<HitProfileTier> Tiers { get; set; } = [];

    public double TotalSeconds => FightSeconds.Sum();
}

public sealed class HitProfileTier
{
    /// <summary>The GCD (2.5s base, trait haste) this tier covers, in centiseconds.</summary>
    public int GcdCs { get; set; }
    /// <summary>The speed value the sims ran at (the middle of the tier).</summary>
    public int Speed { get; set; }
    /// <summary>Lowest and highest speed in the tier.</summary>
    public int MinSpeed { get; set; }
    public int MaxSpeed { get; set; }
    /// <summary>
    /// [potency, attack type, flags, crit bonus, DH bonus, weight]: flags are 1 auto-crit,
    /// 2 auto-DH, 4 damage over time, 8 can't crit or DH, 16 potted; bonuses in ten-thousandths;
    /// weight is the summed buff multiplier of every such hit.
    /// </summary>
    public List<double[]> Hits { get; set; } = [];
}

public sealed class DamageEstimate
{
    public double Dps { get; set; }
    public int GcdCs { get; set; }
    /// <summary>The set's speed is outside the fitted range; the nearest tier was used.</summary>
    public bool OutsideFittedRange { get; set; }
}

/// <summary>
/// Expected DPS from a <see cref="HitProfile"/>: each hit group's exact per-hit damage with the
/// set's stats (potted stats for hits under the potion) times its crit/DH factor and summed buff
/// weight. Reproduces the rotation sim at the tier's speed in a few hundred multiplications.
/// </summary>
public sealed class FittedProfileModel(HitProfile profile)
{
    public const int AutoCrit = 1, AutoDirectHit = 2, Dot = 4, NoCritDh = 8, Potted = 16;

    public HitProfile Profile => profile;

    public DamageEstimate Evaluate(GearsetStats stats, GearsetStats? potted)
    {
        var speed = stats.Final.GetValueOrDefault(profile.SpeedStat == "sps" ? Rmc.GameData.Stats.SpellSpeed : Rmc.GameData.Stats.SkillSpeed);
        var gcd = stats.GcdFor(speed, 2500, profile.GcdHastePercent);
        var tier = profile.Tiers.FirstOrDefault(t => speed >= t.MinSpeed && speed <= t.MaxSpeed)
                   ?? profile.Tiers.MinBy(t => Math.Abs(t.Speed - speed))!;
        var outside = speed < profile.Tiers[0].MinSpeed || speed > profile.Tiers[^1].MaxSpeed;

        double total = 0;
        foreach (var h in tier.Hits)
        {
            var flags = (int)h[2];
            var s = (flags & Potted) != 0 && potted != null ? potted : stats;
            var type = (AttackType)(int)h[1];
            var autoDh = (flags & AutoDirectHit) != 0;
            var hit = DamageMath.BaseDamage(s, (int)h[0], type, autoDh, (flags & Dot) != 0, profile.TraitPercent);
            var factor = (flags & NoCritDh) != 0 ? 1
                : DamageMath.CritDirectHitFactor(s, h[3] / 10_000.0, h[4] / 10_000.0, (flags & AutoCrit) != 0, autoDh);
            total += hit * factor * h[5];
        }
        return new DamageEstimate { Dps = total / profile.TotalSeconds, GcdCs = gcd, OutsideFittedRange = outside };
    }
}
