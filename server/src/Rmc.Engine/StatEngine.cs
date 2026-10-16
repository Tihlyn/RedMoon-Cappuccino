using Rmc.GameData;

namespace Rmc.Engine;

/// <summary>
/// Turns a <see cref="GearsetInput"/> into final stats and their multipliers.
/// </summary>
/// <remarks>
/// Order (as xivgear, which matches the game panel):
/// 1. gear: each item's NQ/HQ stats, relic stats, then melds in slot order against the item's caps;
/// 2. base: job-scaled base main stats, base det/piety, base substats, plus clan modifiers;
/// 3. party bonus on the main stat, the auto-attack stat and vitality;
/// 4. food, computed from the stats *before* the party bonus.
/// </remarks>
public sealed class StatEngine(GameDataIndex data)
{
    private static readonly string[] MainStats = [Stats.Strength, Stats.Dexterity, Stats.Vitality, Stats.Intelligence, Stats.Mind];
    private static readonly string[] BaseMainStatKeyed = [Stats.Determination, Stats.Piety];
    private static readonly string[] BaseSubStatKeyed = [Stats.Critical, Stats.DirectHit, Stats.SkillSpeed, Stats.SpellSpeed, Stats.Tenacity];

    public GameDataIndex Data => data;

    public GearsetStats Compute(GearsetInput input)
    {
        var job = data.Job(input.Job) ?? throw new ArgumentException($"Unknown job '{input.Job}'.");
        var level = data.Snapshot.Levels.FirstOrDefault(l => l.Level == input.Level)
                    ?? throw new ArgumentException($"No level scaling for level {input.Level}.");

        var result = new GearsetStats { Job = job.Abbreviation, Level = level.Level, Role = job.Role, MainStat = job.MainStat, Scaling = level };
        result.Issues.AddRange(Validate(input));

        // 1. Gear.
        var gear = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in Slots.All)
        {
            if (!input.Slots.TryGetValue(slot, out var piece) || data.Item(piece.ItemId) is not { } item)
                continue;
            var report = ApplyPiece(item, piece);
            report.Slot = slot;
            result.Slots.Add(report);
            foreach (var (stat, value) in report.Stats)
                gear[stat] = gear.GetValueOrDefault(stat) + value;

            if (slot == Slots.Weapon && item.Weapon is { } w)
            {
                var hq = report.Hq;
                result.WeaponPhysical = hq ? w.PhysicalDamageHq : w.PhysicalDamage;
                result.WeaponMagical = hq ? w.MagicalDamageHq : w.MagicalDamage;
                result.WeaponDelayMs = w.DelayMs;
            }
        }
        result.GearStats = gear;

        // 2. Base + clan.
        var clan = input.ClanId is int clanId ? data.Snapshot.Clans.FirstOrDefault(c => c.Id == clanId) : null;
        if (input.ClanId != null && clan == null)
            result.Issues.Add(new ValidationIssue(true, $"Unknown clan {input.ClanId}."));
        var preParty = new Dictionary<string, int>(gear, StringComparer.Ordinal);
        foreach (var stat in MainStats)
            preParty[stat] = preParty.GetValueOrDefault(stat) + level.BaseMainStat * job.Modifiers.GetValueOrDefault(stat) / 100
                             + (clan?.Modifiers.GetValueOrDefault(stat) ?? 0);
        foreach (var stat in BaseMainStatKeyed)
            preParty[stat] = preParty.GetValueOrDefault(stat) + level.BaseMainStat + (clan?.Modifiers.GetValueOrDefault(stat) ?? 0);
        foreach (var stat in BaseSubStatKeyed)
            preParty[stat] = preParty.GetValueOrDefault(stat) + level.BaseSubStat;

        // 3. Party bonus.
        var final = new Dictionary<string, int>(preParty, StringComparer.Ordinal);
        var partyBonus = Math.Clamp(input.PartyBonus, 0, 5);
        foreach (var stat in new[] { job.MainStat, AutoAttackStat(job), Stats.Vitality }.Distinct())
            final[stat] = preParty[stat] * (100 + partyBonus) / 100;

        // 4. Food, from the pre-party values.
        if (input.Food is { } foodInput)
        {
            if (data.Food(foodInput.ItemId) is not { } food)
            {
                result.Issues.Add(new ValidationIssue(true, $"Food {foodInput.ItemId} is not in the snapshot."));
            }
            else
            {
                var hq = foodInput.Hq && food.CanBeHq;
                foreach (var bonus in food.Bonuses)
                {
                    var value = hq ? bonus.ValueHq : bonus.Value;
                    var added = bonus.Relative
                        ? Math.Min(hq ? bonus.MaxHq : bonus.Max, preParty.GetValueOrDefault(bonus.Stat) * value / 100)
                        : value;
                    final[bonus.Stat] = final.GetValueOrDefault(bonus.Stat) + added;
                    result.FoodBonus[bonus.Stat] = added;
                }
            }
        }
        result.Final = final;

