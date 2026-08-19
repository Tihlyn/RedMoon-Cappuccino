using System;
using System.Collections.Generic;
using System.Linq;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// A bounded-frontier search that scales to real recipes, where the exact solver does not.
///
/// <para><strong>Why the exact solver is not enough.</strong> On a real standard recipe — 771 CP,
/// 35 durability — the admissible bound saturates at maximum quality, because maximum quality
/// genuinely is reachable. The bound is correct and completely non-binding, so the feasibility
/// filter prunes nothing near the root and depth-first search burns eight million nodes without
/// reaching a single terminal state. Exhaustive search is the wrong tool at this size, not a
/// tool that needs a bigger budget.</para>
///
/// <para><strong>What this does instead.</strong> It advances a frontier one step at a time,
/// keeping only the most promising <see cref="width"/> states at each level. That trades the
/// optimality guarantee for a runtime that depends on width rather than on depth, and the
/// exchange is measurable: the exact solver remains the oracle on small recipes, so the beam can
/// be checked against a known optimum rather than merely trusted.</para>
///
/// <para>States are ranked by quality already banked plus the admissible bound on what remains.
/// A loose bound is still a useful <em>ordering</em> even when it is useless as a filter, which
/// is what makes the saturated case survivable.</para>
///
/// <para>Deterministic by construction: ties break on a total order over the state's own fields,
/// never on enumeration order, so the same inputs always give the same line.</para>
/// </summary>
public sealed class FrontierSolver
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private readonly int width;
    private readonly int maxSteps;
    private readonly int gambleBudget;
    private readonly CraftAction[] candidates;

    /// <param name="width">
    /// States carried between levels. Measured on a real standard recipe: 6,000 returns 13,217
    /// and 24,000 returns 14,204 — the maximum, and the same outcome a human macro achieved with
    /// three Good rolls helping it. The search was width-limited rather than heuristic-limited,
    /// so the default is set where the answer stops improving rather than where it first looks
    /// reasonable. Lower it only with a measurement in hand.
    /// </param>
    /// <param name="gambleBudget">
    /// Fallible casts a line may make. Zero keeps the search strictly certain, which is the right
    /// setting for a baseline. Above zero it admits Rapid Synthesis and the fallible touches,
    /// which real expert play leans on heavily — at this recipe's numbers Rapid Synthesis returns
    /// 84 progress per durability at coin-flip odds and 126 under Centered, against Groundwork's
    /// 60 for 18 CP, so refusing them outright is expensive.
    ///
    /// <para>Capped rather than free because each gamble is a chance node: without a limit the
    /// branching doubles wherever a fallible action is legal. Bounding it also bounds the error,
    /// and bounds it downward — the result is the best line using at most this many gambles,
    /// never an over-estimate of the unrestricted optimum.</para>
    /// </param>
    public FrontierSolver(CraftSim sim, QualityBound bound, int width = 24000, int maxSteps = 60,
                          int gambleBudget = 0)
    {
        this.sim          = sim;
        this.bound        = bound;
        this.width        = width;
        this.maxSteps     = maxSteps;
        this.gambleBudget = gambleBudget;

        var usable = new List<CraftAction>();
        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;
            var spec = CraftActions.Spec(action);
            if (spec.SuccessRate < 100 && gambleBudget <= 0) continue;
            if (spec.CostsDelineation && sim.Player.AvailableDelineations <= 0) continue;
            if (spec.RequiresGoodCondition) continue;  // never legal under all-Normal
            usable.Add(action);
        }
        candidates = usable.ToArray();
    }

    private sealed record Node(CraftState State, List<CraftAction> Line);

    public SolveResult Solve(CraftState? from = null)
    {
        var start = from ?? sim.Initial();
        var frontier = new List<Node> { new(start, new List<CraftAction>()) };

        Node? best = null;
        var expanded = 0;

        // Levels are counted in actions, not steps. Specialist actions are step-neutral, so a
        // craft that uses all five charges needs five levels more than it has steps — budgeting by
        // steps alone truncates those lines mid-craft.
        var levels = maxSteps + CraftActions.CarefulObservationCharges
                              + CraftActions.HeartAndSoulCharges
                              + CraftActions.QuickInnovationCharges
                              + CraftActions.TrainedPerfectionCharges;

        for (var depth = 0; depth < levels && frontier.Count > 0; depth++)
        {
            var next = new List<Node>();

            foreach (var node in frontier)
            {
                foreach (var action in candidates)
                {
                    if (CraftActions.Spec(action).SuccessRate < 100
                        && node.State.GamblesUsed >= gambleBudget) continue;

                    var step = sim.Apply(node.State, action, CraftCondition.Normal);
                    if (!step.Ok) continue;
                    if (step.State.Equals(node.State)) continue;

                    expanded++;

                    var line = new List<CraftAction>(node.Line) { action };
                    var child = new Node(step.State, line);

                    if (step.State.Completed)
                    {
                        // Only a completed craft counts, and only quality separates them.
                        if (best is null || step.State.Quality > best.State.Quality)
                            best = child;
                        continue;
                    }

                    if (step.State.Failed) continue;

                    // Two distinct ways to be dead, and the progress one matters more here.
                    // A saturated quality bound clears everything, so without the completion
                    // test the frontier fills with states that banked quality and can no longer
                    // finish — none of which score anything at all.
                    if (!bound.CanStillComplete(step.State, sim.Recipe)) continue;
                    if (!bound.CanStillClear(step.State, sim.Recipe)) continue;

                    next.Add(child);
                }
            }

            frontier = Trim(next);
        }

        return new SolveResult
        {
            Quality       = best?.State.Quality ?? -1,
            Actions       = best?.Line ?? new List<CraftAction>(),
            Cleared       = best is not null && best.State.Quality >= sim.Recipe.RequiredQuality,
            NodesExpanded = expanded,

            // A beam never proves optimality. Saying so is the point: the caller has to know it
            // is holding a good line rather than the best one.
            Exhaustive    = false,
        };
    }

    /// <summary>
    /// Keeps the best states, stratified by how far progress has come.
    ///
    /// <para>A single ranked cut collapses. Because a good line banks quality first and finishes
    /// progress last — which is exactly what the recorded human macro does — every state that
    /// spends durability on progress ranks below one that spent it on quality, and is cut. By the
    /// time the quality-greedy states run out of room to finish, the lines that would have
    /// finished were discarded twenty levels earlier, and the search returns nothing at all.</para>
    ///
    /// <para>Bucketing by progress keeps a share of the beam in each phase of the craft, so the
    /// switch to progress is always represented rather than having to out-rank quality on
    /// quality's own terms.</para>
    /// </summary>
    private List<Node> Trim(List<Node> nodes)
    {
        if (nodes.Count == 0) return nodes;

        var seen = new Dictionary<CraftState, Node>(nodes.Count);
        foreach (var node in nodes)
        {
            // Shorter lines win ties: same state, fewer actions, strictly better.
            if (!seen.TryGetValue(node.State, out var existing) || node.Line.Count < existing.Line.Count)
                seen[node.State] = node;
        }

        if (seen.Count <= width) return seen.Values.ToList();

        const int Buckets = 4;
        var share = Math.Max(1, width / Buckets);
        var kept = new List<Node>(width);

        var byBucket = seen.Values.GroupBy(n => Bucket(n.State, Buckets)).OrderBy(g => g.Key);
        var leftovers = new List<Node>();

        foreach (var group in byBucket)
        {
            var ranked = group.OrderByDescending(Score).ToList();
            kept.AddRange(ranked.Take(share));
            leftovers.AddRange(ranked.Skip(share));
        }

        // Buckets that were under-full give their allowance back to the strongest states overall,
        // so stratification never costs width when the craft is genuinely in one phase.
        if (kept.Count < width)
            kept.AddRange(leftovers.OrderByDescending(Score).Take(width - kept.Count));

        return kept;
    }

    private int Bucket(CraftState state, int buckets)
    {
        var difficulty = Math.Max(1, sim.Recipe.Difficulty);
        return Math.Clamp(state.Progress * buckets / difficulty, 0, buckets - 1);
    }

    private double Score(Node node)
    {
        var state = node.State;

        // The admissible bound saturates on real recipes, so it cannot order anything on its own.
        // What separates states there is how much quality-bearing work the remaining durability
        // can still carry at the Inner Quiet actually held — an estimate, not a bound, which is
        // allowed because only the filter has to stay admissible.
        var perTouch = (double)sim.BaseQuality * 100 * ((state.InnerQuiet + 10) * 10) * 4 / 40000;
        var castsLeft = state.Durability / 10.0;

        return state.Quality + castsLeft * perTouch + state.Cp * 0.01;
    }
}
