using System;
using System.Collections.Generic;
using System.Linq;

namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// Every crafting action the solver may reason about, including the three specialist
/// actions. Values are internal ids, not the game's per-job action ids — a craft action
/// has a different id on each of the eight jobs, so the mapping to game ids is resolved
/// once at runtime and kept out of the model.
/// </summary>
public enum CraftAction : byte
{
    None = 0,

    // ── Progress ──
    BasicSynthesis,
    RapidSynthesis,
    CarefulSynthesis,
    PrudentSynthesis,
    Groundwork,
    IntensiveSynthesis,
    MuscleMemory,
    Reflect,
    DelicateSynthesis,

    // ── Quality ──
    BasicTouch,
    StandardTouch,
    AdvancedTouch,
    ByregotsBlessing,
    PreciseTouch,
    PrudentTouch,
    PreparatoryTouch,
    RefinedTouch,
    TrainedFinesse,
    HastyTouch,
    DaringTouch,

    // ── Buffs and utility ──
    Veneration,
    Innovation,
    GreatStrides,
    WasteNot,
    WasteNotII,
    Manipulation,
    MastersMend,
    ImmaculateMend,
    Observe,
    FinalAppraisal,
    TrainedPerfection,
    TricksOfTheTrade,

    // ── Specialist: step-neutral, and each costs a Crafter's Delineation ──
    CarefulObservation,
    HeartAndSoul,
    QuickInnovation,
}

/// <summary>Which resource an action is primarily spent on. Used to prune the action fan by domain knowledge.</summary>
public enum ActionKind : byte
{
    Progress,
    Quality,
    Buff,
    Repair,
    Utility,
    Specialist,
}

/// <summary>
/// Static properties of one action.
///
/// <para><strong>Provenance.</strong> Efficiencies, CP costs and durability costs here are
/// the community-established values (Teamcraft / raphael-rs). They are the simulator's
/// <em>specification</em>, and this project's stated top risk is that a wrong entry never
/// announces itself but quietly degrades every recommendation downstream. They are therefore
/// treated as assumptions pending the Phase 0 replay gate, not as settled fact — see
/// <c>SimValidator</c> for what the current corpus can and cannot confirm.</para>
/// </summary>
public sealed record ActionSpec
{
    public required CraftAction Action { get; init; }
    public required ActionKind  Kind   { get; init; }

    /// <summary>Progress efficiency as a percentage of base progress. 0 for actions that do not advance progress.</summary>
    public int ProgressEfficiency { get; init; }

    /// <summary>Quality efficiency as a percentage of base quality. 0 for actions that do not add quality.</summary>
    public int QualityEfficiency { get; init; }

    /// <summary>Base CP cost, before the Pliant halving.</summary>
    public int CpCost { get; init; }

    /// <summary>Base durability cost, before Sturdy / Robust / Waste Not reductions.</summary>
    public int DurabilityCost { get; init; }

    /// <summary>Success rate as a percentage, before Centered. 100 for the certain actions.</summary>
    public int SuccessRate { get; init; } = 100;

    /// <summary>
    /// Whether this action advances the step counter. False for all three specialist actions,
    /// which is load-bearing rather than a detail: a step-neutral action does not tick buff
    /// timers, so a condition reroll inside an Innovation window costs none of the window.
    /// </summary>
    public bool AdvancesStep { get; init; } = true;

    /// <summary>Whether using this action grants a stack of Inner Quiet. True for every touch action.</summary>
    public bool GrantsInnerQuiet { get; init; }

    /// <summary>Extra Inner Quiet beyond the standard stack, when the action's condition for it is met.</summary>
    public int BonusInnerQuiet { get; init; }

    /// <summary>Cannot be used while Waste Not or Waste Not II is active.</summary>
    public bool ForbiddenUnderWasteNot { get; init; }

    /// <summary>Requires Good or Excellent, or a stored Heart and Soul to stand in for one.</summary>
    public bool RequiresGoodCondition { get; init; }

    /// <summary>Spends a Crafter's Delineation — a real currency, not a free per-craft charge.</summary>
    public bool CostsDelineation { get; init; }

    /// <summary>Status this action grants, if any, and for how many steps before Primed extends it.</summary>
    public CraftBuff GrantedBuff { get; init; } = CraftBuff.None;
    public int GrantedBuffDuration { get; init; }
}

/// <summary>Statuses tracked in the craft state. Ordered so the enum can index a fixed-size timer array.</summary>
public enum CraftBuff : byte
{
    None = 0,
    Veneration,
    Innovation,
    GreatStrides,
    WasteNot,
    WasteNotII,
    Manipulation,
    MuscleMemory,
    FinalAppraisal,
    Expedience,
}

