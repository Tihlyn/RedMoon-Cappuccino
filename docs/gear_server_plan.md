# Gear Planner Compute Server — Plan

Status: planning (2026-10-16). Nothing built yet.

## Goal

An external server does the heavy computation for the gear planner. The plugin stays thin: it sends what the character has equipped and draws the results. The player should be able to see:

- the upgrade path to BiS, with the DPS change of each step;
- DPS of the current gear vs BiS;
- how materia changes DPS and where stats sit against tier breakpoints;
- the best materia for the current gear, from a materia solver.

All jobs are covered: DPS, healers and tanks.

## Decisions

| Topic | Decision |
|---|---|
| Dependencies | The server is fully self-contained. It can call xivgear's API to cross-check results or import BiS sets while developing, but never needs it while running: an API that can go down without notice must not break the planner. |
| Code reuse | We reimplement from the public formulas. xivgear's repo has no LICENSE file, so its code is all rights reserved by default and we don't copy it. |
| Crafted gear | Included, with full five-slot overmelds. Overmelds are treated as guaranteed: no success rates and no materia cost. Players already know the risk and cost. |
| Roles | DPS, healers and tanks are all in scope. Tanks get tenacity in the damage model. Healers get piety as an optional minimum. |
| Potency profiles | One data file per job, versioned by patch. Job changes are published in the patch notes on patch day, and a simple script updates the values. |
| Request/response schema | Agreed (see "API contract"). |
| Distribution | The plugin is experimental and goes to a known set of users, not the official Dalamud repo, and it already makes external network calls. There's no disclosure or approval process to go through. We reuse the existing server configuration. |
| Stack | C# / ASP.NET Core. The formula code lives in a shared library that the server uses and the plugin can also use for an offline mode. |

## What the plugin does today

- `resources/math/math.json` already has the stat and damage formulas, extracted from xivgear's math package. They match the Allagan Studies (akhmorning) functions.
- `GearPlannerService.BuildEquippedMap` doesn't read materia.
- The BiS JSONs have no melds and no food.
- The gear database lists meld slots, but the loader ignores them.
- The path solver ranks steps with a hand-tuned score (stat sum, item level, breakpoint bonus, cost terms) instead of by DPS.

## Formulas

Level 100 constants: base main stat 440, base substat 420, level divisor 2780. These change every expansion, so they're kept in data, not code.

| Function | Formula |
|---|---|
| Crit rate | `fl(200·(crit−420)/div + 50)/1000` |
| Crit damage | `(1400 + fl(200·(crit−420)/div))/1000` |
| Direct hit rate | `fl(550·(dh−420)/div)/1000`; a direct hit does ×1.25 damage |
| Determination | `(1000 + fl(140·(det−440)/div))/1000` |
| Tenacity, damage | `(1000 + fl(112·(ten−420)/div))/1000` (tanks only) |
| Tenacity, damage taken | `(1000 − fl(200·(ten−420)/div))/1000` |
| Main stat | `(trunc(apMod·(main−440)/440) + 100)/100`, where apMod is 190 for tanks and 237 for everyone else |
| Weapon damage | `fl(440·jobMod/1000 + WD)/100` |
| GCD, damage over time, auto-attacks, piety | as in `math.json` |
| Rounding order | Physical damage and caster/healer damage round at different steps (`damageCalculation` in `math.json`). |
| Expected damage | `base × (1 + pDH·0.25) × (1 + pCrit·(fCrit−1))` |

### Meld caps

- Cap per stat per item: `round( fl( fl(ilvlMod × meldParam/100) × slotPct/100 ) / 10 )`.
  - `ilvlMod` comes from the ItemLevel sheet for that stat.
  - `slotPct` and `meldParam[meldParamIndex]` come from the BaseParam sheet.
- Off-hand cap: the two-handed cap minus the one-handed cap, for stats where one-handed % plus off-hand % equals two-handed %. Rounding errors stack otherwise.
- A meld adds `min(materia value, cap − current stat)`. Skip a meld that wastes more than 2 points past the cap.
- Materia values: Grade XII = +54, Grade XI = +18.
- Overmeld rules: Grade XII only goes in the first overmeld slot. Later overmeld slots take Grade XI.

### Food and party bonus

- Food: `min(max, floor(stat × pct/100))`, applied before the party bonus.
- Party bonus: +1% per unique role, up to 5%.

## Architecture

