# Navigation, mesh, & movement

For path planning, mesh inspection, OMC handling, marker-driven tasks, mount/fly, teleport, and stuck recovery. Read SKILL.md first.

## Mesh files

NavMeshes live in `Bots\FFXIVMinion64\Navigation\<MeshName>\`, named after the in-game map. File set per zone:

- `.obj` — raw input data (yellow); 0-byte stubs unless authorised.
- `.nx2` — built navmesh (purple) used by the pathfinder.
- `.omc` — Off-Mesh Connections (jumps, aetheryte, boats, elevators, portals).
- `.cub` — fly & under-water cube volumes.
- `.cpc` — cube↔floor connections.
- `.mmn` / `.mmp` — macro-mesh nodes & precomputed paths between them.
- `.info` — markers (Mining/Botany/Fishing/Grind/Hunt) for the zone.

Zone identified by `Player.localmapid`. Mapping registered via `ml_mesh_mgr.SetDefaultMesh(mapid, filename, enforce)` — examples from `ffxiv_init.lua`:

```lua
ml_mesh_mgr.SetDefaultMesh(130, "Ul'dah - Steps of Nald", enforce)
ml_mesh_mgr.SetDefaultMesh(128, "Limsa Lominsa Upper Decks", enforce)
ml_mesh_mgr.SetDefaultMesh(132, "New Gridania", enforce)
```

### Mesh management

```
ml_mesh_mgr.LoadNavMesh(meshname)
ml_mesh_mgr.lastmapid = 0          -- force re-evaluation next tick
ml_mesh_mgr.ResetOMC()
NavigationManager:GetNavMeshState() -- 0 / GLOBAL.MESHSTATE.MESHLOADING / MESHSAVING / MESHBUILDING
NavigationManager:ClearNavMesh()    -- unload
NavigationManager:SaveNavMesh(folder)
```

Idiomatic wait — don't act while the mesh is loading/saving/building:

```lua
return MIsLoading() or In(navmeshstate, 0,
    GLOBAL.MESHSTATE.MESHLOADING,
    GLOBAL.MESHSTATE.MESHSAVING,
    GLOBAL.MESHSTATE.MESHBUILDING)
```

## Player movement API

```
Player:MoveTo(x, y, z, stoppingdistance, followmovement, randomizePaths)              -- wiki signature (6 args)
Player:MoveTo(x, y, z, stoppingdistance, followmovement, navflag, targetid)           -- 7-arg form (in source)
Player:MoveToStraight(x, y, z, stoppingdistance)                                       -- no nav
Player:Move(MOVEMENTSTATE)                                                             -- raw key direction
Player:Stop()
Player:PauseMovement()                                                                 -- transient stop, keep path
Player:IsMoving([MOVEMENTSTATE])
Player:Jump() / IsJumping()
Player:Dive()
Player:SetFacing(x,y,z) | SetFacing(radians)
Player:SetPitch(radians)
Player:FollowTarget(EntityID)
Player:Teleport(AetheryteID)
Player:GetAetheryteList()
Player:Respawn()
Player:Interact(EntityID)
Player:BuildPath(x, y, z, floorfilters, cubefilters, targetid)
Player.pos = {x, y, z, h}                  -- y is up; h heading in radians
Player.flying.isflying / .pitch
Player.diving.isswimming / .isdiving
```

`Player:MoveTo` returns:
- `0..N` — path length / nodes remaining
- `-1..-10` — errors. Notable: `-7` already in range, `-8` no mesh, `-10` invalid coords.

**Wiki vs source**: the GitHub Player wiki documents the 6-arg signature. **Production code uses 7 args** including a `targetid`. Trust source — see `ffxiv_task_assist.lua`.

## Path filters (bitmasks)

```
GLOBAL.NODETYPE.FLOOR / .CUBE
GLOBAL.FLOOR.AVOID
GLOBAL.CUBE.AVOID, GLOBAL.CUBE.AIR
```

In-combat ground avoidance idiom (`ffxiv_common_cne.lua`):

```lua
if not IsFlying() and not IsDiving() and
   ((Player.incombat and not Player.ismounted) or IsTransporting()) then
    cubefilters = bit.bor(cubefilters, GLOBAL.CUBE.AIR)