/// <summary>The action table, and the combo and charge rules that go with it.</summary>
public static class CraftActions
{
    /// <summary>Number of distinct buffs, for sizing the timer array.</summary>
    public const int BuffCount = (int)CraftBuff.Expedience + 1;

    /// <summary>Inner Quiet caps at ten stacks.</summary>
    public const int MaxInnerQuiet = 10;

    /// <summary>Careful Observation may be used three times per synthesis.</summary>
    public const int CarefulObservationCharges = 3;

    /// <summary>Heart and Soul may be used once per synthesis.</summary>
    public const int HeartAndSoulCharges = 1;

    /// <summary>Quick Innovation may be used once per synthesis.</summary>
    public const int QuickInnovationCharges = 1;

    /// <summary>Trained Perfection may be used once per synthesis.</summary>
    public const int TrainedPerfectionCharges = 1;

    /// <summary>Durability restored by Master's Mend.</summary>
    public const int MastersMendRestore = 30;

    /// <summary>Durability restored at the end of each step while Manipulation is active.</summary>
    public const int ManipulationRestore = 5;

    /// <summary>CP restored by Tricks of the Trade.</summary>
    public const int TricksCpRestore = 20;

    /// <summary>Extra duration Primed adds to the next status granted.</summary>
    public const int PrimedBonusDuration = 2;

    private static readonly ActionSpec[] Table = BuildTable();

    public static ActionSpec Spec(CraftAction action) => Table[(int)action];

    /// <summary>
    /// The action's name as the game writes it, for anything a player reads.
    ///
    /// <para>Derived from the enum rather than the client's own sheet on purpose: this is used by
    /// the harness and the advisor's own tests, neither of which has a game attached. The live UI
    /// prefers the sheet's localised name where it has one and falls back to this.</para>
    /// </summary>
    public static string DisplayName(CraftAction action) => action switch
    {
        CraftAction.None => "nothing",
        CraftAction.ByregotsBlessing => "Byregot's Blessing",
        CraftAction.WasteNotII => "Waste Not II",
        CraftAction.MastersMend => "Master's Mend",
        CraftAction.HeartAndSoul => "Heart and Soul",
        CraftAction.TricksOfTheTrade => "Tricks of the Trade",
        _ => Spaced(action.ToString()),
    };

