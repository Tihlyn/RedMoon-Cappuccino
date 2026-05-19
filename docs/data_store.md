Data Pipeline & Storage TODO
High-Level Architecture
Game Data (EXD/Lumina)
        ↓
Offline Extractor
        ↓
Normalization Pipeline
        ↓
JSON Development Assets
        ↓
Validation
        ↓
MessagePack Runtime Blobs
        ↓
Runtime Memory Cache
        ↓
Solver
Phase 1 — Extraction Pipeline
Extractor Project
 Create separate extractor solution/project
 Keep extractor completely independent from plugin runtime
 Add CLI extraction tooling
 Add patch/version detection
 Add batch export support
 Add automated rebuild pipeline
Game Data Extraction
Raw Data Sources
 Extract gear data from game sheets
 Extract materia data
 Extract food data
 Extract stat scaling tables
 Extract level modifiers
 Extract job metadata
 Extract role metadata
 Extract upgrade item metadata
 Extract tome/savage costs
 Extract unique-equip rules
 Extract weapon delay values
 Extract auto-attack metadata
Phase 2 — Normalization
Normalize Raw Sheet Semantics
 Convert BaseParam structures into named stats
 Flatten nested game sheet structures
 Normalize all stat naming
 Normalize slot naming
 Normalize source/acquisition metadata
 Normalize role/job restrictions
 Normalize upgrade chains
 Normalize overmeld rules
 Normalize item categories
Canonical Naming
Ensure Stable Identifiers
 Define canonical stat names
 Define canonical slot names
 Define canonical job identifiers
 Define canonical acquisition source names
 Define canonical patch identifiers

Example:

{
  "slot": "Body",
  "job": "DRK",
  "source": "Savage"
}
Phase 3 — JSON Development Schema
Root Structure
 Create modular JSON files
 Avoid giant monolithic files
 Separate runtime from authoring data

Recommended layout:

data/
├── formulas/
├── scaling/
├── jobs/
├── gear/
├── progression/
├── heuristics/
├── food/
├── materia/
└── metadata/
Recommended JSON Schema
Gear Item Schema
{
  "id": 12345,
  "name": "Example Chest",
  "patch": "7.3",
  "slot": "Body",
  "itemLevel": 760,

  "jobs": ["DRK", "WAR", "PLD", "GNB"],

  "stats": {
    "vit": 580,
    "str": 540,
    "crit": 512,
    "det": 358
  },

  "materia": {
    "slots": 2,
    "overmeld": false
  },

  "weaponData": null,

  "source": {
    "type": "Tome",
    "weeklyLimited": true,

    "cost": {
      "tomes": 825
    }
  },

  "upgradePath": {
    "upgradesTo": 12346,
    "requires": ["Twine"]
  },

  "constraints": {
    "unique": false,
    "uniqueEquip": false
  }
}
Weapon Schema Extension
{
  "weaponData": {
    "physicalDamage": 146,
    "magicDamage": 146,
    "delay": 3.36,
    "autoAttack": 134.12
  }
}
Food Schema
{
  "id": 20001,
  "name": "Food Name",

  "bonuses": {
    "crit": {
      "percent": 0.10,
      "cap": 105
    },

    "det": {
      "percent": 0.10,
      "cap": 62
    }
  }
}
Materia Schema
{
  "id": 30001,
  "stat": "crit",
  "value": 36,
  "tier": 12
}
Progression Metadata Schema
{
  "itemId": 12345,

  "acquisition": {
    "source": "Savage",

    "cost": {
      "books": 4
    },

    "weeklyLockout": true,

    "encounter": "M8S"
  }
}
Solver Metadata Schema
Heuristic Metadata
{
  "job": "SAM",

  "weights": {
    "crit": 1.42,
    "det": 1.01,
    "dh": 0.97
  },

  "breakpoints": {
    "gcd": [2.50, 2.48, 2.47]
  }
}
Phase 4 — Validation
Schema Validation
 Add JSON schema validation
 Add missing-field detection
 Add invalid-stat detection
 Add duplicate-ID detection
 Add invalid-upgrade-chain detection
 Add patch consistency validation
Formula Validation
 Validate stat outputs against live game
 Validate floor ordering
 Validate GCD tiers
 Validate crit calculations
 Validate expected damage outputs
 Build automated regression suite
Phase 5 — Runtime Serialization
MessagePack Integration
 Add MessagePack serialization pipeline
 Create compact runtime DTOs
 Remove unnecessary authoring metadata
 Add binary version tagging
 Add runtime compatibility checks
 Add corruption detection
Runtime DTO Design
Solver-Oriented Packed Structures

Example:

public struct PackedGearItem
{
    public ushort Id;
    public byte Slot;

    public ushort Crit;
    public ushort Det;
    public ushort Dh;
    public ushort Sks;

    public ushort ItemLevel;

    public byte SourceType;
}
Phase 6 — Runtime Cache
In-Memory Indexes
 Build item ID lookup table
 Build slot-based indexes
 Build job-compatible indexes
 Build source-type indexes
 Build obtainable-item indexes
 Build progression-tier indexes
Dominance Pruning
Offline Optimization
 Precompute dominated items
 Remove mathematically inferior items
 Build Pareto-efficient item subsets
 Precompute breakpoint-relevant items
 Build slot-specific efficient frontiers

This will massively reduce A* branching.

Runtime Performance
Memory Efficiency
 Keep runtime data immutable
 Use packed structs where possible
 Avoid string comparisons in hot paths
 Use enum/int identifiers internally
 Precompute lookup tables
 Avoid allocations during solving
Patch Management
Versioning
 Add patch-specific data folders
 Add compatibility metadata
 Add migration tooling
 Add automatic extractor regeneration
 Add deprecated-item handling

Recommended structure:

runtime/
├── 7.2/
├── 7.21/
├── 7.25/
└── 7.3/

Highest Priority Implementation Order
Immediate Priority
 Extractor project
 Canonical schemas
 JSON normalization
 MessagePack serialization
 Runtime indexing
 Immutable runtime data
Critical Design Principles
DO:
 Keep solver fully offline
 Keep runtime fully memory-resident
 Keep data immutable
 Separate authoring vs runtime formats
 Precompute aggressively
 Normalize semantics early
AVOID:
 Live API calls during solving
 Runtime Lumina dependency in hot paths
 SQL/database lookups during A*
 String-heavy runtime operations
 Recomputing derived values repeatedly
 Mutating runtime gear definitions
Ideal Final Runtime
Plugin Startup
    ↓
Load MessagePack blob
    ↓
Build indexes
    ↓
Read current player state
    ↓
Run fully in-memory solver
    ↓
Output progression recommendations
