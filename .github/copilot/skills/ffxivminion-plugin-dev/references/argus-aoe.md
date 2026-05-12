# Argus — AOE detection & dodging

For Argus integration: telegraph reading, geometric hit tests, dodge orchestrators, packet-level event hooks, world-space telegraph drawing, tether/marker queries. Read SKILL.md first — registration timing, color encoding, lifecycle ordering, and the feature-detection idiom apply throughout.

## What Argus is

Argus is a closed-source MMOMinion add-on (companion to TensorCore) that exposes a Lua module named `argus` providing two concerns:

1. **World draws** — high-quality on-the-floor primitives (circle, donut, cone, rect, cross, line, arrow, chevron) with frame and timed variants. Used by `RikuMNK`, `RikuRDM`, `TensorReaper3`, etc., for positionals, knockback indicators, range circles.
2. **Detection feeds** — telegraph-aware AOE list (`getCurrentAOEs` / `getCurrentDirectionalAOEs` / `getCurrentGroundAOEs`), tether map (`getCurrentTethers`), waymarkers (`getWaymarkInfo`), entity auras/visibility, plus packet-level event hooks (`registerOnEntityCast` / `Channel` / `MapEffect` / `MarkerAdd` / `TetherChange` / `AOECreate` / `FloorChange` / `EventObjectScript[2]`).

It's the substrate for healer mitigation triggers, raid-ready dodge logic, and "draw exactly where the cone will land" UX — superior to the built-in `gAvoidAOE` (rectangle/circle heuristic; doesn't see donut inner radii or untelegraphed cone angles).

Argus user docs: https://wiki.mmominion.com/doku.php?id=argus . Full API reference: https://wiki.mmominion.com/doku.php?id=argusdocs (last edited 2022-11-22; some shapes added since are documented in-line).

## Loading & gating

Argus is **not** part of base FFXIVMinion. Always feature-detect — your plugin must work without it:

```lua
local ARGUS = (type(_G.Argus) == "table") and _G.Argus or nil
local function ArgusLoaded() return ARGUS ~= nil end
```

Argus also depends on TensorCore (the binary side); some functions (`forceMisdirectionMovement`, etc.) return `nil` when "TensorCore Exe" isn't loaded. **Treat every Argus call as fallible** — wrap critical paths.

### CRITICAL: registration timing

Every `register*` callback (`registerOnAOECreateFunc`, `registerOnEntityCast`, `registerOnEntityChannel`, `registerOnEventObjectScript[2]Func`, `registerOnFloorChangeFunc`, `registerOnMapEffect`, `registerOnMarkerAdd`, `registerOnTetherChange`) **must be called from your `Module.Initalize` handler, NOT at file-load time**, or you'll get nil errors. The wiki repeats this on every register function.

```lua
RegisterEventHandler("Module.Initalize", function()
    if not Argus then return end
    Argus.registerOnEntityCast(my_addon.OnEntityCast)
    Argus.registerOnAOECreateFunc(my_addon.OnAOECreate)
    Argus.registerOnTetherChange(my_addon.OnTetherChange)
end, "my_addon.ArgusInit")
```

## Color encoding

Argus draw functions take **`u32color`** — a packed unsigned 32-bit color. **Convert from float RGBA via `GUI:ColorConvertFloat4ToU32(r, g, b, a)`** where each channel is `[0,1]`. Same encoding ImGui uses for `GUI:AddRectFilled` etc.

```lua
local cFillRed   = GUI:ColorConvertFloat4ToU32(1.0, 0.20, 0.20, 0.40)
local cOutline   = GUI:ColorConvertFloat4ToU32(1.0, 1.0,  1.0,  1.0)
local cFillAmber = GUI:ColorConvertFloat4ToU32(1.0, 0.65, 0.0,  0.35)
```

The deprecated `Argus.addTimed*` family takes an `rgbFill` *struct* `{r=,g=,b=,a=}` instead of `u32color` — those are deprecated. **Use `Argus2.*` timed draws** which use the same `u32color` encoding as the per-frame `Argus.*` family.

## Detection structures

The data shapes you reason over.

### `DirectionalAOE` (originates from an entity center / attached)

