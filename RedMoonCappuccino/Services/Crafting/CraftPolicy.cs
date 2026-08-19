using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Openings taken from play rather than search.
///
/// <para>Searching from step one is decision paralysis: the opening of an expert craft is a
/// twenty-step commitment whose payoff arrives at the end, and no one-ply lookahead can discover
/// it. Both recorded crafts — a human macro on a standard recipe and a reconstructed expert run —
/// open the same way, so that shared prefix is treated as known and the search starts where the
/// decisions actually become live.</para>
///
/// <para>This is the horizon reduction the plan called for, in its cheapest possible form: a fixed
/// prefix rather than a learned option library, but the same idea — collapse the part of the craft
/// that is not really a decision, and spend the search where it is.</para>
/// </summary>
public static class OpeningBook
{
    /// <summary>
    /// The reconstructed expert opener: bank Inner Quiet, arm the durability engine, then take
    /// free progress while Trained Perfection is paying for it.
    /// </summary>
    public static readonly CraftAction[] Expert =
    {
        CraftAction.Reflect,
        CraftAction.TrainedPerfection,
        CraftAction.Manipulation,
        CraftAction.Groundwork,
    };

    /// <summary>The standard-recipe macro's opener, which banks quality before progress.</summary>
    public static readonly CraftAction[] Standard =
    {
        CraftAction.Reflect,
        CraftAction.Manipulation,
        CraftAction.Innovation,
    };
}


/// <summary>What a search leaf is worth, and therefore what the search is trying to do.</summary>
public enum LeafValue
{
    /// <summary>Play the position out with the community ruleset; score the quality reached.</summary>
    Rollout,

    /// <summary>Estimate the chance the position still clears; score that.</summary>
    ClearChance,
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
    private readonly CraftAction[] opening;
    private int openingCursor;

    private readonly DecisionCache? cache;
    private readonly LeafValue leaf;
    private readonly double spread;

