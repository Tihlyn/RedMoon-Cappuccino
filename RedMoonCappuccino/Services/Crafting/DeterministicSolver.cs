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
    private readonly int targetQuality;
    private readonly int nodeLimit;

    private readonly Dictionary<CraftState, int> memo = new();
    private readonly Dictionary<CraftState, CraftAction> choice = new();

    private int nodes;
    private bool exhausted;

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
    }

    public SolveResult Solve(CraftState? from = null)
    {
        memo.Clear();
        choice.Clear();
        nodes = 0;
        exhausted = false;

        var start = from ?? sim.Initial();
        var best = Best(start);

        var line = new List<CraftAction>();
        if (best >= 0)
        {
            var cursor = start;
            while (!cursor.IsTerminal && choice.TryGetValue(cursor, out var action))
            {
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

    /// <summary>Best final quality reachable from <paramref name="state"/>, or -1 if none completes.</summary>
    private int Best(CraftState state)
    {
        if (state.Completed) return state.Quality;
        if (state.Failed) return -1;

        if (memo.TryGetValue(state, out var cached)) return cached;

        if (nodes >= nodeLimit) { exhausted = true; return -1; }
        nodes++;

        // Static feasibility filter: nothing this branch can do reaches the target.
        if (targetQuality > 0 && state.Quality + bound.Remaining(state, sim.Recipe) < targetQuality)
        {
            memo[state] = -1;
            return -1;
        }

        var best = -1;
        var bestAction = CraftAction.None;

        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;

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

        memo[state] = best;
        if (bestAction != CraftAction.None) choice[state] = bestAction;
        return best;
    }
}
