Core Engine TODO
Formula Integrity
 Verify all formulas against current live patch behavior
 Add explicit patch/version tagging to formulas
 Separate:
 game-accurate formulas
 solver heuristics
 expected-value approximations
 Add automated regression tests for all formula outputs
 Add tolerance validation for floating-point floor behavior
 Build formula test vectors from known in-game stat snapshots
Data Architecture
JSON Structure
 Split data into modular files:
 formulas.json
 levelScaling.json
 jobs.json
 gear.json
 progression.json
 raidSources.json
 heuristicProfiles.json
 Add semantic versioning to data
 Add expansion/patch compatibility metadata
 Add schema validation
 Add integrity/hash checking
Gear Data TODO
Gear Metadata
 Add acquisition source
 Add tome costs
 Add weekly lockout metadata
 Add savage book costs
 Add upgrade material requirements
 Add upgrade chain relationships
 Add crafted gear metadata
 Add overmeld capabilities
 Add relic progression metadata
 Add unique-equip constraints
 Add role/job restrictions
 Add item replacement priority metadata

Example future structure:

{
  "itemId": 12345,
  "source": "tome",
  "cost": {
    "tomes": 495
  },
  "weeklyLimited": true,
  "upgradePath": {
    "to": 12346,
    "requires": ["twine"]
  }
}
Progression System TODO
State Transition Modeling
 Define upgrade actions
 Define acquisition actions
 Define resource consumption rules
 Define reset/weekly refresh behavior
 Define deterministic vs RNG acquisition
 Define encounter unlock requirements
 Define raid progression state
 Define branching progression paths
Solver Architecture
A* / Dynamic Programming
 Define immutable gear state structure
 Implement canonicalized state hashing
 Add memoized state evaluation cache
 Add duplicate state elimination
 Add dominated-state pruning
 Add transition graph generation
 Add heuristic scoring engine
 Add path reconstruction
 Add multi-goal support
 Add search depth limits
 Add beam/priority pruning
 Add frontier caching
Heuristic Engine
Recommendation Intelligence
 Define utility scoring formula
 Add DPS-per-week weighting
 Add tome-efficiency weighting
 Add “future regret” penalty
 Add dead-end purchase penalties
 Add crit/speed breakpoint awareness
 Add delayed-value scoring
 Add weapon priority modeling
 Add expected future upgrade synergy

Example concept:

utility =
    dps_gain
    - acquisition_cost
    - future_regret
    + breakpoint_bonus
Breakpoint/Tier System
Stat Tier Modeling
 Precompute crit tiers
 Precompute GCD tiers
 Precompute DH breakpoints
 Add speed-tier evaluation
 Add breakpoint delta scoring
 Add breakpoint explanation system

This is crucial because:

 +1 stat can matter enormously
 many upgrades are nonlinear
Combat Evaluation
Damage Modeling
 Add rotational profile abstraction
 Add job-specific weight profiles
 Add expected burst alignment modeling
 Add kill-time sensitivity
 Add party buff assumptions
 Add encounter-type modifiers
 Add sim integration possibility
 Add variance modeling
Explanation Engine
Human-Friendly Recommendations
 Add recommendation rationale generation
 Add “why this upgrade” explanations
 Add “why not this upgrade” explanations
 Add future consequence explanations
 Add tome planning explanations
 Add alternative path explanations
 Add contingency recommendations

Example output target:

Buy Tome Chest next.

Reason:
- highest 2-week DPS gain
- preserves weapon timing
- unlocks crit tier
- avoids overcapping tomes
Multi-Path Optimization
Advanced Planning
 Add multiple valid route generation
 Add Pareto frontier evaluation
 Add:
 fastest route
 cheapest route
 safest route
 low-RNG route
 alt-friendly route
 Add contingency branching
State Modeling
Snapshot System
 Add immutable character snapshots
 Add stat provenance tracking
 Track:
 gear stats
 materia
 food
 buffs
 racial modifiers
 Add snapshot serialization
 Add reproducible solver states

Example:

{
  "crit": {
    "base": 420,
    "gear": 2400,
    "materia": 320,
    "food": 105
  }
}
Plugin Runtime
Dalamud Integration
 Read equipped gear
 Read materia safely
 Read current stats
 Detect gear changes
 Detect food changes
 Detect job changes
 Detect sync state
 Build lightweight live-state reader
 Keep runtime logic minimal
Performance TODO
Optimization
 Cache derived stats
 Avoid recomputing formulas
 Add lazy evaluation
 Add state pooling
 Add incremental recomputation
 Add profiling instrumentation
 Benchmark large search trees
 Add async/background solving
Data Science / Research TODO
Validation
 Compare against:
 FFLogs
 xivgear
 in-game values
 Build golden reference datasets
 Validate expected-value accuracy
 Validate breakpoint predictions
 Build simulation replay testing
Future Features
Long-Term Expansion Ideas
 Team/party-aware optimization
 Loot coordination planner
 Static-wide progression planning
 Alt-job optimization
 Cross-job shared gear optimization
 Relic progression solver
 Dungeon/casual gearing mode
 Budget/gil-aware optimization
 Crafting integration
 Mobile/web companion app
Highest Priority Recommendations

If I were prioritizing immediately:

Phase 1
 immutable state model
 state hashing
 progression metadata
 acquisition semantics
 heuristic scoring
Phase 2
 A* implementation
 breakpoint system
 memoization cache
 recommendation explanations
Phase 3
 branching plans
 multi-objective optimization
 rotational modeling
 contingency paths
Most Important Architectural Principle

Keep these fully separated:

Game State Reader
        ↓
Canonical Character Snapshot
        ↓
Formula Engine
        ↓
Evaluation Engine
        ↓
Solver
        ↓
Recommendation/Explanation Layer

Do NOT let:

UI logic
Dalamud logic
heuristic logic
formula logic

bleed into each other.