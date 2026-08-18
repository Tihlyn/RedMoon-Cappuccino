using System;
using System.Collections.Generic;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// An admissible upper bound on the quality still obtainable from a state, and — because the
/// objective is binary — the feasibility filter that decides whether a branch is already dead.
///
/// <para><strong>Why it must over-estimate.</strong> The bound prunes. If it ever reports less
/// than a line can actually achieve, that line is cut and the solver returns worse play with no
/// error and no symptom. Every relaxation below therefore errs upward, and the harness proves
/// it empirically by running lines to completion and checking the bound was never exceeded.</para>
///
/// <para><strong>The relaxation.</strong> Conditions, buff expiry and Inner Quiet ramp-up are all
/// dropped: every quality action is priced as though Inner Quiet were already at ten, Innovation
/// and Great Strides were both running and free, and the best condition the recipe can roll were
/// active. What remains are the two resources that genuinely bound how many actions fit —
/// <em>CP and durability</em> — so the bound is a two-dimensional unbounded knapsack over them.
/// Dropping either would make the bound infinite: Trained Finesse costs no durability and Hasty
/// Touch costs no CP, so each is unbounded in isolation and only the other resource stops it.</para>
///
/// <para><strong>Why it is a DAG.</strong> Quality actions strictly decrease CP or durability;
/// repairs decrease CP while raising durability. Iterating CP ascending and durability ascending
/// within it means every dependency is already computed, so one pass fills the table.</para>
///
/// <para>Progress is deliberately ignored. A craft must finish progress <em>and</em> clear the
/// quality threshold, so spending the whole budget on quality is not a real plan — which is
/// exactly why using it as an upper bound is safe. Reserving a progress budget would tighten
/// this considerably and is the obvious first optimisation if pruning proves too weak.</para>
/// </summary>
public sealed class QualityBound
{
    private readonly int maxCp;
    private readonly int maxDurability;

    /// <summary>table[cp * (maxDurability + 1) + durability] — max quality obtainable from that budget.</summary>
    private readonly int[] table;

    /// <summary>Flat allowance for the durability-free cast Trained Perfection grants.</summary>
    private readonly int freeAction;

    /// <summary>Quality per cast at the most favourable assumptions the relaxation allows.</summary>
    public IReadOnlyDictionary<CraftAction, int> BestCaseGain { get; }

    public QualityBound(CraftSim sim)
    {
        maxCp         = sim.Player.MaxCp;
        maxDurability = sim.Recipe.Durability;

        var stride = maxDurability + 1;
        table = new int[(maxCp + 1) * stride];

        // Best condition the recipe can actually roll. Overshooting here would still be
        // admissible, but pricing Excellent on a recipe that cannot roll it makes the bound
        // needlessly loose, and loose bounds prune nothing.
        var condition = BestQualityCondition(sim.Recipe.ConditionsFlag, sim.Player.GoodMultiplier);

        // Inner Quiet pinned at ten, Great Strides and Innovation both free.
        const int EffectMod = (10 + 10) * (10 + 10 + 5);

        var gains = new Dictionary<CraftAction, int>();
        var moves = new List<(int Cp, int Dur, int Gain)>();

        foreach (var action in CraftActions.All)
        {
            var spec = CraftActions.Spec(action);
            if (spec.QualityEfficiency == 0) continue;
            if (spec.Kind == ActionKind.Specialist) continue;

            // Byregot's scales with the stacks it consumes; at the assumed cap that is 300.
            var efficiency = action == CraftAction.ByregotsBlessing
                ? 100 + 20 * CraftActions.MaxInnerQuiet
                : spec.QualityEfficiency;

            var gain = (int)((long)sim.BaseQuality * efficiency * EffectMod * condition / 40000);
            if (gain <= 0) continue;

            gains[action] = gain;
            moves.Add((spec.CpCost, MinimumDurability(spec.DurabilityCost, sim.Recipe.ConditionsFlag), gain));
        }

        BestCaseGain = gains;

        // Repairs buy durability with CP. Manipulation is priced at its full eight steps of
        // restoration delivered at once, which is the most it could ever be worth.
        var repairs = new List<(int Cp, int Dur)>
        {
            (CraftActions.Spec(CraftAction.MastersMend).CpCost, CraftActions.MastersMendRestore),
            (CraftActions.Spec(CraftAction.ImmaculateMend).CpCost, maxDurability),
            (CraftActions.Spec(CraftAction.Manipulation).CpCost, CraftActions.ManipulationRestore * 8),
        };

        // Trained Perfection zeroes one action's durability outright. Pricing any action at zero
        // would make the table unbounded, so it is carried as a flat allowance of the single
        // best cast instead — bounded, and never less than the charge can actually be worth.
        var freeCast = 0;
        foreach (var (_, gain) in gains) freeCast = Math.Max(freeCast, gain);
        freeAction = freeCast * CraftActions.TrainedPerfectionCharges;

        for (var cp = 0; cp <= maxCp; cp++)
        for (var dur = 0; dur <= maxDurability; dur++)
        {
            var best = 0;

            foreach (var (mCp, mDur, mGain) in moves)
            {
                if (mCp > cp || mDur > dur) continue;
                var candidate = mGain + table[(cp - mCp) * stride + (dur - mDur)];
                if (candidate > best) best = candidate;
            }

            foreach (var (rCp, rDur) in repairs)
            {
                if (rCp > cp) continue;
                var restored = Math.Min(maxDurability, dur + rDur);
                if (restored <= dur) continue;
                var candidate = table[(cp - rCp) * stride + restored];
                if (candidate > best) best = candidate;
            }

            table[cp * stride + dur] = best;
        }
    }