```
Plugin (thin)                                  Compute server (stateless, self-contained)
─────────────                                  ──────────────────────────────────────────
reads: job, level, clan, equipped              /v1/evaluate      stats, DPS, breakpoints
  item ids + HQ + materia, food,  ──JSON──▶    /v1/meld-solve    best materia (+ food)
  in-game attribute panel                      /v1/upgrade-path  BiS path ranked by DPS
renders results; keeps the local    ◀─JSON──   own game data per patch (items, caps, materia, food)
path solver as offline fallback                own potency profiles per job + BiS definitions
```

- Transport: HTTPS POST with JSON. Each request is one computation, cached by `hash(request + dataVersion)`.
- Every response carries `patch` and `dataVersion`.

## API contract

**Request**

- job, level, clan;
- slots: item id, HQ flag and materia (stat + grade per meld slot) for each slot;
- food id;
- party bonus (0–5);
- options: fight length, target GCD, locked melds, solve food, minimum piety;
- the attribute panel as read in game: final stats, used for the parity check.

**Response**

- computed stats, and how far they are from the in-game panel;
- derived values: crit rate and damage, direct hit rate, determination, tenacity, GCD;
- DPS, with a breakdown;
- breakpoints;
- solver results, or the upgrade path.

No character names are sent. Item ids only.

## Action plan

### Phase 1 — Game data pipeline (server) — DONE 2026-10-16

How it was built: see `server/README.md`.

- Snapshot `data/gamedata/7.5.json`, taken from game version 2026.10.06:
  - 2,502 level-100 items, 75 foods, 7 materia types, 23 jobs.
- BiS sheets with melds and food for all 21 non-limited jobs.
  - 16 come from xivgear's static repo.
  - WHM, SCH, AST, SGE and BLM come from The Balance's xivgear links. AST and SGE publish one link per GCD, merged into one sheet.
- All checks pass offline:
  - The primary substat equals the computed meld cap on 2,480 of 2,502 items, including off-hands and crafted gear.
  - Base params, item levels, materia and all 160 BiS items match the xivgear reference exactly.
  - Every BiS set is legal.
- Deviation from the plan: item sources are derived from the game files instead of a hand-kept list. Recipes mark crafted gear, the exchange shops (SpecialShop) give the currency that buys everything else, and augment exchanges link each augmented item to its base and materials. A short name-rule file, `server/data/overlays/currencies.json`, names the currencies. All current-tier and BiS items resolve, and new currencies fail the checks.
- Found while building it: the plugin's `gear_database.json` mislabels several sets, which gives them the wrong source and cost text in the current planner:
  - Crafted Courtly Lover's i770 is labelled AllianceRaid.
  - Its augmented i780 versions are labelled AllianceRaid. They're crafted gear augmented with alliance raid materials.
  - Ornate i770 is labelled AllianceRaid. It comes from Wondrous Tails.
  - The Phantom relic weapons are labelled Ultimate.

- 1.1 Per-patch export of all gear, crafted included:
  - base and HQ stats;
  - `meldParamIndex`;
  - number of meld slots and whether overmelding is allowed (five-slot crafted gear);
  - weapon damage and delay;
  - whether the item is unique.
- 1.2 Also export the ItemLevel sheet, the BaseParam sheet (slot % and `meldParam`), the Materia sheet (stat and value per grade), Food (% and max) and ClassJob modifiers.
  - Source: our own Lumina or xivapi pipeline, which already produces `gear_database.json`.
- 1.3 Own BiS definitions per job and tier, including melds and food.
  - These are maintained by us.
  - The Balance's xivgear links can be imported with a script while developing. The result is committed as a snapshot, so nothing depends on xivgear at runtime.
  - This also fixes the current 7.4 BiS vs 7.5 gear database mismatch.
- 1.4 Data tests run on every refresh:
  - all required fields are present;
  - known caps are checked against a committed snapshot of xivgear values, taken offline.

### Phase 2 — Stat engine — DONE 2026-10-16

How it was built: `server/src/Rmc.Engine`, gated by `server/tests/Rmc.Engine.Checks`.

- Integer-exact versions of the Allagan Studies / xivgear formulas. Assembly order: gear with capped melds, then base stats and clan, then party bonus, then food computed from the pre-party values.
- Per-meld report: the value applied and the points lost to the item's cap.
- Breakpoints for every stat that matters to the role: where the current tier starts, the points wasted inside it, the points to the next tier, and the current and next GCD for speed stats.
- Parity: all 4,050 compared values across the 135 BiS sets (21 jobs) match xivgear `/fulldata` exactly.
  - Covers main stats after party bonus, food after caps, crit, DH, det, tenacity, speed and DoT multipliers, main stat, weapon damage, auto-attack, HP, and MP per tick.
  - Clans are imported per set from the xivgear sheets.
