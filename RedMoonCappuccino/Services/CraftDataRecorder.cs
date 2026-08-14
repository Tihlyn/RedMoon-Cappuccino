using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models;

// Disambiguated from FFXIVClientStructs.FFXIV.Client.UI.AddonRecipeNote, which is the
// window; this is the game-side state behind it.
using GameRecipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Collects condition-transition samples from expert crafts, so the weights the
/// solver will depend on are fitted from observation rather than trusted from a
/// published table.
///
/// Two modes. <see cref="RecorderMode.Observe"/> only reads the Synthesis addon
/// while the player crafts by hand. <see cref="RecorderMode.Auto"/> additionally
/// drives a trial synthesis: it presses cheap actions to run the step counter up,
/// and restarts the craft when it fails. Trial synthesis consumes no materials
/// and yields no item, so a failed craft costs nothing but time.
///
/// The driving policy is deliberately <em>condition-blind</em>. Choosing actions
/// by condition would correlate the action with the thing being measured and
/// bias the fit — the whole point of the exercise is an unbiased transition
/// matrix, so the policy only ever looks at CP.
///
/// All game access happens on the framework thread.
/// </summary>
public sealed unsafe class CraftDataRecorder : IDisposable
{
    public enum RecorderMode
    {
        /// <summary>Not recording.</summary>
        Off,
        /// <summary>Record steps from crafts the player drives by hand.</summary>
        Observe,
        /// <summary>Record and drive trial syntheses in a loop.</summary>
        Auto,
    }

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework   framework;
    private readonly IGameGui     gameGui;
    private readonly IObjectTable objectTable;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog   log;

    // ── Tuning ────────────────────────────────────────────────────────────────

    /// <summary>How often the addon is polled. Fast enough not to miss a step, slow enough to cost nothing.</summary>
    private const long PollIntervalMs = 120;

    /// <summary>
    /// Minimum gap between sent actions, matching the 2s the game's own macro system
    /// uses. Anything faster is rejected by the client.
    /// </summary>
    private const long ActionIntervalMs = 2000;

    /// <summary>Delay before re-pressing Trial Synthesis, so the previous craft's addon has fully torn down.</summary>
    private const long RestartDelayMs = 1200;

    /// <summary>
    /// How long a craft may sit without the step counter moving before Auto gives up.
    /// An action that is never usable would otherwise retry forever, and this runs
    /// unattended — a stopped recorder is recoverable, a silent spin is not.
    /// </summary>
    private const long StallTimeoutMs = 30_000;

    /// <summary>Throttle for the unreadable-addon warning, so a bad read cannot spam the log.</summary>
    private const long UnreadableLogIntervalMs = 5000;

    /// <summary>Observe's CP cost, used to identify it in the sheet and to decide when it is no longer affordable.</summary>
    private const byte ObserveCpCost = 7;

    /// <summary>No craft action targets anything; this is the client's "no target" sentinel.</summary>
    private const ulong NoTarget = 0xE000_0000;

    // ── State ─────────────────────────────────────────────────────────────────
    public RecorderMode Mode { get; private set; } = RecorderMode.Off;

    /// <summary>Steps written this run, across all crafts.</summary>
    public int StepsRecorded { get; private set; }

    /// <summary>Crafts completed or failed this run.</summary>
    public int SessionsRecorded { get; private set; }

    /// <summary>Observed frequency of each condition name, for an at-a-glance sanity check.</summary>
    public IReadOnlyDictionary<string, int> ConditionCounts => conditionCounts;
    private readonly Dictionary<string, int> conditionCounts = new(StringComparer.Ordinal);

    /// <summary>Path of the file this run is appending to, surfaced by the status command.</summary>
    public string? OutputPath { get; private set; }

    private StreamWriter? writer;
    private string  sessionId  = string.Empty;
    private bool    inCraft;
    private int     lastStep = -1;
    private long    lastPollTick;
    private long    lastActionTick;
    private long    craftEndedTick;
    private long    lastStepTick;
    private long    lastUnreadableTick;

    // Sample captured on entering a step, held until the action taken from it is
    // known so the pair lands on one line. Written with action 0 if the craft ends first.
    private CraftStepSample? pending;

    // Resolved once per job: the two actions the driving policy uses.
    private uint fillerActionId;   // 0 CP, costs durability — runs the craft down once CP is gone
    private uint observeActionId;  // 7 CP, costs no durability — the cheapest way to advance a step
    private string fillerName  = "?";
    private string observeName = "?";
    private uint   resolvedForJob;