end
```

## NavigationManager (C-side)

```
NavigationManager:GetPath(x1,y1,z1, x2,y2,z2)
NavigationManager:MoveTo(x1,y1,z1, x2,y2,z2)
NavigationManager:GetExcludeFilter(GLOBAL.NODETYPE.FLOOR)
NavigationManager:SetExcludeFilter(GLOBAL.NODETYPE.FLOOR, mask)
NavigationManager:GetRandomPointOnCircle(x,y,z, innerR, outerR)
NavigationManager:IsReachable(pos)
NavigationManager:ClearAvoidanceAreas()
NavigationManager:ResetPath()
NavigationManager.NavPathNode
NavigationManager.FloorEditorMode / .RecordDistance / .UseMouseEditor / .AutoSaveMesh
```

## `ml_navigation` — high-level wrapper

```
ml_navigation.path / .pathindex / .lasttargetid / .targetposition / .canPath
ml_navigation:HasPath()
ml_navigation:EnablePathing() / :DisablePathing()
ml_navigation:MoveTo(x, y, z, targetid)
ml_navigation:IsUsingConnection()
ml_navigation.IsHandlingInstructions(tickcount)
ml_navigation.IsHandlingOMC(tickcount)
ml_navigation.GetMovementType()  -- "2dwalk"|"3dwalk"|"3dfly"|"3dswim"|...
ml_navigation:GetRaycast_Player_Node_Distance(ppos, nodepos)
ml_navigation:IsGoalClose(ppos, node, lastnode)
ml_navigation:IsDestinationClose(ppos, goal)
ml_navigation.CanRun()                 -- ingame, not loading, alive
RaycastNav / RayCast(x1,y1,z1, x2,y2,z2)
```

**`navtype` is implicit, not numeric.** Selected at runtime from `Player.flying.isflying / .diving.isdiving / .diving.isswimming / .ismounted`. The engine builds against the cube mesh when flying/diving, otherwise the floor mesh. Per-mode tunables (verbatim from `ffxiv_navigation.lua`):

```lua
ml_navigation.NavPointReachedDistances = {
    ["3dwalk"]=2, ["2dwalk"]=.5, ["3dmount"]=5, ["2dmount"]=1,
    ["3dswim"]=5, ["2dswim"]=.75, ["3ddive"]=2.5, ["2ddive"]=1.25,
    ["3dfly"]=5, ["2dfly"]=1.5,
}
ml_navigation.PathDeviationDistances = { ["3dwalk"]=6, ["2dwalk"]=3, ["3dfly"]=10, ... }
```

**Don't spam `Player:MoveTo` every tick.** Build once, then poll until `not ml_navigation:HasPath()`. Source comment: *"MoveTo will now only build a path if one does not exist or the one it wants to use is not compatible."*

A common pitfall: `if not Player:IsMoving() then Player:MoveTo(...) end`. After mounting/transition `IsMoving()` is briefly false even though a path is active — this loop blindly reissues, builds a fresh path, and ends up oscillating. Use `Player:PauseMovement()` for transient stops and gate on `ml_navigation:HasPath()` instead.

## OMC instruction types (verbatim list)

Off-Mesh Connections: aetheryte teleports, boats, elevators, falls, custom interactions. The OMC system runs an instruction list when the path crosses a connection.

```
Ascend, QuickAscend, Descend, StraightDescend,
Stop, Mount, Dismount, Dive,
RefreshMesh, Wait, Jump, FacePosition,
MoveForward, MoveForward2, CheckIfLocked, CheckIfMoveable,
Action, Teleport, Return,
CheckIfNear, MoveStraightTo, MoveStraightToContinue,
FlyStraightTo, FlyStraightToContinue, Interact
```

Example handler (`Teleport` instruction):

```lua
elseif itype == "Teleport" then
    local aetheryteid = iparams[1] or 0
    table.insert(ml_navigation.receivedInstructions, function()
        if not Player:IsMoving() then
            if Player:Teleport(aetheryteid) then
                ml_global_information.Await(10000, function() return MIsLoading() end)
                return true
            end
        else
            Player:Stop()
            ml_global_information.Await(3000, function() return not Player:IsMoving() end)
        end
        return false
    end)
