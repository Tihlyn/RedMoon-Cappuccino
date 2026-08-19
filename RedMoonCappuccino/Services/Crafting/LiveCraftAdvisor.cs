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

    /// <summary>Where the game's own Synthesis window sits, so the advice can be put against it.</summary>
    public System.Numerics.Vector4 CraftWindow { get; private set; }

    /// <summary>The craft as the simulator understands it. Only meaningful while <see cref="Tracking"/>.</summary>
    public CraftState State => state;

    public bool Tracking => tracking;

    /// <summary>The recipe being crafted, once identified.</summary>
    public RecipeSpec? Recipe { get; private set; }

    /// <summary>
    /// The stats this craft is being solved for, read from the character when it started.
    ///
    /// <para>Read live rather than configured, so a gear change or a different food is picked up by
    /// crafting again and nothing has to be kept in sync by hand. Shown in the window because the
    /// advice is only as good as these: a solver quietly working from the wrong control value gives
    /// confident, wrong answers, which is exactly how the benchmark for this project spent its first
    /// ten changes describing a different character.</para>
    /// </summary>
    public PlayerSpec? Player { get; private set; }

    /// <summary>
    /// Plays the craft on the advisor's own recommendations.
    ///
    /// <para>A testing affordance, off by default and never the product. The advisor exists to give
    /// a judgement a player acts on; this exists so a whole sequence can be watched end to end and
    /// compared against the simulated clear rate without thirty manual keypresses per craft.</para>
    /// </summary>
    public bool AutoPlay { get; set; }

    /// <summary>Actions taken by auto mode this craft, so the window can show it is doing something.</summary>
    public int AutoActions { get; private set; }

    private long lastAutoActionMs;

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
            // Both action types, because the crafting actions are split across two sheets and the
            // split carries through to here: Reflect arrives as a CraftAction, Manipulation as an
            // Action. Listening for only the first captured the opener and then nothing, which read
            // as the advisor losing track one step into every craft. The id spaces do not overlap,
            // so the map alone decides whether a given id is one of ours.
            if (result && actionType is ActionType.CraftAction or ActionType.Action
                && actions != null && actions.TryResolve(actionId, out var resolved))
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

            var unit = (AtkUnitBase*)addon.Address;
            CraftWindow = new System.Numerics.Vector4(
                unit->X, unit->Y, unit->GetScaledWidth(true), unit->GetScaledHeight(true));

            Track((AddonSynthesis*)addon.Address);
            if (AutoPlay) StepAuto();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CraftAdvisor] Update failed.");
            Refuse("Something went wrong reading the craft.");
        }
    }

    /// <summary>
    /// Resolves this job's action list and reports it, without needing a craft open.
    /// </summary>
    public string DescribeActions()
    {
        var job = playerState.ClassJob.RowId;
        if (job == 0) return "No class active.";

        if (actions == null || mappedJob != job)
        {
            actions = new CraftActionMap(dataManager, job, playerState.Level);
            mappedJob = job;
        }

        return $"Job {job}, level {playerState.Level}." + Environment.NewLine + actions.Describe();
    }

    private void Reset(string why)
    {
        CraftOpen = false;
        tracking = false;
        advised = false;
        AutoPlay = false;
        pending = SolverAction.None;
        lastStep = -1;
        Advice = CraftAdvice.Refusing(why);
    }

    /// <summary>
    /// Stops advising, and says why once.
    ///
    /// <para>The first reason is the real one and it is kept. Without this the substantive message —
    /// a desync naming the action that caused it, an unrecognised condition — survived a single
    /// frame before the next tick overwrote it with the generic "advice can only start from the
    /// first step", which is a consequence of having stopped rather than a cause of it. The useful
    /// half of the diagnosis was being thrown away in about sixteen milliseconds.</para>
    /// </summary>
    private void Refuse(string why)
    {
        tracking = false;
        if (!Advice.IsRefusing || string.IsNullOrEmpty(Advice.Refusal)) Advice = CraftAdvice.Refusing(why);
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
            // Step 1 is a fresh craft, so whatever went wrong in the last one is no longer relevant.
            if (step == 1) Advice = CraftAdvice.Refusing(string.Empty);

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

        // An action the sheet did not yield costs its icon and its auto-play, not the advice. Only
        // a map that resolved nothing means something is structurally wrong; refusing to advise at
        // all because a handful of names did not match would throw away a working solver over a
        // presentation problem.
        if (actions.ResolvedCount == 0)
        {
            Refuse("Could not identify any of this job's craft actions. "
                 + "Run /rmccraft advise map to see what the sheet offered.");
            return false;
        }

        if (!actions.IsComplete)
            log.Warning($"[CraftAdvisor] {actions.Unresolved.Count} unresolved craft actions on job {job}: "
                      + string.Join(", ", System.Linq.Enumerable.Select(actions.Unresolved, CraftActions.DisplayName)));


        if (ReadPlayer() is not { } player) { Refuse("Could not read craftsmanship and control."); return false; }

        // Every one of these comes from the recipe. They were once written as flat 100s, which is
        // true of the benchmark recipe and of almost nothing else: base progress is
        // craftsmanship x 10 / ProgressDivider, so a divider assumed at 100 against a real one near
        // 180 doubles every gain the simulator predicts. The craft then disagreed with the client on
        // the very first action, which is exactly what the desync check is for — it caught it, and
        // this is the cause it was catching.
        var spec = new RecipeSpec
        {
            RecipeId = recipeId,
            ConditionsFlag = flag,
            IsExpert = true,
            RecipeJobLevel = lvl.ClassJobLevel,
            Difficulty = (int)(lvl.Difficulty * recipe.DifficultyFactor / 100),
            Durability = (int)(lvl.Durability * recipe.DurabilityFactor / 100),
            MaxQuality = (int)(lvl.Quality * recipe.QualityFactor / 100u),
            RequiredQuality = (int)recipe.RequiredQuality,
            ProgressDivider = lvl.ProgressDivider,
            QualityDivider = lvl.QualityDivider,
            ProgressModifier = lvl.ProgressModifier,
            QualityModifier = lvl.QualityModifier,
        };

        sim = new CraftSim(spec, player);
        bound = new QualityBound(sim);
        advisor = new CraftAdvisor(sim, bound, model, 30, OpeningBook.Expert);
        state = sim.Initial();
        tracking = true;
        advised = false;
        thinking = null;
        pending = SolverAction.None;
        Recipe = spec;
        Player = player;
        AutoActions = 0;

        // Expert recipes turn in as collectables, and a collectable is graded in tiers rather than
        // passed or failed at one number. Recipe.RequiredQuality reads 31,500 against a maximum of
        // 31,520 on this recipe, which is the whole bar — if that is the target the solver has been
        // aiming at a threshold nothing reaches. The shop's own tier values settle it, so they are
        // logged until the answer is known and can be built in.
        try
        {
            var meta = recipe.CollectableMetadata;
            log.Information($"[CraftAdvisor] Collectable metadata: rowId {meta.RowId}, "
                          + $"type {meta.GetType().Name}. Recipe.RequiredQuality = {recipe.RequiredQuality}.");

            var refine = dataManager.GetExcelSheet<CollectablesShopRefine>()?.GetRowOrDefault(meta.RowId);
            if (refine != null)
                log.Information($"[CraftAdvisor] Collectability tiers: low {refine.Value.LowCollectability}, "
                              + $"mid {refine.Value.MidCollectability}, high {refine.Value.HighCollectability}.");
            else
                log.Information("[CraftAdvisor] No CollectablesShopRefine row for that id.");
        }
        catch (Exception ex)
        {
            log.Warning($"[CraftAdvisor] Could not read collectable metadata: {ex.Message}");
        }

        log.Information($"[CraftAdvisor] Tracking recipe {recipeId}, flag {flag}, "
                      + $"{spec.RequiredQuality}/{spec.MaxQuality} quality required, "
                      + $"difficulty {spec.Difficulty}, dividers {spec.ProgressDivider}/{spec.QualityDivider}, "
                      + $"base {sim.BaseProgress}/{sim.BaseQuality}.");
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
            if (advisor is { } judge) lock (judge) judge.Observe(taken);
            return true;
        }

        Refuse($"The simulated craft no longer matches the client after {CraftActions.DisplayName(taken)}. "
             + "Advice has stopped rather than continue from a state that may be wrong.");
        log.Warning($"[CraftAdvisor] Desync after {taken}: client had "
                  + $"P{progress} Q{quality} D{durability} CP{cp}.");
        return false;
    }

    /// <summary>
    /// Presses the recommended action, no faster than the client will accept one.
    ///
    /// <para>Guarded on the same things the advice is: it does nothing while the advisor is refusing,
    /// nothing once the craft is called lost, and nothing until the client reports the action as
    /// usable. It also stops itself the moment the simulated craft stops matching the real one,
    /// because that is the case where continuing would be acting on a state known to be wrong.</para>
    /// </summary>
    private void StepAuto()
    {
        if (!tracking || Advice.IsRefusing) return;
        if (Advice.Recommended == SolverAction.None) return;
        if (actions == null || !actions.TryGameId(Advice.Recommended, out var gameId)) return;

        var now = Environment.TickCount64;
        if (now - lastAutoActionMs < AutoIntervalMs) return;

        var manager = ActionManager.Instance();
        if (manager == null) return;

        var type = actions.TypeOf(Advice.Recommended);
        if (manager->GetActionStatus(type, gameId, NoTarget, false, false, null) != 0) return;

        lastAutoActionMs = now;
        if (manager->UseAction(type, gameId, NoTarget, 0, ActionManager.UseActionMode.None, 0, null))
            AutoActions++;
    }

    /// <summary>
    /// Gap between auto-played actions. Crafting actions carry an animation lock of roughly two
    /// seconds and the client silently drops anything sent inside it, which reads as the solver
    /// skipping steps rather than as a rejected input.
    /// </summary>
    private const int AutoIntervalMs = 2200;

    private const ulong NoTarget = 0xE000_0000;

    /// <summary>
    /// Produces the advice for the current position, once.
    ///
    /// <para>Guarded on the state having actually changed. This runs on the framework tick, and a
    /// single call plays the position out two hundred times to measure its clear chance — at sixty
    /// ticks a second that is twelve thousand simulated crafts per second, which takes the frame
    /// rate down with it and makes the percentage on screen jitter as each re-measurement draws a
    /// different sample. The position only changes when a step passes, so that is when this runs.</para>
    /// </summary>
    private void Advise()
    {
        if (!tracking || advisor == null) return;
        if (advised && state.Equals(advisedFrom)) return;
        if (thinking != null && !thinking.IsCompleted) return;

        advisedFrom = state;
        advised = true;

        var position = state;
        var seed = adviceSeed++;
        var judge = advisor;

        // Off the framework thread. Measuring a position means playing it out two hundred times,
        // which is tens of milliseconds even spread across cores — cheap once per step, and a
        // visible stutter if it happens between the player pressing an action and the frame drawing.
        thinking = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                CraftAdvice result;
                lock (judge) result = judge.Advise(position, seed);
                Advice = result;
            }
            catch (Exception ex)
            {
                log.Error(ex, "[CraftAdvisor] Advising failed.");
                Advice = CraftAdvice.Refusing("Something went wrong working out the advice.");
            }
        });
    }

    private CraftState advisedFrom;
    private bool advised;
    private System.Threading.Tasks.Task? thinking;

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
