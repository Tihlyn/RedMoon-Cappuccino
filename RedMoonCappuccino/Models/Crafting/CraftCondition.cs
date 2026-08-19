using System;
using System.Collections.Generic;

namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// A synthesis condition, numbered as the game numbers it.
///
/// The values are the game's own condition ids, recovered rather than assumed:
/// <c>ConditionsFlag</c> bit <em>i</em> corresponds to condition id <em>i</em>+1.
/// That mapping was derived by intersecting two observed flags against the condition
/// sets they actually rolled, and it reproduces both exactly — flag 1523 yields
/// {Normal, Good, Centered, Sturdy, Pliant, Malleable, Primed, Robust} and flag 1011
/// yields the same set with Good Omen in place of Robust, which is what was recorded.
///
/// <see cref="Excellent"/> and <see cref="Poor"/> never appear on expert recipes but
/// exist in the numbering, so they are kept to preserve the id alignment.
/// </summary>
public enum CraftCondition : byte
{
    /// <summary>Not read, or read as something unrecognised. Never a legal simulation input.</summary>
    Unknown = 0,

    Normal    = 1,
    Good      = 2,
    Excellent = 3,
    Poor      = 4,
    Centered  = 5,
    Sturdy    = 6,
    Pliant    = 7,
    Malleable = 8,
    Primed    = 9,

    /// <summary>Telegraph: guarantees <see cref="Good"/> next step, no effect of its own.</summary>
    GoodOmen  = 10,

    /// <summary>
    /// Telegraph <em>and</em> a discount: −50% durability this step, and guarantees
    /// <see cref="Sturdy"/> next step. Expert recipes from patch 7.41.
    /// </summary>
    Robust    = 11,
}

/// <summary>
/// The mechanical effect of each condition, and the flag decoding that says which
/// conditions a recipe can roll.
///
/// Everything here was read from in-game tooltips rather than inferred. Several
/// entries contradict what the plan assumed before they were checked — most of all
/// <see cref="CraftCondition.Robust"/>, which is a two-step half-durability window
/// rather than a bare warning.
/// </summary>
public static class ConditionEffects
{
    /// <summary>Highest condition id the numbering defines; bounds every per-condition array.</summary>
    public const int MaxConditionId = (int)CraftCondition.Robust;

    /// <summary>Length for an array indexed directly by <see cref="CraftCondition"/>.</summary>
    public const int TableSize = MaxConditionId + 1;

    /// <summary>
    /// Standard Good-condition quality multiplier.
    ///
    /// Deliberately not a constant at the call sites: relic tools raise this to
    /// <see cref="RelicGoodMultiplier"/>, and since relics are BiS that is the default
    /// case rather than an edge case. It is threaded through as a parameter so the
    /// admissible bound and the simulator cannot silently disagree about it — the
    /// failure mode being a bound computed at 1.5× while play happens at 1.75×, which
    /// stops the bound being admissible and prunes optimal lines with no symptom.
    /// </summary>
    public const double StandardGoodMultiplier = 1.50;

    /// <summary>Good-condition multiplier with a relic tool equipped.</summary>
    public const double RelicGoodMultiplier = 1.75;

    /// <summary>
    /// Quality multiplier applied to a touch action performed under this condition.
    /// <paramref name="goodMultiplier"/> is the player's actual Good multiplier.
    /// </summary>
    /// <summary>
    /// Quality condition multiplier in <em>quarters</em>, for the exact integer formula.
    ///
    /// <para>Raphael expresses this as halves — Poor 1, Normal 2, Good 3, Excellent 8 over a
    /// divisor of 2 — which cannot represent the 1.75x Good that Splendorous relic tools grant,
    /// because 3.5 is not an integer. Quarters can, and the relic case is the default for a
    /// current expert crafter, so the scale is quartered here and the divisor carries the
    /// matching factor of four.</para>
    /// </summary>
    public static int QualityConditionQuarters(CraftCondition condition, double goodMultiplier) => condition switch
    {
        CraftCondition.Good      => (int)Math.Round(goodMultiplier * 4),
        CraftCondition.Excellent => 16,
        CraftCondition.Poor      => 2,
        _                        => 4,
    };

    /// <summary>Progress condition multiplier in halves. Malleable is the only condition that touches progress.</summary>
    public static int ProgressConditionHalves(CraftCondition condition) =>
        condition == CraftCondition.Malleable ? 3 : 2;

    public static double QualityMultiplier(CraftCondition condition, double goodMultiplier) => condition switch
    {
        CraftCondition.Good      => goodMultiplier,
        CraftCondition.Excellent => 4.00,
        CraftCondition.Poor      => 0.50,
        _                        => 1.00,
    };