```

The handler returns `true` when the instruction completes, `false` to stay queued for the next tick. The `Await` calls yield the current frame.

## Markers

Persisted per-mesh in `Navigation/<mesh>.info`. Templates (display strings via `GetString(...)`): `"Mining"`, `"Botany"`, `"Fishing"`, `"Grind"`, `"Hunt"`, `"Evac"`, plus `unspoiledMarker` variant.

Marker modes: `Marker List`, `Single Marker`, `Random Marker`.

```
ml_marker_mgr.currentMarker
ml_marker_mgr.GetNextMarker(typeKey, filter)
ml_marker_mgr.AddMarker(markerAddType)
ml_marker_mgr.templateDisplay
ml_marker_mgr.modesDisplay
marker:GetPosition()  -- {x,y,z,h}
marker:GetTimeRemaining()
marker.pos / .type / .name / .minlevel / .maxlevel / .maxradius
marker.whitelist / .blacklist / .mincontentlevel / .maxcontentlevel
marker.nogpitem / .flags
```

Marker → task plumbing (`ffxiv_common_cne.lua`) — note that Fishing preserves heading because you have to face the water:

```lua
if markerType == "Fishing" then
    newTask.pos.h    = markerPos.h
    newTask.range    = 0.5
    newTask.doFacing = true
end
```

## Movement task classes (`ffxiv_common_tasks.lua`)

| Class | Name | Purpose |
|---|---|---|
| `ffxiv_task_movetopos` | `MOVETOPOS` | Walk/fly to `self.pos`; handles OMCs/connections |
| `ffxiv_task_movetomap` | `MOVETOMAP` | Cross-zone — picks aetheryte → spawns teleport |
| `ffxiv_task_teleport` | `TELEPORT` | Casts Teleport, waits load |
| `ffxiv_task_movetointeract` | — | Move + `Player:Interact()` |
| `ffxiv_task_movetofate` | — | Chase moving fate target |
| `ffxiv_mesh_interact` | — | OMC end-point interact (door/aetheryte) |
| `ffxiv_nav_interact` | — | Generic NPC interact |
| `ffxiv_task_syncadjust` | `SYNC_ADJUSTMENT` | Final facing nudge |

### Standard CnE element wiring (priorities shown)

```lua
local ke_unlockAethernet  = ml_element:create("UnlockAethernet",  c_unlockaethernet,  e_unlockaethernet,  150)
local ke_teleportToMap    = ml_element:create("TeleportToMap",    c_teleporttomap,    e_teleporttomap,    140)
local ke_teleportToPos    = ml_element:create("TeleportToPos",    c_teleporttopos,    e_teleporttopos,    130)
local ke_useNavInteraction= ml_element:create("UseNavInteraction",c_usenavinteraction,e_usenavinteraction, 90)
local ke_getMovementPath  = ml_element:create("GetMovementPath",  c_getmovementpath,  e_getmovementpath,   85)
local ke_falling          = ml_element:create("Falling",          c_falling,          e_falling,           60)
local ke_walkToPos        = ml_element:create("WalkToPos",        c_walktopos,        e_walktopos,         40)
self:AddTaskCheckCEs()
```

Key gating cause `c_walktopos` (verbatim — note that returning `true` even on the disable branch is intentional because `e_walktopos:execute` is now committed to running):

```lua
function c_walktopos:evaluate()
    if Busy() or Player:IsJumping() or IsMounting() then return false end
    if ml_navigation:HasPath() then
        if ml_navigation:EnablePathing() then ... end
        return true
    else
        if ml_navigation:DisablePathing() then ... end
        return true
    end
