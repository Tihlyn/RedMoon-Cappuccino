---
name: dalamud-party-and-game-state
description: Read FFXIV game state from a Dalamud / XIVLauncher plugin — local player, party / alliance members, current target, focus / mouseover / soft target, condition flags (in combat, mounted, casting, between areas), duty progress (started, wiped, completed), territory and world / job / level info, and Lumina Excel sheet rows (ClassJob, World, Action, Status, Item, TerritoryType, ContentFinderCondition). Use whenever a Dalamud plugin needs party HP / MP, current target details, in-combat checks, duty-started events, the local player's class abbreviation / world / content ID, or row data from the game's Excel sheets. Critical for v15 correctness — IClientState.LocalPlayer was REMOVED, use IObjectTable.LocalPlayer instead; IPlayerState.ContentId / HomeWorld / CurrentWorld survive logout and zone-change boundaries; IObjectTable / IPartyList throw on non-main-thread access (wrap with IFramework.RunOnFrameworkThread). Also covers FFXIVClientStructs as the escape hatch for state Dalamud's services don't expose.
---

# Dalamud Party and Game State

This is the "what is the game doing right now" skill — reading the local player, party, target, conditions, duty, and Excel data inside a Dalamud plugin. The bundled `dalamud-services-reference` has the lookup table; this skill has the working idioms with the v15-correct API surface.

## When this skill applies

Trigger phrases include "party HP", "party members", "current target", "focus target", "in combat", "in duty", "world name", "job", "current player", "territory", "what zone", "what raid", "ContentFinder", "iterate the party", "loop over players", "find by ObjectId". If the user is wiring up a feature that *responds* to game state — overlays, tracking, alerts, UI that shows live values — this skill applies.

## The v15 LocalPlayer rule (the #1 deprecated-pattern trap)

**`IClientState.LocalPlayer` was removed in v15. Use `IObjectTable.LocalPlayer` instead.** Most older code samples (including the Mintlify wiki) still show `Plugin.ClientState.LocalPlayer` — that path no longer exists.

```csharp
// v15-correct
var player = Plugin.ObjectTable.LocalPlayer;
if (player == null) return;   // happens during loading screens, character select, etc.

var name      = player.Name.TextValue;
var classJob  = player.ClassJob.Value.Abbreviation.ExtractText();
var territory = Plugin.ClientState.TerritoryType;
```

For values that need to survive the local player GameObject's lifetime (pre-login, zone transitions, mid-cutscene), use `IPlayerState` instead — it's stable across those gaps:

```csharp
var contentId = Plugin.PlayerState.ContentId;
var homeWorld = Plugin.PlayerState.HomeWorld.Value.Name.ExtractText();
var current   = Plugin.PlayerState.CurrentWorld.Value.Name.ExtractText();
```

## Iterating the party — the headline use case

`IPartyList` snapshots the game's group manager at the moment of access. It's fine to enumerate it inside `Draw()` or `Framework.Update`; just don't cache the per-member references across frames since the underlying objects can be invalidated.

```csharp
foreach (var member in Plugin.PartyList)
{
    var name      = member.Name.TextValue;
    var hp        = member.CurrentHP;
    var maxHp     = member.MaxHP;
    var jobAbbr   = member.ClassJob.Value.Abbreviation.ExtractText();
    var worldName = member.World.Value.Name.ExtractText();
    var level     = member.Level;
    var statuses  = member.Statuses;        // status-effect list
    var pos       = member.Position;        // Vector3 world coords
    var objectId  = member.ObjectId;

    var hpPercent = maxHp == 0 ? 0f : (float)hp / maxHp;
    // ...render or compute...
}
```

### Edge cases to handle

- **Solo but in a group:** `Plugin.PartyList.Length == 0` even though the player has joined a party in some pre-load states. Fall back to filtering `IObjectTable` by `ObjectKind.Player` if you need a result during these moments.
- **Alliance raids:** `Plugin.PartyList.IsAlliance` is `true`. Slots 0–7 are your immediate party; slots 8–47 are the other two alliances, accessed via `Plugin.PartyList.GetAllianceMember(int index)`.
- **Cross-world parties:** `member.World` is the *member's* home world (a `RowRef<World>`), not necessarily the same as `Plugin.PlayerState.HomeWorld`.
- **Empty members:** in 8-person duties some slots can read as null/zero during transitions. Check `member.ObjectId != 0` before treating data as valid.