    public CraftDataRecorder(IDalamudPluginInterface pluginInterface, IFramework framework, IGameGui gameGui,
                             IObjectTable objectTable, IPlayerState playerState, IDataManager dataManager,
                             IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework       = framework;
        this.gameGui         = gameGui;
        this.objectTable     = objectTable;
        this.playerState     = playerState;
        this.dataManager     = dataManager;
        this.log             = log;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        CloseWriter();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start(RecorderMode mode)
    {
        if (mode == RecorderMode.Off) { Stop(); return; }

        if (Mode != RecorderMode.Off)
        {
            log.Information($"[CraftRecorder] Already running; switching to {mode}.");
            Mode = mode;
            return;
        }

        var dir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "craftdata");
        Directory.CreateDirectory(dir);
        OutputPath = Path.Combine(dir, $"craft-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        writer = new StreamWriter(OutputPath, append: true) { AutoFlush = true };

        StepsRecorded = 0;
        SessionsRecorded = 0;
        conditionCounts.Clear();
        lastStep = -1;
        inCraft  = false;
        pending  = null;

        Mode = mode;
        log.Information($"[CraftRecorder] Started in {mode}. Writing to {OutputPath}");
    }

    public void Stop()
    {
        if (Mode == RecorderMode.Off) return;
        if (inCraft) EndSession();
        Mode = RecorderMode.Off;
        CloseWriter();
        log.Information($"[CraftRecorder] Stopped. {SessionsRecorded} craft(s), {StepsRecorded} step(s).");
    }

    /// <summary>Human-readable summary for the status command.</summary>
    public string DescribeActions()
    {
        ResolveActions();
        return fillerActionId == 0 && observeActionId == 0
            ? "No craft actions resolved — log in on a crafter with the recipe's job."
            : $"filler={fillerName} ({fillerActionId}), observe={observeName} ({observeActionId})";
    }

    /// <summary>
    /// One-shot dump of everything the reader can currently see, run on demand with a
    /// craft open. It deliberately ignores the gates the polling loop applies and reads
    /// anyway, because the question it answers is <em>which</em> gate is shut.
    ///
    /// A node reporting <c>Text</c> with sensible content means the struct layout matches
    /// and any empty log is a gating problem. Nodes reporting a wrong type, or a non-null
    /// address with nothing readable behind it, mean the compiled ClientStructs disagrees
    /// with the running client and the offsets are pointing at the wrong memory.
    /// </summary>
    public string Probe()
    {
        var lines = new List<string>();

        // Each line is logged the moment it is produced, not at the end. A native fault
        // takes the client down without unwinding, so the last line in the Dalamud log
        // names the read that caused it — which is the whole diagnostic value here.
        void Emit(string line)
        {
            lines.Add(line);
            log.Information($"[CraftRecorder] probe: {line}");
        }

        var synthPtr = gameGui.GetAddonByName("Synthesis");
        Emit(synthPtr.IsNull
            ? "Synthesis: not found"
            : $"Synthesis: ready={synthPtr.IsReady} visible={synthPtr.IsVisible} addr=0x{synthPtr.Address:X}");

        if (!synthPtr.IsNull)
        {
            var a = (AddonSynthesis*)synthPtr.Address;
            Emit($"  Step       {Describe(a->StepNumber)}");
            Emit($"  Condition  {Describe(a->Condition)}");
            Emit($"  Progress   {Describe(a->CurrentProgress)} / {Describe(a->MaxProgress)}");
            Emit($"  Quality    {Describe(a->CurrentQuality)} / {Describe(a->MaxQuality)}");
            Emit($"  Durability {Describe(a->CurrentDurability)}");
            Emit($"  Effects    [{string.Join(" | ", ReadEffects(a))}]");
        }

        var notePtr = gameGui.GetAddonByName("RecipeNote");
        Emit(notePtr.IsNull
            ? "RecipeNote: not found"
            : $"RecipeNote: ready={notePtr.IsReady} visible={notePtr.IsVisible}");

        if (!notePtr.IsNull)
        {
            var button = ((AddonRecipeNote*)notePtr.Address)->TrialSynthesisButton;
            Emit($"  TrialSynthesis button: {(button == null ? "null" : $"enabled={button->IsEnabled}")}");
        }

        var player = objectTable.LocalPlayer;
        Emit($"Player: job={playerState.ClassJob.RowId} level={playerState.Level} " +
             $"cp={player?.CurrentCp.ToString() ?? "?"}/{player?.MaxCp.ToString() ?? "?"}");
        Emit(DescribeActions());

        return string.Join("\n", lines);
    }

    /// <summary>Reports a node's address, self-declared type and text, so a bad offset is visible rather than inferred.</summary>
    private static string Describe(AtkTextNode* node)
    {
        if (node == null) return "<null>";

        var type = ((AtkResNode*)node)->Type;
        if (type != NodeType.Text) return $"0x{(nint)node:X} type={type} <not a text node>";

        // The raw buffer fields are reported alongside the value so that an empty read is
        // immediately distinguishable from an empty node, rather than needing another pass.
        var text = TryReadNodeText(node, out var value) ? value.Trim() : "<unreadable>";
        return $"0x{(nint)node:X} type={type} '{text}' " +
               $"(bufUsed={node->NodeText.BufUsed} strLen={node->NodeText.StringLength} " +
               $"empty={node->NodeText.IsEmpty})";
    }

    // ── Framework loop ────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework _)
    {
        if (Mode == RecorderMode.Off) return;

        var now = Environment.TickCount64;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        try
        {
            // Deliberately gated on visibility alone. IsReady is the addon's own opinion
            // of itself and gating on it risks closing the door permanently for a window
            // that never reports ready; the per-node type validation below is the actual
            // safety guarantee, and it is the stronger of the two.
            var synthPtr = gameGui.GetAddonByName("Synthesis");
            if (!synthPtr.IsNull && synthPtr.IsVisible)
            {
                HandleCraft((AddonSynthesis*)synthPtr.Address, now);
                return;
            }

            if (inCraft)
            {
                EndSession();
                craftEndedTick = now;
            }

            if (Mode == RecorderMode.Auto && now - craftEndedTick >= RestartDelayMs)
                TryStartTrialSynthesis();
        }
        catch (Exception ex)
        {
            // A read that throws mid-craft would otherwise repeat every tick.
            log.Error(ex, "[CraftRecorder] Poll failed; stopping to avoid a spin.");
            Stop();
        }
    }

    private void HandleCraft(AddonSynthesis* synth, long now)
    {
        var step = ReadInt(synth->StepNumber);
        if (step < 0)
        {
            // Never fail silently here: an open craft whose step node cannot be read is
            // the signature of a struct-layout drift, and staying quiet about it produces
            // an empty log with no explanation.
            if (now - lastUnreadableTick > UnreadableLogIntervalMs)
            {
                lastUnreadableTick = now;
                log.Warning("[CraftRecorder] Synthesis is open but its step node is unreadable. " +
                            "Run /rmccraft probe to see what the reader sees.");
            }
            return;
        }

        if (!inCraft)
        {
            BeginSession();
            inCraft      = true;
            lastStep     = -1;
            lastStepTick = now;
        }

        if (step != lastStep)
        {
            lastStepTick = now;

            // Previous step's action never landed (player acted, or craft moved on) — record what we have.
            FlushPending(0);

            lastStep = step;
            pending  = CaptureStep(synth, step);

            var condition = pending.Condition;
            conditionCounts.TryGetValue(condition, out var seen);
            conditionCounts[condition] = seen + 1;

            // Nothing will fill in an action in Observe mode, so the line is complete as-is.
            if (Mode == RecorderMode.Observe)
                FlushPending(0);
        }

        if (Mode != RecorderMode.Auto) return;

        if (now - lastStepTick > StallTimeoutMs)
        {
            log.Warning($"[CraftRecorder] Step {step} did not advance in {StallTimeoutMs / 1000}s — stopping. " +
                        $"Check that {fillerName}/{observeName} are usable on this craft.");
            Stop();
            return;
        }

        if (pending == null) return;
        if (now - lastActionTick < ActionIntervalMs) return;

        var actionId = ChooseAction(pending.Cp);
        if (actionId == 0) return;

        if (SendAction(actionId))
        {
            lastActionTick = now;
            FlushPending(actionId);
        }
    }

    // ── Sampling ──────────────────────────────────────────────────────────────

    private CraftStepSample CaptureStep(AddonSynthesis* synth, int step) => new()
    {
        SessionId  = sessionId,
        Step       = step,
        Condition  = ReadText(synth->Condition),
        ActionId   = 0,
        Progress   = ReadInt(synth->CurrentProgress),
        Quality    = ReadInt(synth->CurrentQuality),
        Durability = ReadInt(synth->CurrentDurability),
        Cp         = objectTable.LocalPlayer?.CurrentCp ?? 0,
        Effects    = ReadEffects(synth),
        TickMs     = Environment.TickCount64,
    };

    private void FlushPending(uint actionId)
    {
        if (pending == null) return;
        Write("step", pending with { ActionId = actionId });
        StepsRecorded++;
        pending = null;
    }

    private void BeginSession()
    {
        sessionId = Guid.NewGuid().ToString("N")[..12];

        var note = GameRecipeNote.Instance();

        // SelectedRecipe indexes the recipe array directly, so the bounds are checked
        // here rather than trusted — an out-of-range read would fault the client.
        GameRecipeNote.RecipeEntry* entry = null;
        if (note != null)
        {
            var list = note->RecipeList;
            if (list != null && list->Recipes != null && list->RecipeCount > 0 &&
                list->SelectedIndex < list->RecipeCount)
                entry = list->SelectedRecipe;
        }

        Write("session", new CraftSessionHeader
        {
            Id              = sessionId,
            RecipeId        = note != null ? note->ActiveCraftRecipeId : (ushort)0,
            ItemName        = entry != null ? ReadUtf8(entry->ItemName) : string.Empty,
            JobId           = playerState.ClassJob.RowId,
            ConditionsFlag  = entry != null ? entry->ConditionsFlag : (ushort)0,
            Difficulty      = entry != null ? entry->Difficulty : (ushort)0,
            MaxQuality      = entry != null ? entry->Quality : 0,
            RequiredQuality = entry != null ? entry->RequiredQuality : 0,
            Durability      = entry != null ? entry->Durability : (ushort)0,
            Stars           = entry != null ? entry->Stars : (byte)0,
            MaxCp           = objectTable.LocalPlayer?.MaxCp ?? 0,
            StartedAtMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private void EndSession()
    {
        FlushPending(0);
        inCraft  = false;
        lastStep = -1;
        SessionsRecorded++;
    }

    // ── Driving policy (condition-blind by design) ────────────────────────────

    /// <summary>
    /// Observe advances a step for 7 CP and no durability, so spamming it is by far
    /// the cheapest way to see conditions — roughly 85 steps on a 600 CP pool. Once
    /// CP is spent, the 0-cost filler burns durability until the craft fails and the
    /// loop restarts. Neither choice looks at the condition.
    /// </summary>
    private uint ChooseAction(uint cp)
    {
        ResolveActions();
        if (observeActionId != 0 && cp >= ObserveCpCost) return observeActionId;
        return fillerActionId;
    }

    private bool SendAction(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;

        if (am->GetActionStatus(ActionType.CraftAction, actionId, NoTarget, false, false, null) != 0)
            return false;

        return am->UseAction(ActionType.CraftAction, actionId, NoTarget, 0,
                             ActionManager.UseActionMode.None, 0, null);
    }

    /// <summary>
    /// Craft action ids differ per DoH class, so they are resolved from the sheet for
    /// the job actually in use rather than hardcoded. Identification is by CP cost,
    /// which separates the two cleanly: the filler is the lowest-level 0-cost action
    /// (Basic Synthesis), and Observe is the only non-specialist 7-cost one. Specialist
    /// rows are excluded so Careful Observation cannot be mistaken for the filler —
    /// and because a reroll would bias the very distribution being measured.
    /// </summary>
    private void ResolveActions()
    {
        var job = playerState.ClassJob.RowId;
        if (job == 0 || job == resolvedForJob) return;

        var sheet = dataManager.GetExcelSheet<CraftAction>();
        if (sheet == null) return;

        uint filler = 0, observe = 0;
        int fillerLevel = int.MaxValue, observeLevel = int.MaxValue;
        string fillerN = "?", observeN = "?";

        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (row.ClassJob.RowId != job) continue;
            if (row.Specialist) continue;
            if (row.ClassJobLevel > playerState.Level) continue;

            if (row.Cost == 0 && row.ClassJobLevel < fillerLevel)
            {
                filler = row.RowId; fillerLevel = row.ClassJobLevel; fillerN = row.Name.ExtractText();
            }
            else if (row.Cost == ObserveCpCost && row.ClassJobLevel < observeLevel)
            {
                observe = row.RowId; observeLevel = row.ClassJobLevel; observeN = row.Name.ExtractText();
            }
        }

        fillerActionId  = filler;
        observeActionId = observe;
        fillerName      = fillerN;
        observeName     = observeN;
        resolvedForJob  = job;

        log.Information($"[CraftRecorder] Actions for job {job}: filler={fillerN} ({filler}), observe={observeN} ({observe})");
    }

    // ── Restarting the craft ──────────────────────────────────────────────────

    /// <summary>
    /// Presses Trial Synthesis on the open Recipe Note. Silent no-op when the window
    /// is closed or the button is disabled, so the loop simply idles until the player
    /// puts the game back in a state where it can continue.
    /// </summary>
    private void TryStartTrialSynthesis()
    {
        var notePtr = gameGui.GetAddonByName("RecipeNote");
        if (notePtr.IsNull || !notePtr.IsReady || !notePtr.IsVisible) return;

        var note = (AddonRecipeNote*)notePtr.Address;
        if (ClickButton(&note->AtkUnitBase, note->TrialSynthesisButton))
            craftEndedTick = Environment.TickCount64; // re-arm the delay in case the click did not take
    }

    /// <summary>
    /// Dispatches the button's own registered ButtonClick event rather than guessing a
    /// callback index, so it stays correct across patches that renumber callbacks.
    /// </summary>
    private static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null) return false;
        if (!button->IsEnabled) return false;

        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null) return false;

        var evt = ((AtkResNode*)owner)->AtkEventManager.Event;
        while (evt != null && evt->State.EventType != AtkEventType.ButtonClick)
            evt = evt->NextEvent;
        if (evt == null) return false;

        addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt, null);
        return true;
    }

    // ── Addon reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a text node before dereferencing it. A pointer that does not report
    /// <see cref="NodeType.Text"/> is either not built yet or is not the node the struct
    /// definition claims — and reading its string would fault the client outright rather
    /// than throw something catchable, because access violations in native memory are not
    /// .NET exceptions. This check is also what keeps a struct-layout drift between the
    /// compiled ClientStructs and the running client from becoming a crash instead of an
    /// empty read.
    /// </summary>
    private static bool TryReadNodeText(AtkTextNode* node, out string text)
    {
        text = string.Empty;
        if (node == null) return false;
        if (((AtkResNode*)node)->Type != NodeType.Text) return false;

        // Read through the pointer and do not gate on StringLength. The client does not
        // maintain that field for addon-populated nodes — it reads 0 on nodes that are
        // plainly displaying text — so using it as an emptiness signal silently discards
        // every value. ToString handles a genuinely empty buffer on its own.
        text = node->NodeText.ToString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// Text nodes carry localised display strings, so digits are extracted rather than
    /// parsed — this survives thousands separators and any client language. Returns -1
    /// when the node is unreadable or holds no digits, which the caller treats as
    /// "not ready" and skips the tick.
    /// </summary>
    private static int ReadInt(AtkTextNode* node)
    {
        if (!TryReadNodeText(node, out var text)) return -1;

        long value = 0;
        var any = false;

        foreach (var ch in text)
        {
            if (ch is < '0' or > '9') continue;
            value = (value * 10) + (ch - '0');
            any = true;
            if (value > int.MaxValue) return int.MaxValue;
        }

        return any ? (int)value : -1;
    }

    private static string ReadText(AtkTextNode* node) =>
        TryReadNodeText(node, out var text) ? text.Trim() : string.Empty;

    /// <summary>Reads an inline game string, treating an unset buffer as empty rather than reading it.</summary>
    private static string ReadUtf8(Utf8String value) =>
        value.StringLength <= 0 ? string.Empty : value.ToString();

    /// <summary>
    /// Active buffs with their remaining steps, named as the addon displays them.
    /// Slots are skipped unless their container component is present — the effect pane
    /// is rebuilt whenever a buff is applied or expires, which is precisely when an
    /// action is used, so this runs against a structure in flux every single step.
    /// </summary>
    private static string[] ReadEffects(AddonSynthesis* a)
    {
        var slots = new[]
        {
            a->CraftEffect1, a->CraftEffect2, a->CraftEffect3, a->CraftEffect4, a->CraftEffect5,
            a->CraftEffect6, a->CraftEffect7, a->CraftEffect8, a->CraftEffect9,
        };

        var active = new List<string>(slots.Length);
        foreach (var slot in slots)
        {
            if (slot.Container == null) continue;

            var name = ReadText(slot.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var steps = ReadText(slot.StepsRemaining);
            active.Add(string.IsNullOrWhiteSpace(steps) ? name : $"{name}:{steps}");
        }

        return active.ToArray();
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private void Write(string type, object payload)
    {
        if (writer == null) return;
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new { type, data = payload }));
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CraftRecorder] Write failed.");
        }
    }

    private void CloseWriter()
    {
        writer?.Dispose();
        writer = null;
    }

    /// <summary>One-line status for the command handler.</summary>
    public string Summarise()
    {
        if (Mode == RecorderMode.Off) return "Craft recorder: off.";

        var seen = new List<string>(conditionCounts.Count);
        foreach (var (name, count) in conditionCounts)
            seen.Add($"{name} {count}");
        seen.Sort(StringComparer.Ordinal);

        var tally = StepsRecorded > 0 ? string.Join(", ", seen) : "no steps yet";
        return $"Craft recorder: {Mode}. {SessionsRecorded} craft(s), {StepsRecorded} step(s). [{tally}] → {OutputPath}";
    }
}
