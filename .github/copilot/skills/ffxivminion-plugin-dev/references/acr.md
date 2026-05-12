# ACR — Advanced Combat Routine authoring

For writing combat profiles that run a class's rotation. Read SKILL.md first — Lua runtime, lifecycle, idioms aren't repeated here.

## Discovery & registration

ACRs are **a separate closed-source LuaMod** (`LuaMods\ACR\`). User profiles drop in:

```
C:\MINIONAPP\Bots\FFXIVMinion64\LuaMods\ACR\CombatRoutines\<MyProfile>.lua
```

The wiki confirms: *"Simply placing your ACR LUA file into a folder named CombatRoutines is enough for the ACR module to detect and load your ACR."* A single `.lua` file is all that's required. Larger ACRs (KaliMinion's Blue-Mage-ACR, etc.) use a top-level loader file plus a sibling helpers folder `dofile`'d in.

**Programmatic registration** — for shipping an ACR inside another LuaMod:

```lua
acelib.routinePath = GetStartupPath()..[[\LuaMods\AceLib\CombatRoutines\]]
function AceLib.LoadCombatProfile(filename, alias)
    if FileExists(acelib.routinePath..filename) then
        local profileData = persistence.load(acelib.routinePath..filename)
        if ValidTable(profileData) then ACR.AddPrivateProfile(profileData, alias) end
    end
end
```

## Public ACR API surface

```
ACR.GetClassOptions(class:int = Player.job, pvp:bool = false)
ACR.IsActive() -> bool, profilename:string
ACR.OpenProfileOptions()       -- triggers profile.OnOpen()
ACR.AddPrivateProfile(profileData, alias)
ACR.GetSetting(key, default)   -- persistent settings load
ACR.SetGUIVar(value, key)      -- persistent settings save
```

## ACR vs SkillManager

ACR **replaces** `SkillMgr.Cast()` when an ACR profile is enabled and claims `Player.job`. Otherwise FFXIVMinion falls back to SkillManager. From `ffxiv.lua`:

```lua
local acrValid = (not inPvP and acrEnabled and table.valid(gACRSelectedProfiles)
                  and gACRSelectedProfiles[Player.job])
                 or (inPvP and gACREnabledPVP and ...)
```

Implication: a profile that returns `false` from `Cast()` doesn't pass through to SkillMgr — that gate is *upstream* of `Cast`. If you want SkillMgr fallback for unhandled situations, don't claim the job in `profile.classes`.

## Profile contract (full required interface)

```lua
local profile = {}
profile.GUI     = { open = false, visible = true, name = "Batman" }
profile.region  = { 1, 2, 3 }            -- 1=NA, 2=CN, 3=KR
profile.classes = { [FFXIV.JOBS.NINJA] = true, [FFXIV.JOBS.ROGUE] = true }
profile.tags    = "assistonly;grindmode;dungeons"

function profile.Cast()
    local t = MGetTarget()
    if t then
        local spinningEdge = ActionList:Get(2240, 1, t.id)
        if spinningEdge and spinningEdge.isready then
            spinningEdge:Cast(t.id); return true
        end
    end
    return false
end

function profile.Draw()       end   -- ImGui drawing when GUI open
function profile.DrawHeader() end   -- header above ffxivminion task window
function profile.DrawFooter() end   -- footer below
function profile.OnOpen()     profile.GUI.open = true end
function profile.OnLoad()     end   -- prep, e.g. ACR.GetSetting()
function profile.OnClick(mouse, shift, ctrl, alt, entity) end
function profile.OnUpdate(event, tickcount) end

return profile
```

Contract notes:

- **`profile.Cast()` must return `true` if it queued an action, `false` otherwise.** This gates the bot's animation-lock waits.
- **`OnUpdate()` fires every frame regardless of combat** — gate combat-only logic on `Player.incombat` or `MGetTarget()`.
- **`OnClick(mouse, shift, ctrl, alt, entity)`** fires on the multi-clickable Party Interface — `mouse` is `0/1/2` (left/right/middle).
- **The set of `OnX` hooks above is the whole API.** Don't expect `OnEnterCombat`, `OnTargetChange`, etc. — those don't exist; build them yourself by tracking state in `OnUpdate`.

## ActionList API

```
ActionList(filterstring)               -- returns table { [actionid] = Action }
ActionList:Get(actionid, actiontype?)  -- single Action; default type = ACTIONS (1)
ActionList:Cast(actionID, targetID, actionType)   -- avoid; use action:Cast()
ActionList:IsCasting()                 -- on cast bar OR on GCD
ActionList:IsReady()
ActionList:CanCast(actionID, targetID, actionType)
ActionList:StopCasting()
```

Filter strings: `"actionid=N"`, `"type=N"`, `"job=N"` (excludes cross-class), `"minlevel=N"`, `"maxlevel=N"`. **Multiple `type=` filters do NOT combine** (last one wins).

### ACTIONTYPE enum

```
ACTIONS = 1   ITEM = 2      KEYITEM = 3    ABILITY = 4
GENERAL = 5   COMPANION = 6 MINIONS = 8    CRAFT = 9
MAINCOMMANDS = 10   PET = 11   MOUNT = 13   PET2 = 16
```

Common General actions: Sprint = `(5, 3)`, Return = `(1, 6)`, Teleport = `(5, 7)`. Mount actions are type 13.

### Action object fields & methods

Fields: `.id, .name, .level, .job, .skilltype, .cost, .casttime, .recasttime, .cd, .cdmax, .isoncd, .range, .radius, .usable, .iscasting, .isgroundtargeted, .canfly, .isready`.

Methods: `action:IsReady([targetid])`, `:IsFacing([targetid])`, `:Cast([targetid])`, `:CanCastResult([targetid])`. Use `targetid = 0` or `Player.id` for self-cast.

Canonical pattern from `ffxiv_unstuck.lua`:

```lua
local returnHome = ActionList:Get(1, 6)            -- type 1 (ACTION) id 6 (Return)
if returnHome and returnHome:IsReady() then
    if returnHome:Cast(Player.id) then ... end
end
```

### GCD detection & weave windows

`ActionList:IsCasting()` returns true while a cast bar runs OR GCD is active. `SkillMgr.gcdTime = 2.5` is the base GCD; oGCDs typically have non-2.5 `recasttime`.

Weave window heuristic — fire an oGCD when 0.7–1.5s remains on GCD:

```lua
local cd = ActionList:Get(1, gcdId)
local gcdRemain = (cd and cd.cd) or 0
if gcdRemain > 0.7 and gcdRemain < 1.5 then
    -- safe oGCD weave
end
```

For confirming a GCD landed (server-confirmed, not just queued):

```lua
ml_global_information.Await(5000, function()
    return Player.castinginfo.lastcastid == castid
end)
```

The `castingid` vs `channelingid` distinction matters: `castingid` is the **client-side cast bar** (can be cancelled, can desync), `channelingid` is the **server-confirmed** cast that will actually land. For weave gating, gate on `Player.castinginfo.channelingid ~= 0` — that's "I'm committed to landing something". See `references/combat-telemetry.md` for the full `castinginfo` shape.

## Buff helpers (`ffxiv_helpers.lua`)

```
HasBuff(entityid, buffid [, ownerid] [, minDuration])
HasBuffs(entity, "id1+id2+id3" or "id1,id2", minStacks, ownerid)
   -- "+" = ALL ids;  "," = ANY id
MissingBuff(entity, buffid, stacks, duration)
MissingBuffs(entity, "ids", minStacks, ownerid)
BuffDuration(entityid, buffid)           -- seconds remaining
HasBuffs{ entity = e, buffs = "...", duration = 5,
         ownerid = Player.id, stacks = 3 }
```

`ownerid` matters for DoTs and stacked buffs — always pass `Player.id` when checking your own DoT to avoid matching another player's DoT of the same id on the same target.

## EntityList filters (the rotation target-selection toolkit)

```lua
local el = EntityList("nearest,onmesh,attackable,maxdistance=20")
EntityList:Get(entityid)
```

Filter tokens observed in source:

```
nearest, lowesthealth, highesthealth
alive, attackable, targetable, friendly, aggressive
onmesh, los, targetingme, aggro, incombat, myparty, mounted
chartype=N, type=N, job=N, minlevel=N, maxlevel=N
maxdistance=N, mindistance=N, maxdistance2d=N
distanceto=ID         -- distance from another entity, not Player
contentid=N, exclude_contentid=N, fateid=N, exclude_fateid=N
gatherable, ephemeral, unspoiled, legendary, concealed
clustered=N           -- best AoE target (most enemies within Ny of it)
targeting=ID
```

Combine filters with comma: `MEntityList("alive,attackable,los,clustered=8,maxdistance=25")`. Use `MEntityList` (memoized) inside `Cast()` and `OnUpdate()`.

For named-target abstractions (Heal Priority, Tankable Target, etc.) and target-of-target reconstruction, see `references/combat-telemetry.md` — that's where the threat-web work lives.

## Job IDs (`FFXIV.JOBS`)

```
ADVENTURER=0  GLADIATOR=1   PUGILIST=2    MARAUDER=3    LANCER=4
ARCHER=5      CONJURER=6    THAUMATURGE=7
CARPENTER=8   BLACKSMITH=9  ARMORER=10    GOLDSMITH=11  LEATHERWORKER=12
WEAVER=13     ALCHEMIST=14  CULINARIAN=15 MINER=16      BOTANIST=17  FISHER=18
PALADIN=19    MONK=20       WARRIOR=21    DRAGOON=22    BARD=23
WHITEMAGE=24  BLACKMAGE=25  ARCANIST=26   SUMMONER=27   SCHOLAR=28
ROGUE=29      NINJA=30      MACHINIST=31  DARKKNIGHT=32 ASTROLOGIAN=33
SAMURAI=34    REDMAGE=35    BLUEMAGE=36   GUNBREAKER=37 DANCER=38
REAPER=39     SAGE=40       VIPER=41      PICTOMANCER=42
```

`ffxivminion.classes[FFXIV.JOBS.PALADIN] == "PLD"` for the 3-letter abbreviation. **There is no class-change event** — re-key off `Player.job` in `OnUpdate` and reset state when it changes.

## Priority / rotation patterns

Two patterns coexist.

### ACR-native: ordered priority list walked in `Cast()`

```lua
local rotation = {
  { id=141,  type=1, level=4,
    condition = function(t) return t and Player.mp.current >= 800 end,
    target    = function(t) return t end },
  { id=147,  type=1, level=2,
    condition = function(t) return Player:IsMoving() end,
    target    = function(t) return t end },
}

function profile.Cast()
    local t = MGetTarget()
    for _, entry in ipairs(rotation) do
        if Player.level >= entry.level and entry.condition(t) then
            local a = ActionList:Get(entry.type, entry.id)
            local tgt = entry.target(t)
            if a and a:IsReady(tgt and tgt.id or 0) then
                a:Cast(tgt and tgt.id or 0); return true
            end
        end
    end
    return false
end
```

### SkillMgr declarative schema (used by built-in profiles)

Built-in SkillMgr profiles in `LuaMods\ffxivminion\SkillManagerProfiles\*.lua` use a declarative ~150-field per-skill schema:

```
id, type, prio, levelmin, levelmax, combat, phpl, phpb, pmppl,
thpl, ptcount, pbuff, pbuffdura, pnbuff, tbuff, tbuffowner, tnbuff,
tecount, terange, frontalconeaoe, gauge1lt..gauge8gt, gcd, gcdtime,
gcdtimelt, ppos, skready, sknready, skncdtimemin, tcastids, tcastonme, ...
```

Read existing profiles in `SkillManagerProfiles/` if you want to extend an SkillMgr-style ACR — the field names are largely self-documenting (`phpl` = player HP less than, `tbuff` = target has buff, etc.).

For new ACR development, the imperative pattern is far more common in the community. Use SkillMgr-style only if you're maintaining an existing profile.

## Settings persistence (ACR-specific)

ACR profiles persist via `ACR.GetSetting(key, default)` / `ACR.SetGUIVar(value, key)`. Idiomatic merge-on-load (preserves new defaults across schema additions):

```lua
local function OnLoad()
    local persisted = ACR.GetSetting(GetStateName(), state.config)
    for k,v in pairs(persisted) do state.config[k] = v end
end

-- Save when GUI changes a setting:
ACR.SetGUIVar(state.config, GetStateName())
```

## BotMode integration

`gBotMode` is a global string compared via `GetString(...)`:

```lua
if gBotMode == GetString("assistMode") then
    -- player steers; don't override Player:SetTarget
elseif gBotMode == GetString("grindMode")
    or gBotMode == GetString("partyGrindMode") then
    -- bot steers; you may select your own target if none exists
end
```

Other modes you'll see: `"hardcoreMode"`, `"fishMode"`, `"gatherMode"`, `"questMode"`, `"fateMode"`. Most ACR logic only needs to distinguish "player is driving" (assist) from "bot is driving" (everything else).

## Out-of-combat / pre-pull

```lua
local food   = GetItem(foodid, {0,1,2,3})
local action = ActionList:Get(2, foodid)            -- type 2 = ITEM
if food and action and not action.isoncd
   and MissingBuff(Player, 48, 0, 60) then          -- 48 = "Well Fed"
    action:Cast(Player.id)
end
```

Common general actions: Sprint = `(1, 3)`, Return = `(1, 6)`, Teleport = `(5, 7)`. Mount actions = type 13. Buff 50 = Sprint.

## Interrupt example

```lua
if target.castinginfo and target.castinginfo.channelingid ~= 0
   and target.castinginfo.casttime > 0.5 then
    local int = ActionList:Get(1, INTERRUPT_ID)   -- e.g. 7538 Interject (PLD/WAR)
    if int and int:IsReady(target.id) then int:Cast(target.id) end
end
```

`channelingid ~= 0` means the cast is server-confirmed (not just on the client cast bar). Gating on `casttime > 0.5` avoids interrupting trivial 0.5s instant casts.

## Minimal Black Mage ACR (single-target skeleton)

Drop into `LuaMods\ACR\CombatRoutines\MyBLM.lua`. Demonstrates: profile contract, `ACR.GetSetting/SetGUIVar` persistence, GUI tab, rotation in `Cast()`, target acquisition in `OnUpdate()`.

```lua
local profile = {}
profile.GUI     = { open = false, visible = true, name = "MyBLM" }
profile.region  = { 1, 2, 3 }
profile.classes = { [FFXIV.JOBS.THAUMATURGE]=true, [FFXIV.JOBS.BLACKMAGE]=true }
profile.tags    = "single-target;dungeons"

local cfg = { useAoE=false, manaTickHP=35 }
local function StateName() return "MyBLM_State" end

local SPELL = {
    fire1     = { id=141,  t=1 },
    blizzard1 = { id=142,  t=1 },
    thunder1  = { id=144,  t=1 },
    transpose = { id=149,  t=1 },
    scathe    = { id=156,  t=1 },
}
local ASTRAL_FIRE, UMBRAL_ICE, THUNDER_DOT = 165, 166, 161

local function ready(spell, t)
    local a = ActionList:Get(spell.t, spell.id)
    if a and a.usable and a:IsReady(t.id) then return a end
end

local function OnCast()
    local t = MGetTarget()
    if not t or not t.attackable or not t.alive then return false end
    if Player.castinginfo.castingid ~= 0 then return false end

    if MissingBuffs(t, tostring(THUNDER_DOT), 1, Player.id)
       or BuffDuration(t.id, THUNDER_DOT) < 3 then
        local a = ready(SPELL.thunder1, t); if a then a:Cast(t.id); return true end
    end
    if Player.mp.percent < cfg.manaTickHP and not HasBuff(Player.id, UMBRAL_ICE) then
        local a = ready(SPELL.transpose, t); if a then a:Cast(Player.id); return true end
    end
    if Player:IsMoving() then
        local a = ready(SPELL.scathe, t); if a then a:Cast(t.id); return true end
    end
    if Player.mp.current >= 800 or HasBuff(Player.id, ASTRAL_FIRE) then
        local a = ready(SPELL.fire1, t); if a then a:Cast(t.id); return true end
    else
        local a = ready(SPELL.blizzard1, t); if a then a:Cast(t.id); return true end
    end
    return false
end

local function OnUpdate(event, tickcount)
    if gBotMode == GetString("grindMode") and not Player:GetTarget() then
        local el = MEntityList("nearest,onmesh,attackable,alive,los,maxdistance=25")
        if el then local id = next(el); if id then Player:SetTarget(id) end end
    end
end

profile.Cast     = function() return OnCast() end
profile.OnUpdate = OnUpdate
profile.OnLoad   = function()
    local stored = ACR.GetSetting(StateName(), cfg)
    for k,v in pairs(stored) do cfg[k] = v end
end
profile.OnOpen   = function() profile.GUI.open = true end
profile.Draw     = function()
    if not profile.GUI.open then return end
    profile.GUI.visible, profile.GUI.open =
        GUI:Begin(profile.GUI.name, profile.GUI.open)
    if profile.GUI.visible then
        cfg.useAoE     = GUI:Checkbox("Use AoE", cfg.useAoE)
        cfg.manaTickHP = GUI:SliderInt("Transpose MP%", cfg.manaTickHP, 0, 100)
        if GUI:Button("Save", 64, 18) then ACR.SetGUIVar(cfg, StateName()) end
    end
    GUI:End()
end
profile.OnClick = function() end
profile.DrawHeader, profile.DrawFooter = function() end, function() end
return profile
```

## Source pointers

- **Wiki — ACR API**: https://wiki.mmominion.com/doku.php?id=acr
- **Source — `ffxiv_skillmgr.lua`**: `gSMTargets`, `gSMTargetTypes`, named-target idioms; `IsCaster`, `IsHealingSkill`
- **Source — `ffxiv_helpers.lua`**: `HasBuff*`, `GetBestHealTarget`, `GetBestDoTTarget`, `GetNearestGrindAttackable`
- **Source — `ffxiv_task_assist.lua`**: `FFXIV_Assist_Modes = {none, lowestHealth, highestHealth, nearest, tankAssist}` — canonical assist-target taxonomy
- **Source — `ffxiv_common_cne.lua`**: `Player.castinginfo.lastcastid` Await pattern; tank/aggro idioms
- **Community examples**:
  - https://github.com/KaliMinion/Blue-Mage-ACR (large multi-file ACR)
  - https://github.com/Rikudouu/XIVOpeners (positional-aware rotations)
- **Forum thread 18543** (Skygirl779 ACR tutorial) on the MMOMinion forums
