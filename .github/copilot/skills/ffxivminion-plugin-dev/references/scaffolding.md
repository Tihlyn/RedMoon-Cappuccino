# Scaffolding — module structure, `ml_task`, persistence

For first plugins, `module.def` quirks, the `ml_task` pattern, the three persistence layers, localization, and a complete minimum-viable plugin. Read SKILL.md first — runtime, lifecycle events, and `ml_global_information` aren't repeated here.

## `module.def` — extended notes

The basic shape is in SKILL.md. The keys that matter beyond that:

- **`Files=` order is significant.** Top-level locals from earlier files don't survive into later files (file-merge model), but top-level *globals* (no `local`) do. So when ordering, put files that *define globals others depend on* first.
- **`CreateFolders=settings,log`** is case-sensitive. The folder names must match exactly when you later write to them with `FolderExists`.
- **`ExportFiles=files/PlayerAlert.wav`** copies bundled assets out of the plugin directory on first load. Use sparingly; the path inside the addon can read directly without export.
- **`SharedAccess=<UUID1>,<UUID2>`** restricts access to listed bot UUIDs. Used for store/private addons and combined with `GetPrivateModuleFunctions().ReadModuleFile` for sandboxed file I/O against the addon's `data/` subfolder.

Conventional subfolders observed in `MINIONBOTS/FFXIVMinion/ffxivminion/`: `data/` (BT files; the only folder readable by `GetPrivateModuleFunctions().ReadModuleFile` for store addons), `Settings/`, `GUI/UI_Textures/`, `class_routines/`, `<Mode>Profiles/` (e.g. `GatherProfiles/`, `SkillManagerProfiles/`, `MMLFiles/`).

## Module entrypoint convention

There is no fixed `OnLoad`. The convention:

1. Top-level Lua runs once at load.
2. Declare a single `local mymod = {}` table (or unprefixed module table to expose globally — pick deliberately).
3. Call `RegisterEventHandler(event, fn, "uniqueId")` for each lifecycle hook.

Three of the most common hookups every plugin uses:

```lua
RegisterEventHandler("Module.Initalize", mymod.OnInit,    "MyMod.Init")
RegisterEventHandler("Gameloop.Update",  mymod.OnUpdate,  "MyMod.Update")
RegisterEventHandler("Gameloop.Draw",    mymod.OnDraw,    "MyMod.Draw")
```

If you need cleanup, also bind `Module.Cleanup` (alias `Module.Unload`).

## `ml_task` pattern (verbatim from `ffxiv_task_assist.lua`)

Tasks are the primary work unit. Subclass via `inheritsFrom(ml_task)`, then enqueue with `ml_task_hub:Add(t, IMMEDIATE_GOAL, TP_IMMEDIATE)` or attach as a child via `ml_task_hub:CurrentTask():AddSubTask(t)`.

```lua
ffxiv_task_assist = inheritsFrom(ml_task)
ffxiv_task_assist.name = "LT_ASSIST"

function ffxiv_task_assist.Create()
    local newinst = inheritsFrom(ffxiv_task_assist)
    newinst.valid = true
    newinst.completed = false
    newinst.subtask = nil
    newinst.auxiliary = false
    newinst.process_elements = {}
    newinst.overwatch_elements = {}
    newinst.name = "LT_ASSIST"
    newinst.targetid = 0
    return newinst
end

function ffxiv_task_assist:Init()
    local ke_pressConfirm = ml_element:create("ConfirmDuty",
        c_pressconfirm, e_pressconfirm, 150)
    self:add(ke_pressConfirm, self.process_elements)
end
```

**Required members** on every instance: `valid`, `completed`, `subtask`, `auxiliary`, `process_elements`, `overwatch_elements`, `name`. Forgetting any of these causes silent failures in the dispatch loop.

### Cause/effect framework

Inside `process_elements`, the engine evaluates causes in priority order (high → low). When a cause's `:evaluate()` returns true, its effect's `:execute()` runs. This replaces a state machine for most plugin logic.

```lua
c_walktopos = inheritsFrom(ml_cause)
e_walktopos = inheritsFrom(ml_effect)

function c_walktopos:evaluate()
    -- Cheap, side-effect-free, idempotent. Called speculatively.
    return ml_navigation:HasPath() and not Player:IsJumping()
end

function e_walktopos:execute()
    -- Mutate state. Only called after evaluate() returned true.
    ml_navigation:EnablePathing()
end

local ke_walk = ml_element:create("WalkToPos", c_walktopos, e_walktopos, 40)
self:add(ke_walk, self.process_elements)
```

**Priority conventions** observed in source:

