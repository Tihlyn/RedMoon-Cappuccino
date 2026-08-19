using System;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>Why an action cannot be used from a given state. <see cref="Usable"/> is the only non-refusal.</summary>
public enum ActionLegality
{
    Usable,
    CraftOver,
    NotEnoughCp,
    NoDurability,
    RequiresGoodCondition,
    ForbiddenUnderWasteNot,
    FirstStepOnly,
    RequiresInnerQuiet,
    RequiresMaxInnerQuiet,
    RequiresExpedience,
    NoChargesLeft,
    InnovationAlreadyActive,
    NoDelineations,
}

/// <summary>The outcome of one simulated action.</summary>
public readonly record struct StepResult
{
    public required CraftState State { get; init; }
    public required ActionLegality Legality { get; init; }
    public int ProgressGained { get; init; }
    public int QualityGained  { get; init; }
    public int CpSpent         { get; init; }
    public int DurabilitySpent { get; init; }

    public bool Ok => Legality == ActionLegality.Usable;
}

/// <summary>
/// The bit-exact crafting simulator: Phase 0, and the piece everything else is gated behind.
///
/// <para><strong>What this is for.</strong> Every later phase measures itself against a
/// simulated craft, so a rounding rule or buff-ordering bug here does not produce a visible
/// error — it quietly degrades every recommendation the tool ever makes. That is the
/// project's top risk, and it is the reason the class is written to make each arithmetic
/// decision explicit and individually checkable rather than folded into one expression.</para>
///
/// <para><strong>Rounding.</strong> Gains are computed in <em>exact integer arithmetic</em>,
/// matching Raphael's simulator: every modifier is an integer, they multiply together, and a
/// single truncating division at the end carries the whole scale. This is not a stylistic
/// choice. The same formula written with doubles and one final floor disagrees with the exact
/// result on 0.73% of inputs — the product lands a hair below an integer and floors to the
/// value beneath it. On a recipe requiring 31,500 of 31,520 quality, an invisible off-by-one
/// on one calculation in 137 is the difference between a clear and a failure, with no error
/// and no symptom to trace.</para>
///
/// <para>Condition multipliers are therefore held on integer scales: progress in halves
/// (Malleable 3, else 2) and quality in quarters (Poor 2, Normal 4, Good 6 or 7, Excellent 16).
/// Raphael uses halves throughout, which cannot express the 1.75x Good that relic tools grant;
/// quarters can, and that case is the default for a current expert crafter.</para>
///
/// <para>Costs keep their ceilings. The CP ceiling is confirmed by the recorded corpus —
/// Observe costs 7 normally and was recorded costing 4 under Pliant, and 7/2 rounds up. The
/// durability ceiling is the community-established rule and remains unconfirmed here: every
/// recorded durability cost is 10, which halves to 5 under both ceiling and floor.</para>
///
/// <para>The simulator is deterministic. Condition randomness lives entirely in the caller,
/// which supplies the next condition; success/failure for the two fallible touches is
/// likewise a parameter. That keeps the state transition a pure function, which is what the
/// memoised search in Phase 2 needs.</para>
/// </summary>
public sealed class CraftSim
{
    private readonly RecipeSpec recipe;
    private readonly PlayerSpec player;

    /// <summary>Base progress per 100% efficiency, computed once — it depends only on stats and recipe.</summary>
    public int BaseProgress { get; }

    /// <summary>Base quality per 100% efficiency, at zero Inner Quiet and no buffs.</summary>
    public int BaseQuality { get; }

    public CraftSim(RecipeSpec recipe, PlayerSpec player)
    {
        this.recipe = recipe;
        this.player = player;

        BaseProgress = ComputeBaseProgress(recipe, player);
        BaseQuality  = ComputeBaseQuality(recipe, player);
    }

    /// <summary>
    /// A simulator whose base values are given rather than computed.
    ///
    /// <para>These two numbers are the only thing the formulas above exist to produce, and computing
    /// them needs the recipe's dividers and the player's stats to both be right. In a live craft
    /// neither can be relied on — the client reported a QualityDivider that would require 8,550
    /// control to explain a gain the game had just displayed — whereas the gain itself is sitting on
    /// screen and needs no interpretation. Where an observation is available it beats a derivation.</para>
    /// </summary>
    public CraftSim(RecipeSpec recipe, PlayerSpec player, int baseProgress, int baseQuality)
    {
        this.recipe = recipe;
        this.player = player;

        BaseProgress = baseProgress;
        BaseQuality  = baseQuality;
    }