end
```

## Mount / fly

There is no `Player:Mount(id)`. Mount via casting a `type=13` action; helpers `Mount()` / `Dismount()` exist in `ffxiv_helpers.lua`.

State queries: `Player.ismounted`, `IsMounting()`, `Player.flying.isflying`, `IsFlying()`, `CanFlyInZone()`, `QuestCompleted(2117)` (ARR flight), `QuestCompleted(524)` (5.3 flight unlock).

### Pitch control for fly-straight-to

```lua
local minVector = math.normalize(math.vectorize(myPos, pos))
local pitch     = math.asin(-1 * minVector.y)
Player:SetPitch(pitch)
Player:SetFacing(pos.x, pos.y, pos.z, true)   -- 4th arg = include vertical
```

### Ascend instruction

```lua
if IsFlying() then
    if Player:IsMoving(FFXIV.MOVEMENT.UP) then
        return true
    else
        Player:Move(128); ml_global_information.Await(math.random(300,500))
        return false
    end
else
    Player:Jump(); ml_global_information.Await(math.random(50,150))
    return false
end
```

`FFXIV.MOVEMENT.FORWARD/BACKWARD/UP/DOWN` — with `UP == 128` numerically.

## Teleport

```
Player:Teleport(AetheryteID)        -- casts in-game Teleport
Player:GetAetheryteList()           -- attuned list at runtime — pull live
```

Common ids (subject to drift across patches — pull live for correctness):

```
8   Limsa Lominsa Lower Decks
9   New Gridania
53  Ul'dah - Steps of Nald
70  Mor Dhona
75  Foundation
104 Idyllshire
105 Rhalgr's Reach
128 The Crystarium
132 Old Sharlayan
182 Solution Nine
```

Cross-zone task spawn (`e_teleporttomap:execute`):

```lua
local newTask = ffxiv_task_teleport.Create()
newTask.aetheryte = e_teleporttomap.aeth.id
newTask.mapID     = e_teleporttomap.aeth.territory
ml_task_hub:Add(newTask, IMMEDIATE_GOAL, TP_IMMEDIATE)
```

## Distance helpers

```
math.distance2d(a, b)       math.distance3d(a, b)
Distance2D(x1,z1,x2,z2)     Distance3D(x1,y1,z1,x2,y2,z2)
Distance2DT(posA, posB)     Distance3DT(posA, posB)
PDistance3D(...)            -- path distance via navigation
math.angle(v1, v2)          math.normalize(v)          math.vectorize(a, b)
AngleFromPos(pos1, pos2)
DegreesToHeading(deg)       ConvertHeading(h)
GetPosFromDistanceHeading(pos, distance, heading)
FindClosestMesh(pos, radius, includeAir, includeUnreachable)
```

`PDistance3D` is the right call when you care about *walking* distance not Euclidean — e.g. evaluating "is this gather node actually closer when I have to go around the mountain".

## Stuck detection (`ffxiv_unstuck.lua`)

The bot tracks coarse displacement `coarse.lastDist` over a window. If `ml_navigation:HasPath() and ml_navigation.canPath and not ffnav.IsProcessing()` but the player didn't cover ~10 units (×0.3 if a slow buff is present), an unstuck sequence runs:

1. Step backward
2. Remesh local triangles
3. Save mesh
4. Escalate to "Teleport to local Aetheryte" if `gStuckTeleport` is set

The escalation path has historically been unreliable — gate it.

For your own stuck detection inside a long-running task, the same heuristic works: sample `Player.pos` periodically, compare to expected progress along `ml_navigation.path`, and trigger recovery if displacement falls below threshold.

## Travel task example (~60 lines)

A complete cross-zone travel task:

```lua
ffxiv_task_travelto = inheritsFrom(ml_task)
ffxiv_task_travelto.name = "TRAVELTO"

function ffxiv_task_travelto.Create(mapid, x, y, z, aetheryteid)
    local t = inheritsFrom(ffxiv_task_travelto)
    t.valid, t.completed, t.subtask = true, false, nil
    t.process_elements, t.overwatch_elements = {}, {}
    t.destMapID = mapid
    t.aetheryte = aetheryteid
    t.pos       = { x = x, y = y, z = z }
    t.range     = 2
    return t
end