- 150 — pre-conditions / unlock flows (UnlockAethernet, ConfirmDuty)
- 140 — cross-zone teleport
- 130 — same-zone teleport-to-position
- 120 — high-stakes interrupts (Argus dodge, emergency mit)
- 90  — interactions (NPC, mesh end-points)
- 85  — path planning (`GetMovementPath`)
- 60  — falling / mid-air recovery
- 40  — main movement (`WalkToPos`)
- below 40 — passive observation, idle behaviors

Higher number = higher priority = evaluated first.

### Task lifecycle

A task's evaluation loop:

1. `task_complete_eval()` → if true, run `task_complete_execute()`, set `completed = true`, end task.
2. `task_fail_eval()` → if true, run `task_fail_execute()`, mark invalid.
3. `Process()` is called every frame the task is active and not complete/failed. The cause/effect dispatch happens inside the engine's `Process` — you can either let it iterate `process_elements` or override `Process()` to write imperative logic directly (the gather example in `references/navigation.md` does the latter).

`auxiliary = true` lets a task run alongside a primary task without being the "current task". Used for monitoring/overwatch loops.

## Persistence — three layers, pick the right one

### A. Engine `Settings.<bot>.<key>` (auto-saved on shutdown)

```lua
if Settings.MyAddon == nil then Settings.MyAddon = {} end
if not Settings.MyAddon.someBool then Settings.MyAddon.someBool = false end
```

For per-character: key by `local uuid = GetUUID()`.

**Quirk that bites people**: nested-table mutations are persisted *only if the top-level key is re-assigned*. The shutdown serializer compares top-level identities, not deep-equals.

```lua
Settings.FFXIVMINION.gFoo.subkey = newValue          -- may not persist
Settings.FFXIVMINION.gFoo = Settings.FFXIVMINION.gFoo  -- forces re-assign; persists
```

Idiomatic full pattern:

```lua
Settings.FFXIVMINION.gFoo[uuid] = newValue
Settings.FFXIVMINION.gFoo = Settings.FFXIVMINION.gFoo
```

### B. `persistence.store(path, t)` / `persistence.load(path)`

The lua-users TablePersistence library exposed as the global `persistence`. Stores tables as readable Lua source — diffable, version-control-friendly.

```lua
local file = GetStartupPath()..[[\LuaMods\MyAddon\Settings\]]..GetUUID()..".info"
persistence.store(file, { counter = 7, mode = "aggressive" })
local data, err = persistence.load(file)
if err or type(data) ~= "table" then data = {} end
```

Convention: per-character files at `GetStartupPath()..[[\LuaMods\<plugin>\Settings\<charname-or-uuid>.info]]`.

`persistence.load` returns `(value, errstring)` — always check both.

### C. `FileSave(path, table)` / `FileLoad(path)`

MinionLib raw I/O with similar semantics to `persistence`. Less common; use `persistence.*` unless you need the raw form.

### Private (store) addon I/O

For sandboxed addons:

```lua
local pmf = GetPrivateModuleFunctions()
local raw = pmf.ReadModuleFile("data/myfile.lua")
local fn = loadstring(raw)
local result = fn()
```

Only `data/` is readable. Keep `pmf` `local` — never global, never cached on a public table.

## Localization

Wrap every user-visible string with `GetString("English text")`. The engine looks up translations in `languages.lua` (keyed by phrase, with columns `de`, `fr`, `ja`, `ko`, `zh`); falls back to original if missing.

```lua
GUI:Button(GetString("Save").."###save_btn")   -- localised label, stable widget id
```

Markers use a separate helper: `GetStringML(name)`.

Don't pre-concatenate strings before passing to `GetString` — the lookup is exact-match. Use `string.format(GetString("Counter: %d"), n)` instead of `GetString("Counter: ")..n`.

## Deprecation / version notes

- `Player:SetSpeed()` removed 2014-12-05 — don't use.
- Old flat `Settings.<plugin>.<key>` was replaced by per-UUID nested `Settings.FFXIVMINION[key][uuid]` after multi-character support was added.
- BehaviorTree framework was added later than Cause/Effect; FFXIVMinion uses cause/effect idiomatically. New plugins can pick either, but cause/effect is more common in source.
- `MGetGameState()` enums replaced older `ESO_*`-style globals.
- Minion Menu integration moved from a legacy widget to `ml_gui.ui_mgr:AddComponent` around the 2018 ImGui rewrite.

## Minimum viable plugin (~85 lines)

A working plugin with persistence, GUI window, side-menu integration, and a per-frame task. Drop into `LuaMods\HelloMinion\`.

`module.def`:

```ini
[Module]
Name=HelloMinion
Dependencies=minionlib,FFXIVMinion
Version=1
Files=hello.lua
Enabled=1
CreateFolders=settings
```

`hello.lua`:

```lua
local hellomod = {}
hellomod.name      = "HelloMinion"
hellomod.version   = "1.0.0"
hellomod.settingsPath = GetStartupPath() .. [[\LuaMods\HelloMinion\settings\]]
hellomod.gui = { open = true, visible = true, counter = 0 }

