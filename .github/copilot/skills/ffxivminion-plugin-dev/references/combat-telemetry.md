# Combat targeting & telemetry — `Player`/`Entity`, ToT, threat, TTK

For the Player/Entity field model, target-of-target & threat-web reconstruction, party iteration, role detection, and the HP/MP velocity tracking that drives "is this fight stalled / hold big CD / tank lost aggro" decisions. Read SKILL.md first.

## Why a dedicated reference

ACR rotation logic, off-GCD reactions, healer mitigation, and tank-stance arbitration all need the same primitives: who is everyone targeting, what's their HP velocity, who will I kill first, who's about to die. The Player/Entity APIs expose the raw fields, but they're scattered across `Entity` (per-frame), `MEntityList` (filter DSL), `EntityList.myparty` (party iteration), and stateful tracking you must build yourself. This reference consolidates them.

## Player vs Entity object model

Every entity returned by `EntityList` / `MEntityList` / `Player:GetTarget()` shares a base shape; `Player` adds player-only fields. **Trust source over wiki** — the GitHub Player wiki was last edited 2015-01-12 and lags reality.

### Per-entity (vitals & state)

| Field | Type | Notes |
|---|---|---|
| `id` | int | Engine handle. **Re-fetch every frame** — never cache. |
| `name` | string | Display name. |
| `contentid` | int | Stable content/template id. Use to identify mob species. |
| `level` | int | – |
| `job` / `classjob` | int | See `FFXIV.JOBS` enum. `0` for non-jobbed NPCs. |
| `chartype` | int | 1=PC, 2=Battle NPC, 3=Event NPC, 4=Battle NPC w/ ID, etc. |
| `type` | int | 1=Enemy, 2=Friendly, etc. |
| `hp` | `{current, max, percent}` | `percent` is 0–100 float. |
| `mp` | `{current, max, percent}` | **Aliased to TP/GP/CP for non-mana jobs** (the table struct is reused). |
| `gp` / `cp` / `tp` | `{current, max, percent}` | Gathering Points / Crafting Points / Tactical Points. |
| `pos` | `{x, y, z, h}` | `h` heading in radians. |
| `hitradius` | float | Mob's hit cylinder. Use for facing/range checks, NOT a flat constant. |
| `distance` | float | 3D distance to player, refreshed per frame. |
| `distance2d` | float | Ignores Y. |
| `targetid` | int | Who *this* entity is targeting. **0 = no target.** |
| `aggro` | bool | This entity is aggro'd to player. |
| `claimedbyid` | int | Tap claim — only this id (or its party) gets credit. |
| `incombat` | bool | – |
| `alive` | bool | – |
| `attackable` | bool | Engine-side combinable flag. |
| `targetable` | bool | – |
| `los` | bool | In line of sight to player. |
| `onmesh` | bool | On the currently loaded navmesh. |
| `ismounted` | bool | – |
| `npccastinginfo` / `castinginfo` | table | See below. |
| `currentaction` / `lastaction` | int | Action id mid-animation; `lastaction` is most recent finished. |
| `buffs` | table | Iterable buff table — see ACR `HasBuff*` helpers. |
| `fateid` | int | Fate this entity belongs to (mob inside a FATE). |
| `pet` / `companion` | bool | – |

### Player-only (extends Entity)

```lua
Player.id, .name, .level, .job, .classjob, .pos
Player.hp, .mp, .tp, .gp, .cp        -- {current,max,percent} each
Player.hasaggro                      -- something is aggro'd to me
Player.aggro                         -- I am aggro'd to current target
Player.revivestate                   -- 0 alive / non-zero KO state
Player.localmapid                    -- current zone
Player.experience = {current, max, percent, required}
Player.role                          -- 1 Tank / 2 Healer / 3 Melee / 4 Range / 5 Caster
Player.alive, .incombat, .ismounted, .onmesh
Player.flying = {isflying, pitch}
Player.diving = {isswimming, isdiving}
Player.castinginfo = {
   castingid,        -- action being charged on cast bar (0 if none)
   channelingid,     -- channel/server-confirmed cast id
   channeltargetid,  -- target of that channel
   casttime,         -- remaining cast time (sec)
   channeltime,      -- remaining channel time (sec)
   lastcastid        -- most recent successful cast (used to confirm)
}
Player.target                        -- shorthand; same as Player:GetTarget()
Player:GetTarget()                   -- prefer MGetTarget() inside hot loops
Player:SetTarget(id) / :ClearTarget()
Player:Interact(id)
MGetTarget()                         -- memoized — use inside Gameloop.Update
```