    /// <summary>
    /// Upper bound on the quality still obtainable from <paramref name="state"/>, already capped
    /// by how much room the recipe leaves.
    /// </summary>
    public int Remaining(CraftState state, RecipeSpec recipe)
    {
        if (state.IsTerminal) return 0;

        var cp  = Math.Clamp(state.Cp, 0, maxCp);
        var dur = Math.Clamp(state.Durability, 0, maxDurability);

        var allowance = state.TrainedPerfectionLeft > 0 || state.TrainedPerfectionActive ? freeAction : 0;

        var headroom = Math.Max(0, recipe.MaxQuality - state.Quality);
        return Math.Min(table[cp * (maxDurability + 1) + dur] + allowance, headroom);
    }

    /// <summary>
    /// Whether the state can still clear the recipe's quality threshold. Under a binary objective
    /// a false here means the craft is already lost — the single most useful thing the tool can
    /// say, and far earlier than a player could see it.
    /// </summary>
    public bool CanStillClear(CraftState state, RecipeSpec recipe) =>
        state.Quality + Remaining(state, recipe) >= recipe.RequiredQuality;

    /// <summary>
    /// The least durability an action can ever cost: Waste Not halves it, and Sturdy or Robust
    /// halves it again where the recipe can roll them. Both are priced as free and always active,
    /// which is what keeps the bound above anything real play can reach — charging full cost is
    /// precisely the error that made an earlier version inadmissible.
    /// </summary>
    private static int MinimumDurability(int cost, ushort conditionsFlag)
    {
        if (cost == 0) return 0;

        cost = (int)Math.Ceiling(cost / 2.0);   // Waste Not

        var halvesAgain = conditionsFlag == 0;
        foreach (var condition in ConditionEffects.Decode(conditionsFlag))
            if (ConditionEffects.DurabilityMultiplier(condition) < 1.0) halvesAgain = true;

        if (halvesAgain) cost = (int)Math.Ceiling(cost / 2.0);

        return cost;
    }

    /// <summary>
    /// The strongest quality condition the recipe's flag permits, on the quarters scale. Expert
    /// recipes top out at Good; only the standard set includes Excellent.
    /// </summary>
    private static int BestQualityCondition(ushort conditionsFlag, double goodMultiplier)
    {
        var best = ConditionEffects.QualityConditionQuarters(CraftCondition.Normal, goodMultiplier);

        // An unknown flag must not silently narrow the bound, so fall back to the strongest
        // condition in the game rather than assuming the recipe is tame.
        var rollable = conditionsFlag == 0
            ? Enum.GetValues<CraftCondition>()
            : ConditionEffects.Decode(conditionsFlag);

        foreach (var condition in rollable)
        {
            if (condition == CraftCondition.Unknown) continue;

            var quarters = ConditionEffects.QualityConditionQuarters(condition, goodMultiplier);
            if (quarters > best) best = quarters;
        }

        return best;
    }
}