- Independent check: 126 Balance sets are labelled with a GCD, and the engine's GCD matches every label except one loosely labelled SAM set. That set is allowlisted with the reason.
- In-game panel parity (2.5b) is wired up (`server/data/reference/ingame/*.json`) and waits on Phase 6, when the plugin can send real panel readings.

- 2.1 Compute final stats: gear + race/clan + food + party bonus.
- 2.2 Derive crit, direct hit, determination, tenacity, GCD (skill or spell speed, with haste), the damage-over-time speed multiplier, and MP per tick from piety.
- 2.3 Apply melds with caps, the off-hand rule, and the overmeld grade rules.
- 2.4 Breakpoints for each stat:
  - points wasted past the last tier boundary;
  - points needed for the next tier;
  - the GCD tier reached, and the speed needed for the next one.
- 2.5 Parity gate:
  - (a) for every BiS set, final stats match the committed xivgear snapshot exactly;
  - (b) stats match the in-game attribute panel. We anchor on observed values.

### Phase 3 — Damage model — IN PROGRESS (rotation sims for all 21 jobs done 2026-10-16)

Direction changed to option (a), at the user's request: our own rotation sims for every job, with the profile counts fitted from them. xivgear becomes a cross-check rather than a dependency.

Done:
- **Sim engine** (`server/src/Rmc.Engine/Sim`).
  - Deterministic: each hit counts its expected value; random procs count as their expected value.
  - Timeline in milliseconds; GCD and cast times from the exact speed formula plus haste; fixed recasts (dance steps, mudras, Enshroud).
  - oGCD weaving with up to 100ms of clip; animation locks; auto-attacks wait out casts.
  - Cooldowns with charges and cooldown reduction; buffs (damage, crit/DH rate, haste, auto-attack-only haste, forced crit/DH, stacks, custom durations); DoTs snapshot on application and can be re-snapshotted (Iron Jaws).
  - Gauge- and buff-dependent potencies through a rotation hook; per-action damage multipliers for pets.
  - Potion swaps in the potted stats; xivgear's healer sims don't drink one, so healer rotations opt out.
- **Rotations for all 21 combat jobs** (`Sim/Rotations`), one class per job at level 100.
- **Potencies as data.** `rmc-extract refresh-potencies` regenerates `data/potencies/<JOB>.json` from the game's tooltips for every rotation; all labels resolve.
- **Calibration against xivgear** (17 jobs with a full xivgear sim): all pass.
  - Absolute within 2% (worst: WHM -1.76%, SAM -1.74%, GNB -1.43%, NIN +1.38%).
  - Non-speed re-melds within 0.3% relative.
  - Speed re-melds within 2%. Where xivgear was also run at seven fight lengths (420–540s), the length-averaged ratio is used instead. This cancels where the last burst falls against the fight's end, which moves single-length results by up to ~3%. PLD, RPR and SAM still value SkS 1.1–1.8% higher than xivgear's scripted sims do:
    - PLD: xivgear drifts Fight or Flight by about 2s a minute at faster GCDs.
    - SAM: xivgear swaps to a different hand-written rotation per GCD tier.
    - RPR: the per-length ability counts match, so the gap is in where the extra GCDs land.
  - The other 14 jobs agree within 0.9%.
  - Each job's reference records the length xivgear actually simulated (MNK, RPR, VPR and the healers at 720s; NIN at 392s; scripted sims stop early or late).
- **Measured from xivgear's sims, recorded in the rotations:**
  - MCH Automaton Queen hits at 0.91× the machinist's damage per potency.
  - DRK's Living Shadow at 1.058×.
  - Leaping Opo always crits.
  - Ouroboros recasts in 2.0s.
  - Enhanced Infuriate takes 5s per Fell Cleave or Inner Chaos.
- **Unvalidated:** BRD, PCT, RDM and SMN have no full rotation sim on xivgear, only a potency-ratio estimate, so their rotations follow the standard openers and loops without a reference.
- **Tooling:**
  - `rmc-extract sim --job X [--all] [--timeline] [--lengths …]` compares against the references.
  - `potencies --find` / `status --find` look up actions and statuses by name, with cast and recast times.

Next:
- Fit the fast profile model from the sims (averaged over several fight lengths), for the meld solver.
- Retire the hand-written profile approach (`data/profiles/DRG.json` was the prototype; the profile-based calibration check is already removed).
- Length-averaged references for the remaining jobs (only PLD, RPR and SAM have them).

