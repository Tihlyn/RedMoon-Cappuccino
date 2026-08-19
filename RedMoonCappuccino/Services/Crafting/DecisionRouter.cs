using System;
using System.Collections.Generic;
using System.Text;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>Who owned a decision, for tracing.</summary>
public enum DecisionOwner
{
    Opener,
    Rule,
    Gamble,
    Evaluator,
    None,
}

/// <summary>One resolved decision, with the reasoning that produced it.</summary>
public readonly record struct Decision(CraftAction Action, DecisionOwner Owner, string Why);

/// <summary>
/// The solver's decision layer: one state at a time, one decision at a time.
///
/// <para><strong>What changed and why.</strong> Four evaluators failed here before this one, each
/// by trying to be a value function. Ranking by quality was myopic; a binary rollout had no
/// gradient; a graded rollout assumed Normal conditions and was optimistic about finishing; a
/// forward projection counted unspent potential and so rewarded hoarding. The common fault was
/// scope — each tried to score a whole future from a single state.</para>
///
/// <para>This one does not score futures. It inspects the state in front of it and asks who owns
/// the decision. If a rule owns it, the rule decides. If it is a gamble, the gamble resolver
/// decides on its own terms. Only what is left over reaches the evaluator, which enumerates what
/// is actually available, discards what cannot help, and takes the best of the remainder against
/// the objectives — then throws the reasoning away and re-inspects, because the action just taken
/// changed the resources, the constraints and the option set that produced it.</para>
///
/// <para>The branching that made every earlier attempt expensive never happens: each committed
/// action shrinks the future rather than multiplying it, and no decision passes through
/// unevaluated.</para>
/// </summary>
public sealed class DecisionRouter : ICraftPolicy
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private readonly ConditionModel model;
    private readonly int gambleBudget;
    private readonly CraftAction[] opening;
    private int openingCursor;

    private readonly DecisionCache? cache;

    public DecisionRouter(CraftSim sim, QualityBound bound, ConditionModel model,
                          int gambleBudget = 0, CraftAction[]? opening = null,
                          DecisionCache? cache = null)
    {
        this.cache = cache;
        this.sim          = sim;
        this.bound        = bound;
        this.model        = model;
        this.gambleBudget = gambleBudget;
        this.opening      = opening ?? Array.Empty<CraftAction>();
    }

    public string Name => gambleBudget > 0 ? $"router, {gambleBudget} gambles" : "router";

    public CraftAction Choose(CraftState state)
    {
        // The opener is stateful, so it stays outside the cache; everything after it is a pure
        // function of the position and is worth remembering.
        if (openingCursor < opening.Length) return Resolve(state).Action;

        return cache is null
            ? Resolve(state).Action
            : cache.GetOrAdd(state, s => Resolve(s).Action);
    }

    /// <summary>The decision and its owner, so a craft can be traced rather than guessed at.</summary>
    public Decision Resolve(CraftState state)
    {
        // 1. The opener is not a decision; it is a commitment already made.
        while (openingCursor < opening.Length)
        {
            var scripted = opening[openingCursor++];
            if (Usable(state, scripted)) return new(scripted, DecisionOwner.Opener, "opening book");
        }

        // 2. Rules own what published play does not deliberate over.
        var ruled = Rule(state);
        if (ruled.Action != CraftAction.None) return ruled;

        // 3. Gambles are somebody else's arithmetic.
        var gamble = Gamble(state);
        if (gamble.Action != CraftAction.None) return gamble;

        // 4. Everything else is evaluated here, and nothing is allowed past unevaluated.
        return Evaluate(state);
    }

    // ── 2. Rules ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Decisions with a settled answer. Each is a case where searching would spend effort to
    /// rediscover what published guidance already agrees on, and where the condition on offer is
    /// what makes the answer obvious rather than merely reasonable.
    /// </summary>
    private Decision Rule(CraftState state)
    {
        var owed = sim.Recipe.Difficulty - state.Progress;

        // Pliant halves the cost of the most expensive thing worth buying.
        if (state.Condition == CraftCondition.Pliant
            && !state.HasBuff(CraftBuff.Manipulation)
            && Usable(state, CraftAction.Manipulation))
            return new(CraftAction.Manipulation, DecisionOwner.Rule, "Pliant halves Manipulation");

        // Primed adds two steps to whatever status comes next, so lay one down now.
        if (state.Condition == CraftCondition.Primed
            && !state.HasBuff(CraftBuff.Innovation)
            && Usable(state, CraftAction.Innovation))
            return new(CraftAction.Innovation, DecisionOwner.Rule, "Primed extends Innovation");

        // A full Inner Quiet bar under Great Strides is the largest touch the craft will offer.
        if (state.InnerQuiet >= CraftActions.MaxInnerQuiet
            && state.HasBuff(CraftBuff.GreatStrides)
            && Usable(state, CraftAction.ByregotsBlessing))
            return new(CraftAction.ByregotsBlessing, DecisionOwner.Rule, "cash a full bar");

        // Durability about to run out with progress still owed is not a judgement call.
        if (owed > 0 && state.Durability <= 10)
        {
            if (Usable(state, CraftAction.ImmaculateMend))
                return new(CraftAction.ImmaculateMend, DecisionOwner.Rule, "durability critical");
            if (Usable(state, CraftAction.MastersMend))
                return new(CraftAction.MastersMend, DecisionOwner.Rule, "durability critical");
        }

        return new(CraftAction.None, DecisionOwner.None, "");
    }

    // ── 3. Gambles ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fallible actions, judged on their own terms: expected payoff against what a miss costs,
    /// and only where the odds are improved enough to be worth the durability.
    /// </summary>
    private Decision Gamble(CraftState state)
    {
        if (state.GamblesUsed >= gambleBudget) return new(CraftAction.None, DecisionOwner.None, "");

        var owed = sim.Recipe.Difficulty - state.Progress;
        if (owed <= 0) return new(CraftAction.None, DecisionOwner.None, "");

        if (!Usable(state, CraftAction.RapidSynthesis)) return new(CraftAction.None, DecisionOwner.None, "");

        // Centered lifts the odds from a coin flip to three in four, which is what makes the
        // trade worth taking rather than merely positive.
        var success = state.Condition == CraftCondition.Centered ? 0.75 : 0.50;

        var expected = sim.ProgressGain(state, CraftAction.RapidSynthesis) * success;
        var safe = SafestProgress(state, out var safeGain);

        if (safe != CraftAction.None && expected <= safeGain)
            return new(CraftAction.None, DecisionOwner.None, "");

        return new(CraftAction.RapidSynthesis, DecisionOwner.Gamble,
                   $"E[{expected:0}] beats safe {safeGain}");
    }

    private CraftAction SafestProgress(CraftState state, out int gain)
    {
        var best = CraftAction.None;
        gain = 0;

        foreach (var action in CraftActions.All)
        {
            var spec = CraftActions.Spec(action);
            if (spec.ProgressEfficiency == 0 || spec.SuccessRate < 100) continue;
            if (!Usable(state, action)) continue;

            var value = sim.ProgressGain(state, action);
            if (value > gain) { gain = value; best = action; }
        }

        return best;
    }

    // ── 4. Evaluation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Everything not already owned. Enumerate what is available, discard what cannot help, and
    /// take the best of the rest.
    ///
    /// <para>Value is a ratio on both sides: what fraction of the remaining <em>need</em> an action
    /// satisfies, against what fraction of the remaining <em>resources</em> it consumes. That is
    /// what an earlier projection got wrong by scoring levels instead — a level that counts unspent
    /// potential is maximised by never spending it, so the policy hoarded. A ratio has no such
    /// fixed point: standing still satisfies nothing.</para>
    /// </summary>
    private Decision Evaluate(CraftState state)
    {
        var owed = Math.Max(0, sim.Recipe.Difficulty - state.Progress);
        var urgent = owed > 0 && state.Durability <= ProgressReserve(state) + UrgencyMargin;

        // Everything is priced in one currency: quality, per point of durability spent to get it.
        //
        // Subtracting a cost ratio from a gain ratio does not work, and the trace showed exactly
        // how it fails. A touch gains 918 of 29,970 still needed — 0.03 — while spending 10 of 50
        // durability, which is 0.20. Every touch scores negative and only the zero-durability
        // buffs survive, so the craft banks its opener and nothing else. The two ratios were never
        // on the same scale.
        //
        // Durability is the resource that limits how many actions a craft gets, so it is the
        // denominator. A status produces no quality itself but buys quality later, and that is
        // expressed in touches it is worth rather than left as an arbitrary constant.
        var perTouch = BestTouchQuality(state);

        var best = CraftAction.None;
        var bestScore = double.NegativeInfinity;
        var bestWhy = "";

        foreach (var action in CraftActions.All)
        {
            if (action == CraftAction.None) continue;
            if (!Usable(state, action)) continue;
            if (CraftActions.Spec(action).SuccessRate < 100) continue;   // gambles already offered
            if (Redundant(state, action)) continue;

            var quality = sim.QualityGain(state, action);
            var progress = sim.ProgressGain(state, action);

            // A binding constraint does not compete on value, it preempts. Scoring worth per
            // durability structurally favours a cheap touch over an expensive progress action, so
            // "urgent" progress never actually won and the craft banked quality until it could no
            // longer finish. Once the debt is at risk, quality is simply off the table until it is
            // not — the only thing that scores is a craft that completes.
            if (urgent && progress == 0 && !RestoresDurability(action)) continue;
            var worth = quality + EnablingQuality(state, action, perTouch);

            // Progress is a constraint, not an objective: exactly Difficulty is needed and nothing
            // beyond it scores. While the debt is comfortably payable it barely competes; once
            // durability nears the reserve it needs, it outranks everything.
            if (progress > 0 && owed > 0)
                worth += perTouch * (urgent ? UrgentProgressWeight : IdleProgressWeight)
                       * Math.Min(1.0, progress / (double)owed);

            if (worth <= 0) continue;

            var durability = sim.DurabilityCost(state, action);
            var cp = sim.CpCost(state, action);

            // CP owed to the progress debt is not available to spend on quality. Without this the
            // craft banks quality beautifully and then cannot finish: the trace reached step 19
            // holding 53 CP with 7,594 progress still owed, having spent the rest on statuses.
            // Quality that never completes is worth nothing at all.
            if (progress == 0 && cp > 0 && state.Cp - cp < CpOwedToProgress(state))
                continue;

            // CP is not the binding resource here, but spending it all strands the craft, so it
            // shows as a mild tax rather than a second denominator.
            var score = worth / (durability + DurabilityFloor);

            // Charge the action against the pool. An action that leaves the craft unable to reach
            // the end is not a cheap action, whatever its rate looks like — this is what stops a
            // policy spending big on a combo it cannot afford to follow through, and what makes
            // small steps and zero-CP gambles attractive when the budget is tight.
            var after = state with
            {
                Durability = state.Durability - durability,
                Cp = state.Cp - cp,
            };

            var slack = Runway(after) - StepsNeeded(after, perTouch) - SolvencyMargin;
            if (slack < 0) score *= 1.0 / (1.0 + InsolvencyPenalty * -slack);

            if (score > bestScore)
            {
                bestScore = score;
                best = action;
                bestWhy = $"{worth:0}q per {durability}d";
            }
        }

        return new(best, best == CraftAction.None ? DecisionOwner.None : DecisionOwner.Evaluator, bestWhy);
    }

    /// <summary>
    /// Steps the craft can still afford, taking durability and CP as one pool rather than two.
    ///
    /// <para>They are separate meters but a single constraint: whichever runs dry first ends the
    /// craft, so the runway is the smaller of the two and spending either is spending the same
    /// budget. Scoring them independently is what let a policy buy six repairs — each looked
    /// affordable against CP alone while the durability they bought was never going to be used.</para>
    ///
    /// <para>Manipulation is counted because it is durability already paid for and still arriving.</para>
    /// </summary>
    private double Runway(CraftState state)
    {
        var durability = (double)state.Durability;
        if (state.HasBuff(CraftBuff.Manipulation))
            durability += state.Buff(CraftBuff.Manipulation) * CraftActions.ManipulationRestore;

        return Math.Min(durability / AverageDurabilityPerStep,
                        state.Cp / AverageCpPerStep);
    }

    /// <summary>
    /// Steps still required to finish: what progress owes plus what quality is short, each at the
    /// best rate this state can deliver.
    /// </summary>
    private double StepsNeeded(CraftState state, int perTouch)
    {
        var owed = Math.Max(0, sim.Recipe.Difficulty - state.Progress);
        var short_ = Math.Max(0, sim.Recipe.RequiredQuality - state.Quality);

        SafestProgress(state, out var perProgress);

        var progressSteps = owed > 0 && perProgress > 0 ? owed / (double)perProgress : 0;
        var qualitySteps = short_ > 0 && perTouch > 0 ? short_ / (double)perTouch : 0;

        return progressSteps + qualitySteps;
    }

    /// <summary>Typical durability a step costs once halvings and Manipulation are averaged in.</summary>
    private const double AverageDurabilityPerStep = 7.0;

    /// <summary>Typical CP a step costs across touches, buffs and the free progress actions.</summary>
    private const double AverageCpPerStep = 20.0;

    /// <summary>Steps of slack the craft aims to still hold when it finishes.</summary>
    private const double SolvencyMargin = 2.0;

    /// <summary>Quality the strongest touch available would produce right now, the unit everything is priced in.</summary>
    private int BestTouchQuality(CraftState state)
    {
        var best = 0;
        foreach (var action in CraftActions.All)
        {
            if (CraftActions.Spec(action).QualityEfficiency == 0) continue;
            if (CraftActions.Spec(action).SuccessRate < 100) continue;
            if (!Usable(state, action)) continue;

            var value = sim.QualityGain(state, action);
            if (value > best) best = value;
        }
        return best > 0 ? best : sim.BaseQuality;
    }

    /// <summary>
    /// What a status is worth in quality it will buy later, rather than as a bare constant.
    /// Innovation and Great Strides multiply the touches that follow; Manipulation and the mends
    /// buy durability, and durability is measured in touches.
    /// </summary>
    private double EnablingQuality(CraftState state, CraftAction action, int perTouch) => action switch
    {
        // +50% across roughly two touches before it lapses.
        CraftAction.Innovation => perTouch * 1.0,

        // +100% on the single touch that consumes it.
        CraftAction.GreatStrides => state.InnerQuiet >= 5 ? perTouch * 1.0 : perTouch * 0.2,

        // Eight steps of restoration is forty durability, four touches — but only the part that
        // fits under the bar as it drains, so it is discounted when durability is already full.
        CraftAction.Manipulation =>
            perTouch * 4.0 * (state.Durability >= sim.Recipe.Durability ? 0.5 : 1.0),

        // Worth only the durability actually recovered. Priced at the full restore they were the
        // best action in the game at full durability — the trace showed six Master's Mends cast
        // back to back into a full bar, each scoring 3,672 quality for zero durability, until the
        // CP ran out. A restore that restores nothing is worth nothing.
        CraftAction.MastersMend =>
            perTouch * (Math.Min(CraftActions.MastersMendRestore,
                                 sim.Recipe.Durability - state.Durability) / 10.0),

        CraftAction.ImmaculateMend =>
            perTouch * ((sim.Recipe.Durability - state.Durability) / 10.0),

        // Halved durability is doubled touches for as long as it runs.
        CraftAction.WasteNotII => perTouch * 2.0,
        CraftAction.WasteNot => perTouch * 1.0,

        _ => 0,
    };

    /// <summary>
    /// CP the craft must keep back to finish the progress it still owes, priced at the cheapest
    /// progress action per point of progress rather than at the strongest one.
    /// </summary>
    private int CpOwedToProgress(CraftState state)
    {
        var owed = sim.Recipe.Difficulty - state.Progress;
        if (owed <= 0) return 0;

        var bestPerCp = 0.0;
        var freeGain = 0;

        foreach (var action in CraftActions.All)
        {
            var spec = CraftActions.Spec(action);
            if (spec.ProgressEfficiency == 0 || spec.SuccessRate < 100) continue;
            if (!Usable(state, action)) continue;

            var gain = sim.ProgressGain(state, action);
            var cost = sim.CpCost(state, action);

            // A free progress action only settles the debt for nothing if the durability it needs
            // actually exists. Basic Synthesis costs no CP, which made this reserve compute as
            // zero on every craft — while finishing on it alone would take 280 durability against
            // a 60-point bar.
            if (cost == 0)
            {
                var castsNeeded = owed / (double)Math.Max(1, gain);
                var durabilityNeeded = castsNeeded * Math.Max(1, sim.DurabilityCost(state, action));
                if (durabilityNeeded <= state.Durability && gain > freeGain) freeGain = gain;
                continue;
            }

            var rate = gain / (double)cost;
            if (rate > bestPerCp) bestPerCp = rate;
        }

        if (freeGain > 0) return 0;
        if (bestPerCp <= 0) return state.Cp;

        return (int)Math.Ceiling(owed / bestPerCp);
    }

    /// <summary>Keeps zero-durability actions finite without flattering them.</summary>
    private const double DurabilityFloor = 5.0;

    /// <summary>How sharply an action is discounted for leaving the craft short of the finish.</summary>
    private const double InsolvencyPenalty = 0.6;

    /// <summary>Durability kept in hand so one bad condition cannot make the progress debt unpayable.</summary>
    private const int UrgencyMargin = 15;

    /// <summary>Weight on progress once the debt is at risk of becoming unpayable.</summary>
    private const double UrgentProgressWeight = 4.0;

    /// <summary>Weight on progress while it is still comfortably affordable.</summary>
    private const double IdleProgressWeight = 0.15;

    /// <summary>Whether an action buys durability back, which still helps while the debt is pressing.</summary>
    private static bool RestoresDurability(CraftAction action) =>
        action is CraftAction.MastersMend or CraftAction.ImmaculateMend
               or CraftAction.Manipulation or CraftAction.WasteNot or CraftAction.WasteNotII;

    /// <summary>A status already running, or a second opener. Nothing a second cast would add.</summary>
    private static bool Redundant(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        return spec.GrantedBuff != CraftBuff.None && state.HasBuff(spec.GrantedBuff);
    }

    /// <summary>
    /// What a status is worth when it produces nothing itself. Expressed on the same scale as the
    /// gain ratio so buffs compete with touches rather than being ranked separately.
    /// </summary>
    private double Enabling(CraftState state, CraftAction action) => action switch
    {
        CraftAction.Innovation   => 0.30,
        CraftAction.Veneration   => sim.Recipe.Difficulty > state.Progress ? 0.30 : 0,
        CraftAction.GreatStrides => state.InnerQuiet >= 5 ? 0.25 : 0.05,
        CraftAction.Manipulation => 0.35,
        CraftAction.WasteNotII   => 0.20,
        CraftAction.WasteNot     => 0.10,
        CraftAction.MastersMend or CraftAction.ImmaculateMend =>
            state.Durability <= 20 ? 0.30 : 0,
        _ => 0,
    };

    private int ProgressReserve(CraftState state)
    {
        var owed = sim.Recipe.Difficulty - state.Progress;
        if (owed <= 0) return 0;

        var safe = SafestProgress(state, out var gain);
        if (safe == CraftAction.None || gain <= 0) return int.MaxValue;

        var cost = Math.Max(1, sim.DurabilityCost(state, safe));
        return (int)Math.Ceiling(owed / (double)gain * cost);
    }

    /// <summary>Repairs allowed per craft. A policy cap, not a game rule.</summary>
    private const int MendBudget = 1;

    private bool Usable(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        if (spec.CostsDelineation && sim.Player.AvailableDelineations <= 0) return false;

        // Master's Mend and Immaculate Mend are the two largest CP sinks in the game, and a
        // policy that values durability will keep buying them until the CP quality needed is
        // gone — six in a row in one traced craft. One per craft, and the rest of the durability
        // has to come from Manipulation and the conditions.
        if (spec.Kind == ActionKind.Repair && state.MendsUsed >= MendBudget) return false;

        return sim.Legality(state, action) == ActionLegality.Usable;
    }

    /// <summary>
    /// Plays one craft and reports every decision with its owner.
    ///
    /// <para>Four evaluator designs were diagnosed by re-running a whole Monte Carlo and reading a
    /// single aggregate number, which says that something failed but never where. This says where.</para>
    /// </summary>
    public string Trace(ConditionSampler sampler, int seed, int maxSteps = 60)
    {
        var rng = new Random(seed);
        var state = sim.Initial();
        var log = new StringBuilder();
        openingCursor = 0;

        for (var i = 0; i < maxSteps && !state.IsTerminal; i++)
        {
            var decision = Resolve(state);
            if (decision.Action == CraftAction.None)
            {
                log.AppendLine($"   s{state.Step,-3} {state.Condition,-10} STOP (no action)");
                break;
            }

            var spec = CraftActions.Spec(decision.Action);
            var succeeded = spec.SuccessRate >= 100 || rng.Next(100) < spec.SuccessRate;
            var next = sampler.Next(state.Condition, rng);
            var result = sim.Apply(state, decision.Action, next, succeeded);
            if (!result.Ok) break;

            log.AppendLine($"   s{state.Step,-3} {state.Condition,-10} {decision.Owner,-9} "
                         + $"{decision.Action,-20} P{result.State.Progress,-6} Q{result.State.Quality,-6} "
                         + $"D{result.State.Durability,-3} C{result.State.Cp,-4}"
                         + (succeeded ? "" : " MISS") + $"  {decision.Why}");

            state = result.State;
        }

        log.AppendLine($"   final: completed={state.Completed} quality={state.Quality} "
                     + $"cleared={sim.IsClear(state)}");
        return log.ToString();
    }
}
