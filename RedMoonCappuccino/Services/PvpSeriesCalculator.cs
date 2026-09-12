using System;
using System.Collections.Generic;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Series Malmstone arithmetic: how much EXP separates a snapshot from a target
/// rank, and how many matches of each mode would cover it.
///
/// Deliberately has no Dalamud dependency so the numbers can be checked from a
/// plain console app (see <c>tests/PvpSeriesChecks</c>). The level curve comes in
/// through the constructor — normally the game's own <c>PvPSeriesLevel</c> sheet,
/// with <see cref="FallbackExpToNext"/> standing in when that cannot be read.
/// </summary>
public sealed class PvpSeriesCalculator
{
    /// <summary>The level that unlocks the last unique series reward.</summary>
    public const int TargetRank = 25;

    /// <summary>Levels that carry a reward milestone, ending at <see cref="TargetRank"/>.</summary>
    public static readonly IReadOnlyList<int> Milestones = new[] { 5, 10, 15, 20, TargetRank };

    /// <summary>The first milestone above <paramref name="rank"/>; the final one once all are reached.</summary>
    public static int NextMilestone(int rank)
    {
        foreach (var milestone in Milestones)
            if (milestone > rank) return milestone;
        return TargetRank;
    }

    /// <summary>
    /// EXP to advance from each level to the next, indexed by level, as the game
    /// tables it: 2,000 for levels 1–4, 3,000 for 5–9, 4,000 for 10–14, 5,500 for
    /// 15–19, 7,500 for 20–24, 10,000 for 25–29 and 20,000 from 30 up. The last
    /// entry repeats for every level past the end. Reaching level 25 therefore
    /// takes 108,000 EXP in total.
    /// </summary>
    public static readonly IReadOnlyList<int> FallbackExpToNext = new[]
    {
        0,
        2000, 2000, 2000, 2000,
        3000, 3000, 3000, 3000, 3000,
        4000, 4000, 4000, 4000, 4000,
        5500, 5500, 5500, 5500, 5500,
        7500, 7500, 7500, 7500, 7500,
        10000, 10000, 10000, 10000, 10000,
        20000,
    };

    /// <summary>
    /// Series EXP per match. Frontline pays by placing; the daily bonus is the extra
    /// 1,500 the first Frontline roulette of the day awards on top of the placing.
    /// </summary>
    public static readonly IReadOnlyList<PvpModeExp> Modes = new[]
    {
        new PvpModeExp("Crystalline Conflict", null, 900, null, 700),
        new PvpModeExp("Frontline", null, 1500, 1250, 1000),
        new PvpModeExp("Frontline (daily bonus)",
            "First Frontline roulette of the day: +1,500 EXP on top of the placing.", 3000, 2750, 2500),
        new PvpModeExp("Rival Wings", null, 1250, null, 750),
    };

    private readonly IReadOnlyList<int> expToNext;

    /// <param name="expToNextByLevel">
    /// EXP to advance from each level to the next, indexed by level (index 0 unused).
    /// Must hold at least one level; the last value repeats beyond the end.
    /// </param>
    public PvpSeriesCalculator(IReadOnlyList<int> expToNextByLevel)
    {
        if (expToNextByLevel.Count < 2)
            throw new ArgumentException("Level curve needs at least level 1.", nameof(expToNextByLevel));
        expToNext = expToNextByLevel;
    }

    /// <summary>EXP needed to leave <paramref name="level"/>. Levels past the table use its last value.</summary>
    public int ExpToNext(int level)
    {
        var index = Math.Clamp(level, 1, expToNext.Count - 1);
        return expToNext[index];
    }

    /// <summary>Total EXP earned by the moment <paramref name="level"/> is reached.</summary>
    public long TotalExpAtLevel(int level)
    {
        long total = 0;
        for (var l = 1; l < level; l++)
            total += ExpToNext(l);
        return total;
    }

    public PvpSeriesPlan Plan(PvpSeriesSnapshot snapshot, int targetRank = TargetRank)
    {
        var rank = Math.Max(1, snapshot.Rank);
        var current = TotalExpAtLevel(rank) + Math.Max(0, snapshot.Experience);
        var target = TotalExpAtLevel(targetRank);
        var remaining = Math.Max(0, target - current);

        var requirements = new List<PvpModeRequirement>(Modes.Count);
        foreach (var mode in Modes)
        {
            requirements.Add(new PvpModeRequirement(
                mode,
                MatchesNeeded(remaining, mode.First),
                mode.Second is { } second ? MatchesNeeded(remaining, second) : null,
                MatchesNeeded(remaining, mode.Third)));
        }

        return new PvpSeriesPlan
        {
            TargetRank = targetRank,
            ExpToNextLevel = ExpToNext(rank),
            CurrentTotalExp = current,
            TargetTotalExp = target,
            RemainingExp = remaining,
            Requirements = requirements,
        };
    }

    /// <summary>Whole matches needed to earn at least <paramref name="remainingExp"/>.</summary>
    public static int MatchesNeeded(long remainingExp, int expPerMatch)
    {
        if (remainingExp <= 0) return 0;
        if (expPerMatch <= 0) throw new ArgumentOutOfRangeException(nameof(expPerMatch));
        return (int)((remainingExp + expPerMatch - 1) / expPerMatch);
    }
}