### `castinginfo` — the cast-bar vs channel distinction

`castingid` is the **client-side cast bar** (can be cancelled, can desync). `channelingid` is the **server-confirmed** cast that will actually land.

For ACR scheduling, gate weaves on `Player.castinginfo.channelingid ~= 0` — that's "I'm committed to landing something". For "did my last GCD land?" use:

```lua
ml_global_information.Await(5000, function()
    return Player.castinginfo.lastcastid == castid
end)
```

Pattern from `ffxiv_skillmgr.lua`.

## Target-of-target & threat web

There is no dedicated `Player:GetTargetOfTarget()` — derive it:

```lua
local function MGetTargetOfTarget()
    local t = MGetTarget(); if not t then return nil end
    if t.targetid == 0 then return nil end
    return EntityList:Get(t.targetid)
end
```

Threat-list reconstruction (no flat API; build it from `targetid`):

```lua
-- Returns: { [enemyId] = { mainTargetId, hitsMe (bool), hitsParty (bool) }, ... }
local function BuildThreatTable(maxRange)
    maxRange = maxRange or 30
    local out, party = {}, EntityList.myparty
    local partyIds = {}
    for _, m in pairs(party or {}) do partyIds[m.id] = true end
    partyIds[Player.id] = true

    local enemies = MEntityList("alive,attackable,maxdistance="..maxRange)
    if not enemies then return out end
    for _, e in pairs(enemies) do
        out[e.id] = {
            mainTargetId = e.targetid,
            hitsMe       = (e.targetid == Player.id),
            hitsParty    = (e.targetid ~= 0 and partyIds[e.targetid] == true),
            distance     = e.distance,
            hpp          = e.hp.percent,
            claimed      = e.claimedbyid ~= 0,
        }
    end
    return out
end
```

Tank-stance arbitration ("is this mob actually on me"):

```lua
local function MobIsOnMe(e)
    return e and e.targetid == Player.id and e.aggro
end
```

The reverse lookup uses the engine-side `targeting=ID` filter. Returns every mob whose `targetid` equals my id — used in `ffxiv_helpers.lua` for AoE clusters:

```lua
MEntityList("alive,attackable,targeting="..Player.id)
```

## Party iteration

Party is exposed two ways:

### A. Direct list — `EntityList.myparty`

An iterable table keyed by party index (1..8 for full party, 1..24 for alliance). Members are full Entity objects, so all of `hp`/`mp`/`pos`/`buffs` are available. **Empty slots have `id == 0`** — always check.

```lua
for i, m in pairs(EntityList.myparty) do
    if m.id ~= 0 and m.alive then
        d(string.format("[%d] %s  HP %d%%  Job %d  d=%.1f",
            i, m.name, m.hp.percent, m.job, m.distance))
    end
end
```

### B. Filter DSL — add `myparty` to filters

```lua
local nearbyAllies = MEntityList(
    "friendly,alive,chartype=4,myparty,targetable,maxdistance=30")
```

Verbatim usage from `ffxiv_helpers.lua`:

```lua
local el = MEntityList("friendly,alive,chartype=4,myparty,targetable,maxdistance="..tostring(range))
```

## Role detection

`Player.role` is `1=Tank, 2=Healer, 3=Melee, 4=PhysRange, 5=Caster`. Other party members expose `.role` the same way. Job-id-to-role helpers (build once, cache):

