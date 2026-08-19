using System;

namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// The recipe's fixed parameters. Everything the base progress and base quality
/// formulas need, plus the threshold that makes the objective binary.
/// </summary>
public sealed record RecipeSpec
{
    public required ushort RecipeId { get; init; }
    public required ushort ConditionsFlag { get; init; }
    public required bool   IsExpert { get; init; }

    /// <summary>
    /// The job level the recipe requires — <c>ClassJobLevel</c> from the recipe level table,
    /// not the internal rlvl.
    ///
    /// The two are deliberately not conflated. The level penalty applies when the crafter is
    /// below the recipe's required job level, and comparing a job level (1–100) against an
    /// rlvl (which runs past 700) would make that test true for every max-level crafter and
    /// silently scale every base value down. This is precisely the class of bug the Phase 0
    /// gate exists to catch, so the field is named for the scale it is on.
    /// </summary>
    public required int RecipeJobLevel { get; init; }

    public required int Difficulty  { get; init; }
    public required int MaxQuality  { get; init; }
    public required int Durability  { get; init; }

    /// <summary>
    /// Quality the craft must reach to count as a clear. Expert recipes fail outright below
    /// it even at full progress, which is what makes the objective P(clear) rather than
    /// expected quality — on the measured recipe this is 31,500 against a max of 31,520.
    /// </summary>
    public required int RequiredQuality { get; init; }

    public required int ProgressDivider { get; init; }
    public required int QualityDivider  { get; init; }
    public required int ProgressModifier { get; init; }
    public required int QualityModifier  { get; init; }
}

/// <summary>
/// The player's crafting stats. These are why nothing static can be precomputed and
/// shipped: the policy depends on craftsmanship, control and CP, which move with gear,
/// food and potion, so any per-recipe table would need an entry per stat tuple.
/// </summary>
public sealed record PlayerSpec
{
    public required int Craftsmanship { get; init; }
    public required int Control { get; init; }
    public required int MaxCp { get; init; }
    public required int Level { get; init; }

    /// <summary>
    /// The player's actual Good-condition quality multiplier — 1.5 normally, 1.75 with a
    /// relic tool. Threaded explicitly rather than defaulted because a bound computed at
    /// 1.5× while play happens at 1.75× stops being admissible and prunes optimal lines
    /// with no error and no symptom. This is the project's risk #03 in one field.
    /// </summary>
    public required double GoodMultiplier { get; init; }

    /// <summary>
    /// Crafter's Delineations the player is willing to spend on this craft. The three
    /// specialist actions each consume one, so they are not free per-craft charges and
    /// their cost belongs in the advice rather than only in the search.
    /// </summary>
    public int AvailableDelineations { get; init; }
}