## Conditions and duty events

```csharp
// Synchronous condition checks — cheap, OK to call every Draw().
if (Plugin.Condition[ConditionFlag.InCombat])      { /* ... */ }
if (Plugin.Condition[ConditionFlag.BetweenAreas])  return;       // mid-zone-load
if (Plugin.Condition[ConditionFlag.BoundByDuty])   { /* ... */ }
if (Plugin.Condition[ConditionFlag.Mounted])       { /* ... */ }
if (Plugin.Condition[ConditionFlag.Casting])       { /* ... */ }
if (Plugin.Condition[ConditionFlag.WatchingCutscene]) return;

// Event-driven duty lifecycle.
Plugin.DutyState.DutyStarted    += (_, territoryId) => Plugin.Log.Info($"Duty started: {territoryId}");
Plugin.DutyState.DutyWiped      += (_, territoryId) => Plugin.Log.Info($"Wiped in: {territoryId}");
Plugin.DutyState.DutyRecommenced += (_, territoryId) => Plugin.Log.Info($"Restarted: {territoryId}");
Plugin.DutyState.DutyCompleted  += (_, territoryId) => Plugin.Log.Info($"Cleared: {territoryId}");
```

Always unsubscribe in `Dispose()` to avoid leaking handlers across plugin reloads.

Useful condition flags for typical plugins: `InCombat`, `Mounted`, `Casting`, `BetweenAreas`, `BoundByDuty`, `Crafting`, `Gathering`, `OccupiedInQuestEvent`, `WatchingCutscene`, `Performing`, `Disguised`. The full enum is in `Dalamud.Game.ClientState.Conditions.ConditionFlag`.

## Targets — current, focus, mouseover, soft

```csharp
var current   = Plugin.TargetManager.Target;          // current hard target (Tab)
var focus     = Plugin.TargetManager.FocusTarget;     // focus target (Shift+F2)
var mouseover = Plugin.TargetManager.MouseOverTarget; // hovered nameplate / model
var soft      = Plugin.TargetManager.SoftTarget;      // controller / "soft" target
var previous  = Plugin.TargetManager.PreviousTarget;
```

All return `IGameObject?` — null is the common case. Check `Kind` (`ObjectKind.Player`, `Pc`, `BattleNpc`, `EventNpc`, etc.) before downcasting to `IBattleChara` for HP/MP/level.

## Lumina Excel sheets — the canonical data source

v11 upgraded Dalamud to Lumina 5. Rows are now `struct` types implementing `IExcelRow<T>`, and string fields are `ReadOnlySeString` — call `.ExtractText()` to get a plain `string`.

```csharp
using Lumina.Excel.Sheets;

var jobs   = Plugin.DataManager.GetExcelSheet<ClassJob>();
var pld    = jobs.GetRow(19);                  // 19 = Paladin
var name   = pld.Name.ExtractText();           // "Paladin"
var abbr   = pld.Abbreviation.ExtractText();   // "PLD"

var worlds   = Plugin.DataManager.GetExcelSheet<World>();
var actions  = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
//             ^ explicit Lumina.Excel.Sheets.Action — there's also System.Action
var statuses = Plugin.DataManager.GetExcelSheet<Status>();
var items    = Plugin.DataManager.GetExcelSheet<Item>();
var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();
var duties   = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
```

Iteration is straightforward — `ExcelSheet<T>` is enumerable:

```csharp
foreach (var status in statuses) {
    if (status.RowId == 0) continue;
    var statusName = status.Name.ExtractText();
    // ...
}
```

For sheets with sub-rows (multiple records per RowId, e.g. recipes per item), use `Plugin.DataManager.GetSubrowExcelSheet<T>()` and access via `(rowId, subRowId)`.

The most commonly used sheets in party / overlay plugins: `ClassJob`, `World`, `TerritoryType`, `Action` (`Lumina.Excel.Sheets.Action` — qualify it), `Status`, `Item`, `ContentFinderCondition`, `BNpcName`, `Mount`, `Companion`, `Achievement`, `Quest`, `OnlineStatus`.

