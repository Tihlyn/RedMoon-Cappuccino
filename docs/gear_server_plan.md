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

### Phase 3 — Damage model — DONE 2026-10-16

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

- **Fitted profiles** (`server/data/profiles/<JOB>.json`, `rmc-extract fit-profiles`): the fast model for the meld solver.
  - **How it works:** a rotation only reacts to time, so the hits it lands depend on speed but not on the other stats. For each speed tier, the fitter runs the sim at five fight lengths (450–570s, to smooth out where the last burst falls) and records every hit's context instead of its damage. The context is potency, attack type, forced crit/DH, potion, and crit/DH rate buffs, with the buff multipliers summed.
  - **Tiers:** a tier is a run of speed values on which every recast and cast the rotation uses, under each combination of its haste buffs, scales to the same value. That's 39–179 tiers per job, 5.6 MB in total.
  - **Evaluation:** `FittedProfileModel` applies the exact per-hit formula with the set's own stats (potted stats under the potion).
  - **Accuracy:** it matches the full sim exactly on every BiS set and re-meld for 20 jobs. MNK differs by up to 0.18%, because its Chakra comes from crits and is fitted at the reference set's crit rate.
  - **Speed:** 13–80 µs per gear set including the stat computation.
  - **Gate:** fails if a profile drifts from its sim or was fitted on a different game build than the potencies.
- The hand-written profile approach (`JobProfile`, `PotencyProfileModel`, the DRG prototype) is retired.
- Per the user, no multi-length xivgear references for the other jobs. Some of xivgear's hand-written rotations are less optimal than they look, so matching them more closely would pull our numbers down.

Design as built:
- 3.1 Interface: `FittedProfileModel.Evaluate(stats, pottedStats) → { dps, gcd, outsideFittedRange }`. The full `Simulation` sits behind it for breakdowns and checks.
- 3.2 Hit profiles, one data file per job, versioned by patch and game build, generated from the rotation sims (see above) rather than written by hand.
- 3.3 Roles: tanks get tenacity in the damage formula. Healers use their damage rotation, without a potion as in xivgear's healer sims; piety stays an optional minimum for the solver.
- 3.4 Patch-day update: `refresh-potencies`, then `fit-profiles`, then the checks. Rotation changes from the patch notes are code edits in `Sim/Rotations`.
- 3.5 Calibration: the sims against xivgear (absolute ≤ 2%, relative ≤ 0.3%, speed re-melds ≤ 2%), and the profiles against the sims (≤ 0.1%; MNK ≤ 0.3%).

### Phase 4 — Materia solver — DONE 2026-10-16

`server/src/Rmc.Engine/Melds/MeldSolver.cs`: the best materia for a gear set under the fitted profile.

- **Per piece:** every way to fill the free slots, deduplicated by resulting stats and Pareto-pruned.
  - Normal slots take grade XII.
  - Five-slot crafted overmelds take XII in the first overmeld slot and XI after, and always succeed.
  - Gains are capped per item.
- **Across pieces:** combined one at a time, fewest options first. For every exact speed total, the exact Pareto front of the other damage stats is kept (crit, DH, det, plus tenacity for tanks and piety when a floor is set).
  - Speed is a key, not a dimension, because more speed isn't always more damage.
  - The three-stat front uses an O(n log n) sort plus a Fenwick tree.
