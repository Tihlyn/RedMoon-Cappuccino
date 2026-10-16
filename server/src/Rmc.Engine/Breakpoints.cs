using Rmc.GameData;

namespace Rmc.Engine;

/// <summary>
/// Where a stat sits inside its current tier. Every substat function is a floor of a linear
/// function, so points past the tier start do nothing until the next tier: these are the
/// "wasted" points a meld swap can move elsewhere.
/// </summary>
public sealed class StatBreakpoint
{
    public string Stat { get; set; } = string.Empty;
    public int Value { get; set; }
    /// <summary>Lowest stat value with the same effect as <see cref="Value"/>.</summary>
    public int TierStart { get; set; }
    /// <summary>Stat value where the effect next improves.</summary>
    public int NextTier { get; set; }
    public int Wasted => Value - TierStart;
    public int ToNext => NextTier - Value;
    /// <summary>For speed stats: the GCD now and at <see cref="NextTier"/>, in centiseconds.</summary>
    public int? GcdCs { get; set; }
    public int? NextGcdCs { get; set; }
}

public static class Breakpoints
{
    private static readonly IReadOnlyDictionary<string, string[]> IrrelevantByRole = new Dictionary<string, string[]>
    {
        ["Tank"] = [Stats.SpellSpeed, Stats.Piety],
        ["Healer"] = [Stats.SkillSpeed, Stats.Tenacity],
        ["Melee"] = [Stats.SpellSpeed, Stats.Tenacity, Stats.Piety],
        ["Ranged"] = [Stats.SpellSpeed, Stats.Tenacity, Stats.Piety],
        ["Caster"] = [Stats.SkillSpeed, Stats.Tenacity, Stats.Piety],
    };

    public static bool IsRelevant(string role, string stat)
        => !IrrelevantByRole.TryGetValue(role, out var skip) || !skip.Contains(stat);

    public static List<StatBreakpoint> For(GearsetStats r, ClassJobInfo job, LevelInfo l)
    {
        var result = new List<StatBreakpoint>();
        void Linear(string stat, int k, int baseStat)
        {
            if (!IsRelevant(job.Role, stat))
                return;
            var value = r.Final.GetValueOrDefault(stat);
            var tier = StatMath.Tier(k, value, baseStat, l.LevelDiv);
            result.Add(new StatBreakpoint
            {
                Stat = stat,
                Value = value,
                TierStart = StatMath.TierStart(k, tier, baseStat, l.LevelDiv),
                NextTier = StatMath.TierStart(k, tier + 1, baseStat, l.LevelDiv),
            });
        }

        Linear(Stats.Critical, 200, l.BaseSubStat);
        Linear(Stats.DirectHit, 550, l.BaseSubStat);
        Linear(Stats.Determination, 140, l.BaseMainStat);
        Linear(Stats.Tenacity, 112, l.BaseSubStat);
        Linear(Stats.Piety, 150, l.BaseMainStat);
        Speed(Stats.SkillSpeed);
        Speed(Stats.SpellSpeed);
        return result;

        // Speed matters through the GCD (0.01s steps), which is coarser than its own tiers.
        void Speed(string stat)
        {
            if (!IsRelevant(job.Role, stat))
                return;
            var value = r.Final.GetValueOrDefault(stat);
            var gcd = StatMath.GcdCentiseconds(l, value);
            var start = value;
            while (start > l.BaseSubStat && StatMath.GcdCentiseconds(l, start - 1) == gcd)
                start--;
            var next = value + 1;
            while (StatMath.GcdCentiseconds(l, next) == gcd && next < value + 10 * l.LevelDiv)
                next++;
            result.Add(new StatBreakpoint
            {
                Stat = stat,
                Value = value,
                TierStart = start,
                NextTier = next,
                GcdCs = gcd,
                NextGcdCs = StatMath.GcdCentiseconds(l, next),
            });
        }
    }
}
