using System;
using System.Collections.Concurrent;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace RedMoonCappuccino.RotationRecorder;

/// <summary>
/// Hooks ActionManager.UseAction to record every action the player presses.
/// Thread-safe: hook fires on framework thread, Events queue is ConcurrentQueue.
///
/// Usage:
///   Start()  → begin recording
///   Stop()   → end session, Events + SessionStart/End are ready to read
///   Clear()  → wipe state for a new session
///   Dispose() in your plugin's Dispose()
/// </summary>
public sealed unsafe class ActionRecorder : IDisposable
{
    // ── Services ─────────────────────────────────────────────────────────────
    private readonly IObjectTable  _objectTable;
    private readonly IDataManager  _dataManager;
    private readonly IPluginLog    _log;

    // ── Hook ─────────────────────────────────────────────────────────────────
    private readonly Hook<ActionManager.Delegates.UseAction> _useActionHook;

    // ── State ─────────────────────────────────────────────────────────────────
    public ConcurrentQueue<ActionEvent> Events       { get; } = new();
    public bool                         IsRecording  { get; private set; }
    public DateTimeOffset?              SessionStart { get; private set; }
    public DateTimeOffset?              SessionEnd   { get; private set; }

    public ActionRecorder(
        IGameInteropProvider gameInterop,
        IObjectTable         objectTable,
        IDataManager         dataManager,
        IPluginLog           log)
    {
        _objectTable = objectTable;
        _dataManager = dataManager;
        _log         = log;

        // Pattern 3: CS-typed delegate — most stable across patches
        _useActionHook = gameInterop.HookFromAddress<ActionManager.Delegates.UseAction>(
            (nint)ActionManager.MemberFunctionPointers.UseAction,
            OnUseAction);
        _useActionHook.Enable();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRecording) return;
        Clear();
        SessionStart = DateTimeOffset.UtcNow;
        IsRecording  = true;
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        SessionEnd  = DateTimeOffset.UtcNow;
    }

    public void Clear()
    {
        IsRecording  = false;
        SessionStart = null;
        SessionEnd   = null;
        while (Events.TryDequeue(out _)) { }
    }

    public void Dispose() => _useActionHook.Dispose();

    // ── Hook detour ───────────────────────────────────────────────────────────

    private bool OnUseAction(
        ActionManager* self,
        ActionType     actionType,
        uint                        actionId,
        ulong                       targetId,
        uint                        a4,
        ActionManager.UseActionMode a5,
        uint                        a6,
        bool*                       a7)
    {
        try
        {
            // Only record combat actions while session is active
            if (IsRecording && actionType == ActionType.Action)
                RecordAction(self, actionId);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[RotationRecorder] UseAction detour failed");
        }

        return _useActionHook.Original(self, actionType, actionId, targetId, a4, a5, a6, a7);
    }

    private void RecordAction(ActionManager* self, uint actionId)
    {
        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // ── Action metadata from Lumina ───────────────────────────────────
        string actionName;
        bool   isGcd;
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()!;
            var row   = sheet.GetRow(actionId);
            actionName = row.Name.ExtractText();
            // ActionCategory: 2 = Weaponskill (GCD), 3 = Spell (GCD), 4 = Ability (oGCD)
            isGcd = row.ActionCategory.RowId is 2 or 3;
        }
        catch
        {
            // Unknown action (e.g. limit break, sprint) — still record it
            actionName = $"Action#{actionId}";
            isGcd      = false;
        }

        // ── Cooldown state at time of press ───────────────────────────────
        var recastTotal   = self->GetRecastTime(ActionType.Action, actionId);
        var recastElapsed = self->GetRecastTimeElapsed(ActionType.Action, actionId);
        var wasOnCooldown = recastTotal > 0 && recastElapsed < recastTotal;

        Events.Enqueue(new ActionEvent
        {
            Timestamp     = DateTimeOffset.UtcNow,
            ActionId      = actionId,
            ActionName    = actionName,
            IsGcd         = isGcd,
            Mp            = player.CurrentMp,
            MaxMp         = player.MaxMp,
            WasOnCooldown = wasOnCooldown,
        });
    }
}
