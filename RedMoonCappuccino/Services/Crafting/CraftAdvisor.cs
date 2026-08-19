using System;
using System.Threading.Tasks;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Turns a craft state into the one sentence a player needs.
///
/// <para>Deliberately free of any game binding, so the judgement can be tested against constructed
/// positions rather than only observed in play. Everything that touches the client lives in the
/// layer above; this decides what to say.</para>
///
/// <para>It answers a different question from the search and can afford to answer it a different
/// way. The search picks a move under a budget measured in microseconds, thousands of times per
/// craft; this speaks once per player action, seconds apart. That gap is worth a great deal — it is
/// what pays for an honest probability instead of a ranking score.</para>
/// </summary>
public sealed class CraftAdvisor
{
    private readonly CraftSim sim;
    private readonly QualityBound bound;
    private readonly ConditionModel model;
    private readonly ConditionSampler sampler;
    private readonly ExpectimaxPolicy policy;
    private readonly int gambleBudget;
    private readonly int samples;

    public CraftAdvisor(CraftSim sim, QualityBound bound, ConditionModel model,
                        int gambleBudget = 30, CraftAction[]? opening = null, int samples = DefaultSamples)
    {
        this.sim = sim;
        this.bound = bound;
        this.model = model;
        this.gambleBudget = gambleBudget;
        this.samples = samples;
        sampler = new ConditionSampler(model);
        policy = new ExpectimaxPolicy(sim, bound, model, gambleBudget, opening ?? OpeningBook.Expert);
    }

    /// <summary>
    /// How many continuations to sample before reporting a chance.
    ///
    /// <para>At roughly half a millisecond each this is some tens of milliseconds per decision,
    /// against a player acting every few seconds. Enough that a reported 0% means none of two
    /// hundred tries succeeded rather than a lucky miss, which is what the dead-craft call has to
    /// rest on.</para>
    /// </summary>
    public const int DefaultSamples = 200;

    /// <summary>Tells the advisor what the player actually did, so its scripted opening stays honest.</summary>
    public void Observe(CraftAction taken) => policy.Observe(taken);

    /// <summary>
    /// Chance this position still clears, measured rather than scored.
    ///
    /// <para>Plays the position out many times under sampled conditions and counts. The obvious
    /// cheaper answer — asking the search what it thinks a position is worth — does not work, and
    /// the failure is total rather than marginal: the search's value is a sigmoid over a single
    /// all-Normal playout, and an all-Normal expert craft caps at 14,204 against a requirement of
    /// 31,500, so it reports every position from the opening onward as hopeless. Measured over 2,000
    /// crafts it called all of them dead at step 0 while 89.9% went on to clear. It is an excellent
    /// ordering and a worthless probability.</para>
    /// </summary>
    public double Confidence(CraftState state, int seed = 0)
    {
        var recipe = sim.Recipe;

        if (state.Completed) return state.Quality >= recipe.RequiredQuality ? 1.0 : 0.0;
        if (state.Failed) return 0.0;

        // The bound is a proof, so it settles the question outright when it fires.
        if (!bound.CanStillComplete(state, recipe) || !bound.CanStillClear(state, recipe)) return 0.0;

        var cleared = 0;
        Parallel.For(0, samples, () => 0, (i, _, local) =>
        {
            var rng = new Random(unchecked(seed * 7919 + i));
            var play = new ExpectimaxPolicy(sim, bound, model, gambleBudget);
            var cursor = state;

            for (var step = 0; step < 80 && !cursor.IsTerminal; step++)
            {
                var action = play.Choose(cursor);
                if (action == CraftAction.None) break;

                var spec = CraftActions.Spec(action);
                var ok = spec.SuccessRate >= 100 || rng.Next(100) < spec.SuccessRate;
                var next = sampler.Next(cursor.Condition, rng);

                var result = sim.Apply(cursor, action, next, ok);
                if (!result.Ok) break;
                cursor = result.State;
            }

            return local + (cursor.Completed && cursor.Quality >= recipe.RequiredQuality ? 1 : 0);
        },
        local => System.Threading.Interlocked.Add(ref cleared, local));

        return cleared / (double)samples;
    }

    /// <summary>Posture bands. Calibrated against measured clear rates, not chosen for roundness.</summary>
    private const double BehindBelow = 0.35;
    private const double AheadAbove = 0.75;

    /// <summary>
    /// Below this the craft is called lost even though nothing has proved it.
    ///
    /// <para>Set from the calibration run rather than by taste: it is the point at which crafts that
    /// trip it stop clearing often enough to be worth playing on. The call is stated as a judgement
    /// and never as the proof the bound provides, because it is one.</para>
    /// </summary>
    private const double HopelessBelow = 0.02;

