using Rmc.GameData;

namespace Rmc.Engine;

public sealed class GearsetStats
{
    public string Job { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Role { get; set; } = string.Empty;
    public string MainStat { get; set; } = string.Empty;
    /// <summary>Level scaling the stats were computed with.</summary>
    public LevelInfo Scaling { get; set; } = new();

    /// <summary>GCD in centiseconds for a speed value, base recast and haste.</summary>
    public int GcdFor(int speed, int baseGcdMs = 2500, int hastePercent = 0)
        => StatMath.GcdCentiseconds(Scaling, speed, baseGcdMs, hastePercent);

    /// <summary>Items, relic stats and applied melds only.</summary>
    public Dictionary<string, int> GearStats { get; set; } = new(StringComparer.Ordinal);
    /// <summary>What the food actually added (after its caps).</summary>
    public Dictionary<string, int> FoodBonus { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Final stats: gear + base + clan, party bonus, food. What the attribute panel shows.</summary>
    public Dictionary<string, int> Final { get; set; } = new(StringComparer.Ordinal);

    public int WeaponPhysical { get; set; }
    public int WeaponMagical { get; set; }
    public int WeaponDelayMs { get; set; }

    public int CritRatePerMille { get; set; }
    public int CritDamagePerMille { get; set; }
    public int DirectHitRatePerMille { get; set; }
    public int AutoDirectHitBonusPerMille { get; set; }
    public int DeterminationPerMille { get; set; }
    public int TenacityPerMille { get; set; }
    public int TenacityIncomingPerMille { get; set; }
    public int MainStatPercent { get; set; }
    public int AutoAttackStatPercent { get; set; }
    public int WeaponDamagePercent { get; set; }
    public int AutoAttackPercent { get; set; }
    public int SkillSpeedDotPerMille { get; set; }
    public int SpellSpeedDotPerMille { get; set; }
    /// <summary>Unhasted 2.50s GCD from skill speed / spell speed, in centiseconds.</summary>
    public int GcdSkillSpeedCs { get; set; }
    public int GcdSpellSpeedCs { get; set; }
    public int Hp { get; set; }
    public int MpPerTick { get; set; }

    public List<StatBreakpoint> Breakpoints { get; set; } = [];

    /// <summary>A copy with one final stat replaced (derived values untouched): for timing-only sims.</summary>
    public GearsetStats WithFinalStat(string stat, int value)
    {
        var copy = (GearsetStats)MemberwiseClone();
        copy.Final = new Dictionary<string, int>(Final, StringComparer.Ordinal) { [stat] = value };
        return copy;
    }
    public List<SlotReport> Slots { get; set; } = [];
    public List<ValidationIssue> Issues { get; set; } = [];
}

public sealed class SlotReport
{
    public string Slot { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Hq { get; set; }
    /// <summary>The piece's stats after melds.</summary>
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    public List<MeldReport> Melds { get; set; } = [];
}

public sealed class MeldReport
{
    public string Stat { get; set; } = string.Empty;
    public int Grade { get; set; }
    public int Value { get; set; }
    /// <summary>What the meld actually added; the rest was lost to the item's cap.</summary>
    public int Applied { get; set; }
    public int Wasted => Value - Applied;
}
