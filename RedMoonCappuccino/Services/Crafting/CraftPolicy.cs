using System;
using System.Collections.Generic;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>Chooses the next action from a state. <see cref="CraftAction.None"/> means stop.</summary>
public interface ICraftPolicy
{
    string Name { get; }
    CraftAction Choose(CraftState state);
}

/// <summary>
/// Draws the next condition from a fitted model.
///
/// <para>Two cases, and the first is the whole reason expert crafting rewards adaptation: from a
/// telegraph the next condition is <em>certain</em>, so a policy that reads it is acting on
/// knowledge rather than on a guess. Everything else is an i.i.d. draw from the fitted weights.</para>
/// </summary>
public sealed class ConditionSampler
{
    private readonly ConditionModel model;
    private readonly double[] cumulative;

    public ConditionSampler(ConditionModel model)
    {
        this.model = model;

        cumulative = new double[model.Members.Length];
        var running = 0.0;
        for (var i = 0; i < model.Members.Length; i++)
        {
            running += model.Weights[i];
            cumulative[i] = running;
        }
    }

    public CraftCondition Next(CraftCondition from, Random rng)
    {
        if (model.IsTelegraphed(from)) return model.TelegraphTarget;

        var roll = rng.NextDouble() * cumulative[^1];
        for (var i = 0; i < cumulative.Length; i++)
            if (roll <= cumulative[i]) return model.Members[i];

        return model.Members[^1];
    }
}

/// <summary>
/// Replays a line computed in advance, ignoring whatever conditions actually roll — a macro.
///
/// <para>This is what adaptation has to beat, and it is not a straw man: the line comes from the
/// same solver on the same recipe, and is optimal for the conditions it assumed. What it cannot
/// do is notice that the assumption was wrong.</para>
///
/// <para>An action that has become unaffordable is skipped rather than ending the craft, which is
/// what an in-game macro does.</para>
/// </summary>
public sealed class StaticPolicy : ICraftPolicy
{
    private readonly CraftSim sim;
    private readonly IReadOnlyList<CraftAction> line;
    private int cursor;

    public StaticPolicy(CraftSim sim, IReadOnlyList<CraftAction> line)
    {
        this.sim  = sim;
        this.line = line;
    }

    public string Name => "static line";

    public void Reset() => cursor = 0;

    public CraftAction Choose(CraftState state)
    {
        while (cursor < line.Count)
        {
            var action = line[cursor++];
            if (sim.Legality(state, action) == ActionLegality.Usable) return action;
        }
        return CraftAction.None;
    }
}

