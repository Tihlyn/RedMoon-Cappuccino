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

    /// <summary>Action sent from this state, or 0 if none was (craft ended, or observe-only mode).</summary>
    public required uint ActionId { get; init; }

    /// <summary>
    /// What produced this sample. <c>step</c> is the ordinary case. <c>reroll</c> means the
    /// condition changed while the step counter did not — the signature of Careful Observation,
    /// which the recorder was previously blind to because it only emitted on step changes.
    ///
    /// A reroll sample shares its <see cref="Step"/> with the sample immediately before it, so
    /// the pair gives the source condition, the action that rerolled it, and the result.
    ///
    /// Deliberately not <c>required</c>: this field was added after the bulk of the corpus was
    /// recorded, and a record written before it exists is a step by definition — the earlier
    /// recorder only ever emitted on a step change. Making it required would make every legacy
    /// line fail to deserialize, which silently discarded 98% of the collected transitions
    /// rather than reporting a problem.
    /// </summary>
    public string Trigger { get; init; } = "step";

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

    /// <summary>
    /// Whether the game flags this as an expert recipe. A non-expert recipe rolls a
    /// different condition set entirely, so its samples must never be pooled with expert
    /// ones — this is the field that catches such a mix-up after the fact.
    /// </summary>
    public required bool IsExpert { get; init; }

    public required ushort Difficulty     { get; init; }
    public required uint   MaxQuality     { get; init; }
    public required uint   RequiredQuality { get; init; }
    public required ushort Durability     { get; init; }
    public required byte   Stars          { get; init; }

    /// <summary>
    /// <see cref="ConditionsFlag"/> decoded into its set bit positions — one per condition the
    /// recipe can roll. Good Omen is granted per recipe rather than universally, so this set
    /// genuinely varies and cannot be assumed from the game's full condition list.
    ///
    /// The names are deliberately not resolved: fitting runs on the observed condition strings,
    /// and the flag only has to identify which crafts belong to the same population. What this
    /// does give is the pipeline's known-answer test — the number of distinct conditions observed
    /// across a recipe must equal this array's length. More means steps are being misread; fewer
    /// means the sample is simply not yet complete.
    /// </summary>
    public required int[] ConditionBits { get; init; }

    /// <summary>Player stats at craft start; the policy depends on these, so fits must not mix them silently.</summary>
    public required uint MaxCp { get; init; }

    public required long StartedAtMs { get; init; }
}
