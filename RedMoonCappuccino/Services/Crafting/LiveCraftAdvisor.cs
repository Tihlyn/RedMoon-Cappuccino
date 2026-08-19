using System;
using System.IO;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models.Crafting;
using GameRecipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote;
using SolverAction = RedMoonCappuccino.Models.Crafting.CraftAction;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Watches a live craft and keeps <see cref="CraftAdvisor"/> supplied with a state it can trust.
///
/// <para>Advises. Never acts — no action is ever pressed from here, and the hook below only listens.</para>
///
/// <para>The state is rebuilt by simulation rather than read: the addon shows progress, quality,
/// durability and the condition, but not Inner Quiet, not a buff's remaining steps, and not how
/// many specialist charges are left, and the solver needs all three. So the craft is replayed into
/// <see cref="CraftSim"/> from its first step, driven by the actions the player actually takes and
/// the conditions the client actually rolls.</para>
///
/// <para>What makes that safe is that it is checkable. After every step the simulated progress,
/// quality, durability and CP are compared against the four the addon reports, and any disagreement
/// stops the advice rather than degrading it. Four independent numbers agreeing across thirty steps
/// is strong evidence the parts that cannot be read are right too, since they are what produce the
/// parts that can.</para>
/// </summary>
public sealed unsafe class LiveCraftAdvisor : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly ConditionModelRegistry models;
    private readonly Hook<ActionManager.Delegates.UseAction> useActionHook;

    private CraftActionMap? actions;
    private uint mappedJob;

    private CraftSim? sim;
    private QualityBound? bound;
    private CraftAdvisor? advisor;
    private CraftState state;
    private bool tracking;

    private SolverAction pending = SolverAction.None;
    private int lastStep = -1;
    private int adviceSeed;

    /// <summary>The current judgement, or a refusal. Never null once a craft is on screen.</summary>
    public CraftAdvice Advice { get; private set; } = CraftAdvice.Refusing("No craft in progress.");

    /// <summary>Whether a craft is on screen at all, advisable or not.</summary>
    public bool CraftOpen { get; private set; }

    /// <summary>Action map for the job being played, for icons. Null until a craft opens.</summary>
    public CraftActionMap? Actions => actions;

    public LiveCraftAdvisor(IDalamudPluginInterface pluginInterface, IFramework framework, IGameGui gameGui,
                            IObjectTable objectTable, IPlayerState playerState, IDataManager dataManager,
                            IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;

        models = new ConditionModelRegistry();
        models.LoadFrom(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "craftdata"));

        useActionHook = gameInterop.HookFromAddress<ActionManager.Delegates.UseAction>(
            (nint)ActionManager.MemberFunctionPointers.UseAction, OnUseAction);
        useActionHook.Enable();

        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        useActionHook.Dispose();
    }

    private bool OnUseAction(ActionManager* self, ActionType actionType, uint actionId, ulong targetId,
                             uint a4, ActionManager.UseActionMode a5, uint a6, bool* a7)
    {
        var result = useActionHook.Original(self, actionType, actionId, targetId, a4, a5, a6, a7);

        try
        {
            if (result && actionType == ActionType.CraftAction && actions != null
                && actions.TryResolve(actionId, out var resolved))
                pending = resolved;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CraftAdvisor] UseAction listener failed.");
        }

        return result;
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            var addon = gameGui.GetAddonByName("Synthesis");
            if (addon.IsNull || !addon.IsVisible)
            {
                if (CraftOpen) Reset("No craft in progress.");
                return;
            }

            CraftOpen = true;
            Track((AddonSynthesis*)addon.Address);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CraftAdvisor] Update failed.");
            Refuse("Something went wrong reading the craft.");
        }
    }

    private void Reset(string why)
    {
        CraftOpen = false;
        tracking = false;
        pending = SolverAction.None;
        lastStep = -1;
        Advice = CraftAdvice.Refusing(why);
    }

    private void Refuse(string why)
    {
        tracking = false;
        Advice = CraftAdvice.Refusing(why);
    }

    private void Track(AddonSynthesis* synth)
    {
        var step = ReadInt(synth->StepNumber);
        if (step <= 0) return;

        var progress = ReadInt(synth->CurrentProgress);
        var quality = ReadInt(synth->CurrentQuality);
        var durability = ReadInt(synth->CurrentDurability);
        var cp = objectTable.LocalPlayer is { } self ? (int)self.CurrentCp : -1;
        var conditionText = ReadText(synth->Condition);

        if (progress < 0 || quality < 0 || durability < 0 || cp < 0) return;

        // A craft that has gone backwards to step 1 is a new craft.
        if (step < lastStep || !tracking)
        {
            if (step != 1) { Refuse("Advice can only start from the first step of a craft."); lastStep = step; return; }
            if (!Begin(conditionText)) { lastStep = step; return; }
        }

        if (step == lastStep) { Advise(); return; }

        // A step passed: fold the action the player took into the simulated state.
        if (tracking && lastStep > 0)
        {
            if (!Advance(conditionText, progress, quality, durability, cp)) return;
        }

        lastStep = step;
        Advise();
    }

    private bool Begin(string conditionText)
    {
        var note = GameRecipeNote.Instance();
        var recipeId = note != null ? note->ActiveCraftRecipeId : (ushort)0;
        var row = dataManager.GetExcelSheet<Recipe>()?.GetRowOrDefault(recipeId);
        if (row == null) { Refuse("Could not identify the recipe."); return false; }

        var recipe = row.Value;
        var table = recipe.RecipeLevelTable.ValueNullable;
        if (table == null) { Refuse("Could not read the recipe's level table."); return false; }

        var lvl = table.Value;
        var flag = lvl.ConditionsFlag;

        if (!recipe.IsExpert)
        {
            Refuse("This advisor is for expert recipes. Ordinary recipes are already solved well by existing tools.");
            return false;
        }

        if (!models.TryGetAdmissible(flag, out var model, out var why))
        {
            Refuse($"No measured condition model for this recipe's condition set ({flag}): {why}");
            return false;
        }

        var job = playerState.ClassJob.RowId;
        if (actions == null || mappedJob != job)
        {
            actions = new CraftActionMap(dataManager, job, playerState.Level);
            mappedJob = job;
        }

        if (!actions.IsComplete)
        {
            Refuse($"Could not identify {actions.Unresolved.Count} of this job's craft actions, "
                 + "so a recommendation could not be named reliably.");
            return false;
        }

        if (ReadPlayer() is not { } player) { Refuse("Could not read craftsmanship and control."); return false; }

        var spec = new RecipeSpec
        {
            RecipeId = recipeId,
            ConditionsFlag = flag,
            IsExpert = true,
            RecipeJobLevel = (int)playerState.Level,
            Difficulty = (int)(lvl.Difficulty * recipe.DifficultyFactor / 100),
            Durability = (int)(lvl.Durability * recipe.DurabilityFactor / 100),
            MaxQuality = (int)(lvl.Quality * recipe.QualityFactor / 100u),
            RequiredQuality = (int)recipe.RequiredQuality,
            ProgressDivider = 100, QualityDivider = 100, ProgressModifier = 100, QualityModifier = 100,
        };

        sim = new CraftSim(spec, player);
        bound = new QualityBound(sim);
        advisor = new CraftAdvisor(sim, bound, model, 30, OpeningBook.Expert);
        state = sim.Initial();
        tracking = true;
        pending = SolverAction.None;

        log.Information($"[CraftAdvisor] Tracking recipe {recipeId}, flag {flag}, "
                      + $"{spec.RequiredQuality}/{spec.MaxQuality} quality required.");
        return true;
    }

    /// <summary>
    /// Folds one taken action into the simulated state and checks the result against the client's.
    /// </summary>
    private bool Advance(string conditionText, int progress, int quality, int durability, int cp)
    {
        if (sim == null) { Refuse("Not tracking."); return false; }

        var taken = pending;
        pending = SolverAction.None;

        if (taken == SolverAction.None)
        {
            Refuse("A step passed without an action this advisor recognised.");
            return false;
        }

        var condition = ConditionEffects.FromDisplayName(conditionText);
        if (condition == CraftCondition.Unknown)
        {
            Refuse($"Unrecognised condition on screen: \"{conditionText}\".");
            return false;
        }

        // Success is not reported anywhere, so it is inferred: whichever outcome reproduces the
        // client's numbers is the one that happened, and neither reproducing them is a desync.
        foreach (var succeeded in stackalloc[] { true, false })
        {
            var result = sim.Apply(state, taken, condition, succeeded);
            if (!result.Ok) continue;

            var next = result.State;
            if (next.Progress != progress || next.Quality != quality
                || next.Durability != durability || next.Cp != cp) continue;

            state = next;
            advisor?.Observe(taken);
            return true;
        }

        Refuse($"The simulated craft no longer matches the client after {CraftActions.DisplayName(taken)}. "
             + "Advice has stopped rather than continue from a state that may be wrong.");
        log.Warning($"[CraftAdvisor] Desync after {taken}: client had "
                  + $"P{progress} Q{quality} D{durability} CP{cp}.");
        return false;
    }

    private void Advise()
    {
        if (!tracking || advisor == null) return;
        Advice = advisor.Advise(state, adviceSeed++);
    }

    private PlayerSpec? ReadPlayer()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return null;

        var attributes = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (attributes == null) return null;

        var craftsmanship = attributes->Attributes[(int)BaseParam.Craftsmanship];
        var control = attributes->Attributes[(int)BaseParam.Control];
        if (craftsmanship <= 0 || control <= 0) return null;

        return new PlayerSpec
        {
            Craftsmanship = craftsmanship,
            Control = control,
            MaxCp = (int)player.MaxCp,
            Level = (int)playerState.Level,

            // Relic tools raise the Good multiplier to 1.75 and are best-in-slot, so this is the
            // ordinary case rather than the exception. Not yet read from the equipped tool.
            GoodMultiplier = 1.75,
            AvailableDelineations = int.MaxValue,
        };
    }

    /// <summary>Base parameter ids, as the game numbers them.</summary>
    private enum BaseParam
    {
        Craftsmanship = 70,
        Control = 71,
    }

    private static int ReadInt(AtkTextNode* node)
    {
        if (!TryReadNodeText(node, out var text)) return -1;

        long value = 0;
        var any = false;
        foreach (var ch in text)
        {
            if (ch is < '0' or > '9') continue;
            value = value * 10 + (ch - '0');
            any = true;
            if (value > int.MaxValue) return int.MaxValue;
        }

        return any ? (int)value : -1;
    }

    private static string ReadText(AtkTextNode* node) =>
        TryReadNodeText(node, out var text) ? text.Trim() : string.Empty;

    /// <summary>
    /// Reads a node's text, refusing anything that is not actually a text node.
    /// The effect pane is rebuilt whenever a buff is applied or expires, so this runs against a
    /// structure in flux; reading a mistyped node is how the client gets taken down.
    /// </summary>
    private static bool TryReadNodeText(AtkTextNode* node, out string text)
    {
        text = string.Empty;
        if (node == null) return false;
        if (((AtkResNode*)node)->Type != NodeType.Text) return false;

        var span = node->NodeText.AsSpan();
        text = span.IsEmpty ? string.Empty : SeString.Parse(span).TextValue;
        return true;
    }
}