/// <summary>
/// One-ply expectimax over the fitted condition model.
///
/// <para>Each candidate action is scored by the expectation of what follows it, taken over the
/// conditions the recipe can actually roll and weighted by the fitted probabilities. This is the
/// smallest policy that genuinely uses the condition model rather than a rule of thumb, and it is
/// cheap enough to run tens of thousands of times — which is what the gate needs.</para>
///
/// <para>From a telegraph the chance node collapses to one outcome, so those steps cost a single
/// evaluation instead of eight. That is the search saving the deterministic Robust-to-Sturdy rule
/// buys, showing up in practice rather than on paper.</para>
///
/// <para>Fallible actions are weighted across both outcomes rather than assumed to land, and are
/// offered only while the gamble budget holds.</para>
/// </summary>
public sealed class ExpectimaxPolicy : ICraftPolicy
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private readonly ConditionModel model;
    private readonly int gambleBudget;
    private readonly CraftAction[] candidates;

    public ExpectimaxPolicy(CraftSim sim, QualityBound bound, ConditionModel model, int gambleBudget = 0)
    {
        this.sim          = sim;
        this.bound        = bound;
        this.model        = model;
        this.gambleBudget = gambleBudget;

        var usable = new List<CraftAction>();
        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;
            var spec = CraftActions.Spec(action);
            if (spec.CostsDelineation) continue;
            if (spec.SuccessRate < 100 && gambleBudget <= 0) continue;
            usable.Add(action);
        }
        candidates = usable.ToArray();
    }

    public string Name => gambleBudget > 0 ? $"expectimax, {gambleBudget} gambles" : "expectimax";

    public CraftAction Choose(CraftState state)
    {
        var best = CraftAction.None;
        var bestValue = double.NegativeInfinity;

        foreach (var action in candidates)
        {
            var spec = CraftActions.Spec(action);
            if (sim.Legality(state, action) != ActionLegality.Usable) continue;
            if (spec.SuccessRate < 100 && state.GamblesUsed >= gambleBudget) continue;

            double value;
            if (spec.SuccessRate < 100)
            {
                var p = spec.SuccessRate / 100.0;
                var hit  = ExpectOverConditions(state, action, true);
                var miss = ExpectOverConditions(state, action, false);
                if (double.IsNegativeInfinity(hit) || double.IsNegativeInfinity(miss)) continue;
                value = p * hit + (1 - p) * miss;
            }
            else
            {
                value = ExpectOverConditions(state, action, true);
            }

            if (value > bestValue)
            {
                bestValue = value;
                best = action;
            }
        }

        return best;
    }

    /// <summary>Expectation over the next condition, weighted by the fitted model.</summary>
    private double ExpectOverConditions(CraftState state, CraftAction action, bool succeeded)
    {
        if (model.IsTelegraphed(state.Condition))
        {
            var only = sim.Apply(state, action, model.TelegraphTarget, succeeded);
            return only.Ok ? Evaluate(only.State) : double.NegativeInfinity;
        }

        var total = 0.0;
        var weight = 0.0;

        for (var i = 0; i < model.Members.Length; i++)
        {
            var p = model.Weights[i];
            if (p <= 0) continue;

            var step = sim.Apply(state, action, model.Members[i], succeeded);
            if (!step.Ok) return double.NegativeInfinity;

            total  += p * Evaluate(step.State);
            weight += p;
        }

        return weight > 0 ? total / weight : double.NegativeInfinity;
    }

    /// <summary>
    /// Value of a resulting state, obtained by playing it out to the end.
    ///
    /// <para>A one-ply evaluator ranking states by quality is fatally myopic here, and measurably
    /// so: it banked nearly double the quality of a static macro and completed <em>none</em> of
    /// 2,000 crafts. Every quality action outranks every progress action, because progress adds no
    /// quality, so the policy never pays for completion until it can no longer afford it. That is
    /// the same collapse the bounded frontier suffered, arriving by a different route.</para>
    ///
    /// <para>The fix is a terminal signal rather than a cleverer ranking. A cheap deterministic
    /// rollout carries each candidate to a finished craft and reports whether it cleared, so
    /// completion is valued because it is reached, not because a heuristic was told to like it.</para>
    /// </summary>
    private double Evaluate(CraftState state)
    {
        if (state.Completed)
            return (state.Quality >= sim.Recipe.RequiredQuality ? 1e9 : 0) + state.Quality;

        if (state.Failed) return 0;
        if (!bound.CanStillComplete(state, sim.Recipe)) return 0;
        if (!bound.CanStillClear(state, sim.Recipe)) return 0;

        return Rollout(state);
    }

    /// <summary>
    /// Durability this state genuinely needs for the progress it has left, priced at what its
    /// current buffs and condition actually deliver rather than at the best case.
    /// </summary>
    private int RealisticProgressReserve(CraftState state)
    {
        var remaining = sim.Recipe.Difficulty - state.Progress;
        if (remaining <= 0) return 0;

        var bestPerDurability = 0.0;
        foreach (var action in candidates)
        {
            var spec = CraftActions.Spec(action);
            if (spec.ProgressEfficiency == 0 || spec.SuccessRate < 100) continue;

            var cost = sim.DurabilityCost(state, action);
            if (cost <= 0) continue;

            var rate = (double)sim.ProgressGain(state, action) / cost;
            if (rate > bestPerDurability) bestPerDurability = rate;
        }

        if (bestPerDurability <= 0) return int.MaxValue;
        return (int)Math.Ceiling(remaining / bestPerDurability);
    }

    /// <summary>
    /// Plays a state out under a fixed, cheap policy: bank quality while there is durability to
    /// spare, then spend what is reserved on finishing progress.
    ///
    /// <para>Deliberately simple. Its job is to say whether a position is winnable, not to play
    /// well — a rollout good enough to be worth optimising would cost more than the search it is
    /// serving. Conditions are assumed Normal throughout, which understates every position
    /// equally and so preserves the ordering that matters.</para>
    /// </summary>
    private double Rollout(CraftState state)
    {
        for (var guard = 0; guard < 80 && !state.IsTerminal; guard++)
        {
            var needsProgress = state.Progress < sim.Recipe.Difficulty;

            // The switch has to be judged on what this rollout can actually do, not on the
            // admissible bound's reserve. That reserve assumes best-case buffs and conditions,
            // because a bound must never under-estimate — but the rollout plays unbuffed, so
            // treating it as a threshold delays the switch until completion is impossible. Every
            // candidate then scores zero and the policy has nothing to choose between.
            var wantProgress = needsProgress && state.Durability <= RealisticProgressReserve(state);

            var chosen = CraftAction.None;
            var bestGain = -1;

            foreach (var action in candidates)
            {
                var spec = CraftActions.Spec(action);
                if (spec.SuccessRate < 100) continue;             // rollouts do not gamble
                if (sim.Legality(state, action) != ActionLegality.Usable) continue;

                var gain = wantProgress
                    ? (spec.ProgressEfficiency > 0 ? sim.ProgressGain(state, action) : -1)
                    : (spec.QualityEfficiency  > 0 ? sim.QualityGain(state, action)  : -1);

                if (gain > bestGain) { bestGain = gain; chosen = action; }
            }

            // Nothing useful left in the preferred category — try the other before giving up.
            if (chosen == CraftAction.None || bestGain <= 0)
            {
                foreach (var action in candidates)
                {
                    var spec = CraftActions.Spec(action);
                    if (spec.SuccessRate < 100) continue;
                    if (spec.ProgressEfficiency == 0) continue;
                    if (sim.Legality(state, action) != ActionLegality.Usable) continue;

                    var gain = sim.ProgressGain(state, action);
                    if (gain > bestGain) { bestGain = gain; chosen = action; }
                }
            }

            if (chosen == CraftAction.None) break;

            var step = sim.Apply(state, chosen, CraftCondition.Normal);
            if (!step.Ok) break;
            state = step.State;
        }

        // Graded, not binary. Returning cleared/not gives no gradient on a recipe requiring
        // 31,500 of 31,520 — a crude rollout never clears, every candidate scores zero, and the
        // policy has nothing to choose between. Reporting the quality a completed craft reaches
        // preserves the ordering that matters while still valuing completion above everything,
        // since an unfinished craft scores nothing at all.
        if (!state.Completed) return 0;

        var bonus = state.Quality >= sim.Recipe.RequiredQuality ? 1e9 : 0;
        return bonus + state.Quality;
    }
}

