using System;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Why a flag's fitted condition model may or may not be used by the solver.
///
/// <para>Only <see cref="Admissible"/> permits the solver to run. Every other value is a
/// refusal, and the refusal is the point: the failure this guards against is meeting an
/// uncharacterised flag and quietly proceeding on default or uninitialised weights, which
/// produces confident advice with no error and no symptom.</para>
/// </summary>
public enum ConditionModelStatus
{
    /// <summary>Fitted, precise, and it passed its own homogeneity test. Safe to solve against.</summary>
    Admissible,

    /// <summary>No data has ever been collected for this flag.</summary>
    Absent,

    /// <summary>Fewer transitions than the gate requires; the weights exist but are not yet trustworthy.</summary>
    InsufficientData,

    /// <summary>
    /// One or more conditions the flag declares have never been observed, so their weights
    /// would be estimated at zero — a value that is certainly wrong rather than merely imprecise.
    /// </summary>
    IncompleteCoverage,

    /// <summary>
    /// The transitions reject the one-telegraph-plus-i.i.d. shape. The weights may be individually
    /// precise and still describe the wrong model, so they are refused rather than used.
    /// </summary>
    FailedHomogeneity,

    /// <summary>Enough transitions to pass the count gate, but at least one weight is still wider than the precision target.</summary>
    ImpreciseWeights,

    /// <summary>
    /// More distinct conditions were observed than the flag declares. This is not an
    /// imprecision — it means steps are being misread or samples from two flags have been
    /// pooled, and the fit is worthless rather than incomplete.
    /// </summary>
    PopcountViolation,

    /// <summary>The telegraph did not hold deterministically, so the deterministic half of the model is wrong.</summary>
    TelegraphBroken,
}

/// <summary>
/// The measurements behind a fitted model, kept alongside it so a refusal can say what is
/// missing and how much more collection would close it.
/// </summary>
public sealed record ConditionModelEvidence
{
    /// <summary>Transitions used for the i.i.d. fit — that is, excluding the deterministic telegraph rows.</summary>
    public required int FittedTransitions { get; init; }

    /// <summary>Telegraph transitions observed, and how many honoured the telegraph.</summary>
    public required int TelegraphTransitions { get; init; }
    public required int TelegraphHonoured { get; init; }

    /// <summary>Homogeneity test across source conditions, with the telegraph row removed.</summary>
    public required double ChiSquare { get; init; }
    public required int    DegreesOfFreedom { get; init; }
    public required double PValue { get; init; }

    /// <summary>Widest 95% half-interval across all fitted weights; the precision the gate reads.</summary>
    public required double MaxHalfWidth { get; init; }

    /// <summary>Distinct conditions actually observed, against the count the flag declares.</summary>
    public required int DistinctObserved { get; init; }
    public required int DeclaredCount { get; init; }

    /// <summary>Conditions the flag declares that have never been seen.</summary>
    public required CraftCondition[] UnobservedConditions { get; init; }
}

/// <summary>
/// A fitted condition model for one <c>ConditionsFlag</c>.
///
/// <para>The model is one deterministic rule plus an i.i.d. draw. Every recipe measured
/// carries exactly one telegraph — Robust yields Sturdy, or Good Omen yields Good, never
/// both — and with that row removed the remaining sources are statistically indistinguishable.
/// That collapses the transition matrix from 56 free parameters to seven.</para>
///
/// <para>Weights belong to the <em>flag</em>, not the recipe: two different recipes sharing
/// flag 1523 fit the same distribution, and two flags differing by a single bit do not. This
/// is what makes collection reusable — and it is exactly why an unmeasured flag must be
/// treated as unmeasured rather than approximated from a neighbouring one.</para>
/// </summary>
public sealed record ConditionModel
{
    public required ushort Flag { get; init; }

    /// <summary>Conditions this flag can roll, decoded from its bits.</summary>
    public required CraftCondition[] Members { get; init; }

    /// <summary>The telegraph source, or Unknown if this flag carries none.</summary>
    public required CraftCondition TelegraphSource { get; init; }

    /// <summary>What the telegraph guarantees.</summary>
    public required CraftCondition TelegraphTarget { get; init; }

    /// <summary>
    /// The i.i.d. draw, indexed by <see cref="CraftCondition"/>. Entries for conditions the
    /// flag cannot roll are zero. Sums to 1 across the flag's members.
    /// </summary>
    public required double[] Weights { get; init; }

    public required ConditionModelEvidence Evidence { get; init; }

    public required ConditionModelStatus Status { get; init; }

    /// <summary>The only property the solver is permitted to branch on.</summary>
    public bool IsAdmissible => Status == ConditionModelStatus.Admissible;

    /// <summary>
    /// Distribution of the next condition given the current one. A telegraph source yields a
    /// point mass; everything else yields the i.i.d. draw.
    /// </summary>
    public double Probability(CraftCondition from, CraftCondition to)
    {
        if (from == TelegraphSource && TelegraphSource != CraftCondition.Unknown)
            return to == TelegraphTarget ? 1.0 : 0.0;

        return Weights[(int)to];
    }

    /// <summary>Whether the next condition is known with certainty — the exploitable part of the model.</summary>
    public bool IsTelegraphed(CraftCondition from) =>
        TelegraphSource != CraftCondition.Unknown && from == TelegraphSource;