    public ExpectimaxPolicy(CraftSim sim, QualityBound bound, ConditionModel model,
                            int gambleBudget = 0, CraftAction[]? opening = null,
                            DecisionCache? cache = null,
                            LeafValue leaf = LeafValue.ClearChance,
                            double spread = DefaultSpread)
    {
        this.leaf = leaf;
        this.spread = spread;
        this.cache = cache;
        this.opening = opening ?? Array.Empty<CraftAction>();
        this.sim          = sim;
        this.bound        = bound;
        this.model        = model;
        this.gambleBudget = gambleBudget;

        var usable = new List<CraftAction>();
        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;
            var spec = CraftActions.Spec(action);
            if (spec.SuccessRate < 100 && gambleBudget <= 0) continue;
            usable.Add(action);
        }
        candidates = usable.ToArray();
    }

    public string Name =>
        (opening.Length > 0 ? "opened + " : "")
        + (leaf == LeafValue.ClearChance ? "expectimax/clear" : "expectimax/rollout")
        + (gambleBudget > 0 ? $", {gambleBudget} gambles" : "");

    public CraftAction Choose(CraftState state)
    {
        if (openingCursor >= opening.Length && cache is not null)
            return cache.GetOrAdd(state, Decide);

        return Decide(state);
    }

    /// <summary>The two best actions in a position and what the search thinks each is worth.</summary>
    /// <param name="Best">What to play.</param>
    /// <param name="BestValue">Its value, as a chance of clearing.</param>
    /// <param name="Runner">The next best, or <see cref="CraftAction.None"/> if nothing else is legal.</param>
    /// <param name="RunnerValue">The runner-up's value.</param>
    /// <param name="FromOpening">Whether the choice came from the scripted opening rather than the search.</param>
    public readonly record struct Ranking(CraftAction Best, double BestValue,
                                          CraftAction Runner, double RunnerValue,
                                          bool FromOpening);

    /// <summary>
    /// Ranks a position without playing it.
    ///
    /// <para>Separate from <see cref="Choose"/> because an advisor has to show its work: the margin
    /// between the top two decides whether a recommendation is worth stating firmly or is a coin
    /// toss dressed up as a verdict, and <see cref="Choose"/> discards exactly that. Peeks the
    /// scripted opening rather than consuming it — this is a question, not a move.</para>
    /// </summary>
    public Ranking RankFrom(CraftState state)
    {
        for (var i = openingCursor; i < opening.Length; i++)
        {
            if (sim.Legality(state, opening[i]) != ActionLegality.Usable) continue;
            return new Ranking(opening[i], Confidence(state), CraftAction.None, 0, true);
        }

        CraftAction best = CraftAction.None, runner = CraftAction.None;
        double bestValue = double.NegativeInfinity, runnerValue = double.NegativeInfinity;

        foreach (var action in candidates)
        {
            if (!Allowed(state, action)) continue;

            var value = ScoreAction(state, action, 0);
            if (value > bestValue)
            {
                runner = best; runnerValue = bestValue;
                best = action; bestValue = value;
            }
            else if (value > runnerValue)
            {
                runner = action; runnerValue = value;
            }
        }

        if (double.IsNegativeInfinity(bestValue)) bestValue = 0;
        if (double.IsNegativeInfinity(runnerValue)) runnerValue = 0;

        return new Ranking(best, bestValue, runner, runnerValue, false);
    }

    /// <summary>
    /// Chance this position still clears, on the same curve the search steers by.
    ///
    /// <para>Reported independently of which leaf the search is configured with, because it answers
    /// a different question: the search asks what to play, this asks how the craft is going. It is
    /// what separates a craft that is behind from one that is already lost, which is the single
    /// thing a player cannot read off the quality bar.</para>
    /// </summary>
    public double Confidence(CraftState state)
    {
        if (state.Completed) return state.Quality >= sim.Recipe.RequiredQuality ? 1.0 : 0.0;
        if (state.Failed) return 0.0;
        if (!bound.CanStillComplete(state, sim.Recipe)) return 0.0;
        if (!bound.CanStillClear(state, sim.Recipe)) return 0.0;

        return ClearChance(state);
    }

    /// <summary>
    /// Keeps the scripted opening in step with what the player actually did.
    ///
    /// <para>An advisor does not get to assume its advice was taken. If the player departs from the
    /// opening the book is abandoned outright rather than resumed later — a known-good prefix is
    /// only known-good from its start, and replaying the rest of it into a position it was never
    /// written for is worse than searching.</para>
    /// </summary>
    public void Observe(CraftAction taken)
    {
        if (openingCursor >= opening.Length) return;
        openingCursor = opening[openingCursor] == taken ? openingCursor + 1 : opening.Length;
    }

    private CraftAction Decide(CraftState state)
    {
        while (openingCursor < opening.Length)
        {
            var scripted = opening[openingCursor++];
            if (sim.Legality(state, scripted) == ActionLegality.Usable) return scripted;
        }

        var best = CraftAction.None;
        var bestValue = double.NegativeInfinity;

        foreach (var action in candidates)
        {
            if (!Allowed(state, action)) continue;

            var value = ScoreAction(state, action, 0);
            if (value > bestValue) { bestValue = value; best = action; }
        }

        return best;
    }

    /// <summary>Charges left, delineation budget respected, and legal from here.</summary>
    private bool Allowed(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        if (spec.SuccessRate < 100 && state.GamblesUsed >= gambleBudget) return false;
        if (spec.CostsDelineation && sim.Player.AvailableDelineations <= 0) return false;
        return sim.Legality(state, action) == ActionLegality.Usable;
    }

    /// <summary>
    /// How deep a chain of step-neutral actions is followed inside one decision.
    ///
    /// <para>One. A continuation costs a full candidate scan, so each level of depth multiplies
    /// the branching — at four it was slow enough to abandon the run. One also matches what the
    /// actions are for: pause, then act. Chaining two specialists before doing anything is not a
    /// line worth finding, and the next decision re-evaluates from the new state anyway, so
    /// nothing is lost that the following step does not recover.</para>
    /// </summary>
    private const int MaxContinuations = 1;

    /// <summary>
    /// Value of taking an action.
    ///
    /// <para>Step-neutral actions are looked <em>through</em> rather than scored on their own.
    /// Careful Observation, Heart and Soul and Quick Innovation add no quality and no progress, so
    /// judged in isolation they are worthless and a search would never take one — which is why
    /// their use previously had to be hard-coded into the heuristic. They do not end the decision:
    /// no step passes, so what they are worth is whatever they enable. Scoring them by their best
    /// continuation is the treatment a combo gets, not a cost to budget around.</para>
    ///
    /// <para>Careful Observation also redraws the condition, so it opens a chance node; the other
    /// two leave the condition standing and simply continue.</para>
    /// </summary>
    private double ScoreAction(CraftState state, CraftAction action, int depth)
    {
        var spec = CraftActions.Spec(action);

        if (!spec.AdvancesStep && depth < MaxContinuations)
        {
            return action == CraftAction.CarefulObservation
                ? ExpectOverConditions(state, action, true, depth + 1)
                : ContinueFrom(sim.Apply(state, action, state.Condition), depth + 1);
        }

        if (spec.SuccessRate < 100)
        {
            var p = spec.SuccessRate / 100.0;
            var hit  = ExpectOverConditions(state, action, true, depth);
            var miss = ExpectOverConditions(state, action, false, depth);
            if (double.IsNegativeInfinity(hit) || double.IsNegativeInfinity(miss))
                return double.NegativeInfinity;
            return p * hit + (1 - p) * miss;
        }

        return ExpectOverConditions(state, action, true, depth);
    }

    /// <summary>Best action available after a continuation, or the state's own value if none is.</summary>
    private double ContinueFrom(StepResult step, int depth)
    {
        if (!step.Ok) return double.NegativeInfinity;
        if (depth >= MaxContinuations) return Evaluate(step.State);

        var best = double.NegativeInfinity;
        foreach (var action in candidates)
        {
            if (!Allowed(step.State, action)) continue;
            var value = ScoreAction(step.State, action, depth);
            if (value > best) best = value;
        }

        return double.IsNegativeInfinity(best) ? Evaluate(step.State) : best;
    }


    /// <summary>Expectation over the next condition, weighted by the fitted model.</summary>
    private double ExpectOverConditions(CraftState state, CraftAction action, bool succeeded, int depth)
    {
        // The depth test belongs here as well as at the call site. Without it an exhausted
        // continuation still reports itself as neutral, continues at the same depth, and recurses
        // forever — the guard has to hold wherever the decision to look through is made.
        var neutral = !CraftActions.Spec(action).AdvancesStep && depth < MaxContinuations;

        if (model.IsTelegraphed(state.Condition))
        {
            var only = sim.Apply(state, action, model.TelegraphTarget, succeeded);
            if (!only.Ok) return double.NegativeInfinity;
            return neutral ? ContinueFrom(only, depth) : Evaluate(only.State);
        }

        var total = 0.0;
        var weight = 0.0;

        for (var i = 0; i < model.Members.Length; i++)
        {
            var p = model.Weights[i];
            if (p <= 0) continue;

            var step = sim.Apply(state, action, model.Members[i], succeeded);
            if (!step.Ok) return double.NegativeInfinity;

            total  += p * (neutral ? ContinueFrom(step, depth) : Evaluate(step.State));
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
        {
            var made = state.Quality >= sim.Recipe.RequiredQuality;
            return leaf == LeafValue.ClearChance
                ? (made ? 1.0 : 0.0)
                : (made ? 1e9 : 0) + state.Quality;
        }

        if (state.Failed) return 0;
        if (!bound.CanStillComplete(state, sim.Recipe)) return 0;
        if (!bound.CanStillClear(state, sim.Recipe)) return 0;

        return leaf == LeafValue.ClearChance ? ClearChance(state) : Rollout(state);
    }

    /// <summary>
    /// How much quality separates a position that probably misses from one that probably clears.
    ///
    /// <para>Both ends fail in their own way: too tight and the curve is a step, so every candidate
    /// scores 0 or 1 and there is nothing to climb; too loose and it straightens back out into
    /// expected quality, which is the risk-neutral objective it exists to replace. Swept against
    /// two independent seed blocks, 600, 700 and 900 all clear 89-91% while 500 and 1,100 fall to
    /// roughly 80% — a plateau rather than a spike, so this sits in the middle of it and has room
    /// to drift in either direction before it matters.</para>
    /// </summary>
    public const double DefaultSpread = 700.0;

    /// <summary>
    /// Chance this position still clears, on a smooth curve through the requirement.
    ///
    /// <para>This is the whole of the adaptive behaviour, and it is a curve rather than a rule.
    /// A sigmoid centred on the requirement is convex below it and concave above, so one search
    /// both protects a lead and throws a craft at the wall when it is behind — because at 20,000
    /// against a requirement of 31,500, a tidy finish and a ruined craft score exactly the same,
    /// and only variance crosses the line. Expected quality cannot say that: it takes the safe
    /// 20,000 over a coin flip between 5,000 and 31,520, which is precisely backwards under a
    /// threshold, and it is why the previous evaluator banked a comfortable median and cleared
    /// almost nothing.</para>
    ///
    /// <para>No playout. The rollout this replaces was <see cref="HeuristicPolicy"/>, which
    /// abandons a craft at step eleven holding 456 of 771 CP and banks 3,129 quality — every leaf
    /// value the search ranked by came from a policy that cannot play the recipe, evaluated in an
    /// all-Normal world where the requirement is unreachable by more than a factor of two.</para>
    /// </summary>
    private double ClearChance(CraftState state)
    {
        var reached = PlayOut(state);
        if (reached < 0) return 0;

        var margin = (reached - sim.Recipe.RequiredQuality) / spread;
        return 1.0 / (1.0 + Math.Exp(-margin));
    }

    // A quality tiebreaker inside the saturated regions was tried here and removed. It is largest
    // exactly where the curve has flattened to nearly zero — the positions that almost certainly
    // miss — and ordering those by the quality they reach is the risk-neutral behaviour this whole
    // objective exists to get rid of: at 20,000 against a requirement of 31,500 the policy should
    // be indifferent, and therefore free to gamble. It cost eight points of clear rate.

    /// <summary>
    /// Plays a state to a finish with the community ruleset and reports what it reached.
    ///
    /// <para>The hand-written rollout this replaces could not finish an expert recipe — unbuffed
    /// Groundwork returns 60.6 progress per point of durability, so a full bar bought 3,639
    /// against 11,250 owed. It therefore returned zero for every candidate and the search had
    /// nothing to rank. <see cref="HeuristicPolicy"/> cycles durability and lays down Veneration
    /// the way play actually does, and completes most crafts, so the same call now carries a real
    /// terminal signal.</para>
    ///
    /// <para>Graded rather than binary: on a recipe requiring 31,500 of 31,520 a clear is too rare
    /// to steer by, so a completed craft is scored by the quality it reached, with clearing worth
    /// a decisive bonus on top.</para>
    ///
    /// <para>Conditions are assumed Normal throughout. That understates every candidate equally,
    /// which is what preserves the ordering while keeping one playout cheap.</para>
    /// </summary>
    private double Rollout(CraftState state)
    {
        var reached = PlayOut(state);
        if (reached < 0) return 0;

        var bonus = reached >= sim.Recipe.RequiredQuality ? 1e9 : 0;
        return bonus + reached;
    }

    /// <summary>
    /// Plays the position to a finish and reports the quality reached, or -1 if it never completed.
    /// </summary>
    private int PlayOut(CraftState state)
    {
        var playout = new HeuristicPolicy(sim, bound, gambleBudget);

        for (var guard = 0; guard < 80 && !state.IsTerminal; guard++)
        {
            var action = playout.Choose(state);
            if (action == CraftAction.None) break;

            var step = sim.Apply(state, action, CraftCondition.Normal);
            if (!step.Ok) break;
            state = step.State;
        }

        return state.Completed ? state.Quality : -1;
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

    /// <summary>
    /// Runs a policy across independent trials.
    ///
    /// <para>Parallel because the trials genuinely are independent — each seeds its own generator
    /// and builds its own policy — and because this is a tuning loop where the wall clock is the
    /// thing actually limiting how many ideas get tested.</para>
    /// </summary>
    public Outcome Run(Func<ICraftPolicy> makePolicy, int trials, int seed)
    {
        var cleared = 0;
        var completed = 0;
        long qualityTotal = 0;
        var name = makePolicy().Name;

        Parallel.For(0, trials, () => (Cleared: 0, Completed: 0, Quality: 0L),
            (trial, _, local) =>
            {
                // Seeded per trial so every policy meets the identical condition sequence,
                // regardless of the order threads happen to run them in.
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

                return (local.Cleared + (sim.IsClear(state) ? 1 : 0),
                        local.Completed + (state.Completed ? 1 : 0),
                        local.Quality + state.Quality);
            },
            local =>
            {
                Interlocked.Add(ref cleared, local.Cleared);
                Interlocked.Add(ref completed, local.Completed);
                Interlocked.Add(ref qualityTotal, local.Quality);
            });

        return new Outcome(name, trials, cleared, completed, (double)qualityTotal / trials);
    }
}