    /// <summary>Splits a PascalCase name into words, leaving runs of capitals alone.</summary>
    private static string Spaced(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) builder.Append(' ');
            builder.Append(name[i]);
        }

        return builder.ToString();
    }


    /// <summary>Every action except <see cref="CraftAction.None"/>, in enum order.</summary>
    public static IReadOnlyList<CraftAction> All { get; } =
        Enum.GetValues<CraftAction>().Where(a => a != CraftAction.None).ToArray();

    private static ActionSpec[] BuildTable()
    {
        var table = new ActionSpec[Enum.GetValues<CraftAction>().Length];

        void Add(ActionSpec spec) => table[(int)spec.Action] = spec;

        // Deliberately absent, after a field-by-field cross-check against Raphael's action table:
        //
        //   Trained Eye      — fills quality outright, but only on recipes far below the
        //                      crafter's level. An expert recipe is never one of those.
        //   (Rapid Synthesis was in this list until a recorded human expert craft turned out to
        //    use it. It is now in the table but still out of the solver's candidate set, which
        //    refuses gambles — the distinction between "cannot represent" and "will not choose".)
        //   Stellar Steady Hand — makes the fallible actions certain. Both it and the actions it
        //                      enables are excluded together; the solver refuses gambles anyway,
        //                      so admitting them would widen the branching factor for lines it
        //                      would never choose.
        //
        // Each is a real action rather than an oversight, and each is out of scope for expert
        // recipes specifically. Revisit before pointing this simulator at anything else.

        Add(new ActionSpec { Action = CraftAction.None, Kind = ActionKind.Utility, AdvancesStep = false });

        // ── Progress ──────────────────────────────────────────────────────────
        Add(new ActionSpec { Action = CraftAction.BasicSynthesis,   Kind = ActionKind.Progress, ProgressEfficiency = 120, CpCost = 0,  DurabilityCost = 10 });
        // Rapid Synthesis. Excluded from the solver's candidate set as a gamble, but present in
        // the table because a recorded human expert craft uses it — three casts at 0 CP, each
        // gaining exactly 1685, which only 500% efficiency produces. A baseline that cannot be
        // expressed cannot be compared against.
        Add(new ActionSpec { Action = CraftAction.RapidSynthesis, Kind = ActionKind.Progress, ProgressEfficiency = 500, CpCost = 0, DurabilityCost = 10, SuccessRate = 50 });

        Add(new ActionSpec { Action = CraftAction.CarefulSynthesis, Kind = ActionKind.Progress, ProgressEfficiency = 180, CpCost = 7,  DurabilityCost = 10 });
        Add(new ActionSpec { Action = CraftAction.PrudentSynthesis, Kind = ActionKind.Progress, ProgressEfficiency = 180, CpCost = 18, DurabilityCost = 5, ForbiddenUnderWasteNot = true });

        // Groundwork's efficiency halves when remaining durability cannot pay its full cost.
        // That conditional lives in the simulator, not the table.
        Add(new ActionSpec { Action = CraftAction.Groundwork, Kind = ActionKind.Progress, ProgressEfficiency = 360, CpCost = 18, DurabilityCost = 20 });

        Add(new ActionSpec { Action = CraftAction.IntensiveSynthesis, Kind = ActionKind.Progress, ProgressEfficiency = 400, CpCost = 6, DurabilityCost = 10, RequiresGoodCondition = true });

        Add(new ActionSpec
        {
            Action = CraftAction.MuscleMemory, Kind = ActionKind.Progress,
            ProgressEfficiency = 300, CpCost = 6, DurabilityCost = 10,
            GrantedBuff = CraftBuff.MuscleMemory, GrantedBuffDuration = 5,
        });

        Add(new ActionSpec { Action = CraftAction.DelicateSynthesis, Kind = ActionKind.Progress, ProgressEfficiency = 150, QualityEfficiency = 100, CpCost = 32, DurabilityCost = 10, GrantsInnerQuiet = true });

        // ── Quality ───────────────────────────────────────────────────────────
        // Reflect: the quality-side opener, first step only. Two Inner Quiet after one cast,
        // derived from recorded play — 550 quality from an uncomboed Advanced Touch on the
        // following step pins (IQ + 10) at 12.
        Add(new ActionSpec
        {
            Action = CraftAction.Reflect, Kind = ActionKind.Quality,
            QualityEfficiency = 300, CpCost = 6, DurabilityCost = 10,
            GrantsInnerQuiet = true, BonusInnerQuiet = 1,
        });

        Add(new ActionSpec { Action = CraftAction.BasicTouch,    Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 18, DurabilityCost = 10, GrantsInnerQuiet = true });
        Add(new ActionSpec { Action = CraftAction.StandardTouch, Kind = ActionKind.Quality, QualityEfficiency = 125, CpCost = 32, DurabilityCost = 10, GrantsInnerQuiet = true });
        Add(new ActionSpec { Action = CraftAction.AdvancedTouch, Kind = ActionKind.Quality, QualityEfficiency = 150, CpCost = 46, DurabilityCost = 10, GrantsInnerQuiet = true });

        // Byregot's efficiency is 100 + 20 per Inner Quiet stack, and it consumes the stacks.
        // Both parts live in the simulator.
        Add(new ActionSpec { Action = CraftAction.ByregotsBlessing, Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 24, DurabilityCost = 10 });

        Add(new ActionSpec { Action = CraftAction.PreciseTouch,     Kind = ActionKind.Quality, QualityEfficiency = 150, CpCost = 18, DurabilityCost = 10, GrantsInnerQuiet = true, BonusInnerQuiet = 1, RequiresGoodCondition = true });
        Add(new ActionSpec { Action = CraftAction.PrudentTouch,     Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 25, DurabilityCost = 5,  GrantsInnerQuiet = true, ForbiddenUnderWasteNot = true });
        Add(new ActionSpec { Action = CraftAction.PreparatoryTouch, Kind = ActionKind.Quality, QualityEfficiency = 200, CpCost = 40, DurabilityCost = 20, GrantsInnerQuiet = true, BonusInnerQuiet = 1 });

        // Refined Touch grants its bonus stack only when comboed from Basic Touch; the combo
        // test is in the simulator, which is what knows the previous action.
        Add(new ActionSpec { Action = CraftAction.RefinedTouch, Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 24, DurabilityCost = 10, GrantsInnerQuiet = true, BonusInnerQuiet = 1 });

        Add(new ActionSpec { Action = CraftAction.TrainedFinesse, Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 32, DurabilityCost = 0, GrantsInnerQuiet = true });

        Add(new ActionSpec { Action = CraftAction.HastyTouch,  Kind = ActionKind.Quality, QualityEfficiency = 100, CpCost = 0, DurabilityCost = 10, SuccessRate = 85, GrantsInnerQuiet = true, GrantedBuff = CraftBuff.Expedience, GrantedBuffDuration = 1 });
        Add(new ActionSpec { Action = CraftAction.DaringTouch, Kind = ActionKind.Quality, QualityEfficiency = 150, CpCost = 0, DurabilityCost = 10, SuccessRate = 85, GrantsInnerQuiet = true });

        // ── Buffs and utility ─────────────────────────────────────────────────
        Add(new ActionSpec { Action = CraftAction.Veneration,   Kind = ActionKind.Buff, CpCost = 18, GrantedBuff = CraftBuff.Veneration,   GrantedBuffDuration = 4 });
        Add(new ActionSpec { Action = CraftAction.Innovation,   Kind = ActionKind.Buff, CpCost = 18, GrantedBuff = CraftBuff.Innovation,   GrantedBuffDuration = 4 });
        Add(new ActionSpec { Action = CraftAction.GreatStrides, Kind = ActionKind.Buff, CpCost = 32, GrantedBuff = CraftBuff.GreatStrides, GrantedBuffDuration = 3 });
        Add(new ActionSpec { Action = CraftAction.WasteNot,     Kind = ActionKind.Buff, CpCost = 56, GrantedBuff = CraftBuff.WasteNot,     GrantedBuffDuration = 4 });
        Add(new ActionSpec { Action = CraftAction.WasteNotII,   Kind = ActionKind.Buff, CpCost = 98, GrantedBuff = CraftBuff.WasteNotII,   GrantedBuffDuration = 8 });
        Add(new ActionSpec { Action = CraftAction.Manipulation, Kind = ActionKind.Buff, CpCost = 96, GrantedBuff = CraftBuff.Manipulation, GrantedBuffDuration = 8 });
        Add(new ActionSpec { Action = CraftAction.FinalAppraisal, Kind = ActionKind.Buff, CpCost = 1, GrantedBuff = CraftBuff.FinalAppraisal, GrantedBuffDuration = 5, AdvancesStep = false });

        Add(new ActionSpec { Action = CraftAction.MastersMend,    Kind = ActionKind.Repair, CpCost = 88 });
        Add(new ActionSpec { Action = CraftAction.ImmaculateMend, Kind = ActionKind.Repair, CpCost = 112 });

        Add(new ActionSpec { Action = CraftAction.Observe, Kind = ActionKind.Utility, CpCost = 7 });

        // Trained Perfection zeroes the next action's durability cost. It is a charge, not a
        // timed buff, so the simulator holds it as a flag rather than in the timer array.
        Add(new ActionSpec { Action = CraftAction.TrainedPerfection, Kind = ActionKind.Utility, CpCost = 0 });

        Add(new ActionSpec { Action = CraftAction.TricksOfTheTrade, Kind = ActionKind.Utility, CpCost = 0, RequiresGoodCondition = true });

        // ── Specialist ────────────────────────────────────────────────────────
        // All three are step-neutral and all three spend a Crafter's Delineation. The step
        // neutrality is measured, not assumed: 6 of 6 Careful Observations used on Robust
        // yielded Sturdy, honouring the telegraph, without the step counter moving.
        Add(new ActionSpec { Action = CraftAction.CarefulObservation, Kind = ActionKind.Specialist, CpCost = 0, AdvancesStep = false, CostsDelineation = true });
        Add(new ActionSpec { Action = CraftAction.HeartAndSoul,       Kind = ActionKind.Specialist, CpCost = 0, AdvancesStep = false, CostsDelineation = true });
        Add(new ActionSpec { Action = CraftAction.QuickInnovation,    Kind = ActionKind.Specialist, CpCost = 0, AdvancesStep = false, CostsDelineation = true, GrantedBuff = CraftBuff.Innovation, GrantedBuffDuration = 1 });

        for (var i = 1; i < table.Length; i++)
        {
            if (table[i] is null)
                throw new InvalidOperationException($"Action table has no entry for {(CraftAction)i}.");
        }

        return table;
    }

    /// <summary>
    /// The reduced CP cost when an action follows its combo predecessor, or null when the
    /// action has no combo. Standard Touch after Basic Touch, and Advanced Touch after
    /// Standard Touch, both drop to 18.
    /// </summary>
    public static int? ComboCost(CraftAction action, CraftAction previous) => (action, previous) switch
    {
        (CraftAction.StandardTouch, CraftAction.BasicTouch)    => 18,
        (CraftAction.AdvancedTouch, CraftAction.StandardTouch) => 18,

        // Observe sets the same combo state Standard Touch does, so it also discounts Advanced
        // Touch from 46 to 18. Cross-checked against Raphael, which models Observe as setting
        // Combo::StandardTouch outright. Worth 21 CP net for a step, so omitting it was not
        // cosmetic — the solver simply could not see the line.
        (CraftAction.AdvancedTouch, CraftAction.Observe)       => 18,

        _ => null,
    };

    /// <summary>Whether Refined Touch is comboed, which is what grants its bonus Inner Quiet.</summary>
    public static bool IsRefinedTouchCombo(CraftAction previous) => previous == CraftAction.BasicTouch;
}