```lua
local TANKS   = { [FFXIV.JOBS.PALADIN]=true, [FFXIV.JOBS.WARRIOR]=true,
                  [FFXIV.JOBS.DARKKNIGHT]=true, [FFXIV.JOBS.GUNBREAKER]=true,
                  [FFXIV.JOBS.GLADIATOR]=true, [FFXIV.JOBS.MARAUDER]=true }
local HEALERS = { [FFXIV.JOBS.WHITEMAGE]=true, [FFXIV.JOBS.SCHOLAR]=true,
                  [FFXIV.JOBS.ASTROLOGIAN]=true, [FFXIV.JOBS.SAGE]=true,
                  [FFXIV.JOBS.CONJURER]=true }
local function IsTank(e)   return e and (e.role == 1 or TANKS[e.job]) end
local function IsHealer(e) return e and (e.role == 2 or HEALERS[e.job]) end
```

## SkillMgr's named-target abstractions (copy these for your own ACR)

From `ffxiv_skillmgr.lua` — the canonical list of meaningful combat target categories:

```
Target          -- current Player:GetTarget()
Ground Target   -- ground reticle
Player          -- self-cast
Cast Target     -- target the cast was started on (snapshot)
Party / PartyS  -- best party member by criterion (S = sticky)
Low TP / Low MP -- party member with lowest TP / MP
Pet             -- Player.pet
Ally / Tank     -- nearest ally / tank
Tankable Target -- enemy not currently on a tank (off-tank targets)
Tanked Target   -- enemy whose targetid is a tank
Heal Priority   -- output of GetBestHealTarget
Dead Ally / Dead Party -- raise candidates
```

Roles for skill targeting: `Any, Tank, DPS, Caster, Healer, RangeDPS, MeleeDPS`.

## Best-heal-target pattern (verbatim shape from `ffxiv_helpers.lua`)

```lua
local function GetBestHealTarget(minHP, range, excludeNPC)
    range = range or 30
    minHP = minHP or 100
    local el = MEntityList(
        "friendly,alive,chartype=4,myparty,targetable,maxdistance="..tostring(range))
    if not el then return nil end
    local best, bestHP = nil, minHP + 0.01
    for _, m in pairs(el) do
        local validNpc = excludeNPC and m.type == 1 or true
        if validNpc and m.id ~= 0 and m.targetable
           and m.distance <= range and m.hp.percent <= bestHP then
            best, bestHP = m, m.hp.percent
        end
    end
    return best
end
```

For "best DoT target" or "best AoE target", reuse engine filter operators rather than rolling your own:

- `clustered=N` — picks the enemy with the most other enemies within N yalms (best AoE pivot)
- `targeting=ID` — only enemies hitting party member `ID`
- `lowesthealth` / `highesthealth` — sort modifiers
- `distanceto=ID` — distance from another entity, not the player

Combine: `MEntityList("alive,attackable,los,clustered=8,targeting="..tostring(member.id)..",maxdistance=25")`.

## HP/MP velocity & TTK tracking — you build this

The engine gives instantaneous HP, not derivative. Build a small ring buffer keyed by entity id; sample at a fixed cadence. **Throttle to ~5 Hz; faster is noise.**