```
x, y, z          number   Position (usually entity center)
heading          number   Radians; direction the AOE faces
aoeType          int      Animation/omen template id
aoeLength        int      Length / radius (yalms)
aoeWidth         int      Width (yalms; 0 for cones & circles)
aoeName          string
aoeID            int      Cast/Spell ID — same id space as ActionList
aoeCastType      number   See castType enum below
targetAttach     int|nil  Entity id this AOE is anchored to (nil = static)
aoeAnimationInfo aoeAnimationInfo
aoeEffectInfo    aoeEffectInfo
isAreaTarget     bool     True = ability picks ground at cast time (heuristics applied)
```

### `GroundAOE` (puddles / non-attached)

Same fields as `DirectionalAOE` but `heading` is always `0` and the AOE is rooted to ground coordinates rather than tracking a caster.

### `castType` enum (canonical shapes — wiki verbatim)

| Value(s) | Shape |
|---|---|
| `2, 5, 7` | Circle (point-blank or targeted radius) |
| `3, 13` | Cone / arc |
| `4, 12` | Line (rectangle, originating from caster) |
| `6` | Meteor (proximity-falloff; usually unavoidable) |
| `8` | Targeted line — Argus auto-adjusts length and heading toward target |
| `10` | Donut (inner-radius from `aoeEffectInfo`) |
| `11` | Cross (4-arm, post-Shadowbringers) |

**Heuristic notes**:

- For `castType` 2/5/7: `aoeLength` is the radius. Width is 0.
- For 3/13 cones: `aoeLength` is cone radius (full circle equivalent), `aoeWidth` is 0; **arc angle has to be looked up via `aoeEffectInfo.aoeEffectName`** for telegraphed cones, or guessed for non-telegraphed (90°/120° common). Untelegraphed cone arc is *not* sent to client (per Argus wiki disclaimer).
- For 8 (targeted line): heading & length are pre-resolved by Argus when you read it.
- For 10 donuts: outer = `aoeLength`; **inner radius is not directly in `DirectionalAOE`** — telegraphed donuts encode it via `aoeEffectInfo`.
- For 11 cross: 4 perpendicular rectangles centered on `(x,y,z)` of `aoeLength` × `aoeWidth`.

## Three ways to read AOEs

```lua
-- (1) Merged, in-order list — best for dodge logic
Argus.getCurrentAOEs()                       -- table of GroundAOE/DirectionalAOE

-- (2) Just directional, optionally ordered (default keyed by source entityID)
Argus.getCurrentDirectionalAOEs(false)
Argus.getCurrentDirectionalAOEs(true)        -- ordered by appearance

-- (3) Just ground-anchored
Argus.getCurrentGroundAOEs(false)
```

A "current AOE" is one whose telegraph is up OR whose pre-telegraph window has begun. Argus will emit AOEs with **no telegraph at all** for fast/un-telegraphed casts.

## Geometric hit tests

Argus does **not** ship a "would this hit me" function. Roll your own — these four shapes cover 99% of cases. Heading is in radians, FFXIV convention (north = 0, increasing clockwise looking down the Y axis; **Y is up**, ground plane is X/Z).

```lua
local TWO_PI = math.pi * 2

local function NormalizeAngle(a)            -- to (-pi, pi]
    while a >  math.pi do a = a - TWO_PI end
    while a <= -math.pi do a = a + TWO_PI end
    return a
end

local function HeadingTo(fromPos, toPos)
    return math.atan2(toPos.x - fromPos.x, toPos.z - fromPos.z)
end

-- ground-projected distance (FFXIV uses Y as up)
local function Dist2D(a, b)
    local dx, dz = a.x - b.x, a.z - b.z
    return math.sqrt(dx*dx + dz*dz)
end

local function PointInCircle(p, cx, cy, cz, radius)
    local d = Dist2D(p, {x=cx, z=cz})
    return d <= radius
end

local function PointInDonut(p, cx, cy, cz, rInner, rOuter)
    local d = Dist2D(p, {x=cx, z=cz})
    return d >= rInner and d <= rOuter
end

-- Cone: tip at (cx,cz) facing heading, half-angle = arcAngle/2, length = radius
local function PointInCone(p, cx, cy, cz, heading, arcAngle, radius)
    local d = Dist2D(p, {x=cx, z=cz})
    if d > radius or d < 0.01 then return d <= radius end
    local hToP = HeadingTo({x=cx, z=cz}, p)
    return math.abs(NormalizeAngle(hToP - heading)) <= arcAngle * 0.5
end

-- Line/rect: caster at (cx,cz), goes "length" yalms in heading, half-width
local function PointInRect(p, cx, cy, cz, heading, length, width)
    local dx, dz = p.x - cx, p.z - cz
    -- rotate into rect-local frame; in FFXIV heading 0 = +Z
    local cosH, sinH = math.cos(heading), math.sin(heading)
    local localZ =  dx * sinH + dz * cosH    -- forward axis
    local localX =  dx * cosH - dz * sinH    -- side axis
    return localZ >= 0 and localZ <= length
       and math.abs(localX) <= width * 0.5
end

-- Cross: union of two perpendicular rects (4 arms, each `length` long)
local function PointInCross(p, cx, cy, cz, heading, length, width)
    return PointInRect(p, cx, cy, cz, heading,             length, width)
        or PointInRect(p, cx, cy, cz, heading + math.pi,   length, width)
        or PointInRect(p, cx, cy, cz, heading + math.pi/2, length, width)
        or PointInRect(p, cx, cy, cz, heading - math.pi/2, length, width)
end
```