    /// <summary>Progress multiplier. Malleable is the only condition that touches progress.</summary>
    public static double ProgressMultiplier(CraftCondition condition) =>
        condition == CraftCondition.Malleable ? 1.50 : 1.00;

    /// <summary>
    /// Durability multiplier. Sturdy and Robust each halve the cost, and Robust does so
    /// while also guaranteeing a Sturdy next step — the two-step window.
    /// </summary>
    public static double DurabilityMultiplier(CraftCondition condition) =>
        condition is CraftCondition.Sturdy or CraftCondition.Robust ? 0.50 : 1.00;

    /// <summary>CP multiplier. Pliant halves it; visible in the recorded data as Observe costing 4 instead of 7.</summary>
    public static double CpMultiplier(CraftCondition condition) =>
        condition == CraftCondition.Pliant ? 0.50 : 1.00;

    /// <summary>
    /// Added success rate, as a percentage.
    ///
    /// <para>Centered is irrelevant only because of a choice made elsewhere: the solver's
    /// candidate set excludes fallible actions, so nothing it considers can benefit. Human expert
    /// play does the opposite — community guidance is consistent that Centered exists to make
    /// Rapid Synthesis and Hasty Touch worth casting, and a recorded human craft duly opens with
    /// three Rapid Syntheses. Two of those missed.</para>
    ///
    /// <para>So this is a live cost of refusing gambles, not a dead mechanic. Modelled in full
    /// so that admitting fallible actions later is a policy change rather than a simulator
    /// change.</para>
    /// </summary>
    public static int SuccessBonus(CraftCondition condition) =>
        condition == CraftCondition.Centered ? 25 : 0;

    /// <summary>Whether this condition extends the next status granted by two steps.</summary>
    public static bool IsPrimed(CraftCondition condition) => condition == CraftCondition.Primed;

    /// <summary>
    /// The condition this one guarantees next, or <see cref="CraftCondition.Unknown"/> if it
    /// guarantees nothing. This is the deterministic half of the fitted model: every recipe
    /// carries exactly one telegraph, never both, and it fired without exception across the
    /// recorded corpus (1064/1064 for Robust, 410/410 for Good Omen).
    /// </summary>
    public static CraftCondition Telegraphs(CraftCondition condition) => condition switch
    {
        CraftCondition.Robust   => CraftCondition.Sturdy,
        CraftCondition.GoodOmen => CraftCondition.Good,
        _                       => CraftCondition.Unknown,
    };

    /// <summary>
    /// Decode a recipe's <c>ConditionsFlag</c> into the conditions it can roll.
    ///
    /// This is also the pipeline's known-answer test. The number of distinct conditions
    /// ever observed on a recipe must not exceed this set's size: more means steps are
    /// being misread or misattributed, fewer just means the sample is incomplete. It is
    /// one-directional, works on every recipe, and needs nothing on screen — which is
    /// exactly why it is a *sanity* check and not an admissibility criterion. See
    /// <c>ConditionModelStatus</c> for the gate that actually guards the solver.
    /// </summary>
    public static CraftCondition[] Decode(ushort conditionsFlag)
    {
        var members = new List<CraftCondition>(TableSize);
        for (var bit = 0; bit < MaxConditionId; bit++)
        {
            if ((conditionsFlag & (1 << bit)) == 0) continue;
            members.Add((CraftCondition)(bit + 1));
        }

        return members.ToArray();
    }

    /// <summary>Number of conditions the flag declares — the popcount, named for what it means.</summary>
    public static int DeclaredConditionCount(ushort conditionsFlag) =>
        System.Numerics.BitOperations.PopCount(conditionsFlag);

    /// <summary>
    /// Map a condition name as the Synthesis addon displays it onto its id.
    ///
    /// Recorded samples carry the client-language display string rather than an enum,
    /// deliberately: recording the raw string is what surfaced Robust at all, when the
    /// plan expected six or seven conditions and the recipe rolled eight. Unrecognised
    /// names return <see cref="CraftCondition.Unknown"/> rather than guessing.
    /// </summary>
    public static CraftCondition FromDisplayName(string? name) => name?.Trim() switch
    {
        "Normal"     => CraftCondition.Normal,
        "Good"       => CraftCondition.Good,
        "Excellent"  => CraftCondition.Excellent,
        "Poor"       => CraftCondition.Poor,
        "Centered"   => CraftCondition.Centered,
        "Sturdy"     => CraftCondition.Sturdy,
        "Pliant"     => CraftCondition.Pliant,
        "Malleable"  => CraftCondition.Malleable,
        "Primed"     => CraftCondition.Primed,
        "Good Omen"  => CraftCondition.GoodOmen,
        "Robust"     => CraftCondition.Robust,
        _            => CraftCondition.Unknown,
    };
}