```lua
combat_track = combat_track or {}
combat_track.buffers = combat_track.buffers or {}   -- [id] = { samples, last }
combat_track.WINDOW  = 5000     -- 5-second window
combat_track.HZ_MS   = 200      -- sample every 200 ms

local function SampleEntity(e)
    if not e or e.id == 0 or not e.alive then return end
    local b = combat_track.buffers[e.id]
    if not b then
        b = { samples = {}, name = e.name, hpmax = e.hp.max, lastDmgTs = 0 }
        combat_track.buffers[e.id] = b
    end
    if Now() - (b.last or 0) < combat_track.HZ_MS then return end
    b.last = Now()
    -- absolute hp (current_pct * max) gives a stable damage measure even
    -- through SE's bounded-precision percent updates
    local absHP = (e.hp.current ~= 0) and e.hp.current
                  or (e.hp.percent / 100 * e.hp.max)
    table.insert(b.samples, { t = Now(), hp = absHP, hpp = e.hp.percent })
    -- Drop old samples
    while b.samples[1] and (Now() - b.samples[1].t) > combat_track.WINDOW do
        table.remove(b.samples, 1)
    end
    -- Track last damage event for "is this fight stalled" checks
    if #b.samples >= 2 then
        local prev = b.samples[#b.samples - 1]
        if absHP < prev.hp - 1 then b.lastDmgTs = Now() end
    end
end

-- DPS over the window (negative = healing)
local function GetDPS(id)
    local b = combat_track.buffers[id]
    if not b or #b.samples < 2 then return 0 end
    local first, last = b.samples[1], b.samples[#b.samples]
    local dt = (last.t - first.t) / 1000
    if dt < 0.5 then return 0 end
    return (first.hp - last.hp) / dt
end

-- TTK in seconds; nil if not dying or healing
local function GetTTK(id)
    local b = combat_track.buffers[id]; if not b then return nil end
    local dps = GetDPS(id)
    if dps <= 0.01 then return nil end
    local last = b.samples[#b.samples]
    return last.hp / dps
end

-- Has anyone touched this for X ms? (stalled fight detector)
local function StalledMs(id)
    local b = combat_track.buffers[id]
    if not b or b.lastDmgTs == 0 then return math.huge end
    return Now() - b.lastDmgTs
end

-- Periodic: GC dead/missing entities
local function GCBuffers()
    for id in pairs(combat_track.buffers) do
        local e = EntityList:Get(id)
        if not e or not e.alive or (Now() - combat_track.buffers[id].last) > 30000 then
            combat_track.buffers[id] = nil
        end
    end
end
```

Wire into `Gameloop.Update`:

```lua
function combat_track.OnUpdate(event, tickcount)
    if MGetGameState() ~= FFXIV.GAMESTATE.INGAME then return end
    if not Player.alive or not Player.incombat then return end
    -- Sample target + nearby attackable
    local t = MGetTarget(); if t then SampleEntity(t) end
    local nearby = MEntityList("alive,attackable,maxdistance=30,los")
    if nearby then for _, e in pairs(nearby) do SampleEntity(e) end end
    -- Sample party for incoming-damage tracking
    for _, m in pairs(EntityList.myparty or {}) do
        if m.id ~= 0 then SampleEntity(m) end
    end
    if (Now() % 5000) < 50 then GCBuffers() end
end
RegisterEventHandler("Gameloop.Update", combat_track.OnUpdate, "combat_track.Update")
```

## Practical decisions you can drive from this

```lua
-- "Don't waste big CD if mob's about to die"
local function ShouldHoldBigCD(t, minTTK)
    local ttk = GetTTK(t.id)
    return ttk ~= nil and ttk < (minTTK or 8)
end

-- "Spend gauge before this pull ends"
local function ShouldDumpGauge(t, dumpBelowSec)
    local ttk = GetTTK(t.id)
    return ttk ~= nil and ttk <= (dumpBelowSec or 12)
end

-- "Tank lost aggro" (tank in party, mob's targetid not a tank)
local function TankLostAggro(mob)
    if not mob or mob.targetid == 0 then return false end
    local who = EntityList:Get(mob.targetid)
    return who and not IsTank(who)
end

-- "Player taking sustained damage" (use party-self HP velocity)
local function IncomingDPS(secondsBack)
    local b = combat_track.buffers[Player.id]
    if not b or #b.samples < 2 then return 0 end
    local cutoff = Now() - (secondsBack or 3) * 1000
    local first, last
    for _, s in ipairs(b.samples) do
        if s.t >= cutoff then first = first or s; last = s end
    end
    if not first or not last or last.t == first.t then return 0 end
    local dt = (last.t - first.t) / 1000
    return (first.hp - last.hp) / dt           -- positive = damage
end

-- "Time until I die at current incoming DPS"
local function PlayerTTD()
    local dps = IncomingDPS(3)
    if dps <= 0 then return nil end
    return Player.hp.current / dps
end
```

## MP/TP/GP/CP shared layout (gotcha)

Different jobs reuse the `mp`/`tp` slots. Always read what you need by job:

| Job category | Vital |
|---|---|
| Mana casters (BLM/RDM/SMN/WHM/SCH/AST/SGE) | `Player.mp` |
| Tanks/melee (most) | `Player.tp` (legacy; mostly unused post-SB) |
| Gatherers (MIN/BTN/FSH) | `Player.gp` |
| Crafters (CRP/BSM/...) | `Player.cp` |