## Dispatch by `aoeCastType`

```lua
local function PointInAOE(p, aoe)
    local ct = aoe.aoeCastType
    -- Resolve attached source position dynamically.
    -- Argus copies a snapshot — you must update for moving casters.
    local sx, sy, sz, sh = aoe.x, aoe.y, aoe.z, aoe.heading or 0
    if aoe.targetAttach then
        local src = EntityList:Get(aoe.targetAttach)
        if src then
            sx, sy, sz = src.pos.x, src.pos.y, src.pos.z
            if not aoe.isAreaTarget then sh = src.pos.h end
        end
    end

    if ct == 2 or ct == 5 or ct == 7 then
        return PointInCircle(p, sx, sy, sz, aoe.aoeLength)
    elseif ct == 3 or ct == 13 then
        local arc = aoe.aoeArc or math.rad(90)            -- supply per-cast lookup
        return PointInCone(p, sx, sy, sz, sh, arc, aoe.aoeLength)
    elseif ct == 4 or ct == 12 or ct == 8 then
        return PointInRect(p, sx, sy, sz, sh, aoe.aoeLength, aoe.aoeWidth)
    elseif ct == 10 then
        local inner = aoe.aoeInnerRadius or 0             -- supply per-cast or 0
        return PointInDonut(p, sx, sy, sz, inner, aoe.aoeLength)
    elseif ct == 11 then
        return PointInCross(p, sx, sy, sz, sh, aoe.aoeLength, aoe.aoeWidth)
    elseif ct == 6 then
        return PointInCircle(p, sx, sy, sz, aoe.aoeLength)
    end
    return false
end
```

**Important**: `Argus.registerOnAOECreateFunc` callbacks receive a **copy** that does *not* update positions. For attached AOEs, re-resolve `targetAttach`'s `pos` every frame — the snapshot will go stale immediately on a moving caster.

## Built-in avoidance vs custom Argus dodge

Base FFXIVMinion's avoidance lives in `ffxiv_common_cne.lua`:

```lua
if (IsFlying() or not gAvoidAOE or tonumber(gAvoidHP) == 0
    or tonumber(gAvoidHP) < Player.hp.percent or not Player.onmesh) then
    -- ...skip avoidance
end
if (spellData.id == lastAvoid.data.id and e.id == lastAvoid.attacker.id
    and Now() < lastAvoid.timer) then
    -- already dodged this recently
end
```

Two settings worth knowing for cooperation: `gAvoidAOE` (on/off) and `gAvoidHP` (skip avoidance below HP%, used to stop AoE-dodging during invuln/kiting). Eureka has a separate `gEurekaAvoidHP`. Some store add-ons (KitanoiFuncs) **disable** the base system and replace it.

If your plugin does dodge logic, pick one:

- **Cooperate**: don't override built-in unless `gAvoidAOE == false` or your toggle is on.
- **Replace**: set your own toggle and `gAvoidAOE = false` while active. Document it.

## Picking a safe spot

Greedy local search (cheap, sufficient for most ARR–EW content). Sample candidate yards at expanding rings, score by minimum distance from any AOE edge, reject samples not on the navmesh.

