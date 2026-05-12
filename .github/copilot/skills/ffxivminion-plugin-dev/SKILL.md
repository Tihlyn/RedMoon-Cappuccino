---
name: ffxivminion-plugin-dev
description: Use whenever the user is building, debugging, or extending plugins for FFXIVMinion (MMOMinion's FFXIV bot — NOT Dalamud/XIVLauncher, which is an entirely different framework). Covers six interlocking domains — module scaffolding (`module.def`, `ml_task`, `RegisterEventHandler` lifecycle), ACR / Advanced Combat Routine authoring, navigation/mesh/movement (`Player:MoveTo`, OMC, markers, teleport), the `GUI:` ImGui Lua binding, combat telemetry (`Player`/`Entity`, target-of-target, threat web, HP velocity / TTK), and Argus AOE detection + dodging (telegraphs, hit tests, dodge orchestrator, packet hooks). Triggers on FFXIVMinion-specific names — `MEntityList`, `MGetTarget`, `ml_task_hub`, `ml_global_information`, `ACR.GetSetting`, `Argus.getCurrentAOEs`, `minionlib`, `LuaMods\FFXIVMinion64` — and tasks like writing a `CombatRoutines/` profile, a healer mitigation plugin, a world-space overlay, or stuck-recovery logic. Apply even when the user describes engine state without naming the framework.
---

# FFXIVMinion Plugin Development

This skill bundles engineering-grade reference material for writing Lua plugins for **FFXIVMinion** (MMOMinion's FFXIV bot). It is **not** for Dalamud / XIVLauncher — that's a separate plugin framework for the same game with entirely different APIs.

## How to use this skill

1. **Read this file first.** It covers the substrate everything else rests on: runtime, file layout, lifecycle events, the `ml_global_information` table, and idioms that bite every plugin regardless of what it does.
2. **Pick the relevant reference(s).** Plugins compose multiple domains (a healer plugin = scaffolding + ACR + telemetry + Argus). Read the references for the domains in scope — see the selector at the bottom of this file.
3. **Write code that integrates with existing conventions.** The codebase has strong idioms (cause/effect framework, named events, memoized accessors). Match them; don't invent parallel patterns.

## Substrate

Every FFXIVMinion plugin runs in the same environment. Internalize this once, then specialise via references.

### Runtime

- **Lua 5.1 / LuaJIT-compatible.** No `goto`, no integer division operator, no Lua 5.2+ features.
- **Non-standard host extensions** are pre-loaded as globals: `hpairs`, `istable`, `Now()` (ms since launch), `TimeSince(t)`, `bit.bor`/`bit.band`/etc., `d(message)` (debug log), `GetString(key)` (i18n), `GetUUID()`, `GetStartupPath()`, `FileExists`, `FolderExists`, `FolderCreate`, `FileSave`/`FileLoad`, the `persistence.*` table-persistence library.
- **File paths use Windows backslashes** — escape them (`"C:\\Foo\\Bar"`) or use long-bracket strings (`[[C:\Foo\Bar]]`).
- **`os.clock()` is wrong** — always use `Now()` (ms) and `TimeSince(t)` so you compose with the engine's tick clock.

### Filesystem & loader

Plugins live under `C:\MINIONAPP\Bots\FFXIVMinion64\LuaMods\<pluginname>\`. The folder name **must match** the `Name=` key in `module.def`. On startup and on **Reload Lua**, the bot enumerates `LuaMods\`, parses each `module.def`, resolves dependencies, then concatenates and executes the listed `.lua` files in order.

Concatenation is the load model — that means **top-level `local` becomes module-scoped** after merge. Wrap your module in a single `local mymod = {}` table; don't rely on file-private `local`.

### `module.def` — INI, not Lua

Every plugin needs one. The minimum:

```ini
[Module]
Name=YourAddon
Dependencies=minionlib,FFXIVMinion
Version=1
Files=yourcode.lua
Enabled=1
```

Optional keys: `CreateFolders=settings,log` (subfolders to ensure exist on load — case-sensitive), `ExportFiles=files/Alert.wav` (extracts bundled assets), `SharedAccess=<UUIDs>` (private/store addons only). Files in `Files=` load **in order** — important when later files reference earlier ones.

### Lifecycle events

There is no fixed `OnLoad`. Hook lifecycle by calling `RegisterEventHandler(event, fn, "uniqueId")` at top-level. The third arg is **mandatory and must be unique per registration** — same name replaces a prior binding, which is what makes Reload Lua idempotent.

| Event | Fires | Signature |
|---|---|---|
| `Module.Initalize` *(sic — wiki misspelling, real event name)* | Once after every module loaded | `fn(event, ticks)` |
| `Module.Start` | User clicked Start | `fn(event)` |
| `Module.Cleanup` / `Module.Unload` | On reload/unload | `fn(event)` |
| `Gameloop.Update` | Per game tick (~10–60 Hz; throttled to ~30 Hz) — main work loop | `fn(event, tickcount)` |
| `Gameloop.Draw` | Per render frame — **only valid place for `GUI:*` calls** | `fn(event, ticks)` |
| `Gameloop.Tick` | Slower scheduled tick | `fn(event, tickcount)` |
| `Game.UIEvent` | In-game UI message | `fn(eventName, eventJson)` |
| `RefreshBehaviorFiles` | BT manager (re)load `.bt` files | `fn()` |
| `GUI.Item` | UI widget interaction | `fn(event, button)` |

Per-frame order: `Gameloop.Update` → engine `ml_global_information.PreUpdate` (clears memoize tables) → bot's `OnUpdate` (dispatches by `MGetGameState()` returning `FFXIV.GAMESTATE.INGAME / MAINMENUSCREEN / CHARACTERSCREEN / ERROR`) → `Gameloop.Draw`. Throttle your own work with `if Now() < self.nextTick then return end`.

**`Module.Initalize` typo is canonical.** Don't "fix" it — that's the event name the engine fires.

Custom events: `QueueEvent("MyEvent", "stringArg")` enqueues; `RegisterEventHandler("MyEvent", fn, "id")` consumes. Args must be strings; at least one is required (use `""`).

### `ml_global_information` — the shared per-frame state table

A giant globally-mutable table holding shared per-frame state. Selected fields:

- **Time/loop**: `Now`, `lasttick`, `throttleTick`, `updateFoodTimer`, `repairTimer`, `syncTimer`, `movementDelay`.
- **Combat**: `AttackRange` (number — **always use this** instead of computing range yourself), `MeshReady` (bool).
- **Engine**: `preparers` (append your fn to run in PreUpdate), `path` (FFXIVMinion's own LuaMods path), `drawMode`, `queueLoader`, `meshTranslations`.
- **Helpers (functions on the table)**:
  - `Init()`, `PreUpdate(event, tickcount)`, `OnUpdate(event, tickcount)`
  - `Queue(delayMs, fn)`, `Queueables()`, `IsYielding()`
  - `Await(intervalMs, predicateFn)` / `Await(intervalMs, timeoutMs, predicateFn)` — yield until predicate true
  - `AwaitDo(timeoutMs, conditionFn, actionFn)`
  - `ShowInformation(message, durationMs)`, `GetMovementInfo(legacy)`, `LoadBehaviorFiles()`
- **Per-class**: `CurrentClass.options.settings`, `CurrentClass.optionsPath`.

The `Await` family is the standard way to wait for state changes (cast confirmation, mesh load, teleport finish) — use it instead of busy loops or sleep.

### `ml_*` managers (the API surface)

| Object | Role |
|---|---|
| `ml_task` | Base class; subclass via `inheritsFrom(ml_task)`. |
| `ml_task_hub` | Singleton — `:CurrentTask()`, `:ThisTask()`, `:Add(task, GOAL_LEVEL, PRIORITY)`, `:CurrentTask():AddSubTask(t)`. |
| `ml_cause` / `ml_effect` | Cause-and-effect framework used inside `ml_task:Process()`. Subclass each. |
| `ml_element` | Triplet `(cause, effect, priority)` — `ml_element:create(name, c_obj, e_obj, prio)` then `task:add(elem, task.process_elements)`. |
| `ml_marker` / `ml_marker_mgr` | Map markers (Mining/Botany/Fishing/Grind/Hunt). |
| `ml_mesh_mgr` | Navigation meshes. |
| `ml_navigation` | Higher-level Lua MoveTo wrapper. |
| `ml_gui.ui_mgr` | Side-menu integration: `:AddComponent(t)`, `:AddMember(t, headerId)`. |
| `BehaviorManager` | BT engine; FFXIVMinion uses cause/effect idiomatically (BT exists but is rarer). |
| `RenderManager` | 3D draw — `:AddObject(name, vertsTable)`, `:WorldToScreen(pos, fast?)`. |
| `EntityList` / `MEntityList` | Entity queries via filter strings. **Always prefer `M*` (memoized) inside `Gameloop.Update`.** |
| `NavigationManager` | C-side nav — `:GetPath(...)`, `:MoveTo(...)`, `:GetNavMeshState()`. |
| `ActionList` | `:Get(id, type)`, `:IsCasting()`. |
| `Inventory` | `:GetItemDetails(id)`. |
| `Quest` | `:HasQuest(id)`, `:IsQuestCompleted(id)`. |

## Universal idioms & gotchas

These bite every plugin, regardless of domain. Internalize them — don't relitigate per file.

- **Pair every `GUI:Begin*` with `GUI:End*`** regardless of `visible`. The ImGui state machine breaks otherwise.
- **Never store an `Entity` reference across frames.** Re-fetch every frame: `local t = EntityList:Get(savedId)` or via `MEntityList(filter)`. Stale refs return junk fields silently.
- **Use `MEntityList` / `MGetTarget()` (memoized) over `EntityList` / `Player:GetTarget()`** inside `Gameloop.Update`. The `M*` versions cache for the frame; the unmemoized ones repeat expensive engine calls.
- **`RegisterEventHandler`'s 3rd arg must be unique** across the bot. Reusing names causes silent handler replacement on reload.
- **Cause functions must be cheap and idempotent** — they're called speculatively in the cause/effect dispatch loop. Side effects belong in `effect:execute()`.
- **BT action nodes must return `self:success() / :fail() / :running()` on every code path.** Missing return = node runs forever.
- **Don't store `GetPrivateModuleFunctions()` in a global** — keep it `local` (security model relies on lexical scoping).
- **File-merge means top-level `local` is module-scoped**; encapsulate via a single `local mymod = {}`.
- **`hp.current == 0` does not mean dead** — there are a few ticks after death where `alive == true` and `hp.current == 0`. Trust `alive`.
- **`distance` only updates when `Player.pos` updates** — frozen during teleports / loading screens. Gate distance checks on `not MIsLoading()`.
- **No "class-change" event.** Re-key job-dependent state off `Player.job` in `OnUpdate` and reset when it changes.
- **Wrap user-visible strings with `GetString("English")`** for i18n. Translations live in `languages.lua`; missing keys fall back to the English source.
- **`Player:SetSpeed()` is removed** (Dec 2014). Don't use it.
- **The bot has 32-bit (`FFXIVMinion`) and 64-bit (`FFXIVMinion64`) folder splits.** Modern code is 64-bit only.

## Pick a reference

Choose by what the plugin actually does. Most non-trivial plugins read 2–4 of these.

| If the task involves... | Read |
|---|---|
| First plugin / `module.def` / lifecycle / `ml_task` subclass / persistence (Settings/persistence.store/FileSave) / localization / minimum viable plugin | `references/scaffolding.md` |
| Authoring a `CombatRoutines/<Profile>.lua` / `profile.Cast()` / `ActionList` / `HasBuff` / rotation priorities / interrupt logic / `ACR.GetSetting`-`SetGUIVar` | `references/acr.md` |
| `Player:MoveTo` / mesh files & `.nx2`/`.omc`/`.cub` / `ml_navigation` / OMC instructions / markers / teleport / mount/fly / stuck recovery / cross-zone tasks | `references/navigation.md` |
| Any `GUI:*` calls / windows / widgets / tabs / popups / custom draw / `RenderManager:WorldToScreen` / button-bar / overlays / icons | `references/gui.md` |
| `Player`/`Entity` field reference / target-of-target / threat web / party iteration / role detection / best-heal-target / HP velocity & TTK / "tank lost aggro" detection | `references/combat-telemetry.md` |
| `Argus.*` / AOE detection / `getCurrentAOEs` / telegraph hit tests / dodge logic / `registerOnEntityCast` & friends / tether/marker hooks / world-space telegraph drawing | `references/argus-aoe.md` |

When multiple apply, read scaffolding first (it's the prereq), then domain-specific. Reference files repeat *some* substrate when it can't be cleanly separated, but they're written assuming you've already read this file.

## Source pointers

When the references aren't enough or seem out of date, these are authoritative:

- **Wiki hub**: https://wiki.mmominion.com/doku.php?id=lua_api
- **MinionLib**: https://wiki.mmominion.com/doku.php?id=minionlib
- **GUI API** (canonical, last updated 2024-06-23): https://wiki.mmominion.com/doku.php?id=gui_api
- **GUI changelog** (deprecated functions): https://wiki.mmominion.com/doku.php?id=gui_api_changelog
- **ACR API**: https://wiki.mmominion.com/doku.php?id=acr
- **Argus user docs**: https://wiki.mmominion.com/doku.php?id=argus
- **Argus full API reference**: https://wiki.mmominion.com/doku.php?id=argusdocs
- **Navmesh editor / file formats**: https://wiki.mmominion.com/doku.php?id=navmesheditor
- **Private/store addons**: https://wiki.mmominion.com/doku.php?id=private_addon_developer_help

**Source code (trust over wiki when they conflict — wiki has stale pages)**:

- **FFXIVMinion repo**: https://github.com/MINIONBOTS/FFXIVMinion
  - High-value files: `ffxivminion/ffxiv.lua`, `ffxiv_init.lua`, `ffxiv_skillmgr.lua`, `ffxiv_helpers.lua`, `ffxiv_navigation.lua`, `ffxiv_common_tasks.lua`, `ffxiv_common_cne.lua`, `ffxiv_radar.lua`, `ffxiv_task_assist.lua`, `ffxiv_task_gather.lua`, `ffxiv_task_grind.lua`, `ffxiv_unstuck.lua`, `Dev/dev.lua`, `languages.lua`.
- **GitHub wiki** (Player, Entity, EntityList, ActionList, Quest, Enumerations — note: Player page last edited 2015, lags reality): https://github.com/MINIONBOTS/FFXIVMinion/wiki
- **MinionLib repo** (e.g. `ml_marker.lua`): https://github.com/mmoalt/MinionLib
- **Sister bots** for triangulating shared API: ESOMinion `globals.lua`, gw2minion.
- **Community plugins** for layout reference: `mushroom8009/AetheryteHelper`, `mmokitanoi/dungeonprofiles`, `KaliMinion/Blue-Mage-ACR`, `Rikudouu/XIVOpeners`.

When the wiki and source disagree (most often on `Player:MoveTo` argument count, removed methods, or post-2018 GUI changes), trust source.

## Caveats / open items

- The ACR LuaMod itself (`LuaMods\ACR\`) is closed-source. `ACR.AddPrivateProfile`, `ACR.SetGUIVar`, `ACR.GetSetting`, the loader, and the multi-clickable Party Interface are reverse-engineered from wiki + community use.
- Aetheryte ids and mesh/mapid mappings drift across patches. Pull live where possible.
- The stuck-handler "Teleport to local Aetheryte" path historically unreliable; gate behind `gStuckTeleport`.
- `OnX` lifecycle hooks beyond those documented (`Cast/OnUpdate/OnLoad/OnOpen/OnClick/Draw/DrawHeader/DrawFooter` for ACR) aren't catalogued — treat the documented set as complete.
- Argus is intentionally **withheld for the first weeks of a new raid tier** for safety. Don't assume freshly-released content has Argus support.
- `Argus.registerOnAOECreateFunc` callbacks receive a **copy** that does not update positions for attached AOEs — re-resolve `targetAttach`'s `pos` every frame.