Modern code mostly cares about `mp` and gauges (read via SkillMgr's `gauge1..gauge8` or direct memory accessors per ACR). Pulling `Player.mp.percent` on a Warrior gives you something but it isn't meaningful — gate vital reads on `Player.job`.

## Entity lifecycle hazards

- **Entity refs go stale across frames.** Always re-fetch by id: `local t = EntityList:Get(savedId)`.
- **`.distance` updates only when `Player.pos` updates.** During teleports / loading screens it's frozen — gate on `not MIsLoading()`.
- **`hp.current` can be 0 with `alive=true`** during a few ticks after death. Trust `alive`.
- **Tap claim**: `claimedbyid ~= 0 and claimedbyid ~= Player.id and not partyIds[claimedbyid]` means another player tagged it; respect or ignore per `gAvoidTapped` style settings.
- **Pets & companions are entities**: `Player.pet`, `Player.companion`. They appear in `EntityList` and can satisfy `myparty` for some filters.
- **Dead party members** still appear in `EntityList.myparty` with `alive=false` and `hp.current=0` — needed for raise targeting.

## Combat-state telemetry "skill window" — drop-in widget

Reuses `combat_track.buffers` from above. Renders a debug HUD:

```lua
function combat_track.Draw(event, ticks)
    if not combat_track.gui or not combat_track.gui.open then return end
    GUI:SetNextWindowSize(360, 280, GUI.SetCond_FirstUseEver)
    local vis, open = GUI:Begin("Combat Telemetry###combat_track", combat_track.gui.open)
    combat_track.gui.open = open
    if vis then
        GUI:Text(string.format("HP %d/%d (%.0f%%)  MP %d (%.0f%%)",
            Player.hp.current, Player.hp.max, Player.hp.percent,
            Player.mp.current, Player.mp.percent))
        local ttd = PlayerTTD()
        GUI:Text("Incoming DPS: "..string.format("%.0f  TTD: %s",
            IncomingDPS(3), ttd and string.format("%.1fs", ttd) or "—"))
        GUI:Separator()
        local t = MGetTarget()
        if t then
            GUI:Text(string.format("Target: %s  HP %.1f%%  d=%.1f",
                t.name, t.hp.percent, t.distance))
            local tot = MGetTargetOfTarget()
            GUI:Text("ToT: "..(tot and tot.name or "—"))
            local ttk = GetTTK(t.id)
            GUI:Text("DPS: "..string.format("%.0f  TTK: %s",
                GetDPS(t.id), ttk and string.format("%.1fs", ttk) or "—"))
        end
        GUI:Separator()
        GUI:Text("Party:")
        for i, m in pairs(EntityList.myparty or {}) do
            if m.id ~= 0 then
                GUI:Text(string.format("  %d %s  HP %.0f%%  %s",
                    i, m.name, m.hp.percent, m.alive and "" or "(KO)"))
            end
        end
    end
    GUI:End()
end
RegisterEventHandler("Gameloop.Draw", combat_track.Draw, "combat_track.Draw")
```

## Source pointers

- **Wiki — Player (lagging, but baseline)**: https://github.com/MINIONBOTS/FFXIVMinion/wiki/Player
- **Wiki — Entity**: https://github.com/MINIONBOTS/FFXIVMinion/wiki/Entity
- **Source — `ffxiv_helpers.lua`**: `GetBestHealTarget`, `GetBestDoTTarget`, `GetNearestGrindAttackable`, `EntityList.myparty` usage
- **Source — `ffxiv_skillmgr.lua`**: `gSMTargets`, `gSMTargetTypes`, named-target idioms; `IsCaster`, `IsHealingSkill`
- **Source — `ffxiv_task_assist.lua`**: `FFXIV_Assist_Modes = {none, lowestHealth, highestHealth, nearest, tankAssist}` — canonical assist-target taxonomy
- **Source — `ffxiv_common_cne.lua`**: `Player.castinginfo.lastcastid` Await pattern; tank/aggro idioms