```lua
local function SafeSpotSearch(currentPos, aoes, minSafe, maxRadius)
    minSafe   = minSafe   or 1.5
    maxRadius = maxRadius or 12
    local best, bestScore = nil, -math.huge
    for r = 0, maxRadius, 2 do
        local steps = (r == 0) and 1 or math.max(8, math.floor(r * 4))
        for i = 0, steps - 1 do
            local a = (i / steps) * TWO_PI
            local cand = {
                x = currentPos.x + math.cos(a) * r,
                y = currentPos.y,
                z = currentPos.z + math.sin(a) * r,
            }
            -- Snap to mesh
            if NavigationManager:IsReachable(cand) then
                local minClear = math.huge
                local hit = false
                for _, aoe in pairs(aoes) do
                    if PointInAOE(cand, aoe) then hit = true; break end
                    -- Approximate clearance by edge distance
                    local d = Dist2D(cand, aoe)
                    minClear = math.min(minClear, d)
                end
                if not hit and minClear >= minSafe then
                    local score = -Dist2D(cand, currentPos)   -- prefer closest safe spot
                    if score > bestScore then best, bestScore = cand, score end
                end
            end
        end
        if best then return best end
    end
    return best
end
```

## Dodge orchestrator (cause/effect element)

Drops into a task's `process_elements`. Priority `120` puts it above `c_walktopos` (40) and above ACR's typical `Cast()` priority but below teleport/load gates — so dodge wins over normal movement and combat, but defers to map transitions.

```lua
local function GetActiveAOEs()
    if not Argus then return {} end
    local raw = Argus.getCurrentAOEs() or {}
    -- Resolve attached positions in-place (shallow copy via metatable)
    local out = {}
    for _, a in pairs(raw) do
        if a.targetAttach then
            local src = EntityList:Get(a.targetAttach)
            if src then
                a = setmetatable({}, { __index = a })
                a.x, a.y, a.z = src.pos.x, src.pos.y, src.pos.z
                if not a.isAreaTarget then a.heading = src.pos.h end
            end
        end
        table.insert(out, a)
    end
    return out
end

c_dodgeaoe = inheritsFrom(ml_cause)
e_dodgeaoe = inheritsFrom(ml_effect)
c_dodgeaoe.safePos = nil
c_dodgeaoe.lastEval = 0

function c_dodgeaoe:evaluate()
    if not Player or not Player.alive or not Player.onmesh then return false end
    if Player.flying and Player.flying.isflying then return false end
    -- Throttle eval to ~20 Hz; the search is cheap but not free
    if Now() - c_dodgeaoe.lastEval < 50 then return c_dodgeaoe.safePos ~= nil end
    c_dodgeaoe.lastEval = Now()

    local aoes = GetActiveAOEs()
    local inAny = false
    for _, a in pairs(aoes) do
        if PointInAOE(Player.pos, a) then inAny = true; break end
    end
    if not inAny then c_dodgeaoe.safePos = nil; return false end

    c_dodgeaoe.safePos = SafeSpotSearch(Player.pos, aoes, 1.5, 12)
    return c_dodgeaoe.safePos ~= nil
end

function e_dodgeaoe:execute()
    local p = c_dodgeaoe.safePos
    if not p then return end
    Player:MoveTo(p.x, p.y, p.z, 0.5, 0, 1, 0)
end

-- Wire into your task:
-- self:add(ml_element:create("DodgeAOE", c_dodgeaoe, e_dodgeaoe, 120),
--          self.process_elements)
```

**Health gate** (don't dodge while invuln'd or being kited intentionally):

```lua
function c_dodgeaoe:evaluate()
    if not gMyAddonAvoid then return false end
    if tonumber(gMyAddonAvoidHP or 0) > 0
       and Player.hp.percent < tonumber(gMyAddonAvoidHP) then return false end
    -- ...rest as above
end
```

## Packet-level event hooks

The `register*` family fires off the *server packet stream* (per wiki: "almost 100% reliable"). Use these for healer reactions, prepull markers, and fast off-GCD weaves that can't wait for the next `getCurrentAOEs` frame.