    /// <summary>A one-line explanation of the current status, for the advisory surface and the log.</summary>
    public string Explain() => Status switch
    {
        ConditionModelStatus.Admissible =>
            $"flag {Flag}: {Evidence.FittedTransitions} transitions, p={Evidence.PValue:F2}, ±{Evidence.MaxHalfWidth:P1}.",

        ConditionModelStatus.Absent =>
            $"flag {Flag}: never measured. Collection needed before this recipe can be solved.",

        ConditionModelStatus.InsufficientData =>
            $"flag {Flag}: {Evidence.FittedTransitions} of {ConditionModelGate.MinFittedTransitions} transitions needed.",

        ConditionModelStatus.IncompleteCoverage =>
            $"flag {Flag}: {Evidence.DistinctObserved} of {Evidence.DeclaredCount} conditions seen; " +
            $"missing {string.Join(", ", Evidence.UnobservedConditions)}.",

        ConditionModelStatus.FailedHomogeneity =>
            $"flag {Flag}: transitions reject the i.i.d. model (chi²={Evidence.ChiSquare:F2}, " +
            $"df={Evidence.DegreesOfFreedom}, p={Evidence.PValue:F4}). The model shape is wrong for this flag.",

        ConditionModelStatus.ImpreciseWeights =>
            $"flag {Flag}: widest interval ±{Evidence.MaxHalfWidth:P1}, target ±{ConditionModelGate.MaxHalfWidth:P1}.",

        ConditionModelStatus.PopcountViolation =>
            $"flag {Flag}: observed {Evidence.DistinctObserved} conditions but the flag declares " +
            $"{Evidence.DeclaredCount}. Samples are being misread or two flags have been pooled.",

        ConditionModelStatus.TelegraphBroken =>
            $"flag {Flag}: telegraph held only {Evidence.TelegraphHonoured}/{Evidence.TelegraphTransitions}.",

        _ => $"flag {Flag}: unknown status.",
    };
}

/// <summary>
/// The admissibility gate: the conditions a flag's fitted model must meet before the solver
/// is allowed to treat its bound as trustworthy.
///
/// <para><strong>Why this exists separately from the popcount check.</strong> The popcount
/// assertion is a good sanity check and a cheap one, but it only establishes that a flag's
/// <em>condition count</em> is consistent with what the recipe declares. It says nothing
/// about whether the fitted distribution is trustworthy: a flag with three recorded
/// transitions passes popcount and is still worthless. Without a distinct gate, the failure
/// mode is a solver meeting its third flag, holding no data for it, and either faulting on
/// an empty table or — much worse — proceeding on default weights and giving confident
/// advice that is quietly wrong. Popcount catches a mix-up; this catches an absence.</para>
/// </summary>
public static class ConditionModelGate
{
    /// <summary>
    /// Transitions required before a flag's weights are trusted.
    ///
    /// Set from the precision target rather than by feel: holding a weight near 10% to a
    /// 95% half-interval of one percentage point needs roughly 3,500 observations. The two
    /// measured flags cleared this comfortably — 10,507 fitted transitions on 1523 — so the
    /// bar costs nothing on characterised flags and is the whole safeguard on new ones.
    /// </summary>
    public const int MinFittedTransitions = 3_500;

    /// <summary>
    /// Significance level for the homogeneity test. The model is refused when the transitions
    /// <em>reject</em> the one-telegraph-plus-i.i.d. shape at this level.
    ///
    /// Deliberately strict rather than the conventional 0.05: rejecting a good model costs
    /// two hours of collection, while accepting a bad one silently corrupts every downstream
    /// phase. The measured flags returned p≈0.99, nowhere near the boundary.
    /// </summary>
    public const double MinHomogeneityP = 0.01;

    /// <summary>Widest 95% half-interval tolerated on any single weight. The plan's stated target; the fit achieved ±0.8%.</summary>
    public const double MaxHalfWidth = 0.01;

    /// <summary>
    /// The telegraph is deterministic or it is not a telegraph. Any exception means either the
    /// rule is wrong or the samples are contaminated — Careful Observation advancing the
    /// condition without advancing the step counter produces exactly this signature, and it
    /// broke the telegraph in 5 of 268 manual transitions before the driver was changed to
    /// exclude specialist actions.
    /// </summary>
    public const double RequiredTelegraphRate = 1.0;

    /// <summary>
    /// Grade a fitted model. Order matters: the checks that indicate corrupt data are reported
    /// ahead of the ones that merely indicate too little of it, so the message a user sees
    /// names the most serious problem rather than the first one alphabetically.
    /// </summary>
    public static ConditionModelStatus Grade(ConditionModelEvidence evidence)
    {
        // Corruption first — these mean the data is wrong, not thin.
        if (evidence.DistinctObserved > evidence.DeclaredCount)
            return ConditionModelStatus.PopcountViolation;

        if (evidence.TelegraphTransitions > 0
            && evidence.TelegraphHonoured < evidence.TelegraphTransitions * RequiredTelegraphRate)
            return ConditionModelStatus.TelegraphBroken;

        if (evidence.FittedTransitions == 0)
            return ConditionModelStatus.Absent;

        if (evidence.FittedTransitions < MinFittedTransitions)
            return ConditionModelStatus.InsufficientData;

        if (evidence.UnobservedConditions.Length > 0)
            return ConditionModelStatus.IncompleteCoverage;

        // The model must pass its own test before its precision is worth discussing.
        if (evidence.DegreesOfFreedom > 0 && evidence.PValue < MinHomogeneityP)
            return ConditionModelStatus.FailedHomogeneity;

        if (evidence.MaxHalfWidth > MaxHalfWidth)
            return ConditionModelStatus.ImpreciseWeights;

        return ConditionModelStatus.Admissible;
    }
}