    /// <summary>Below this the top two actions are a coin toss, and saying otherwise would overstate.</summary>
    private const double CoinToss = 0.02;

    public CraftAdvice Advise(CraftState state, int seed = 0)
    {
        var recipe = sim.Recipe;

        if (state.Failed)
            return CraftAdvice.Refusing("The craft has failed.");

        if (state.Completed)
        {
            var made = state.Quality >= recipe.RequiredQuality;
            return new CraftAdvice
            {
                Recommended = CraftAction.None,
                Posture = made ? CraftPosture.Ahead : CraftPosture.Dead,
                ClearChance = made ? 1 : 0,
                Shortfall = Math.Max(0, recipe.RequiredQuality - state.Quality),
                Verdict = made ? "Cleared." : "Finished short.",
                Because = made
                    ? $"{state.Quality:N0} against {recipe.RequiredQuality:N0} required."
                    : $"{state.Quality:N0} of {recipe.RequiredQuality:N0} — short by "
                      + $"{recipe.RequiredQuality - state.Quality:N0}.",
            };
        }

        var shortfall = Math.Max(0, recipe.RequiredQuality - state.Quality);
        var proved = !bound.CanStillClear(state, recipe) || !bound.CanStillComplete(state, recipe);

        // The headline call. A dead craft is invisible to the player while it is still on screen:
        // at step fifteen, "behind" and "already lost" look identical on the quality bar.
        if (proved)
        {
            var ceiling = state.Quality + bound.Remaining(state, recipe);
            return new CraftAdvice
            {
                Recommended = CraftAction.None,
                Posture = CraftPosture.Dead,
                ClearChance = 0,
                Shortfall = shortfall,
                Verdict = "Stop — this craft cannot clear.",
                Because = !bound.CanStillComplete(state, recipe)
                    ? "There is not enough durability left to finish the progress bar."
                    : $"Best case from here is {ceiling:N0}, short of {recipe.RequiredQuality:N0} "
                      + $"by {recipe.RequiredQuality - ceiling:N0}.",
            };
        }

        var confidence = Confidence(state, seed);

        if (confidence < HopelessBelow)
        {
            return new CraftAdvice
            {
                Recommended = CraftAction.None,
                Posture = CraftPosture.Dead,
                ClearChance = confidence,
                Shortfall = shortfall,
                Verdict = "Almost certainly lost — consider stopping.",
                Because = confidence <= 0
                    ? $"None of {samples} played-out continuations reached {recipe.RequiredQuality:N0}."
                    : $"{confidence * 100:0.#}% of {samples} played-out continuations reached "
                      + $"{recipe.RequiredQuality:N0}. Not proof, unlike the call above it.",
            };
        }

        var ranking = policy.RankFrom(state);
        if (ranking.Best == CraftAction.None)
            return CraftAdvice.Refusing("No legal action in this position.");

        var margin = Math.Max(0, ranking.BestValue - ranking.RunnerValue);
        var posture = confidence < BehindBelow ? CraftPosture.Behind
                    : confidence > AheadAbove ? CraftPosture.Ahead
                    : CraftPosture.OnPace;

        var spec = CraftActions.Spec(ranking.Best);
        var name = CraftActions.DisplayName(ranking.Best);

        var verdict = shortfall == 0
            ? $"Quality is there — finish it with {name}."
            : spec.DurabilityCost > 0
                ? $"Spend it — {name}."
                : $"Bank it — {name}.";

        var because =
            ranking.FromOpening
                ? "Opening line; the search takes over once it is spent."
            : margin < CoinToss && ranking.Runner != CraftAction.None
                ? $"Close call — {CraftActions.DisplayName(ranking.Runner)} is worth nearly the same."
            : posture switch
            {
                CraftPosture.Behind =>
                    $"Behind: {shortfall:N0} still owed, so the advice will take risks it would refuse with a lead.",
                CraftPosture.Ahead =>
                    $"Ahead: {shortfall:N0} owed with room to spare, so the advice protects rather than presses.",
                _ => $"{shortfall:N0} quality still owed, {state.Cp} CP and {state.Durability} durability in hand.",
            };

        return new CraftAdvice
        {
            Recommended = ranking.Best,
            Runner = ranking.Runner,
            Posture = posture,
            ClearChance = confidence,
            Margin = margin,
            Shortfall = shortfall,
            CostsDelineation = spec.CostsDelineation,
            Verdict = verdict,
            Because = because,
        };
    }
}
