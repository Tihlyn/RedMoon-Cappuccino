using Rmc.GameData;

namespace Rmc.Engine;

/// <summary>
/// The stat → multiplier functions (Allagan Studies; xivgear's xivmath), in exact integer
/// arithmetic. The community versions use floats with a floor tolerance; integers make that
/// tolerance unnecessary. Results are scaled integers: per mille (1.104 → 1104), percent, or
/// centiseconds, as named.
/// </summary>
public static class StatMath
{
    /// <summary>Floor division that is correct for negative numerators too.</summary>
    public static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>The level-scaled tier index every substat function is built on: ⌊k·(stat − base)/div⌋.</summary>
    public static int Tier(int k, int stat, int baseStat, int levelDiv) => (int)FloorDiv((long)k * (stat - baseStat), levelDiv);

    public static int CritRatePerMille(LevelInfo l, int crit) => Tier(200, crit, l.BaseSubStat, l.LevelDiv) + 50;
    public static int CritDamagePerMille(LevelInfo l, int crit) => 1400 + Tier(200, crit, l.BaseSubStat, l.LevelDiv);
    public static int DirectHitRatePerMille(LevelInfo l, int dh) => Tier(550, dh, l.BaseSubStat, l.LevelDiv);
    /// <summary>Bonus on actions that always direct hit (scales with DH stat).</summary>
    public static int AutoDirectHitBonusPerMille(LevelInfo l, int dh) => Tier(140, dh, l.BaseSubStat, l.LevelDiv);
    public static int DeterminationPerMille(LevelInfo l, int det) => 1000 + Tier(140, det, l.BaseMainStat, l.LevelDiv);
    public static int TenacityPerMille(LevelInfo l, int ten) => 1000 + Tier(112, ten, l.BaseSubStat, l.LevelDiv);
    public static int TenacityIncomingPerMille(LevelInfo l, int ten) => 1000 - Tier(200, ten, l.BaseSubStat, l.LevelDiv);
    /// <summary>Damage/heal over time multiplier from skill or spell speed.</summary>
    public static int SpeedDotPerMille(LevelInfo l, int speed) => 1000 + Tier(130, speed, l.BaseSubStat, l.LevelDiv);
    public static int MpPerTick(LevelInfo l, int piety) => 200 + Tier(150, piety, l.BaseMainStat, l.LevelDiv);

    /// <summary>
    /// GCD in centiseconds: ⌊⌊(1000 − ⌊130·(speed − sub)/div⌋) · baseGcd⌋ · (100 − haste) / 1000⌋.
    /// </summary>
    public static int GcdCentiseconds(LevelInfo l, int speed, int baseGcdMs = 2500, int hastePercent = 0)
    {
        var scaled = FloorDiv((1000L - Tier(130, speed, l.BaseSubStat, l.LevelDiv)) * baseGcdMs, 1000);
        return (int)Math.Max(0, FloorDiv(scaled * (100 - hastePercent), 1000));
    }

    public static int MainStatPowerMod(LevelInfo l, string role) => role == "Tank" ? l.MainStatPowerModTank : l.MainStatPowerModOther;

    /// <summary>Main stat multiplier in percent: trunc(apMod·(main − base)/base) + 100, floored at 0.</summary>
    public static int MainStatPercent(LevelInfo l, string role, int mainStat)
    {
        var numerator = (long)MainStatPowerMod(l, role) * (mainStat - l.BaseMainStat);
        var truncated = numerator >= 0 ? numerator / l.BaseMainStat : -(-numerator / l.BaseMainStat);
        return (int)Math.Max(0, truncated + 100);
    }

    /// <summary>Weapon damage multiplier in percent: ⌊base·jobMod/1000 + WD⌋.</summary>
    public static int WeaponDamagePercent(LevelInfo l, int jobMainStatMod, int weaponDamage)
        => (int)FloorDiv((long)l.BaseMainStat * jobMainStatMod + 1000L * weaponDamage, 1000);

    /// <summary>Auto-attack multiplier in percent: ⌊⌊base·jobAaMod/1000 + WD⌋ · delay/3⌋.</summary>
    public static int AutoAttackPercent(LevelInfo l, int jobAaStatMod, int weaponDamage, int delayMs)
    {
        var inner = FloorDiv((long)l.BaseMainStat * jobAaStatMod + 1000L * weaponDamage, 1000);
        return (int)FloorDiv(inner * delayMs, 3000);
    }

    public static int Hp(LevelInfo l, string role, int jobHpMod, int vitality)
    {
        var scalarTenths = role == "Tank" ? l.HpScalarTank : l.HpScalarOther;
        return (int)(FloorDiv((long)l.Hp * jobHpMod, 100) + FloorDiv((long)(vitality - l.BaseMainStat) * scalarTenths, 10));
    }

    /// <summary>
    /// The smallest stat value that reaches tier <paramref name="tier"/> of ⌊k·(x − base)/div⌋:
    /// base + ⌈tier·div/k⌉.
    /// </summary>
    public static int TierStart(int k, int tier, int baseStat, int levelDiv)
        => baseStat + (int)-FloorDiv(-(long)tier * levelDiv, k);
}
