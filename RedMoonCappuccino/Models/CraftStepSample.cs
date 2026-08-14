namespace RedMoonCappuccino.Models;

/// <summary>
/// One observed crafting step. Consecutive samples within a session form the
/// (condition, action, next condition) triples the weight fitting needs — the
/// transition is derived from the pair rather than stored, so a dropped tail
/// sample costs one transition instead of corrupting the run.
///
/// Numbers come from the Synthesis addon's text nodes, which are localised
/// display strings; <see cref="Condition"/> is therefore the client-language
/// name and gets normalised at fitting time, not here. Recording the raw string
/// keeps the log honest if a condition is ever added or renamed.
/// </summary>
public sealed record CraftStepSample
{
    /// <summary>Session this step belongs to, matching the header line's Id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Step counter as the game reports it. Does not advance for Careful Observation or Heart and Soul.</summary>
    public required int Step { get; init; }

    /// <summary>Condition name as displayed, in the client's language.</summary>
    public required string Condition { get; init; }

    /// <summary>Action sent from this step, or 0 if the craft ended here.</summary>
    public required uint ActionId { get; init; }

    public required int Progress    { get; init; }
    public required int Quality     { get; init; }
    public required int Durability  { get; init; }
    public required uint Cp         { get; init; }

    /// <summary>Buff names still active, with their remaining steps, as the addon shows them.</summary>
    public required string[] Effects { get; init; }

    /// <summary>Client timestamp; only used to order and to spot stalls, never as data.</summary>
    public required long TickMs { get; init; }
}

/// <summary>
/// Written once per craft, ahead of that craft's steps. Carries everything about
/// the recipe that the fitted weights have to be conditioned on — most of all
/// <see cref="ConditionsFlag"/>, which determines <em>which</em> conditions the
/// recipe can roll and therefore which distribution the samples belong to.
/// </summary>
public sealed record CraftSessionHeader
{
    public required string Id { get; init; }
    public required ushort RecipeId { get; init; }
    public required string ItemName { get; init; }
    public required uint JobId { get; init; }

    /// <summary>Bitmask of conditions this recipe can roll. Samples from differing flags are different populations.</summary>
    public required ushort ConditionsFlag { get; init; }

    public required ushort Difficulty     { get; init; }
    public required uint   MaxQuality     { get; init; }
    public required uint   RequiredQuality { get; init; }
    public required ushort Durability     { get; init; }
    public required byte   Stars          { get; init; }

    /// <summary>Player stats at craft start; the policy depends on these, so fits must not mix them silently.</summary>
    public required uint MaxCp { get; init; }

    public required long StartedAtMs { get; init; }
}