/// <summary>
/// One node of the search: everything that distinguishes two decision problems, and
/// nothing that does not.
///
/// Two histories reaching the same progress, quality, durability, CP, buffs and condition
/// are the same problem, so the state carries no path information beyond
/// <see cref="PreviousAction"/> — which is present only because three actions combo off it.
/// Collapsing by state rather than by path is what makes a 60-step horizon survivable.
///
/// Buff timers are packed four bits apiece into <see cref="BuffTimers"/>. Ten steps is the
/// longest any status runs (Manipulation or Waste Not II at eight, plus Primed's two), so
/// four bits is sufficient and the whole state stays cheap to hash for the transposition
/// table Phase 2 depends on.
/// </summary>
public readonly record struct CraftState
{
    private const int BitsPerBuff = 4;
    private const ulong BuffMask  = 0xF;

    /// <summary>Longest duration representable in a timer field; every status fits well inside it.</summary>
    public const int MaxBuffDuration = 15;

    public int Progress   { get; init; }
    public int Quality    { get; init; }
    public int Durability { get; init; }
    public int Cp         { get; init; }

    /// <summary>Steps elapsed. Strictly increasing, which is what makes the state graph a DAG.</summary>
    public int Step { get; init; }

    public CraftCondition Condition { get; init; }

    public byte InnerQuiet { get; init; }

    /// <summary>Four bits per <see cref="CraftBuff"/>, indexed by the enum value.</summary>
    public ulong BuffTimers { get; init; }

    /// <summary>
    /// Fallible actions cast so far.
    ///
    /// <para>The game imposes no such limit — this is carried for the <em>solver</em>, which does.
    /// Each gamble adds a chance node, so admitting them without a cap doubles the branching at
    /// every step where one is legal. A budget of N caps the added outcomes at 2^N per line, and
    /// distorts the answer only downward: the policy found is the best among those gambling at
    /// most N times, which is a lower bound on the unrestricted optimum rather than an
    /// over-estimate of it.</para>
    ///
    /// <para>It lives in the state rather than beside the search because two positions reached
    /// with different budgets remaining are genuinely different decision problems.</para>
    /// </summary>
    public byte GamblesUsed { get; init; }

    public byte CarefulObservationLeft { get; init; }
    public byte HeartAndSoulLeft { get; init; }
    public byte QuickInnovationLeft { get; init; }
    public byte TrainedPerfectionLeft { get; init; }

    /// <summary>
    /// A Heart and Soul has been used and not yet spent. It is a stored permission — it
    /// unlocks Precise Touch, Intensive Synthesis and Tricks of the Trade regardless of
    /// condition, and is consumed only when one of them is used off-condition. It does
    /// not make the condition Good, and does not affect the quality multiplier.
    /// </summary>
    public bool HeartAndSoulActive { get; init; }

    /// <summary>Trained Perfection is armed: the next action costs no durability.</summary>
    public bool TrainedPerfectionActive { get; init; }

    /// <summary>Present only for the three combo actions; not part of the decision problem otherwise.</summary>
    public CraftAction PreviousAction { get; init; }

    /// <summary>Progress reached <see cref="RecipeSpec.Difficulty"/>; the craft is over.</summary>
    public bool Completed { get; init; }

    /// <summary>Durability hit zero with progress unfinished; the craft is over and failed.</summary>
    public bool Failed { get; init; }

    public bool IsTerminal => Completed || Failed;

    public int Buff(CraftBuff buff) => (int)((BuffTimers >> ((int)buff * BitsPerBuff)) & BuffMask);

    public bool HasBuff(CraftBuff buff) => Buff(buff) > 0;

    /// <summary>Either Waste Not is running; both halve durability identically.</summary>
    public bool HasWasteNot => HasBuff(CraftBuff.WasteNot) || HasBuff(CraftBuff.WasteNotII);

    public CraftState WithBuff(CraftBuff buff, int steps)
    {
        var clamped = (ulong)Math.Clamp(steps, 0, MaxBuffDuration);
        var shift   = (int)buff * BitsPerBuff;
        var cleared = BuffTimers & ~(BuffMask << shift);
        return this with { BuffTimers = cleared | (clamped << shift) };
    }

    /// <summary>
    /// Tick every status down one step. Called only for actions that advance the step
    /// counter — the specialist actions leave timers untouched, which is what makes a
    /// condition reroll inside an Innovation window free of the window.
    /// </summary>
    public CraftState TickBuffs()
    {
        var timers = BuffTimers;
        for (var i = 1; i < CraftActions.BuffCount; i++)
        {
            var shift = i * BitsPerBuff;
            var value = (timers >> shift) & BuffMask;
            if (value == 0) continue;
            timers = (timers & ~(BuffMask << shift)) | ((value - 1) << shift);
        }

        return this with { BuffTimers = timers };
    }

    /// <summary>The opening state for a recipe, before any action is taken.</summary>
    public static CraftState Initial(RecipeSpec recipe, PlayerSpec player) => new()
    {
        Progress   = 0,
        Quality    = 0,
        Durability = recipe.Durability,
        Cp         = player.MaxCp,
        Step       = 1,

        // Step 1 is always Normal — measured across 200 recorded crafts without exception.
        Condition  = CraftCondition.Normal,

        InnerQuiet = 0,
        BuffTimers = 0,
        GamblesUsed = 0,

        CarefulObservationLeft = CraftActions.CarefulObservationCharges,
        HeartAndSoulLeft       = CraftActions.HeartAndSoulCharges,
        QuickInnovationLeft    = CraftActions.QuickInnovationCharges,
        TrainedPerfectionLeft  = CraftActions.TrainedPerfectionCharges,

        PreviousAction = CraftAction.None,
    };
}