    /// <summary>
    /// The base value implied by what an action actually paid, against what it was predicted to pay.
    ///
    /// <para>Every gain is linear in the base — efficiency, Inner Quiet, the statuses and the
    /// condition are all multipliers applied to it — so one observed action determines the base
    /// outright, whatever the action and whatever was running at the time. That is the whole reason
    /// a live craft can correct a stat line or a divider it has no way to verify.</para>
    ///
    /// <para>Returns the assumption unchanged when there is nothing to learn from: a gain of zero
    /// says only that the action was not of that kind.</para>
    /// </summary>
    public static int PinBase(int assumed, int observedGain, int predictedGain)
    {
        if (assumed <= 0 || observedGain <= 0 || predictedGain <= 0) return assumed;

        return Math.Max(1, (int)Math.Round(assumed * (double)observedGain / predictedGain));
    }

    public RecipeSpec Recipe => recipe;
    public PlayerSpec Player => player;

    public CraftState Initial() => CraftState.Initial(recipe, player);

    /// <summary>
    /// 100 (efficiency percent) x 10 (effect tenths) x 2 (condition halves).
    /// </summary>
    private const long ProgressDivisor = 2000;

    /// <summary>
    /// 100 (efficiency percent) x 10 (Inner Quiet tenths) x 10 (status tenths) x 4 (condition quarters).
    /// </summary>
    private const long QualityDivisor = 40000;

    // ── Base value formulas ───────────────────────────────────────────────────

    /// <summary>
    /// Base progress. The level modifier applies only when the crafter is below the recipe's
    /// required job level; at or above it the penalty falls away, which is the normal case for
    /// a max-level crafter on a current expert recipe.
    /// </summary>
    private static int ComputeBaseProgress(RecipeSpec recipe, PlayerSpec player)
    {
        var value = player.Craftsmanship * 10.0 / recipe.ProgressDivider + 2;
        if (player.Level < recipe.RecipeJobLevel)
            value = value * recipe.ProgressModifier / 100.0;

        return (int)Math.Floor(value);
    }

    private static int ComputeBaseQuality(RecipeSpec recipe, PlayerSpec player)
    {
        var value = player.Control * 10.0 / recipe.QualityDivider + 35;
        if (player.Level < recipe.RecipeJobLevel)
            value = value * recipe.QualityModifier / 100.0;

        return (int)Math.Floor(value);
    }

    // ── Costs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// CP cost after the Pliant halving. Ceiling, confirmed by the corpus: Observe's 7 CP was
    /// recorded costing 4 under Pliant.
    /// </summary>
    public int CpCost(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        var cost = CraftActions.ComboCost(action, state.PreviousAction) ?? spec.CpCost;

        if (state.Condition == CraftCondition.Pliant)
            cost = (int)Math.Ceiling(cost / 2.0);

        return cost;
    }

    /// <summary>
    /// Durability cost after Waste Not and the Sturdy/Robust discount, and after Trained
    /// Perfection zeroes it entirely.
    ///
    /// The two halvings are applied in sequence with a ceiling after each, which is the
    /// community-established rule but is one of the entries this project's own data cannot
    /// yet confirm — the recorded corpus contains no durability-spending action.
    /// </summary>
    public int DurabilityCost(CraftState state, CraftAction action)
    {
        if (state.TrainedPerfectionActive) return 0;

        var cost = CraftActions.Spec(action).DurabilityCost;
        if (cost == 0) return 0;

        if (state.HasWasteNot)
            cost = (int)Math.Ceiling(cost / 2.0);

        if (ConditionEffects.DurabilityMultiplier(state.Condition) < 1.0)
            cost = (int)Math.Ceiling(cost / 2.0);

        return cost;
    }