```lua
-- Fires on every successful entity cast packet
function my_addon.OnEntityCast(entityID, actionID, x, y, z, heading, mainTargetID, targets)
    -- Argus tip: get radius from ActionList:Get(1, actionID).radius
    local action = ActionList:Get(1, actionID)
    local radius = action and action.radius or 0
    -- targets is a list of ids actually hit
    if targets then
        for _, tid in pairs(targets) do
            if tid == Player.id then
                -- I just got hit — could trigger healer pre-mit
            end
        end
    end
    -- Snapshot draw of the AOE that just resolved
    if Argus.getSpellAOEInfo then
        local info = Argus.getSpellAOEInfo(actionID)
        -- info has aoeCastType, aoeLength, aoeWidth, etc. (no instance pos)
        -- Combine with x,y,z + heading from this callback to draw
    end
end

-- Channel begins (long cast bar starts)
function my_addon.OnEntityChannel(entityID, channelID, targetID, channelTimeMax)
    -- Plan mitigations channelTimeMax seconds out
end

-- Tether change — TB/Buster swaps
function my_addon.OnTetherChange(srcID, oldTID, oldTFlags, oldTargetID,
                                 newTID, newTFlags, newTargetID)
    if newTargetID == Player.id then
        -- I just got tethered — alert/draw
    end
end

-- Overhead markers (1-8 markers, baits, stack)
function my_addon.OnMarkerAdd(entityID, markerType)
    if entityID == Player.id then
        d("[AOE] Got marker type "..markerType)
    end
end

-- Map-effect (Shadowbringers+ visual cues like Zodiark snake patterns)
function my_addon.OnMapEffect(a1, a2, a3)
    -- a1/a2/a3 are pattern indices; combine to identify mechanic
end
```

## Tether queries (alt to OnTetherChange polling)

```lua
local tethers = Argus.getCurrentTethers()
for srcId, list in pairs(tethers) do
    for _, t in ipairs(list) do
        if t.targetid == Player.id then
            d("Tethered from "..srcId.." (type "..t.type..")")
        end
    end
end

-- Per-entity:
local mine = Argus.getTethersOnEnt(Player.id)
for _, t in ipairs(mine) do
    -- t.type, t.partnerid
end
```

## Misdirection movement

The base movement API can't move while the misdirection finger debuff is up. Argus exposes:

```lua
local h = Argus.getMisdirectionHeading()    -- returns radians, [-pi, pi]
Argus.setMisdirectionHeading(targetHeading) -- only applies to controllable variant
Argus.forceMisdirectionMovement(true)       -- start
Player:Move(FFXIV.MOVEMENT.FORWARD)         -- combined with the above
-- ...arrive...
Argus.forceMisdirectionMovement(false)
```

Returns `nil` when TensorCore exe is not loaded — feature-detect.

## Telegraph drawing

Use these to render your own omens, range circles, knockback indicators, partner-callout overlays, etc.

### Per-frame (call every `Gameloop.Draw`)

```lua
Argus.addCircleFilled(x, y, z, radius, segments, colorFill,
                      colorOutline, outlineThickness,
                      gradientIntensity, gradientMinOpacity, oldDraw)

Argus.addConeFilled(x, y, z, radius, arcAngle, heading, segments,
                    colorFill, colorOutline, ...)

Argus.addRectFilled(x, y, z, length, width, heading, colorFill, ...)

Argus.addDonutFilled(x, y, z, rInner, rOuter, segments, colorFill, ...)

Argus.addLineFilled(x1, y1, z1, x2, y2, z2, colorFill,
                    outlineThickness, endpointThickness)
```

`segments` rule: 50 for circles/donuts, 30 for cones. Don't go higher — perf cost is real.

### Timed (`Argus2.*`, fires once with auto-cleanup)

```lua
local uuid = Argus2.addTimedCircleFilled(timeout_ms, x, y, z, radius, segments,
                                         colorStart, colorEnd, colorMid,
                                         delay_ms, entityAttachID,
                                         colorOutline, outlineThickness,
                                         gradientIntensity, gradientMinOpacity,
                                         oldDraw)
-- Cancel early:
Argus.deleteTimedShape(uuid)        -- nil arg = delete all timed
```

`entityAttachID` — when set, `(x,y,z)` follows that entity each frame. For directional shapes (`Cone`/`Rect`/`Cross`/`Arrow`/`Chevron`), `targetAttachID` makes the shape *point at* another entity (auto-extends length).

### `ShapeDrawer` class (modern, ergonomic)

If you draw the same family of shapes repeatedly with the same colors, build one drawer:

```lua
local d_buster = Argus2.ShapeDrawer:new(
    GUI:ColorConvertFloat4ToU32(1, 0.1, 0.1, 0.10),       -- start
    nil,                                                  -- mid (skip)
    GUI:ColorConvertFloat4ToU32(1, 0.1, 0.1, 0.40),       -- end
    GUI:ColorConvertFloat4ToU32(1, 1.0, 1.0, 1.00),       -- outline
    1.5)                                                  -- outlineThickness
d_buster.segments = 50

-- Frame draw on a tank tether (cone toward partner)
d_buster:addConeOnEnt(boss.id, 25, math.rad(60), tankId)   -- (timeout absent = frame)
-- Timed donut around me for 4s
d_buster:addTimedDonutOnEnt(4000, Player.id, 4, 16)
```

Available `ShapeDrawer:add*` shapes (frame + timed + `OnEnt` variants):
`Arrow`, `Chevron`, `Circle`, `Cone`, `Cross`, `Donut`, `Line`, `Rect`. The `OnEnt` variants accept either an entity table or an entity id — pick whichever you have without coercing.

## Auras & visibility

```
persistent, active1, active2 = Argus.getEntityAuras(entityOrId)   -- 3 ints
Argus.getEntityModel(entityOrId) -> int                           -- subcontentid
Argus.isEntityVisible(entityOrId) -> bool
```

Auras are arena/buff phase identifiers — **not** the same as game buffs. The wiki notes them as low-level numeric ids — combine with content/encounter logic to disambiguate phases. `getEntityModel` exists because some game entities (notably housing target dummies) share a `contentid` and need a sub-id to tell apart.

## Waymarks / field markers

```lua
local x, y, z, isActive, lastModifyTs = Argus.getWaymarkInfo(markerID)
-- markerID is the ActionList type-15 spell id. Same id space.
```

Useful for "did the raid place 1/2/3/4" pattern detection, position-relative call-outs, and avoiding kicking off mechanics until markers are placed.

## Complete "AOE radar" example plugin (~110 lines)

```lua
-- LuaMods/aoe_radar/aoe_radar.lua
local mod = { gui = { open = false, visible = true, name = "AOE Radar" } }

local function ColorU32(r, g, b, a) return GUI:ColorConvertFloat4ToU32(r, g, b, a) end
local CLR = {
    danger     = nil, dangerO   = nil,
    warn       = nil, warnO     = nil,
    safe       = nil, safeO     = nil,
}

local function InitColors()
    CLR.danger  = ColorU32(1.0, 0.10, 0.10, 0.35)
    CLR.dangerO = ColorU32(1.0, 0.40, 0.40, 1.00)
    CLR.warn    = ColorU32(1.0, 0.65, 0.00, 0.30)
    CLR.warnO   = ColorU32(1.0, 0.80, 0.30, 1.00)
    CLR.safe    = ColorU32(0.20, 1.0, 0.30, 0.20)
    CLR.safeO   = ColorU32(0.40, 1.0, 0.50, 1.00)
end

-- Geometry helpers (PointInAOE etc. assumed defined as above) ----------------

local function ResolveAttached(a)
    if a.targetAttach then
        local src = EntityList:Get(a.targetAttach)
        if src then
            local copy = setmetatable({}, { __index = a })
            copy.x, copy.y, copy.z = src.pos.x, src.pos.y, src.pos.z
            if not a.isAreaTarget then copy.heading = src.pos.h end
            return copy
        end
    end
    return a
end

local function DrawOne(a, hitMe)
    local fill, outline = (hitMe and CLR.danger or CLR.warn),
                         (hitMe and CLR.dangerO or CLR.warnO)
    local ct = a.aoeCastType
    if ct == 2 or ct == 5 or ct == 7 then
        Argus.addCircleFilled(a.x, a.y, a.z, a.aoeLength, 50, fill, outline, 1.5)
    elseif ct == 3 or ct == 13 then
        Argus.addConeFilled(a.x, a.y, a.z, a.aoeLength,
                            a.aoeArc or math.rad(90), a.heading, 30,
                            fill, outline, 1.5)
    elseif ct == 4 or ct == 12 or ct == 8 then
        Argus.addRectFilled(a.x, a.y, a.z, a.aoeLength, a.aoeWidth, a.heading,
                            fill, outline, 1.5)
    elseif ct == 10 then
        local inner = a.aoeInnerRadius or 0
        Argus.addDonutFilled(a.x, a.y, a.z, inner, a.aoeLength, 50,
                             fill, outline, 1.5)
    elseif ct == 11 then
        Argus.addCrossFilled(a.x, a.y, a.z, a.aoeLength, a.aoeWidth, a.heading,
                             fill, outline, 1.5)
    end
end

function mod.OnDraw()
    if not Argus or not mod.gui.open then return end
    local raw = Argus.getCurrentAOEs() or {}
    for _, aoe in pairs(raw) do
        local a = ResolveAttached(aoe)
        local hitMe = PointInAOE(Player.pos, a)
        DrawOne(a, hitMe)
    end
end

function mod.OnUpdate()
    if MGetGameState() ~= FFXIV.GAMESTATE.INGAME then return end
    if not mod.gui.open or not Argus then return end
    -- Just keep counters fresh; could add logging, sound, etc.
end

function mod.OnInit()
    InitColors()
    if Argus then
        Argus.registerOnEntityCast(function(eid, aid)
            d(string.format("[AOE Radar] cast by %d action %d", eid, aid))
        end)
    end
    ml_gui.ui_mgr:AddComponent({
        header  = { id = "AOERADAR##MENU_HEADER", expanded = false, name = "AOE Radar" },
        members = {{
            id = "AOERADAR##MENU_MAIN", name = "Toggle",
            onClick = function() mod.gui.open = not mod.gui.open end,
            tooltip = "Render Argus AOE outlines",
        }},
    })
end

RegisterEventHandler("Module.Initalize", mod.OnInit,   "AOE_Radar.Init")
RegisterEventHandler("Gameloop.Update",  mod.OnUpdate, "AOE_Radar.Update")
RegisterEventHandler("Gameloop.Draw",    mod.OnDraw,   "AOE_Radar.Draw")
```

