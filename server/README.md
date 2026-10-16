# Gear planner compute server

The external server behind the gear planner. See `docs/gear_server_plan.md` for the full plan.

Phases 1–5 (game data, stats, damage, meld solver, upgrade planner) are done. `src/Rmc.Server` is the HTTP service the plugin calls (phase 6); see "Running the server".

## Layout

```
server/
  src/Rmc.GameData/              shared model, meld cap formula, BiS validator (no Lumina, no network)
  src/Rmc.Server/                rmc-server: the HTTP API (evaluate, meld-solve, upgrade-plan, meta) over the engine
  src/Rmc.Engine/                stat engine (gear + melds + clan + party + food -> stats, multipliers, breakpoints),
                                 Sim/ rotation sim engine + Sim/Rotations/<Job>Rotation.cs
  tools/Rmc.GameData.Extractor/  rmc-extract: game files -> snapshot, BiS import, xivgear reference
  tests/Rmc.GameData.Checks/     phase 1 gate: snapshot completeness, caps, sources, BiS legality
  tests/Rmc.Engine.Checks/       phase 2 gate: exact parity with xivgear computed stats, GCD labels
  data/
    gamedata/<patch>.json        game data snapshot (generated, committed)
    bis/<JOB>/current.json       our BiS sheets with melds and food (imported, committed)
    overlays/levels.json         base main/sub stats per level (not in any sheet)
    overlays/currencies.json     shop currency name rules -> source type (see "Item sources")
    overlays/overrides.json      per-item source pins for the rare item nothing else covers
    overlays/weekly.json         weekly income per currency (tomestone cap, a book per savage floor) and the hunt currency
    reference/xivgear/           xivgear data API rows + /fulldata computed stats the checks compare against
    reference/ingame/            in-game attribute panel readings (gear + panel numbers), from phase 6 on
    reference/xivgear/variants/  stat-skewed calibration sets (calibration-variants)
    reference/xivgear/sims/      xivgear sim DPS for BiS + variant sets, collected in the browser
    potencies/<JOB>.json         rotation potencies read from the game tooltips (refresh-potencies)
    profiles/<JOB>.json          fitted hit profiles: the rotation sims reduced to data per speed tier (fit-profiles)
    profiles/<N>s/<JOB>.json     the same for other fight lengths (fit-profiles --seconds N): 300s and 720s today
```

At runtime, the server reads only `data/`. It never calls xivgear or xivapi.

## Patch-day refresh

Run these from `server/`. The extractor reads the local game install through the Dalamud dev build's Lumina. To use another Lumina, pass `-p:DalamudLibPath=...`.

```
dotnet build RmcServer.slnx -c Release
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll extract --patch 7.5
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll import-bis --gamedata data/gamedata/7.5.json --refresh
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll snapshot-xivgear --gamedata data/gamedata/7.5.json
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll refresh-potencies
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll fit-profiles
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll fit-profiles --seconds 300
dotnet tools/Rmc.GameData.Extractor/bin/Release/net10.0/rmc-extract.dll fit-profiles --seconds 720
dotnet run --project tests/Rmc.GameData.Checks -c Release
dotnet run --project tests/Rmc.Engine.Checks -c Release
```

`refresh-potencies` prints every potency the patch changed, and flags any ability whose tooltip label it can't find. Rotation logic changes (new procs, changed cooldowns) still need code edits; the patch notes say which.

`fit-profiles` reruns every job's rotation sim across the speed range and writes `data/profiles/<JOB>.json`. Rerun it after any potency or rotation change. The engine checks fail when a profile no longer matches its sim, or was fitted on another game build.

- **What a profile holds:** for each speed tier, every hit the sim landed (summed over 450–570s fights), grouped by potency, attack type, forced crit/DH, potion and crit/DH rate buffs.
- **Tiers:** a tier is a run of speed values on which every recast and cast the rotation uses, under each combination of its haste buffs, comes out the same.
- **Evaluating a set:** `FittedProfileModel` applies the exact damage formula to each group with the set's stats. It matches the full sim exactly, apart from MNK, whose crit-fed Chakra is fitted at the reference set's crit rate (0.18%). It takes tens of microseconds per set, including computing the stats.

To check a rotation after a change: `rmc-extract sim --job SAM` compares our sim against the xivgear references for every set. It also takes `--all` for a per-ability breakdown of every set, `--timeline` for the first set's action sequence, and `--lengths 420,460,500` to run each set at several fight lengths. `potencies --job X --find "A,B"` and `status --find "A"` look up actions and statuses by name, with cast and recast times.

`rmc-extract solve-melds --job DRG [--set "Savage BIS"] [--gcd 2.50] [--food] [--keep] [--piety 700]` re-melds a BiS set from empty slots with the meld solver. It prints the solved melds next to the sheet's, with both sets' DPS. Set `RMC_SOLVER_TRACE=1` to see the search's layer sizes and timings.