    // ── Legality ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an action may be used, and if not, why. The reason is carried rather than
    /// collapsed to a boolean because the advisory surface has to explain a refusal, and
    /// because a solver that silently drops an action it thinks is illegal is very hard to
    /// debug from its output alone.
    /// </summary>
    public ActionLegality Legality(CraftState state, CraftAction action)
    {
        if (state.IsTerminal) return ActionLegality.CraftOver;

        var spec = CraftActions.Spec(action);

        if (state.Cp < CpCost(state, action)) return ActionLegality.NotEnoughCp;

        if (spec.ForbiddenUnderWasteNot && state.HasWasteNot)
            return ActionLegality.ForbiddenUnderWasteNot;

        // A stored Heart and Soul stands in for the condition; it is a permission, not a
        // condition modifier, so it satisfies the requirement without changing multipliers.
        if (spec.RequiresGoodCondition
            && state.Condition is not (CraftCondition.Good or CraftCondition.Excellent)
            && !state.HeartAndSoulActive)
            return ActionLegality.RequiresGoodCondition;

        switch (action)
        {
            case CraftAction.MuscleMemory when state.Step != 1:
            case CraftAction.Reflect      when state.Step != 1:
                return ActionLegality.FirstStepOnly;

            case CraftAction.ByregotsBlessing when state.InnerQuiet == 0:
                return ActionLegality.RequiresInnerQuiet;

            case CraftAction.TrainedFinesse when state.InnerQuiet < CraftActions.MaxInnerQuiet:
                return ActionLegality.RequiresMaxInnerQuiet;

            case CraftAction.DaringTouch when !state.HasBuff(CraftBuff.Expedience):
                return ActionLegality.RequiresExpedience;

            case CraftAction.CarefulObservation when state.CarefulObservationLeft == 0:
            case CraftAction.HeartAndSoul       when state.HeartAndSoulLeft == 0:
            case CraftAction.QuickInnovation    when state.QuickInnovationLeft == 0:
            case CraftAction.TrainedPerfection  when state.TrainedPerfectionLeft == 0:
                return ActionLegality.NoChargesLeft;

            case CraftAction.QuickInnovation when state.HasBuff(CraftBuff.Innovation):
                return ActionLegality.InnovationAlreadyActive;
        }

        return ActionLegality.Usable;
    }

    // ── Gains ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Progress this action would add. Veneration and Muscle Memory are additive with each
    /// other and multiply the efficiency-scaled base; Malleable multiplies on top.
    /// </summary>
    public int ProgressGain(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        if (spec.ProgressEfficiency == 0) return 0;

        var efficiency = spec.ProgressEfficiency;

        // Groundwork pays half efficiency when durability cannot cover its full cost.
        if (action == CraftAction.Groundwork && state.Durability < DurabilityCost(state, action))
            efficiency /= 2;

        // effect_mod is in tenths: 10 baseline, +10 Muscle Memory, +5 Veneration.
        var effect = 10
                   + (state.HasBuff(CraftBuff.MuscleMemory) ? 10 : 0)
                   + (state.HasBuff(CraftBuff.Veneration)   ? 5  : 0);

        var condition = ConditionEffects.ProgressConditionHalves(state.Condition);

        return (int)((long)BaseProgress * efficiency * effect * condition / ProgressDivisor);
    }

    /// <summary>
    /// Quality this action would add.
    ///
    /// Inner Quiet multiplies by 10% per stack, using the stacks held <em>before</em> this
    /// action grants its own. Innovation and Great Strides are additive with each other.
    /// Byregot's efficiency scales with the stacks it is about to consume.
    /// </summary>
    public int QualityGain(CraftState state, CraftAction action)
    {
        var spec = CraftActions.Spec(action);
        if (spec.QualityEfficiency == 0) return 0;

        var efficiency = action == CraftAction.ByregotsBlessing
            ? 100 + 20 * state.InnerQuiet
            : spec.QualityEfficiency;

        // effect_mod folds Inner Quiet and the two quality statuses into one integer:
        // (IQ + 10) tenths of Inner Quiet scaling, times 10 baseline +10 Great Strides
        // +5 Innovation. Inner Quiet uses the stacks held before this action grants its own.
        var effect = (state.InnerQuiet + 10)
                   * (10
                      + (state.HasBuff(CraftBuff.GreatStrides) ? 10 : 0)
                      + (state.HasBuff(CraftBuff.Innovation)   ? 5  : 0));

        var condition = ConditionEffects.QualityConditionQuarters(state.Condition, player.GoodMultiplier);

        return (int)((long)BaseQuality * efficiency * effect * condition / QualityDivisor);
    }