function ffxiv_task_travelto:Init()
    local ke_tp = ml_element:create("TeleportToMap", c_teleporttomap, e_teleporttomap, 140)
    self:add(ke_tp, self.process_elements)
    local ke_path = ml_element:create("GetMovementPath", c_getmovementpath, e_getmovementpath, 85)
    local ke_walk = ml_element:create("WalkToPos",       c_walktopos,       e_walktopos,       40)
    self:add(ke_path, self.process_elements)
    self:add(ke_walk, self.process_elements)
    self:AddTaskCheckCEs()
end

function ffxiv_task_travelto:task_complete_eval()
    if Player.localmapid ~= self.destMapID then return false end
    if NavigationManager:GetNavMeshState() ~= 1 then return false end
    return math.distance3d(Player.pos, self.pos) <= self.range
       and math.distance2d(Player.pos, self.pos) <= self.range
end

function ffxiv_task_travelto:task_complete_execute()
    Player:Stop(); self.completed = true
end

-- Usage:
-- ml_task_hub:Add(ffxiv_task_travelto.Create(135, -274.0, 18.5, 158.4, 18),
--                 IMMEDIATE_GOAL, TP_IMMEDIATE)
```

## Gather-loop skeleton (~50 lines)

Demonstrates an imperative `Process()` style — bypasses cause/effect for a tight pickup loop:

```lua
ffxiv_simple_gather = inheritsFrom(ml_task)
ffxiv_simple_gather.name = "SIMPLE_GATHER"

local function FindNearestNode(maxLevel)
    local list = MEntityList("onmesh,gatherable,targetable,minlevel=1,maxlevel="..maxLevel)
    if not table.valid(list) then return nil end
    local best, bestDist = nil, 99999
    for _, e in pairs(list) do
        local d = math.distance3d(Player.pos, e.pos)
        if d < bestDist then best, bestDist = e, d end
    end
    return best
end

function ffxiv_simple_gather.Create(centerPos, radius)
    local t = inheritsFrom(ffxiv_simple_gather)
    t.valid, t.completed = true, false
    t.process_elements, t.overwatch_elements = {}, {}
    t.center, t.radius = centerPos, radius or 150
    return t
end

function ffxiv_simple_gather:Process()
    if Player.incombat or MIsLoading() or MIsCasting() then return end
    local node = FindNearestNode(Player.level + 5)
    if not node then
        Player:MoveTo(self.center.x, self.center.y, self.center.z, 4, 0, 1, 0); return
    end
    local pos, d = node.pos, math.distance3d(Player.pos, node.pos)
    if d > 3.5 then
        Player:MoveTo(pos.x, pos.y, pos.z, 3.0, 0, 1, node.id); return
    end
    if Player.ismounted then Dismount(); return end
    Player:SetFacing(pos.x, pos.y, pos.z)
    if not Player:GetTarget() or Player:GetTarget().id ~= node.id then
        Player:SetTarget(node.id); return
    end
    Player:Gather(node.id)
end
```

## Caveats

- **Wiki Player page (last edited Jan 2015)** lists 6-arg `Player:MoveTo`. Production uses 7 args including `targetid` — trust source.
- **`Player:SetSpeed()` disabled by SE Dec 2014.** Don't use.
- **Aetheryte ids change across patches.** Pull at runtime via `Player:GetAetheryteList()` instead of hardcoding.
- **`gStuckTeleport` "Teleport to local Aetheryte" stuck recovery has been unreliable historically.** Gate it behind a user-toggleable setting and provide alternate recovery.
- **`distance` is frozen during teleport / load screens** — gate distance checks on `not MIsLoading()`.

## Source pointers

- **Wiki — Navmesh editor / file formats**: https://wiki.mmominion.com/doku.php?id=navmesheditor
- **Source — `ffxiv_navigation.lua`**: `ml_navigation` internals, mode-dependent reach distances
- **Source — `ffxiv_common_tasks.lua`**: standard movement task classes
- **Source — `ffxiv_common_cne.lua`**: cause/effect elements for movement, OMC handlers
- **Source — `ffxiv_unstuck.lua`**: stuck detection + recovery sequences
- **Source — `ffxiv_task_assist.lua`**: `Player:MoveTo` 7-arg usage in production
