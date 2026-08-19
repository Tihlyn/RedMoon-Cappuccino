using System;
using System.Collections.Generic;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// The community ruleset, as a policy: what to do on each condition, without searching.
///
/// <para><strong>Why this exists.</strong> Most steps of an expert craft are not decisions. Pliant
/// shows and Manipulation is down, so you cast Manipulation; Malleable shows and progress is
/// owed, so you push progress. Published guidance is consistent on these and players do not
/// deliberate over them. Searching them anyway is what made a one-ply policy face the whole craft
/// at once and choose nothing — the scope was too wide to produce anything.</para>
///
/// <para><strong>What it is for.</strong> Two things. It is a competent default between decision
/// points, and it is a rollout that <em>finishes crafts</em> — which the previous hand-written one
/// could not. An evaluator that never reaches a terminal state returns zero for every candidate
/// and gives a search nothing to rank; one that plays a real craft to the end returns a signal.</para>
///
/// <para>Deliberately not optimal, and deliberately not complete. It covers the ordinary steps —
/// plain conditions, no gamble on offer, no specialist charge worth spending — and leaves the rest
/// to the search. Anything it decides is something the search never gets to weigh, so the rules
/// here stop where real decisions begin.</para>
/// </summary>
public sealed class HeuristicPolicy : ICraftPolicy
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private readonly int gambleBudget;
    private readonly CraftAction[] opening;
    private int openingCursor;

    public HeuristicPolicy(CraftSim sim, QualityBound bound, int gambleBudget = 0,
                           CraftAction[]? opening = null)
    {
        this.sim          = sim;
        this.bound        = bound;
        this.gambleBudget = gambleBudget;
        this.opening      = opening ?? Array.Empty<CraftAction>();
    }

    public string Name => gambleBudget > 0 ? $"heuristic, {gambleBudget} gambles" : "heuristic";

    public CraftAction Choose(CraftState state)
    {
        while (openingCursor < opening.Length)
        {
            var scripted = opening[openingCursor++];
            if (Usable(state, scripted)) return scripted;
        }

        var owed = sim.Recipe.Difficulty - state.Progress;

        // Progress is not a phase to switch into but a debt to service. Left too late it cannot be
        // paid at all, so urgency is judged against what the remaining durability can actually
        // deliver at this state's buffs — not at the bound's best case, which assumes buffs that
        // are not running and would delay the switch past the point of recovery.
        var urgent = owed > 0 && state.Durability <= ProgressReserve(state) + UrgencyMargin;

        // Keep the durability engine alive. Pliant halves its cost, which is when it is worth
        // paying for; when progress is owed and durability is short it is worth paying anyway.
        if (!state.HasBuff(CraftBuff.Manipulation) && Usable(state, CraftAction.Manipulation)
            && (state.Condition == CraftCondition.Pliant || (urgent && state.Durability <= 20)))
            return CraftAction.Manipulation;

        if (state.Durability <= 10 && owed > 0)
        {
            if (Usable(state, CraftAction.ImmaculateMend)) return CraftAction.ImmaculateMend;
            if (Usable(state, CraftAction.MastersMend)) return CraftAction.MastersMend;
        }

        return urgent || owed > 0 && FavoursProgress(state.Condition)
            ? ProgressAction(state)
            : QualityAction(state, owed);
    }

    /// <summary>Slack kept in hand so a single unlucky condition cannot make the debt unpayable.</summary>
    private const int UrgencyMargin = 15;

    /// <summary>
    /// Conditions the guides spend on progress: Malleable multiplies it outright, and Sturdy
    /// halves the durability of the expensive progress actions that carry it.
    /// </summary>
    private static bool FavoursProgress(CraftCondition condition) =>
        condition is CraftCondition.Malleable or CraftCondition.Sturdy;

    private int ProgressReserve(CraftState state)
    {
        var owed = sim.Recipe.Difficulty - state.Progress;
        if (owed <= 0) return 0;

        var best = 0.0;
        foreach (var action in CraftActions.All)
        {
            var spec = CraftActions.Spec(action);
            if (spec.ProgressEfficiency == 0 || spec.SuccessRate < 100) continue;
            if (!Usable(state, action)) continue;

            var cost = sim.DurabilityCost(state, action);
            if (cost <= 0) continue;

            var rate = (double)sim.ProgressGain(state, action) / cost;
            if (rate > best) best = rate;
        }

        return best <= 0 ? int.MaxValue : (int)Math.Ceiling(owed / best);
    }

    /// <summary>
    /// Progress, with Veneration first when there is enough left to owe for it. Veneration adds
    /// 50% for four steps, so it pays for itself whenever more than one cast follows.
    /// </summary>
    private CraftAction ProgressAction(CraftState state)
    {
        var owed = sim.Recipe.Difficulty - state.Progress;

        if (!state.HasBuff(CraftBuff.Veneration) && Usable(state, CraftAction.Veneration)
            && owed > sim.ProgressGain(state, CraftAction.Groundwork) * 2)
            return CraftAction.Veneration;

        if (gambleBudget > state.GamblesUsed
            && state.Condition == CraftCondition.Centered
            && Usable(state, CraftAction.RapidSynthesis))
            return CraftAction.RapidSynthesis;

        return BestBy(state, a => CraftActions.Spec(a).ProgressEfficiency > 0
                                  && CraftActions.Spec(a).SuccessRate >= 100
            ? sim.ProgressGain(state, a) : -1);
    }

    /// <summary>
    /// Quality, spending the condition on whatever it is good for: Primed extends a status,
    /// Good multiplies a touch, Sturdy pays for the durability-heavy one.
    /// </summary>
    private CraftAction QualityAction(CraftState state, int owed)
    {
        // Primed adds two steps to the next status, so it is the moment to lay one down.
        if (state.Condition == CraftCondition.Primed)
        {
            if (!state.HasBuff(CraftBuff.Innovation) && Usable(state, CraftAction.Innovation))
                return CraftAction.Innovation;
            if (owed > 0 && !state.HasBuff(CraftBuff.Veneration) && Usable(state, CraftAction.Veneration))
                return CraftAction.Veneration;
        }

        // Specialist actions are deliberately absent here. They are decisions, not defaults:
        // each is worth whatever it enables, which depends on the state, and the search values
        // them properly by looking through them. Hard-coding a rule for when to spend one made
        // this policy the whole strategy instead of the part that fills the gaps.

        // Cash Inner Quiet in before it can be wasted, under the biggest multiplier available.
        if (state.InnerQuiet >= CraftActions.MaxInnerQuiet
            && state.HasBuff(CraftBuff.GreatStrides)
            && Usable(state, CraftAction.ByregotsBlessing))
            return CraftAction.ByregotsBlessing;

        if (state.InnerQuiet >= CraftActions.MaxInnerQuiet
            && !state.HasBuff(CraftBuff.GreatStrides)
            && Usable(state, CraftAction.GreatStrides))
            return CraftAction.GreatStrides;

        if (!state.HasBuff(CraftBuff.Innovation) && Usable(state, CraftAction.Innovation))
            return CraftAction.Innovation;

        return BestBy(state, a => CraftActions.Spec(a).QualityEfficiency > 0
                                  && CraftActions.Spec(a).SuccessRate >= 100
            ? sim.QualityGain(state, a) : -1);
    }

    private CraftAction BestBy(CraftState state, Func<CraftAction, int> score)
    {
        var best = CraftAction.None;
        var bestScore = 0;

        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;
            if (CraftActions.Spec(action).CostsDelineation && sim.Player.AvailableDelineations <= 0) continue;
            if (!Usable(state, action)) continue;

            var value = score(action);
            if (value > bestScore) { bestScore = value; best = action; }
        }

        return best;
    }

    /// <summary>
    /// Legality, plus the player's willingness to spend a Delineation.
    ///
    /// <para>The budget check belongs here rather than in the candidate filter. The specialist
    /// rules above return their action directly, so a filter applied only when ranking by gain
    /// would have let them through regardless of the budget — which is exactly what happened, and
    /// the improvement it produced was not real. One choke point, honoured by every path.</para>
    /// </summary>
    private bool Usable(CraftState state, CraftAction action)
    {
        if (CraftActions.Spec(action).CostsDelineation && sim.Player.AvailableDelineations <= 0)
            return false;

        return sim.Legality(state, action) == ActionLegality.Usable;
    }
}
