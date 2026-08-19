namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// How a craft is going, in the only terms that matter under an all-or-nothing quality target.
///
/// <para>Not a quality percentage. A player can already read the quality bar; what they cannot read
/// is whether the bar is on a line that still reaches the requirement, and those come apart badly.
/// At step fifteen a craft at 40% quality and a craft at 40% quality can be one comfortable and one
/// already lost, and nothing on screen separates them.</para>
/// </summary>
public enum CraftPosture
{
    /// <summary>The requirement is no longer reachable. The craft is over; only the reading of it is left.</summary>
    Dead,

    /// <summary>Reachable, but only through the gambles. Expect the advice to take risks it otherwise would not.</summary>
    Behind,

    /// <summary>Reachable on ordinary play.</summary>
    OnPace,

    /// <summary>Enough margin that protecting it beats pressing.</summary>
    Ahead,
}

/// <summary>
/// One decision, as the player should see it.
///
/// <para>The verdict leads and everything else supports it, because the product is the judgement
/// rather than the keystroke: what a player cannot compute unaided is whether <em>this</em> window
/// is worth spending on, not which action class a Sturdy calls for.</para>
/// </summary>
public readonly record struct CraftAdvice
{
    /// <summary>What to use. <see cref="CraftAction.None"/> when nothing should be done.</summary>
    public CraftAction Recommended { get; init; }

    /// <summary>The next best action, for the margin between them.</summary>
    public CraftAction Runner { get; init; }

    public CraftPosture Posture { get; init; }

    /// <summary>Estimated chance this position still clears, 0 to 1.</summary>
    public double ClearChance { get; init; }

    /// <summary>How much of the margin over the runner-up is real, 0 to 1. Near zero means the two are a coin toss.</summary>
    public double Margin { get; init; }

    /// <summary>Quality still owed. Zero once the requirement is met.</summary>
    public int Shortfall { get; init; }

    /// <summary>Whether the recommendation spends a Crafter's Delineation, which is a real currency.</summary>
    public bool CostsDelineation { get; init; }

    /// <summary>One line, player-facing, leading with the judgement.</summary>
    public string Verdict { get; init; }

    /// <summary>One line of supporting evidence for the verdict. Never required to understand it.</summary>
    public string Because { get; init; }

    /// <summary>Set when the advisor will not advise, and why. Advice is not shown while this is present.</summary>
    public string? Refusal { get; init; }

    public bool IsRefusing => !string.IsNullOrEmpty(Refusal);

    /// <summary>
    /// The advisor declining to answer.
    ///
    /// <para>A confident wrong answer is worse than no answer here: acting on advice costs a craft's
    /// worth of materials, and the failure modes are silent ones — a misread stat line, a state that
    /// drifted from the client's. Refusing is a first-class outcome, not an error path.</para>
    /// </summary>
    public static CraftAdvice Refusing(string reason) => new()
    {
        Recommended = CraftAction.None,
        Runner = CraftAction.None,
        Posture = CraftPosture.OnPace,
        Refusal = reason,
        Verdict = "Not advising",
        Because = reason,
    };
}