`RowRef<T>` fields (e.g. `member.ClassJob`, `member.World`) — call `.Value` to dereference, then `.Name.ExtractText()` etc. on the resulting struct.

## FFXIVClientStructs — the escape hatch

When a value is in the running game but Dalamud's services don't expose it directly, `FFXIVClientStructs` (CS) gives you a typed C# wrapper over the game's native structures. CS uses pointers, so the surrounding method or block must be marked `unsafe`:

```csharp
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

unsafe
{
    var cooldown     = ActionManager.Instance()->GetRecastTime(ActionType.Action, 7);
    var groupManager = GroupManager.Instance();
    var memberCount  = groupManager->MainGroup.MemberCount;

    // PlayerState carries things IPlayerState doesn't (yet) expose.
    var playerState  = PlayerState.Instance();
    var isMentor     = playerState->IsMentor();
}
```

CS pointers can be invalidated by zone change / logout / the game tearing down a singleton during a transition. **Don't store them across frames**; re-fetch via `Instance()` each time. Treat them as transient view onto the current game state, not as durable handles.

The CS namespaces you'll touch most often:

- `FFXIVClientStructs.FFXIV.Client.Game` — `ActionManager`, `Control`, `World`, `Object` (entities)
- `FFXIVClientStructs.FFXIV.Client.Game.UI` — `PlayerState`, `UIState`, `RaptureMacroModule`, `RaptureHotbarModule`
- `FFXIVClientStructs.FFXIV.Client.UI` — `GroupManager`, `AgentChatLog`, `RaidGroup`
- `FFXIVClientStructs.FFXIV.Client.UI.Misc` — chat, configs, save state

## Threading rules — when to use `RunOnFrameworkThread`

v12 made `IObjectTable`, `IPartyList`, and several `IClientState` properties throw `InvalidOperationException` when accessed off the framework thread. CS pointer dereferences can also trip if the singleton is being torn down on another thread.

If your code path runs:

- **Inside `Draw()` or `IFramework.Update`** → already on the framework thread, free to read state directly.
- **Inside a chat-message / nameplate / addon-lifecycle handler** → same, those fire on the framework thread.
- **Inside `Task.Run`, async continuations, `Thread`, `Timer.Elapsed`, or any background worker** → wrap with `Plugin.Framework.RunOnFrameworkThread(...)`:

```csharp
var partySnapshot = await Plugin.Framework.RunOnFrameworkThread(() =>
{
    // Snapshot to value types so we can use them on the calling thread.
    var members = new List<(string Name, uint Hp, uint MaxHp)>();
    foreach (var m in Plugin.PartyList)
        members.Add((m.Name.TextValue, m.CurrentHP, m.MaxHP));
    return members;
});
```

For "do this in a few ticks" use `Plugin.Framework.RunOnTick(action, delayTicks: 5)` — runs on the framework thread after N frames.

## Pitfalls (in priority order)

1. **`IClientState.LocalPlayer` does not exist in v15.** Always `IObjectTable.LocalPlayer`. Linting that does not catch this kills hours of your time.
2. **`IPartyList` snapshots at access time** — for cross-frame work, copy the values you need into a `List<...>` inside one tick.
3. **Never call `IObjectTable[i]` or `IPartyList[i]` from a non-main thread.** v12+ throws.
4. **`RowRef<T>.Value` can return a default-zeroed row** if the RowId is invalid — check `RowId != 0` (or use `.ValueNullable`) on rows from user-supplied data.
5. **Don't leak event subscriptions.** Every `+= handler` needs a matching `-=` in `Dispose()`. Reload the plugin once with `/xlreload` to catch leaks — duplicate logs on the second load mean a lingering handler.
6. **Storing other players' content IDs is a bright-line rejection trigger** for the official plugin repo (privacy / security boundary). Don't write a feature that depends on persisting `ObjectId` or `ContentId` of non-self characters.

## Cross-references

- For the full set of services these calls go through, see `dalamud-services-reference`.
- For rendering this state in a window, see `dalamud-imgui-windowing`.
- For reacting to chat events that include game-state context (e.g. "log who pulled a wipe"), see `dalamud-chat-commands-ipc`.
- For hooking specific game functions when the state you need isn't exposed by any service or CS field, see `dalamud-hooking-and-signatures`.
