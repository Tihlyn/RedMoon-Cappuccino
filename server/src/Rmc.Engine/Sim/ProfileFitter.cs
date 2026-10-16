using Rmc.Engine.Damage;
using Rmc.Engine.Sim.Rotations;

namespace Rmc.Engine.Sim;

/// <summary>
/// Builds a job's <see cref="HitProfile"/> from its rotation sim: one tier per GCD value over the
/// speed range, each the sum of the sim's hit contexts over several fight lengths. A rotation only
/// reads time, so the hits it lands don't depend on the other stats (Monk's Chakra, fed by crit,
/// is the exception: it uses the reference set's crit rate).
/// </summary>
public static class ProfileFitter
{
    /// <summary>Fight lengths summed into each tier: half a burst cycle either side of 510s.</summary>
    public static readonly double[] DefaultFightSeconds = [450, 480, 510, 540, 570];
    /// <summary>Speed range fitted above the level's base: covers every realistic build.</summary>
    public const int SpeedRangeAboveBase = 4000;

    public static HitProfile Fit(StatEngine engine, GearsetInput reference, string referenceName, string potencyDirectory,
        IReadOnlyList<double>? fightSeconds = null, string gameVersion = "")
    {
        fightSeconds ??= DefaultFightSeconds;
        var job = reference.Job;
        var probe = RotationRegistry.For(job, potencyDirectory) ?? throw new ArgumentException($"{job} has no rotation sim.");
        var stats = engine.Compute(reference);
        var medicine = probe.UsesPotion ? engine.BestMedicine(job) : null;
        var potted = medicine != null ? engine.WithMedicine(stats, medicine) : null;
        var speedStat = probe.SpeedStat;

        var profile = new HitProfile
        {
            Job = job,
            Patch = engine.Data.Snapshot.Meta.Patch,
            GameVersion = gameVersion,
            SpeedStat = speedStat == Rmc.GameData.Stats.SpellSpeed ? "sps" : "sks",
            TraitPercent = probe.TraitPercent,
            GcdHastePercent = probe.TraitHastePercent,
            WeaponDelayMs = stats.WeaponDelayMs,
            FightSeconds = [.. fightSeconds],
            ReferenceSet = referenceName,
        };
        if (job == "MNK")
            profile.Notes.Add($"Chakra assumes the reference set's crit rate ({stats.CritRatePerMille / 10.0:0.0}%).");

        // Every duration the rotation scales with speed, under each combination of its haste buffs.
        var hastes = probe.Buffs.Where(b => b.HastePercent > 0).Select(b => b.HastePercent)
            .Aggregate(new HashSet<int> { 0 }, (sums, h) => [.. sums, .. sums.Select(x => x + h)])
            .Select(h => probe.TraitHastePercent + h).Order().ToList();
        var bases = probe.Abilities.Where(a => a.Gcd && !a.FixedRecast).Select(a => a.RecastMs)
            .Concat(probe.Abilities.Where(a => a.CastMs > 0).Select(a => a.CastMs)).Append(2500).Distinct().Order().ToList();
        profile.ScaledDurations = [.. bases.SelectMany(b => hastes.Select(h => new[] { b, h }))];

        // Tiers: runs of speed values on which every scaled duration is the same; simulated at their middle.
        var baseSpeed = stats.Scaling.BaseSpeed;
        var runs = new List<(int Min, int Max)>();
        int[]? previous = null;
        for (var v = baseSpeed; v <= baseSpeed + SpeedRangeAboveBase; v++)
        {
            var signature = profile.Signature(stats, v);
            if (previous != null && signature.SequenceEqual(previous))
                runs[^1] = (runs[^1].Min, v);
            else
                runs.Add((v, v));
            previous = signature;
        }
        var tiers = runs.Select(r => (Gcd: stats.GcdFor(r.Min, 2500, probe.TraitHastePercent), Speed: (r.Min + r.Max) / 2, r.Min, r.Max));

        foreach (var (gcd, speed, min, max) in tiers)
        {
            var tierStats = stats.WithFinalStat(speedStat, speed);
            var tierPotted = potted?.WithFinalStat(speedStat, speed);
            var hits = new Dictionary<HitKey, double>();
            foreach (var seconds in fightSeconds)
            {
                var rotation = RotationRegistry.For(job, potencyDirectory)!;
                var settings = new SimSettings { FightSeconds = seconds, RecordHits = true, UsePotion = medicine != null };
                var result = new Simulation(tierStats, tierPotted, (medicine?.DurationSeconds ?? 30) * 1000, rotation, settings).Run();
                foreach (var (key, weight) in result.Hits)
                    hits[key] = hits.GetValueOrDefault(key) + weight;
            }
            profile.Tiers.Add(new HitProfileTier
            {
                GcdCs = gcd,
                Speed = speed,
                MinSpeed = min,
                MaxSpeed = max,
                Hits = hits.OrderBy(kv => kv.Key.Potency).ThenBy(kv => kv.Key.Type)
                    .Select(kv => new[]
                    {
                        kv.Key.Potency, (int)kv.Key.Type, Flags(kv.Key), kv.Key.CritBonus, kv.Key.DirectHitBonus, Math.Round(kv.Value, 6),
                    }).ToList(),
            });
        }
        return profile;
    }

    private static int Flags(HitKey k)
        => (k.AutoCrit ? FittedProfileModel.AutoCrit : 0) | (k.AutoDirectHit ? FittedProfileModel.AutoDirectHit : 0)
           | (k.IsDot ? FittedProfileModel.Dot : 0) | (k.CannotCritOrDh ? FittedProfileModel.NoCritDh : 0)
           | (k.Potted ? FittedProfileModel.Potted : 0);
}
