using Rmc.Engine;
using Rmc.Engine.Upgrades;

namespace Rmc.Server;

// Request and response shapes of the v1 API. Gear is the engine's own GearsetInput (job, level,
// clan, party bonus, food, slots with item id / HQ / melds): no character names, item ids only.

public sealed class ComputeOptions
{
    /// <summary>Fight length in seconds; picks the closest fitted profile. Standard (510s) when unset.</summary>
    public double? FightSeconds { get; set; }
    /// <summary>BiS set name of the job's sheet to compare with or plan toward; the sheet's first when unset.</summary>
    public string? BisSet { get; set; }
    /// <summary>When the gear has no food (the player isn't fed right now), compute with the BiS set's food.</summary>
    public bool BisFoodWhenUnfed { get; set; }
}

public sealed class EvaluateRequest
{
    public GearsetInput Gear { get; set; } = new();
    /// <summary>The in-game attribute panel (final stats by key), for the parity check.</summary>
    public Dictionary<string, int>? Panel { get; set; }
    public ComputeOptions Options { get; set; } = new();
}

public sealed class MeldSolveRequest
{
    public GearsetInput Gear { get; set; } = new();
    public ComputeOptions Options { get; set; } = new();
    /// <summary>GCD to hold, in seconds (e.g. 2.45); any GCD when unset.</summary>
    public double? TargetGcd { get; set; }
    public List<string> LockedSlots { get; set; } = [];
    public bool KeepExistingMelds { get; set; }
    public bool SolveFood { get; set; }
    public int? MinPiety { get; set; }
}

public sealed class UpgradePlanRequest
{
    public GearsetInput Gear { get; set; } = new();
    public ComputeOptions Options { get; set; } = new();
    /// <summary>Currency already held, by the snapshot's currency names.</summary>
    public Dictionary<string, int> Wallet { get; set; } = new(StringComparer.Ordinal);
    public bool IncludeCrafted { get; set; }
    public bool IgnoreWeeklyLimits { get; set; }
    public bool MaterialsFromHunts { get; set; }
    public int? MaxWeeks { get; set; }
}

public sealed class DerivedStats
{
    public double CritRate { get; set; }
    public double CritDamage { get; set; }
    public double DirectHitRate { get; set; }
    public double Determination { get; set; }
    public double Tenacity { get; set; }
    public double Gcd { get; set; }

    public static DerivedStats From(GearsetStats s, int gcdCs) => new()
    {
        CritRate = s.CritRatePerMille / 1000.0,
        CritDamage = s.CritDamagePerMille / 1000.0,
        DirectHitRate = s.DirectHitRatePerMille / 1000.0,
        Determination = s.DeterminationPerMille / 1000.0,
        Tenacity = s.TenacityPerMille / 1000.0,
        Gcd = gcdCs / 100.0,
    };
}

public sealed class BisSummary
{
    public string Name { get; set; } = string.Empty;
    public double Dps { get; set; }
    public double Gcd { get; set; }
}

public sealed class EvaluateResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    /// <summary>Mean fight length of the profile used.</summary>
    public double FightSeconds { get; set; }
    public double Dps { get; set; }
    public bool OutsideFittedRange { get; set; }
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    public DerivedStats Derived { get; set; } = new();
    public List<StatBreakpoint> Breakpoints { get; set; } = [];
    /// <summary>Computed minus in-game panel, per stat that differs. Empty when they agree or no panel was sent.</summary>
    public Dictionary<string, int> PanelDiff { get; set; } = new(StringComparer.Ordinal);
    public List<SlotReport> Slots { get; set; } = [];
    public List<string> Issues { get; set; } = [];
    /// <summary>Slots whose item the server doesn't know (older than the snapshot): left out of the stats.</summary>
    public List<string> UnknownSlots { get; set; } = [];
    public BisSummary? Bis { get; set; }
}

public sealed class MeldSolveResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public double FightSeconds { get; set; }
    public double CurrentDps { get; set; }
    public double Dps { get; set; }
    public double Gain { get; set; }
    public double Gcd { get; set; }
    public bool Infeasible { get; set; }
    public GearsetInput Gear { get; set; } = new();
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    public List<StatBreakpoint> Breakpoints { get; set; } = [];
    public double ElapsedMs { get; set; }
}

public sealed class UpgradePlanResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public double FightSeconds { get; set; }
    public string BisSet { get; set; } = string.Empty;
    public double CurrentDps { get; set; }
    public double CurrentRemeldedDps { get; set; }
    public double BisDps { get; set; }
    public double FinalDps { get; set; }
    public int? WeeksToFinish { get; set; }
    public List<UpgradeStep> Steps { get; set; } = [];
    public List<string> FinalRemeldSlots { get; set; } = [];
    public double FinalRemeldGain { get; set; }
    public List<DropUpgrade> Drops { get; set; } = [];
    public List<string> Unreached { get; set; } = [];
    public List<string> NoGain { get; set; } = [];
    public GearsetInput FinalGear { get; set; } = new();
    public double ElapsedMs { get; set; }
}

public sealed class MetaResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public Dictionary<string, int> WeeklyIncome { get; set; } = new(StringComparer.Ordinal);
    public string HuntCurrency { get; set; } = string.Empty;
    public List<MetaJob> Jobs { get; set; } = [];
    /// <summary>Everything a plan can spend, for the wallet. Item id 0: tomestones, known by name only.</summary>
    public List<MetaCurrency> Currencies { get; set; } = [];
}

public sealed record MetaCurrency(string Name, int ItemId, string Type);

public sealed class MetaJob
{
    public string Job { get; set; } = string.Empty;
    public List<double> FightLengths { get; set; } = [];
    public List<string> BisSets { get; set; } = [];
}

public sealed record ErrorResponse(string Error);