- 3.1 Interface: `IDamageModel.Evaluate(stats, job, options) → { dps, breakdown }`.
- 3.2 Potency profiles, one data file per job, versioned by patch. For each GCD tier, the file gives potency per second split by hit type:
  - GCD hits;
  - off-GCD hits;
  - damage over time;
  - auto-attacks;
  - forced crit/DH;
  - share of potency inside party buff windows.

  Party buffs are modelled as crit and direct-hit rate increases inside their windows. DPS = Σ potency_i × E_i(stats) / duration, with the rounding order from `math.json`.
- 3.3 Roles:
  - Tanks: tenacity is included in the damage multiplier.
  - Healers: the damage profile assumes a healer's damage rotation; piety is only an optional minimum.
- 3.4 Patch-day update script: copy the potency changes from the patch notes into the job files, bump the version, and rerun the golden tests.
- 3.5 Calibration: when a job file is created, its DPS ranking of sets must match a reference. Target: relative DPS between sets within 0.3%. Everything shown to the player is a difference, so the absolute value only needs to be close.
- 3.6 Later and optional: full rotation sims for jobs whose damage depends on GCD speed (SAM, VPR, MNK, BLM, PCT). These would plug in behind the same interface.

### Phase 4 — Materia solver

- 4.1 For each piece, list the possible ways to fill its free meld slots, keeping one per unique stat result.
  - Covers normal slots and five-slot crafted overmelds, treated as guaranteed.
  - Skips options that waste too much past the cap.
  - Respects locked melds.
- 4.2 Search slot by slot across the gear, tracking total stats.
  - At each step, keep only the Pareto front for each GCD tier, and one set per unique stat total.
  - Score the final front exactly with the damage model.
  - This replaces xivgear's multi-GB brute force with a search that stays small.
- 4.3 Options: target GCD, solve food too, keep existing melds, minimum piety.
- 4.4 Output: melds per slot, the DPS change against the current melds, and a breakpoint summary.
- 4.5 Test: with empty meld slots on The Balance's BiS gear, the solver must match or beat their melds under our model.

### Phase 5 — Upgrade path scored by DPS

- 5.1 Score each step by its DPS gain, with each intermediate set re-melded by a fast greedy pass.
  - This replaces the hand-tuned weights in `BuildAction`.
  - Keep the cost terms: tomes, savage books and augments.
- 5.2 The objective becomes DPS per unit of limited currency. Weekly limits (tome cap, one savage book per fight per week) turn the path into a timeline.
- 5.3 Output:
  - current DPS vs BiS DPS (absolute and %);
  - DPS gain per step;
  - "re-meld after this step" notes;
  - an option to craft gear as a step.

### Phase 6 — Plugin integration

- 6.1 `BuildEquippedMap`: read materia from the equipped items (the materia and materia grade fields; check the exact Dalamud API names first), plus HQ, food and clan. Also read the player's actual stats for the parity check.
- 6.2 New `PlannerServerClient`:
  - HTTPS POST with a timeout;
  - cached by request hash;
  - the server address comes from the existing configuration;
  - falls back to the current local solver when the server is unavailable.
- 6.3 UI in `DrawGearPlannerTab`, kept simple for players, with internals in tooltips:
  - a header with current DPS vs BiS DPS;
  - DPS gain on each upgrade step;
  - a meld panel with suggested melds and breakpoint bars;
  - a "Solve melds" button.
- 6.4 Configuration: fight length, party bonus, target GCD.

### Phase 7 — Operations

- 7.1 Data rebuild on each patch: game data (Phase 1) and the potency profile script (3.4), with patch/version tags.
- 7.2 Rate limits and request size limits on the server.
- 7.3 Golden test set in CI: for every job, BiS stats parity and the DPS ranking of candidate sets.

**Order:** 1 → 2 (stat parity is the first gate) → 3.2–3.5 → 4 → 6 (the meld solver is useful on its own) → 5 → 3.6.

## Remaining risks

- The off-hand meld cap rounding still differs by one point in some cases, according to xivgear. The parity snapshot will catch these.
- Healer and tank profiles depend on assumptions about how the rotation is played (healing GCDs given up, tank mitigation timing). Note the assumption in each job file.

## Sources

- xivgear gear-planner: https://github.com/xiv-gear-planner/gear-planner (`API_DOC.md`, `docs/dev-docs/SOLVER.md`, `packages/xivmath/src/xivmath.ts`, `packages/core/src/datamanager_new.ts`, `packages/core/src/solving/gearset_generation.ts`)
- Allagan Studies functions: https://www.akhmorning.com/allagan-studies/how-to-be-a-math-wizard/shadowbringers/functions/
- Consolegameswiki, Advanced Materia Melding: https://ffxiv.consolegameswiki.com/wiki/Advanced_Materia_Melding