function hellomod.LoadSettings()
    local uuid = GetUUID() or "default"
    local file = hellomod.settingsPath .. uuid .. ".info"
    if FileExists(file) then
        local data, err = persistence.load(file)
        if data then hellomod.gui.counter = data.counter or 0
        else d("[HelloMinion] Load err: "..tostring(err)) end
    end
end
function hellomod.SaveSettings()
    local uuid = GetUUID() or "default"
    persistence.store(hellomod.settingsPath..uuid..".info",
                      { counter = hellomod.gui.counter })
end

hello_task = inheritsFrom(ml_task)
hello_task.name = "LT_HELLO"
function hello_task.Create()
    local t = inheritsFrom(hello_task)
    t.valid, t.completed, t.subtask, t.auxiliary = true, false, nil, false
    t.process_elements, t.overwatch_elements = {}, {}
    t.lastPrint = 0
    return t
end
function hello_task:task_complete_eval() return false end
function hello_task:task_fail_eval()     return false end
function hello_task:Process()
    if Now() - self.lastPrint > 1000 and Player and Player.alive then
        d(string.format("[Hello] pos %.1f %.1f %.1f mapid=%d",
            Player.pos.x, Player.pos.y, Player.pos.z, Player.localmapid))
        self.lastPrint = Now()
        hellomod.gui.counter = hellomod.gui.counter + 1
    end
end

function hellomod.OnInit(event, ticks)
    if not FolderExists(hellomod.settingsPath) then FolderCreate(hellomod.settingsPath) end
    hellomod.LoadSettings()
    ml_gui.ui_mgr:AddMember({
        id = "FFXIVMINION##MENU_HELLO", name = "Hello Minion",
        onClick = function() hellomod.gui.open = not hellomod.gui.open end,
        tooltip = "Toggle HelloMinion window",
    }, "FFXIVMINION##MENU_HEADER")
end
function hellomod.OnUpdate(event, tickcount)
    if MGetGameState() ~= FFXIV.GAMESTATE.INGAME then return end
    if not ml_task_hub:CurrentTask() then
        ml_task_hub:Add(hello_task.Create(), IMMEDIATE_GOAL, TP_IMMEDIATE)
    end
end
function hellomod.OnDraw(event, ticks)
    if not hellomod.gui.open then return end
    GUI:SetNextWindowSize(220, 90, GUI.SetCond_FirstUseEver)
    hellomod.gui.visible, hellomod.gui.open =
        GUI:Begin(GetString("HelloMinion"), hellomod.gui.open)
    if hellomod.gui.visible then
        GUI:Text("Counter: "..tostring(hellomod.gui.counter))
        if GUI:Button(GetString("Reset")) then
            hellomod.gui.counter = 0; hellomod.SaveSettings()
        end
    end
    GUI:End()
end
function hellomod.OnUnload() hellomod.SaveSettings() end

RegisterEventHandler("Module.Initalize", hellomod.OnInit,    "HelloMinion.Init")
RegisterEventHandler("Gameloop.Update",  hellomod.OnUpdate,  "HelloMinion.Update")
RegisterEventHandler("Gameloop.Draw",    hellomod.OnDraw,    "HelloMinion.Draw")
RegisterEventHandler("Module.Cleanup",   hellomod.OnUnload,  "HelloMinion.Unload")
```

What this demonstrates: a single module table, settings load/save with `persistence`, an `ml_task` subclass with `Process()` doing per-tick work, side-menu integration, idempotent registration. From here, layering ACR / navigation / Argus is mostly about adding `ml_element`s into `process_elements` or registering Argus callbacks in `OnInit`.

## Source pointers

- **Wiki — MinionLib**: https://wiki.mmominion.com/doku.php?id=minionlib
- **Wiki — Standard Lua API subset**: https://wiki.mmominion.com/doku.php?id=standard_lua_api
- **Wiki — BehaviorTree framework**: https://wiki.mmominion.com/doku.php?id=behaviortree
- **Wiki — Private/store addons**: https://wiki.mmominion.com/doku.php?id=private_addon_developer_help
- **Source — `ffxiv_task_assist.lua`**: canonical `ml_task` subclass
- **Source — `ffxiv_init.lua`, `ffxiv.lua`**: bot-level setup, where `ml_global_information` and `Settings.FFXIVMINION` are wired
- **TablePersistence library origin**: http://lua-users.org/wiki/TablePersistence