/// <summary>
/// Runs policies against sampled condition sequences and reports how often they clear.
///
/// <para>Clear rate is the only metric under a binary objective, and it is the one a player feels.
/// Every policy in a comparison is handed the <em>same</em> seeds, so the sequences each faces are
/// identical and the difference between them is policy rather than luck — a claim a comparison on
/// independent samples could not make.</para>
/// </summary>
public sealed class PolicyEvaluator
{
    private readonly CraftSim sim;
    private readonly ConditionSampler sampler;
    private readonly int maxSteps;

    public PolicyEvaluator(CraftSim sim, ConditionSampler sampler, int maxSteps = 80)
    {
        this.sim     = sim;
        this.sampler = sampler;
        this.maxSteps = maxSteps;
    }

    public sealed record Outcome(string Policy, int Trials, int Cleared, int Completed, double MeanQuality)
    {
        public double ClearRate => Trials == 0 ? 0 : (double)Cleared / Trials;
    }

    public Outcome Run(Func<ICraftPolicy> makePolicy, int trials, int seed)
    {
        var cleared = 0;
        var completed = 0;
        var qualityTotal = 0L;
        var name = makePolicy().Name;

        for (var trial = 0; trial < trials; trial++)
        {
            // Seeded per trial so every policy meets the identical condition sequence.
            var rng = new Random(seed + trial);
            var policy = makePolicy();
            var state = sim.Initial();

            for (var step = 0; step < maxSteps && !state.IsTerminal; step++)
            {
                var action = policy.Choose(state);
                if (action == CraftAction.None) break;

                var spec = CraftActions.Spec(action);
                var succeeded = spec.SuccessRate >= 100 || rng.Next(100) < spec.SuccessRate;
                var nextCondition = sampler.Next(state.Condition, rng);

                var result = sim.Apply(state, action, nextCondition, succeeded);
                if (!result.Ok) break;
                state = result.State;
            }

            if (state.Completed) completed++;
            if (sim.IsClear(state)) cleared++;
            qualityTotal += state.Quality;
        }

        return new Outcome(name, trials, cleared, completed, (double)qualityTotal / trials);
    }
}