`rmc-extract plan-upgrades --job DRG [--set ...] [--no-limits] [--hunts] [--crafted]` plans the upgrade path from a fresh crafted set to a BiS set, week by week, using the weekly income in `overlays/weekly.json`. It prints each step's DPS gain and cost, which other slots to re-meld, and the pieces that only drop.
- `--no-limits` ignores weekly limits for late in a tier: everything lands in week 0, biggest gain first.
- `--hunts` buys augment materials with the hunt currency, so books go to gear.
- `--crafted` considers crafted stopgaps.

- **`extract`:** writes the snapshot and prints any shop currency that `overlays/currencies.json` doesn't classify. The checks fail until you add a rule for it.
- **`import-bis --refresh`:** re-imports every committed sheet from the source recorded in it. Without `--refresh`, it pulls each job's `current` sheet from xivgear's static-bis-sets repo.
  - Healers and BLM aren't in that repo. Re-import them from The Balance's xivgear links with `--jobs WHM --url <link>[,<link>...]`.
  - Imports are rejected if any item, materia or food is unknown, or a meld is illegal.
  - The links used are recorded in each sheet's `meta.source.reference`.
- **`snapshot-xivgear`:** refreshes the reference rows and xivgear's computed stats for every BiS set. Re-run it after re-importing BiS; the checks work from the committed copy.
- **Checks:** exit code is 0 only when everything passes.

## Running the server

```
dotnet publish src/Rmc.Server -c Release -o publish
ASPNETCORE_ENVIRONMENT=Production dotnet publish/rmc-server.dll --urls http://127.0.0.1:5088
```

- **Data:** it loads `data/` at startup (game data, every profile, BiS sheets, weekly overlay) from the first `data` folder with a `gamedata/` above the binary, or from `Rmc:DataRoot`. It runs from that folder's parent, because the rotation sims read `data/potencies`. Restart after a patch-day refresh; every answer carries `patch` and `dataVersion` (a hash of the data files).
- **Settings** (`appsettings.json`, `appsettings.Production.json`, or `Rmc__Name` environment variables):
  - `TotalThreads` (0 = all cores; 32 in Production) and `MaxConcurrentJobs` (4): heavy requests (meld solves, plans) share the machine through `ComputePool`.
  - `MaxQueuedJobs` (64): past this many waiting, heavy requests get 503 "busy".
  - `CacheEntries` (20000): results cached by canonical request JSON, 6 h sliding.
  - `MaxRequestBytes` (64 KB).
- **TLS:** put it behind a reverse proxy for HTTPS. The plugin takes the address from `PlannerServerAddress`, filled at build time from the `RMC_PLANNER_SERVER_ADDRESS` secret.
- **API (v1, JSON, camelCase):** gear is `{ job, level, clanId, partyBonus, food: { itemId, hq }, slots: { WEAPON: { itemId, hq, materia: [{ stat, grade }] }, ... } }`, item ids only, no names.
  - `GET /v1/meta`: patch, data version, jobs with their fight lengths and BiS set names, weekly income, the currencies a plan can spend.
  - `POST /v1/evaluate` `{ gear, panel?, options }`: stats, crit/DH/det/GCD, DPS, breakpoints, the job's BiS DPS, and computed minus in-game panel for substats. Inline, not pooled.
  - `POST /v1/meld-solve` `{ gear, options, targetGcd?, lockedSlots, keepExistingMelds, solveFood, minPiety? }`: best melds for the gear.
  - `POST /v1/upgrade-plan` `{ gear, options, wallet, ignoreWeeklyLimits, materialsFromHunts, includeCrafted, maxWeeks? }`: the week-by-week plan to the BiS set.
  - `options`: `fightSeconds` (closest fitted length), `bisSet` (sheet set name; first by default), `bisFoodWhenUnfed` (no food sent: use the BiS set's).
  - `GET /healthz`.

## Item sources

Sources are derived from the game files, not maintained per item. They're resolved in this order:

1. `overlays/overrides.json` pins, which is empty today.
2. **Crafted:** the item is a recipe result.
3. **Augment exchanges:** the exchange takes a gear piece, or the item is named "Augmented <base>". The item gets the base item's type, plus `augment: { baseItemId, materials }`.
4. **Plain exchanges:** the type of the currency that buys the item. Tomestones and scrips are recognised from the shop's currency index. Every other currency is matched by name against `overlays/currencies.json`; the first match wins.
5. **Drops no shop sells:** green gear is Dungeon, and blue left-side armor is AllianceRaid.

Currency names repeat from tier to tier within an expansion ("AAC Illustrated", "Holo…", "…Totem"), so a new tier usually needs no edits.

A new expansion means new currency names; the extractor prints them and the checks fail until you add rules. Ultimate weapons need no rule: they cost one totem each, while trial weapons cost ten.