`module.def`:
```ini
[Module]
Name=aoe_radar
Dependencies=minionlib,FFXIVMinion
Version=1
Files=aoe_radar.lua
Enabled=1
```

## Caveats & pitfalls

- **Update timing**: Argus updates AOE state on its own cadence. Don't assume the list at `Gameloop.Update` and at `Gameloop.Draw` is identical the same frame. For reactions, call `getCurrentAOEs()` once per Update and pass to Draw via state.
- **Untelegraphed cone arcs**: server-side hit-detection. Client doesn't know the angle. Use a sane default (often 90°) per cast id; build a per-encounter override table from logs.
- **Donut inner radius**: not in `DirectionalAOE` directly. Look in `aoeEffectInfo.aoeEffectName` and maintain a lookup if you need precision; otherwise default `inner = 4` covers most ARR–EW patterns.
- **`oldDraw=true`**: forces the shape to render on top of everything, including boss models. Use sparingly (partner-callout overlays); abusing it costs perf and looks like an addon.
- **Deprecated `Argus.addTimed*`** family takes the `rgbFill` struct. Use `Argus2.addTimed*` (u32 colors with start/mid/end transitions).
- **Argus update lag for raid tiers**: per the public Argus page, the API is intentionally **withheld for the first weeks of a new raid tier** for safety. Don't assume freshly-released content has Argus support.
- **Healer mitigation rule** (verbatim from `healer_reactions_pack` wiki): *"Argus will not trigger mitigation if it doesn't detect certain unavoidable AoEs. Be mindful of this in Savage and Ultimate."* — your plugin must not assume Argus catches everything.
- **No facing-only filter**: many cleaves are `castType==3/13` cones and your `PointInCone` test handles them, but visually the telegraph is sometimes drawn wider than the hitbox. Trust the hit math, not the omen.
- **`registerOnAOECreateFunc` snapshots are stale for moving casters** — re-resolve `targetAttach` every frame.

## Source pointers

- **Wiki — Argus user docs**: https://wiki.mmominion.com/doku.php?id=argus
- **Wiki — Argus full API reference**: https://wiki.mmominion.com/doku.php?id=argusdocs
- **Wiki — Healer reactions pack** (real-world Argus consumer): https://wiki.mmominion.com/doku.php?id=healer_reactions_pack
- **Community ACRs** for integration examples (positional cones, knockback indicators): RikuMNK, RikuRDM, TensorReaper3 wiki pages
- **Source — `ffxiv_common_cne.lua`**: built-in `gAvoidAOE` / `gAvoidHP` / `lastAvoid` block — the system you cooperate with or replace