    // ── Transition ────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply one action.
    ///
    /// <paramref name="nextCondition"/> is supplied by the caller because condition
    /// randomness is the environment's, not the simulator's. For a step-neutral action it is
    /// still consulted: Careful Observation advances the condition exactly as a step would,
    /// measured rather than assumed, so there is no separate reroll distribution.
    ///
    /// <paramref name="succeeded"/> only matters for the two fallible touches; every other
    /// action in the set is certain.
    /// </summary>
    public StepResult Apply(CraftState state, CraftAction action, CraftCondition nextCondition, bool succeeded = true)
    {
        var legality = Legality(state, action);
        if (legality != ActionLegality.Usable)
            return new StepResult { State = state, Legality = legality };

        var spec = CraftActions.Spec(action);
        var next = state;

        var cpSpent = CpCost(state, action);
        next = next with { Cp = next.Cp - cpSpent };

        var progressGain = 0;
        var qualityGain  = 0;
        var durabilitySpent = 0;

        if (succeeded)
        {
            progressGain = ProgressGain(state, action);
            qualityGain  = QualityGain(state, action);
        }

        // Durability is spent whether or not the action succeeds.
        durabilitySpent = DurabilityCost(state, action);

        // Trained Perfection is consumed by the first action that would have cost durability.
        if (state.TrainedPerfectionActive && spec.DurabilityCost > 0)
            next = next with { TrainedPerfectionActive = false };

        next = next with
        {
            Progress   = next.Progress + progressGain,
            Quality    = Math.Min(next.Quality + qualityGain, recipe.MaxQuality),
            Durability = next.Durability - durabilitySpent,
        };

        // ── Inner Quiet ──
        if (action == CraftAction.ByregotsBlessing)
        {
            next = next with { InnerQuiet = 0 };
        }
        else if (succeeded && spec.GrantsInnerQuiet)
        {
            var bonus = spec.BonusInnerQuiet;

            // Refined Touch earns its extra stack only as a combo off Basic Touch.
            if (action == CraftAction.RefinedTouch && !CraftActions.IsRefinedTouchCombo(state.PreviousAction))
                bonus = 0;

            var stacks = Math.Min(next.InnerQuiet + 1 + bonus, CraftActions.MaxInnerQuiet);
            next = next with { InnerQuiet = (byte)stacks };
        }

        // ── Consumed statuses ──
        if (succeeded && qualityGain > 0 && next.HasBuff(CraftBuff.GreatStrides))
            next = next.WithBuff(CraftBuff.GreatStrides, 0);

        if (succeeded && progressGain > 0 && next.HasBuff(CraftBuff.MuscleMemory))
            next = next.WithBuff(CraftBuff.MuscleMemory, 0);

        // Daring Touch consumes Expedience whether or not it lands.
        if (action == CraftAction.DaringTouch)
            next = next.WithBuff(CraftBuff.Expedience, 0);

        // Counted here because the simulator is what knows a cast happened; whether the count
        // constrains anything is the solver's business, not the game's.
        if (spec.SuccessRate < 100)
            next = next with { GamblesUsed = (byte)Math.Min(next.GamblesUsed + 1, byte.MaxValue) };

        // ── Charges and stored permissions ──
        switch (action)
        {
            case CraftAction.CarefulObservation:
                next = next with { CarefulObservationLeft = (byte)(next.CarefulObservationLeft - 1) };
                break;

            case CraftAction.HeartAndSoul:
                next = next with
                {
                    HeartAndSoulLeft   = (byte)(next.HeartAndSoulLeft - 1),
                    HeartAndSoulActive = true,
                };
                break;

            case CraftAction.QuickInnovation:
                next = next with { QuickInnovationLeft = (byte)(next.QuickInnovationLeft - 1) };
                break;

            case CraftAction.TrainedPerfection:
                next = next with
                {
                    TrainedPerfectionLeft   = (byte)(next.TrainedPerfectionLeft - 1),
                    TrainedPerfectionActive = true,
                };
                break;

            case CraftAction.MastersMend:
                next = next with
                {
                    Durability = Math.Min(next.Durability + CraftActions.MastersMendRestore, recipe.Durability),
                    MendsUsed  = (byte)Math.Min(next.MendsUsed + 1, byte.MaxValue),
                };
                break;

            case CraftAction.ImmaculateMend:
                next = next with
                {
                    Durability = recipe.Durability,
                    MendsUsed  = (byte)Math.Min(next.MendsUsed + 1, byte.MaxValue),
                };
                break;

            case CraftAction.TricksOfTheTrade:
                next = next with { Cp = Math.Min(next.Cp + CraftActions.TricksCpRestore, player.MaxCp) };
                break;
        }

        // A stored Heart and Soul is spent only when it was actually needed — that is, when
        // the action required a Good condition and the condition was not one.
        if (spec.RequiresGoodCondition
            && action != CraftAction.HeartAndSoul
            && state.Condition is not (CraftCondition.Good or CraftCondition.Excellent)
            && state.HeartAndSoulActive)
            next = next with { HeartAndSoulActive = false };

        // ── Timers ──
        // Statuses tick only on steps. A step-neutral action leaves every timer alone, which
        // is what makes a reroll inside an Innovation window cost none of the window.
        if (spec.AdvancesStep)
            next = next.TickBuffs();

        // The granted status is set after the tick, so its full duration is available from
        // the following step rather than being immediately decremented.
        if (spec.GrantedBuff != CraftBuff.None)
        {
            var duration = spec.GrantedBuffDuration;
            if (ConditionEffects.IsPrimed(state.Condition))
                duration += CraftActions.PrimedBonusDuration;

            next = next.WithBuff(spec.GrantedBuff, duration);
        }

        // Hasty Touch grants Expedience only when it lands.
        if (action == CraftAction.HastyTouch && !succeeded)
            next = next.WithBuff(CraftBuff.Expedience, 0);

        // ── Step and condition ──
        if (spec.AdvancesStep)
            next = next with { Step = next.Step + 1 };

        next = next with { Condition = nextCondition, PreviousAction = action };

        // Manipulation restores at the end of a step, and only while the craft is still live.
        //
        // Two rules, both measured against recorded play rather than reasoned about:
        //
        // It never restores on the step it is cast, including a recast over a running one — two
        // manual crafts cast Manipulation at steps 2 and 10 with durability unchanged across both.
        //
        // And the test is on the status as it stood <em>before</em> the tick, so the final step of
        // a window still restores. Testing afterwards silently dropped the eighth restore: the
        // timer has already reached zero by then, even though the status was live for that step.
        if (spec.AdvancesStep
            && action != CraftAction.Manipulation
            && state.HasBuff(CraftBuff.Manipulation)
            && next.Durability > 0
            && next.Progress < recipe.Difficulty)
            next = next with { Durability = Math.Min(next.Durability + CraftActions.ManipulationRestore, recipe.Durability) };

        // ── Terminal checks ──
        // Final Appraisal holds the craft one point short rather than completing it.
        if (next.Progress >= recipe.Difficulty)
        {
            if (next.HasBuff(CraftBuff.FinalAppraisal))
            {
                next = next with { Progress = recipe.Difficulty - 1 };
                next = next.WithBuff(CraftBuff.FinalAppraisal, 0);
            }
            else
            {
                next = next with { Completed = true };
            }
        }

        if (!next.Completed && next.Durability <= 0)
            next = next with { Failed = true };

        return new StepResult
        {
            State           = next,
            Legality        = ActionLegality.Usable,
            ProgressGained  = progressGain,
            QualityGained   = qualityGain,
            CpSpent         = cpSpent,
            DurabilitySpent = durabilitySpent,
        };
    }

    /// <summary>
    /// Whether a finished craft counts as a clear. Progress complete is not enough — an
    /// expert recipe below its required quality fails outright, which is the whole reason
    /// the objective is binary.
    /// </summary>
    public bool IsClear(CraftState state) =>
        state.Completed && state.Quality >= recipe.RequiredQuality;
}