- **Last piece:** goes straight into tier vectors. Damage only sees each substat through its tier, so combinations with the same final tiers (food included) are the same set to the model. A tier vector beaten on every stat can't win. The survivors are scored with the fitted profile, in parallel.
- **Local search:** single- and paired-meld changes polish the winner.
- **Options:**
  - target GCD;
  - keep existing melds (fill only empty slots);
  - locked slots;
  - minimum piety;
  - solve food (current-tier foods for the job's stats);
  - Fast mode, for bulk ranking in Phase 5. It prunes within one grade XI materia between pieces and costs at most 0.018% against exact on the BiS sets.
- **Output:** melds per slot, solved and current DPS, GCD, and final stats with their tier breakpoints.
- **Speed:** exact solves average 252 ms over the 135 BiS sets. The worst case is 8 s on SAM's pentamelded crafted set (16M last-piece combinations); healer sets with several overmelded pieces take 2–4 s. Fast mode averages 125 ms.
- **Gates** (all pass):
  - Against the sheets: every BiS set re-melded from empty slots, at its own GCD and unconstrained, matches or beats the sheet's melds under our model (median +0.11%).
  - Larger unconstrained gains come from sheets that skip overmelds to save materia (SAM "Economy Crafted", +3.4%), or that meld SkS for a 2.45 GCD our GNB sim doesn't reward (+2.8%).
  - Grade rules hold, and the fast stat path agrees with a full recompute.
  - Random melds (fixed seeds): starting from random melds, the solver lands on exactly the set it finds from empty slots, beats the random set (random melds lose 2.1% on average, 3.5% at worst) and the sheet. Keep-existing mode leaves kept melds alone, and locked slots come back unchanged.
  - Options: a piety floor is met and costs DPS (+150 piety ≈ 0.8% on every healer), and food solving never does worse than the sheet's food (AST: +0.12% with the crit food).

### Phase 5 — Upgrade path scored by DPS — DONE 2026-10-16

`server/src/Rmc.Engine/Upgrades/UpgradePlanner.cs` replaces the plugin's hand-tuned `BuildAction` weights with DPS.

- **Costs come from the game:**
  - The snapshot now lists every currency and material that gear costs name, with the exchanges that give them (Thundersteeped Twine for 4 AAC Illustrated III, or 3,000 Sacks of Nuts, or three coins).
  - Weekly income is a short overlay, `server/data/overlays/weekly.json`: the tomestone cap and one book per savage floor per week. It also names the hunt currency.
- **Chains per slot:** buy the base piece, then augment it (materials through their book exchange); or buy the target outright. Optionally, a crafted stopgap first.
- **Drops:** pieces that only drop (normal and alliance raid, ultimate, relic) are listed separately, with their DPS gain if obtained.
- **Timeline:** week by week, the wallet and weekly income go to the affordable step with the best DPS gain per week of currency.
  - A currency is saved when a better-valued step that needs it isn't affordable yet. For example, the body's 6 books III before Twine.
  - A base piece that loses DPS on its own waits until its augment can follow, and is then augmented the same week.
  - Each new piece is melded on its own (fast). A full re-meld is suggested when it gains at least 0.05%, and the plan ends with a final re-meld.
- **Toggles (user request):**
  - `IgnoreWeeklyLimits`, for late in a tier: everything lands in week 0, biggest gain first.
  - `MaterialsFromHunts`: augment materials are bought with the hunt currency, so books go to gear. With it, BiS lands around weeks 8–12 instead of 17–21; the weapon's 8 books become the bound.
- **Output:**
  - current DPS, current DPS re-melded, and target DPS;
  - per step: week, slot, kind (buy / augment / craft), cost, DPS before and after, melds, and which slots to re-meld;
  - drop-only pieces, pieces with no DPS gain (stats the job doesn't use), and steps the horizon didn't reach.
- **Solver work it needed:**
  - Fast mode is now coarse within each piece too (36-point slack).
  - It scores only the top 20,000 candidates per food, ranked by a linear proxy.
  - Exact fronts are O(n log n) for three stats and O(n log² n) for four (tanks, piety floors).
  - Fast mode stays within 0.021% of exact on the BiS sets, at 65 ms average.
- **Speed:** a full 26-week plan takes 3–5 s for most jobs. It takes about 17 s for tanks starting from pentamelded crafted gear, which is the extreme case.
- **Gate:** every job planned from a fresh crafted set to its BiS. Everything passes:
  - spending never runs ahead of income;
  - augments follow their base piece, and losing steps are augmented the same week;
  - every target piece lands or is listed as drop-only or no-gain, nothing is unreached;
  - the plan ends within 0.1% of the target set;
  - no-limits ends at the same DPS, all in week 0;
  - hunt-mode augments spend no books;
  - planning from the target itself gives nothing.
- **Not yet in the gate:** crafted stopgaps (`IncludeCrafted`) and a player-supplied wallet.

### Threading and concurrency — DONE 2026-10-16 (before Phase 6)

The production host is an AMD EPYC 7371 (16 cores, 32 threads) with 128 GB RAM.

- **Solver:**
  - Every stage is parallel: layer expansion in fixed chunks, the per-speed fronts, the last-piece tier pass, scoring, and the local search.
  - Chunks merge in a fixed order, so results don't depend on thread timing.
  - States keep their four stat gains inline, with no array each, which cuts millions of allocations per solve.
  - Exact solves: 242 → 133 ms average over the BiS sets; worst case (SAM pentamelded crafted) 6.6 → 2.7 s. Fast mode: 65 → 21 ms.
- **Planner:**
  - Candidate steps are valued in parallel, one thread each.
  - The per-step re-meld check is a `Polish`, a local search from the current melds, instead of a full solve.
  - A piece is only judged "no DPS gain" after polishing, because some only pay once the rest is re-melded around them.
  - The plan ends with one exact solve.
  - A 26-week plan takes 0.8 s on average, 1.4–2.2 s for the heaviest (tanks from pentamelded crafted gear), down from 3–17 s.
- **Requests running at once:** `ComputePool` (in `Rmc.Engine`) caps how many heavy jobs run together, gives each an equal share of the threads (passed as `MaxDegreeOfParallelism`), and queues the rest. Plans per minute on the 16-thread dev machine with Server GC (`rmc-extract bench-concurrency`):

  | Jobs at once × threads each | Plans per minute | Average latency |
  |---|---|---|
  | 1 × 16 | 91 | 0.7 s |
  | 2 × 8 | 119 | 0.8 s |
  | 4 × 4 | 134 | 1.2 s |
  | 8 × 2 | 136 | 1.7 s |
  | 16 × 1 | 95 | 2.3 s |

- **Settings for the host:**
  - Server GC (the ASP.NET Core default).
  - `ComputePool(32, 4)`: 8 threads per job, a 4-job concurrency limit.
  - Light requests (stats, DPS) skip the pool; they're microseconds.
  - Cache results by the request's canonical JSON. The same gear and options give the same answer.

### Phase 6 — Plugin integration — CODE DONE 2026-10-16 (in-game check pending)

- **Server host** (`server/src/Rmc.Server`, ASP.NET Core minimal API):
  - Endpoints: `GET /v1/meta`, `POST /v1/evaluate`, `/v1/meld-solve`, `/v1/upgrade-plan`, plus `/healthz`.
  - Gear goes in as the engine's own input: item ids, HQ, melds, food, clan, level, party bonus. No names are sent.
  - Settings as agreed: Server GC; `ComputePool(32, 4)` in Production; evaluate runs inline outside the pool.
  - Results are cached by canonical request JSON plus the data version, and identical requests in flight share one computation.
  - Over 64 queued heavy jobs the server answers 503. Requests are capped at 64 KB.
  - Every answer carries `patch` and `dataVersion`.
  - From a non-BiS crafted start, local plans match the offline tool (DRG week 18, or week 8 with hunt materials). 12 concurrent plans took 2.5 s.
- **Fight length:** profiles are also fitted for 300 s and 720 s (`fit-profiles --seconds N`, into `data/profiles/<N>s/`), and the server picks the closest.
  - The engine checks cover them; every job is exact except MNK (≤0.19%).
- **6.1 Capture** (`Services/GearCapture.cs`):
  - Equipped items: `GameInventoryItem.Materia` (Materia row) and `MateriaGrade` (0-based; sent +1); stat from `Materia.BaseParam`.
  - HQ from `IsHq`; clan from `IPlayerState.Tribe`; level.
  - Food from the Well Fed status: its param is the ItemFood row, +10000 when HQ, mapped to the meal item through `ItemAction.Data[1]`.
  - Panel substats from `IPlayerState.GetAttribute`, skipped while level synced.
  - Wallet: `InventoryManager.GetTomestoneCount` / `GetInventoryItemCount`, for the currencies `/v1/meta` lists.
  - Parity is on substats only: the panel's main stat depends on the party's roles at that moment.
  - With no food, the server computes DPS with the BiS set's food, but checks parity on the gear as worn.
- **6.2 `PlannerServerClient`** (`Services/PlannerServerClient.cs`):
  - HTTP(S) POST with a 45 s timeout, and a 32-entry cache by request hash.
  - The address is `Configuration.PlannerServerAddress`. CI fills it from the optional `RMC_PLANNER_SERVER_ADDRESS` secret; a placeholder means not configured.
  - Errors are returned, never thrown, and the tab falls back to the local planner.
  - Checked end to end by compiling the client file into a console harness against a local server: meta, evaluate at three lengths, meld solve with a GCD target, a plan with a wallet, a bad job, a server that's down, the placeholder.
- **6.3 UI** (`Windows/MainWindow.Planner.cs`, a partial of `MainWindow`):
  - The header shows DPS now vs BiS, with the GCD, rates, fight length and data version in a tooltip, a "% of BiS damage" bar, and a warning dot when the panel differs.
  - The upgrade order shows week, item, DPS gain (absolute and %), and cost. Tooltips give melds and re-meld advice.
  - Collapsible lists cover drop-only upgrades and no-gain pieces.
  - The meld panel has a "Solve melds" button, now vs suggested melds per slot, and breakpoint bars (points into the tier, points to the next, and GCD for speed stats).
  - Server results show only when the game job matches the selected job; otherwise the local plan shows, with a hint.
- **6.4 Options** (a "Planner options" header in the tab, saved in the config): server on/off, fight length (standard, short, long), party bonus 0–5, target GCD, ignore weekly limits, upgrade materials from hunts.
- **Not yet verified in game:**
  - materia and grade reading;
  - the Well Fed param encoding;
  - tomestone counts;
  - panel parity (in-game observations can go into `data/reference/ingame/`).

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