        Derive(result, job, level);
        return result;
    }

    /// <summary>
    /// The same gear under a medicine (potion): its bonus is applied last, on the final stats
    /// (after party bonus and food), and capped (xivgear's medicine order).
    /// </summary>
    public GearsetStats WithMedicine(GearsetStats stats, FoodInfo medicine, bool hq = true)
    {
        var job = data.Job(stats.Job)!;
        var potted = new GearsetStats
        {
            Job = stats.Job, Level = stats.Level, Role = stats.Role, MainStat = stats.MainStat, Scaling = stats.Scaling,
            GearStats = stats.GearStats, FoodBonus = stats.FoodBonus, Slots = stats.Slots, Issues = stats.Issues,
            WeaponPhysical = stats.WeaponPhysical, WeaponMagical = stats.WeaponMagical, WeaponDelayMs = stats.WeaponDelayMs,
            Final = new Dictionary<string, int>(stats.Final, StringComparer.Ordinal),
        };
        var useHq = hq && medicine.CanBeHq;
        foreach (var bonus in medicine.Bonuses.Where(b => b.Relative))
        {
            var current = potted.Final.GetValueOrDefault(bonus.Stat);
            potted.Final[bonus.Stat] = current + Math.Min(useHq ? bonus.MaxHq : bonus.Max, current * (useHq ? bonus.ValueHq : bonus.Value) / 100);
        }
        Derive(potted, job, stats.Scaling, breakpoints: stats.Breakpoints.Count > 0);
        return potted;
    }

    /// <summary>
    /// Fast re-derivation for solvers: <paramref name="baseline"/> (computed with the same food)
    /// plus extra gear substats from melds. Substats get no party bonus, so each final value is the
    /// pre-food value plus the gain, with the food's relative bonus recomputed on it. Skips the
    /// breakpoint list; slot reports and gear stats stay the baseline's.
    /// </summary>
    public GearsetStats WithGearGains(GearsetStats baseline, FoodInfo? food, bool hqFood, IReadOnlyList<(string Stat, int Gain)> gains)
    {
        var job = data.Job(baseline.Job)!;
        var r = new GearsetStats
        {
            Job = baseline.Job, Level = baseline.Level, Role = baseline.Role, MainStat = baseline.MainStat, Scaling = baseline.Scaling,
            GearStats = baseline.GearStats, Slots = baseline.Slots, Issues = baseline.Issues,
            WeaponPhysical = baseline.WeaponPhysical, WeaponMagical = baseline.WeaponMagical, WeaponDelayMs = baseline.WeaponDelayMs,
            Final = new Dictionary<string, int>(baseline.Final, StringComparer.Ordinal),
            FoodBonus = new Dictionary<string, int>(baseline.FoodBonus, StringComparer.Ordinal),
        };
        var useHq = hqFood && (food?.CanBeHq ?? false);
        foreach (var (stat, gain) in gains)
        {
            if (gain == 0)
                continue;
            var preFood = baseline.Final.GetValueOrDefault(stat) - baseline.FoodBonus.GetValueOrDefault(stat) + gain;
            var bonus = food?.Bonuses.FirstOrDefault(b => b.Stat == stat);
            var added = bonus == null ? 0
                : bonus.Relative ? Math.Min(useHq ? bonus.MaxHq : bonus.Max, preFood * (useHq ? bonus.ValueHq : bonus.Value) / 100)
                : useHq ? bonus.ValueHq : bonus.Value;
            r.Final[stat] = preFood + added;
            if (bonus != null)
                r.FoodBonus[stat] = added;
        }
        Derive(r, job, baseline.Scaling, breakpoints: false);
        return r;
    }

    /// <summary>Best medicine for a job's main stat (highest item level).</summary>
    public FoodInfo? BestMedicine(string jobAbbreviation)
    {
        var job = data.Job(jobAbbreviation);
        return job == null
            ? null
            : data.Snapshot.Medicines.Where(m => m.Bonuses.Any(b => b.Stat == job.MainStat)).MaxBy(m => m.ItemLevel);
    }

    /// <summary>Healers and casters auto-attack with strength; everyone else with their main stat.</summary>
    public static string AutoAttackStat(ClassJobInfo job) => job.Role is "Healer" or "Caster" ? Stats.Strength : job.MainStat;

    private SlotReport ApplyPiece(GearItem item, PieceInput piece)
    {
        var hq = (piece.Hq ?? true) && item.CanBeHq;
        var stats = new Dictionary<string, int>(hq ? item.StatsHq : item.Stats, StringComparer.Ordinal);
        if (piece.RelicStats != null)
            foreach (var (stat, value) in piece.RelicStats)
                stats[stat] = stats.GetValueOrDefault(stat) + value;

        var report = new SlotReport { ItemId = item.Id, Name = item.Name, Hq = hq };
        foreach (var meld in piece.Materia)
        {
            var value = MateriaValue(meld) ?? 0;
            var cap = data.MeldCap(item, meld.Stat);
            var current = stats.GetValueOrDefault(meld.Stat);
            var applied = cap is int c ? Math.Clamp(c - current, 0, value) : value;
            stats[meld.Stat] = current + applied;
            report.Melds.Add(new MeldReport { Stat = meld.Stat, Grade = meld.Grade, Value = value, Applied = applied });
        }
        report.Stats = stats;
        return report;
    }

    public int? MateriaValue(MeldInput meld)
        => data.Snapshot.Materia.FirstOrDefault(m => m.Stat == meld.Stat)?.Grades.FirstOrDefault(g => g.Grade == meld.Grade)?.Value;

    private IEnumerable<ValidationIssue> Validate(GearsetInput input)
    {
        var set = new BisSet { Name = "gearset" };
        foreach (var (slot, piece) in input.Slots)
        {
            var bisSlot = new BisSlot { ItemId = piece.ItemId, RelicStats = piece.RelicStats };
            foreach (var meld in piece.Materia)
            {
                var grade = data.Snapshot.Materia.FirstOrDefault(m => m.Stat == meld.Stat)?.Grades.FirstOrDefault(g => g.Grade == meld.Grade);
                if (grade == null)
                {
                    yield return new ValidationIssue(true, $"{slot}: no grade {meld.Grade} {meld.Stat} materia.");
                    continue;
                }
                bisSlot.Materia.Add(new BisMeld { Stat = meld.Stat, Grade = meld.Grade, ItemId = grade.ItemId });
            }
            set.Slots[slot] = bisSlot;
        }
        foreach (var issue in BisValidator.Validate(data, input.Job, set))
            yield return issue;
    }

    private static void Derive(GearsetStats r, ClassJobInfo job, LevelInfo l, bool breakpoints = true)
    {
        int S(string stat) => r.Final.GetValueOrDefault(stat);

        var physical = job.MainStat is Stats.Strength or Stats.Dexterity;
        var weaponDamage = physical ? r.WeaponPhysical : r.WeaponMagical;

        r.CritRatePerMille = StatMath.CritRatePerMille(l, S(Stats.Critical));
        r.CritDamagePerMille = StatMath.CritDamagePerMille(l, S(Stats.Critical));
        r.DirectHitRatePerMille = StatMath.DirectHitRatePerMille(l, S(Stats.DirectHit));
        r.AutoDirectHitBonusPerMille = StatMath.AutoDirectHitBonusPerMille(l, S(Stats.DirectHit));
        r.DeterminationPerMille = StatMath.DeterminationPerMille(l, S(Stats.Determination));
        // Tenacity only raises outgoing damage for tanks.
        r.TenacityPerMille = job.Role == "Tank" ? StatMath.TenacityPerMille(l, S(Stats.Tenacity)) : 1000;
        r.TenacityIncomingPerMille = StatMath.TenacityIncomingPerMille(l, S(Stats.Tenacity));
        r.MainStatPercent = StatMath.MainStatPercent(l, job.Role, S(job.MainStat));
        r.AutoAttackStatPercent = StatMath.MainStatPercent(l, job.Role, S(AutoAttackStat(job)));
        r.WeaponDamagePercent = StatMath.WeaponDamagePercent(l, job.Modifiers.GetValueOrDefault(job.MainStat), weaponDamage);
        r.AutoAttackPercent = StatMath.AutoAttackPercent(l, job.Modifiers.GetValueOrDefault(AutoAttackStat(job)), weaponDamage, r.WeaponDelayMs);
        r.SkillSpeedDotPerMille = StatMath.SpeedDotPerMille(l, S(Stats.SkillSpeed));
        r.SpellSpeedDotPerMille = StatMath.SpeedDotPerMille(l, S(Stats.SpellSpeed));
        r.GcdSkillSpeedCs = StatMath.GcdCentiseconds(l, S(Stats.SkillSpeed));
        r.GcdSpellSpeedCs = StatMath.GcdCentiseconds(l, S(Stats.SpellSpeed));
        r.Hp = StatMath.Hp(l, job.Role, job.Modifiers.GetValueOrDefault("hp"), S(Stats.Vitality));
        r.MpPerTick = StatMath.MpPerTick(l, S(Stats.Piety));
        r.Breakpoints = breakpoints ? Breakpoints.For(r, job, l) : [];
    }
}
