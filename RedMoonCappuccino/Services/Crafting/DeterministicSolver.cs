using System;
using System.Collections.Generic;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>Outcome of a deterministic solve.</summary>
public sealed record SolveResult
{
    /// <summary>Best final quality reachable, or -1 when no line completes the craft.</summary>
    public required int Quality { get; init; }

    /// <summary>The line achieving it, in order.</summary>
    public required IReadOnlyList<CraftAction> Actions { get; init; }

    public required bool Cleared { get; init; }
    public required int NodesExpanded { get; init; }

    /// <summary>
    /// False when the node budget ran out. The answer is then a lower bound on what is
    /// achievable, not the optimum — reported rather than hidden, because a solver that
    /// quietly returns a truncated search is indistinguishable from one that is simply wrong.
    /// </summary>
    public required bool Exhaustive { get; init; }
}

/// <summary>
/// The Phase 1 baseline: an exact solver for the deterministic all-Normal craft.
///
/// <para><strong>What it is for.</strong> Two things, from one build. It is the yardstick every
/// later phase measures against — an adaptive solver that cannot beat this on the deterministic
/// case has a bug, not an insight. And it is where the admissible bound gets exercised and
/// proven before anything depends on it.</para>
///
/// <para><strong>Why memoised recursion is exact.</strong> The step counter strictly increases
/// and the step-neutral actions each decrement a charge, so the state graph is acyclic. On a DAG
/// there is no fixed point to iterate towards: one memoised pass returns the true optimum for
/// every state it visits, with no value iteration and no discount factor.</para>
///
/// <para><strong>Pruning.</strong> Only the static feasibility filter is used — a branch whose
/// best case cannot reach <see cref="targetQuality"/> is dropped. Being independent of search
/// order, it composes with memoisation; a best-so-far bound would not, since it would leave the
/// table holding bounds rather than exact values.</para>
/// </summary>
public sealed class DeterministicSolver
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private int targetQuality;
    private readonly int nodeLimit;

    /// <summary>
    /// What is known about a state, as an interval rather than a value.
    ///
    /// <para>A probe under target T does not learn the true optimum — pruning stops it short —
    /// so a plain value table cannot be reused when the target moves. Recording what was
    /// <em>proven</em> instead makes the table valid for every target: <c>Low</c> is a line that
    /// definitely exists, <c>High</c> a value definitely out of reach. The true optimum lies in
    /// between, and successive probes narrow the interval rather than restarting.</para>
    /// </summary>
    private struct Bounds
    {
        public int Low;            // proven achievable
        public int High;           // proven strictly greater than the true optimum
        public bool Dead;          // proven that no line completes the craft at all
        public bool Exact;         // searched without pruning, so Low is the true optimum
        public CraftAction Choice; // action attaining Low
    }

    private readonly Dictionary<CraftState, Bounds> memo = new();

    private int nodes;
    private bool exhausted;

    /// <summary>
    /// Actions worth branching on. Trimming this is the single biggest lever on search size —
    /// the cost of a wider set compounds with depth — and every exclusion below is one that
    /// cannot cost optimality on a deterministic all-Normal craft.
    /// </summary>
    private readonly CraftAction[] candidates;

    /// <param name="targetQuality">
    /// Quality a line must be able to reach to be worth exploring. Pass the recipe's requirement
    /// to search only for clears — the binary objective's natural setting — or 0 to disable
    /// pruning entirely, which is what an external comparison against another solver needs.
    /// </param>
    public DeterministicSolver(CraftSim sim, QualityBound bound, int targetQuality, int nodeLimit = 4_000_000)
    {
        this.sim           = sim;
        this.bound         = bound;
        this.targetQuality = targetQuality;
        this.nodeLimit     = nodeLimit;

        var usable = new List<CraftAction>();
        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;

            var spec = CraftActions.Spec(action);

            // Fallible actions are excluded outright rather than assumed to land. Treating an
            // 85% action as certain would inflate the baseline every later phase is measured
            // against, which is worse than leaving a little value on the table.
            if (spec.SuccessRate < 100) continue;

            // Specialist actions cost a Crafter's Delineation. A baseline that spends real
            // currency is not a baseline.
            if (spec.CostsDelineation) continue;

            // Conditions never vary here, so anything gated on Good is dead weight in the
            // branching factor. Legality would reject them at every node anyway.
            if (spec.RequiresGoodCondition) continue;

            usable.Add(action);
        }
        candidates = usable.ToArray();
    }

    /// <summary>
    /// Collapses distinctions the transition function cannot see. Only Basic Touch, Standard
    /// Touch and Observe matter as predecessors — they are the combo sources — so every other
    /// value of <see cref="CraftState.PreviousAction"/> splits the memo into duplicates of one
    /// another. Observe belongs here because it discounts Advanced Touch exactly as Standard
    /// Touch does; collapsing it would hide that line from the search entirely.
    /// </summary>
    private static CraftState Normalize(CraftState state) =>
        state.PreviousAction is CraftAction.BasicTouch or CraftAction.StandardTouch or CraftAction.Observe
            ? state
            : state with { PreviousAction = CraftAction.None };

    /// <summary>
    /// Highest quality reachable, found by repeated feasibility searches rather than one
    /// unpruned enumeration.
    ///
    /// <para>The feasibility filter only bites when a target is set: asking for the maximum
    /// directly means <c>targetQuality</c> is zero, nothing prunes, and the search degenerates
    /// into enumerating the whole reachable graph. Binary searching the target instead keeps
    /// every individual solve heavily pruned, and costs only a logarithmic number of them.</para>
    ///
    /// <para>Probes share one table. Because it records proven intervals rather than values,
    /// work done under one target stays valid under the next — which is what makes the repeated
    /// search cheaper than the single unpruned pass rather than many times more expensive.</para>
    /// </summary>
    public SolveResult SolveBest(CraftState? from = null)
    {
        var start = from ?? sim.Initial();

        memo.Clear();
        var feasible = Solve(start, sim.Recipe.RequiredQuality > 0 ? 1 : 0);
        if (feasible.Quality < 0) return feasible;

        var low = feasible.Quality;                                   // known reachable
        var high = start.Quality + bound.Remaining(start, sim.Recipe); // provably unreachable above
        var best = feasible;
        var probes = 0;
        var totalNodes = feasible.NodesExpanded;

        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            var probe = Solve(start, mid);
            probes++;
            totalNodes += probe.NodesExpanded;

            if (!probe.Exhaustive)
                return best with { Exhaustive = false, NodesExpanded = totalNodes };

            if (probe.Quality >= mid)
            {
                low = probe.Quality;
                best = probe;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best with { NodesExpanded = totalNodes };
    }

    public SolveResult Solve(CraftState? from, int target)
    {
        targetQuality = target;
        return Solve(from);
    }

    public SolveResult Solve(CraftState? from = null)
    {
        nodes = 0;
        exhausted = false;

        var start = from ?? sim.Initial();
        var best = Best(start);

        var line = new List<CraftAction>();
        if (best >= 0)
        {
            var cursor = start;
            while (!cursor.IsTerminal
                   && memo.TryGetValue(Normalize(cursor), out var entry)
                   && entry.Choice != CraftAction.None)
            {
                var action = entry.Choice;
                line.Add(action);
                var step = sim.Apply(cursor, action, CraftCondition.Normal);
                if (!step.Ok) break;
                cursor = step.State;
            }
        }

        return new SolveResult
        {
            Quality       = best,
            Actions       = line,
            Cleared       = best >= sim.Recipe.RequiredQuality,
            NodesExpanded = nodes,
            Exhaustive    = !exhausted,
        };
    }

    /// <summary>
    /// Best final quality reachable from <paramref name="state"/> that meets the current target,
    /// or -1 when none does. The answer is a lower bound on the true optimum whenever pruning
    /// cut the search, which is exactly what the interval table records.
    /// </summary>
    private int Best(CraftState state)
    {
        if (state.Completed) return state.Quality;
        if (state.Failed) return -1;

        state = Normalize(state);

        memo.TryGetValue(state, out var known);
        if (known.High == 0) known.High = int.MaxValue;   // default-constructed entry

        // Dead is target-independent and has to be checked first: a state proven to have no
        // completing line stays dead at every target, and without this the interval table
        // cannot express that at target zero, where High has nothing below it to record.
        if (known.Dead) return -1;

        // An unpruned search settled this state exactly; no target can learn more from it.
        // Without this, a state whose optimum happens to be zero records nothing at all and is
        // re-searched on every visit — which is what silently turned an exhaustive pass into a
        // truncated one.
        if (known.Exact) return known.Low >= targetQuality ? known.Low : -1;

        // Already know a line that meets the target, or already know none can.
        if (known.Low >= targetQuality && known.Low > 0) return known.Low;
        if (known.High <= targetQuality) return -1;

        if (nodes >= nodeLimit) { exhausted = true; return -1; }
        nodes++;

        if (targetQuality > 0 && state.Quality + bound.Remaining(state, sim.Recipe) < targetQuality)
        {
            known.High = Math.Min(known.High, targetQuality);
            memo[state] = known;
            return -1;
        }

        var best = -1;
        var bestAction = CraftAction.None;

        foreach (var action in candidates)
        {
            var step = sim.Apply(state, action, CraftCondition.Normal);
            if (!step.Ok) continue;

            // A step-neutral action that changes nothing observable would recurse forever;
            // on a DAG it cannot, but the guard makes the termination argument local.
            if (step.State.Equals(state)) continue;

            var value = Best(step.State);
            if (value > best)
            {
                best = value;
                bestAction = action;
            }
        }

        if (targetQuality <= 1 && best >= 0)
        {
            // Nothing was pruned, so this is the optimum rather than a lower bound on it.
            known.Exact  = true;
            known.Low    = best;
            known.Choice = bestAction;
        }
        else if (best >= targetQuality && best > 0)
        {
            if (best > known.Low) { known.Low = best; known.Choice = bestAction; }
        }
        else if (best < 0 && targetQuality <= 1)
        {
            // Searched with nothing to prune against and still found no completing line, so the
            // state is dead outright rather than merely short of a target.
            known.Dead = true;
        }
        else
        {
            // Nothing reached the target, so the true optimum is below it.
            known.High = Math.Min(known.High, Math.Max(targetQuality, 1));
        }

        memo[state] = known;
        return best;
    }
}
