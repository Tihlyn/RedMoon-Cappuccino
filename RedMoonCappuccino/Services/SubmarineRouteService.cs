using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Yield model for submersible voyages.
///
/// Every sector pulls loot from three tiers. Surveillance decides how the pulls
/// spread across those tiers, Retrieval decides how much a pull is worth, and
/// meeting the Favor breakpoint buys a chance at a second pull. Expected units
/// per voyage for a material therefore come out as
///
///   E = (weight of the first pull + favor proc × weight of the favor pull)
///       × the material's share of its tier
///       × average quantity at the current retrieval tier
///
/// summed over the tiers the material appears in.
/// </summary>
public sealed class SubmarineRouteService
{
    public static readonly string[] SlotNames = { "Hull", "Stern", "Bow", "Bridge" };
    public static readonly string[] StatNames = { "Surveillance", "Retrieval", "Speed", "Range", "Favor" };
    public static readonly string[] StatShortNames = { "Surv", "Retr", "Speed", "Range", "Favor" };
    public static readonly string[] SurveillanceNames = { "Low", "Mid", "High" };
    public static readonly string[] RetrievalNames = { "Poor", "Normal", "Optimal" };

    /// <summary>Named part sets people usually build towards, in slot order.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Parts)> Presets = new[]
    {
        ("SSUC++", new[] { "Shark-M", "Shark-M", "Unkiu-M", "Coelacanth-M" }),
        ("SSUU++", new[] { "Shark-M", "Shark-M", "Unkiu-M", "Unkiu-M" }),
        ("WSSS++", new[] { "Whale-M", "Shark-M", "Shark-M", "Shark-M" }),
        ("SSCC++", new[] { "Shark-M", "Shark-M", "Coelacanth-M", "Coelacanth-M" }),
    };

    /// <summary>A voyage may visit at most five sectors, all on the same map.</summary>
    public const int MaxSectorsPerVoyage = 5;

    /// <summary>
    /// How many of the best-yielding sectors per map the range-aware search
    /// considers. Everything beyond this is too far down the yield order to win
    /// a slot, and the subset search is exponential in this number.
    /// </summary>
    private const int RangeSearchWidth = 12;

    private readonly IPluginLog? log;

    /// <summary>Part names per slot, in dataset order.</summary>
    private readonly string[][] partNames = new string[4][];
    private readonly int[][][] partStats = new int[4][][];

    public SubmarineRouteData Data { get; private set; } = new();
    public bool IsReady { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>Highest submarine rank the dataset has a stat bonus for.</summary>
    public int MaxRank { get; private set; } = 1;

    public SubmarineRouteService(IDalamudPluginInterface pluginInterface, IPluginLog log)
        : this(Path.Combine(Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName) ?? ".",
                            "Resources", "submarine_routes.json"), log)
    {
    }

    /// <summary>Loads the dataset from an explicit path; the log is optional so the model can run offline.</summary>
    public SubmarineRouteService(string dataPath, IPluginLog? log = null)
    {
        this.log = log;
        Load(dataPath);
    }

    private void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                LoadError = "Resources/submarine_routes.json is missing.";
                log?.Warning($"[RedMoonCappuccino] {LoadError}");
                return;
            }

            var options = new JsonSerializerOptions();
            options.Converters.Add(new TolerantIntConverter());

            var parsed = JsonSerializer.Deserialize<SubmarineRouteData>(File.ReadAllText(path), options);
            if (parsed == null || parsed.Sectors.Length == 0 || parsed.Names.Length == 0)
            {
                LoadError = "Submersible route data is empty.";
                return;
            }

            Data = parsed;

            for (var slot = 0; slot < SlotNames.Length; slot++)
            {
                if (!Data.Parts.TryGetValue(SlotNames[slot], out var slotParts))
                {
                    LoadError = $"Submersible part data for {SlotNames[slot]} is missing.";
                    return;
                }

                partNames[slot] = slotParts.Keys.ToArray();
                partStats[slot] = partNames[slot].Select(n => slotParts[n]).ToArray();
            }

            foreach (var key in Data.Rank.Keys)
                if (int.TryParse(key, out var rank) && rank > MaxRank) MaxRank = rank;

            IsReady = true;
            log?.Information($"[RedMoonCappuccino] Submersible planner loaded {Data.Sectors.Length} sectors, " +
                             $"{Data.Names.Length} materials and ranks up to {MaxRank}.");
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            log?.Error(ex, "[RedMoonCappuccino] Failed to load submersible route data.");
        }
    }

    // ── Builds ────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> PartNames(int slot) => partNames[slot];

    public int PartIndex(int slot, string name)
    {
        var index = Array.IndexOf(partNames[slot], name);
        return index < 0 ? 0 : index;
    }

    /// <summary>Part stats as [surv, retr, speed, range, favor, minimum rank].</summary>
    public int[] PartStats(int slot, int partIndex) => partStats[slot][partIndex];

    /// <summary>Dataset name of the part with these stats, or null when unknown.</summary>
    public string? FindPart(int slot, int surveillance, int retrieval, int speed, int range, int favor)
    {
        if (!IsReady || slot < 0 || slot >= 4) return null;

        for (var i = 0; i < partStats[slot].Length; i++)
        {
            var p = partStats[slot][i];
            if (p[0] == surveillance && p[1] == retrieval && p[2] == speed && p[3] == range && p[4] == favor)
                return partNames[slot][i];
        }

        return null;
    }

    public int[] RankBonus(int rank) =>
        Data.Rank.TryGetValue(rank.ToString(), out var bonus) ? bonus : new int[5];

    /// <summary>Total stats of a build, and the rank its parts require.</summary>
    public (int[] Stats, int MinimumRank) ComputeStats(int[] partIndices, int rank)
    {
        var stats   = new int[5];
        var minRank = 1;

        for (var slot = 0; slot < 4; slot++)
        {
            var part = partStats[slot][partIndices[slot]];
            for (var k = 0; k < 5; k++) stats[k] += part[k];
            minRank = Math.Max(minRank, part[5]);
        }

        var bonus = RankBonus(rank);
        for (var k = 0; k < 5; k++) stats[k] += bonus[k];

        return (stats, minRank);
    }

    // ── Yield model ───────────────────────────────────────────────────────────

    /// <summary>Surveillance / retrieval tiers and favor state a build reaches in a sector.</summary>
    public static (int Surveillance, int Retrieval, bool Favor) TiersFor(SubmarineSector sector, int[] stats)
    {
        var surveillance = stats[0] >= sector.SurveillanceForHigh ? 2
                         : stats[0] >= sector.SurveillanceForMid ? 1 : 0;
        var retrieval    = stats[1] >= sector.RetrievalForOptimal ? 2
                         : stats[1] >= sector.RetrievalForNormal ? 1 : 0;
        return (surveillance, retrieval, stats[4] >= sector.FavorRequired);
    }

    /// <summary>
    /// Expected units of a material from one visit to a sector. The tier
    /// arguments override what the build would reach, which is how the upgrade
    /// hints work out what a breakpoint would be worth.
    /// </summary>
    public SectorEstimate Estimate(SubmarineSector sector, int itemIndex, int[] stats,
                                   int? surveillanceOverride = null, int? retrievalOverride = null,
                                   bool? favorOverride = null)
    {
        var (surveillance, retrieval, favor) = TiersFor(sector, stats);
        surveillance = surveillanceOverride ?? surveillance;
        retrieval    = retrievalOverride ?? retrieval;
        favor        = favorOverride ?? favor;

        var block     = sector.BlockFor(surveillance);
        var favorProc = favor ? block.FavorProc : 0f;

        var estimate = new SectorEstimate
        {
            Sector       = sector,
            Surveillance = surveillance,
            Retrieval    = retrieval,
            Favor        = favor,
        };

        for (var tier = 0; tier < sector.Items.Length; tier++)
        {
            var pool = sector.Items[tier];
            if (pool == null) continue;

            var drop = pool.FirstOrDefault(d => d.ItemIndex == itemIndex);
            if (drop == null) continue;

            var weight = block.FirstDip[tier] + favorProc * block.FavorDip[tier];

            estimate.Expected  += weight * drop.Chance * drop.Yield[retrieval];
            estimate.Found      = true;
            estimate.Tier       = tier;
            estimate.Drop       = drop;
            estimate.DropChance = weight * drop.Chance;
            estimate.Estimated |= drop.Estimated;
        }

        return estimate;
    }

    /// <summary>
    /// Every sector that can drop the material, with upgrade headroom filled in.
    /// Pass <paramref name="rank"/> to mark sectors the submarine is not ranked
    /// high enough to plot; 0 leaves them all unmarked.
    /// </summary>
    public List<SectorEstimate> BuildRows(int itemIndex, int[] stats, int rank = 0)
    {
        var rows = new List<SectorEstimate>();
        if (!IsReady || itemIndex < 0) return rows;

        for (var index = 0; index < Data.Sectors.Length; index++)
        {
            var sector   = Data.Sectors[index];
            var estimate = Estimate(sector, itemIndex, stats);
            if (!estimate.Found) continue;

            estimate.Index      = index;
            estimate.RankLocked = IsRankLocked(sector, rank);

            if (estimate.Retrieval < 2)
            {
                var upgraded = Estimate(sector, itemIndex, stats, retrievalOverride: estimate.Retrieval + 1);
                estimate.RetrievalUpgrade = (
                    estimate.Retrieval == 0 ? sector.RetrievalForNormal : sector.RetrievalForOptimal,
                    upgraded.Expected - estimate.Expected);
            }

            if (estimate.Tier == 2 && estimate.Surveillance < 2)
                estimate.SurveillanceUpgrade = sector.SurveillanceForHigh;
            else if (estimate.Tier == 1 && estimate.Surveillance < 1)
                estimate.SurveillanceUpgrade = sector.SurveillanceForMid;

            if (!estimate.Favor)
            {
                var upgraded = Estimate(sector, itemIndex, stats, favorOverride: true);
                estimate.FavorUpgrade = (sector.FavorRequired, upgraded.Expected - estimate.Expected);
            }

            rows.Add(estimate);
        }

        return rows;
    }

    /// <summary>
    /// Whether a submarine of this rank is barred from the sector. Only the
    /// sectors the dataset carries a requirement for can be locked; for the rest
    /// the answer is always no.
    /// </summary>
    private static bool IsRankLocked(SubmarineSector sector, int rank) =>
        rank > 0 && sector.RankRequirement > 0 && sector.RankRequirement > rank;

    public int MaxTier(int itemIndex)
    {
        var max = -1;
        foreach (var sector in Data.Sectors)
        {
            for (var tier = 0; tier < sector.Items.Length; tier++)
            {
                if (sector.Items[tier]?.Any(d => d.ItemIndex == itemIndex) == true)
                    max = Math.Max(max, tier);
            }
        }
        return max;
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Best voyage per map, richest map first. When <paramref name="options"/>
    /// carries a voyage planner the sectors are ordered for travel and dropped
    /// until the trip fits the submarine's range.
    /// </summary>
    public List<MapRoute> BuildRoutes(List<SectorEstimate> rows, RouteOptions options)
    {
        var routes = new List<MapRoute>();

        foreach (var group in rows.Where(r => r.Expected > 0 && !r.RankLocked && !options.Disabled.Contains(r.Index))
                                  .GroupBy(r => r.Sector.Map))
        {
            var ranked = group.OrderByDescending(r => r.Expected).ToList();
            var route  = options.Planner == null
                ? new MapRoute { Map = group.Key, Sectors = ranked.Take(MaxSectorsPerVoyage).ToList() }
                : PlanBestVoyage(group.Key, ranked, options);

            if (route.Sectors.Count == 0) continue;
            route.Total = route.Sectors.Sum(s => s.Expected);
            routes.Add(route);
        }

        return routes.OrderByDescending(r => r.Total).ToList();
    }

    /// <summary>
    /// Picks the highest-yielding set of sectors that the submarine can actually
    /// reach. Only the <see cref="RangeSearchWidth"/> best sectors are considered
    /// — anything further down the yield order cannot beat them for a slot.
    /// </summary>
    private MapRoute PlanBestVoyage(int map, List<SectorEstimate> ranked, RouteOptions options)
    {
        var pool      = ranked.Take(RangeSearchWidth).ToList();
        var unlimited = ranked.Take(MaxSectorsPerVoyage).ToList();

        // Usually the five richest sectors are reachable as they are, so try
        // that before enumerating subsets.
        var direct = options.Planner!(unlimited.Select(s => s.Sector).ToList());
        if (direct != null && (options.Range <= 0 || direct.Distance <= options.Range))
        {
            var byLetter = unlimited.ToDictionary(s => s.Sector.Letter);
            return new MapRoute
            {
                Map      = map,
                Sectors  = direct.Order.Select(o => byLetter[o.Letter]).ToList(),
                Distance = direct.Distance,
            };
        }

        var best      = new MapRoute { Map = map };
        var bestScore = -1f;
        var anyPlan   = false;

        var indices = new int[MaxSectorsPerVoyage];

        void Search(int start, int depth)
        {
            if (depth > 0)
            {
                var subset = new List<SectorEstimate>(depth);
                for (var i = 0; i < depth; i++) subset.Add(pool[indices[i]]);

                var plan = options.Planner!(subset.Select(s => s.Sector).ToList());
                if (plan != null)
                {
                    anyPlan = true;

                    if (options.Range <= 0 || plan.Distance <= options.Range)
                    {
                        var score = subset.Sum(s => s.Expected);
                        if (score > bestScore + 1e-6f ||
                            (Math.Abs(score - bestScore) <= 1e-6f && plan.Distance < best.Distance))
                        {
                            bestScore    = score;
                            var byLetter = subset.ToDictionary(s => s.Sector.Letter);
                            best = new MapRoute
                            {
                                Map      = map,
                                Sectors  = plan.Order.Select(o => byLetter[o.Letter]).ToList(),
                                Distance = plan.Distance,
                            };
                        }
                    }
                }
            }

            if (depth == MaxSectorsPerVoyage) return;

            for (var i = start; i < pool.Count; i++)
            {
                indices[depth] = i;
                Search(i + 1, depth + 1);
            }
        }

        Search(0, 0);

        // No plan at all means the sectors are not mapped to game data; fall
        // back to the plain yield order rather than claiming a range problem.
        if (!anyPlan)
            return new MapRoute { Map = map, Sectors = unlimited };

        if (best.Sectors.Count == 0)
        {
            // Even one sector is out of range: still show the richest ones so
            // the player can see what a longer-ranged build would reach.
            var plan = options.Planner!(unlimited.Select(s => s.Sector).ToList());
            return new MapRoute
            {
                Map       = map,
                Sectors   = plan == null
                    ? unlimited
                    : plan.Order.Select(o => unlimited.First(u => u.Sector.Letter == o.Letter)).ToList(),
                Distance  = plan?.Distance ?? 0,
                OverRange = true,
            };
        }

        best.Trimmed = options.Range > 0 && best.Sectors.Count < Math.Min(MaxSectorsPerVoyage, ranked.Count);
        return best;
    }

    // ── Build optimiser ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches every hull/stern/bow/bridge combination the rank allows and
    /// returns the one whose best single-map voyage yields most. Speed breaks
    /// ties, since it shortens the voyage without changing the loot.
    /// </summary>
    public BuildSuggestion? SuggestBuild(int itemIndex, int rank, ISet<int> disabled)
    {
        if (!IsReady || itemIndex < 0) return null;

        var candidates = CandidateSectors(itemIndex, disabled, rank);
        if (candidates.Count == 0) return null;

        var scoring = new ScoringSet(candidates, itemIndex);
        var bonus   = RankBonus(rank);
        var usable = new List<int>[4];
        for (var slot = 0; slot < 4; slot++)
        {
            usable[slot] = new List<int>();
            for (var i = 0; i < partStats[slot].Length; i++)
                if (partStats[slot][i][5] <= rank) usable[slot].Add(i);
        }

        // Scoring only depends on surveillance, retrieval and favor, so builds
        // that share those three are scored once. Partial sums keep the inner
        // loop down to one addition per stat.
        var memo  = new Dictionary<long, float>();
        var stats = new int[5];
        var hs    = new int[5];
        var hsb   = new int[5];

        BuildSuggestion? best = null;

        foreach (var hull in usable[0])
        {
            var hullPart = partStats[0][hull];

            foreach (var stern in usable[1])
            {
                var sternPart = partStats[1][stern];
                for (var k = 0; k < 5; k++) hs[k] = hullPart[k] + sternPart[k] + bonus[k];

                foreach (var bow in usable[2])
                {
                    var bowPart = partStats[2][bow];
                    for (var k = 0; k < 5; k++) hsb[k] = hs[k] + bowPart[k];

                    foreach (var bridge in usable[3])
                    {
                        var bridgePart = partStats[3][bridge];
                        for (var k = 0; k < 5; k++) stats[k] = hsb[k] + bridgePart[k];

                        var key = ((long)(stats[0] + 2048) << 40) |
                                  ((long)(stats[1] + 2048) << 20) |
                                   (long)(stats[4] + 2048);
                        if (!memo.TryGetValue(key, out var score))
                        {
                            score = scoring.Score(stats);
                            memo[key] = score;
                        }

                        if (best != null && score <= best.Score + 1e-9f &&
                            (Math.Abs(score - best.Score) >= 1e-9f || stats[2] <= best.Stats[2]))
                            continue;

                        best = new BuildSuggestion
                        {
                            Score = score,
                            Stats = (int[])stats.Clone(),
                            Parts = new[] { partNames[0][hull], partNames[1][stern], partNames[2][bow], partNames[3][bridge] },
                        };
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Finds the part set whose stats add up to <paramref name="target"/>, used
    /// to name the parts of a live submarine when its part ids cannot be looked
    /// up. Returns null when no combination matches exactly.
    /// </summary>
    public string[]? SolveParts(int[] target, int rank)
    {
        if (!IsReady) return null;

        var usable = new List<int>[4];
        for (var slot = 0; slot < 4; slot++)
        {
            usable[slot] = new List<int>();
            for (var i = 0; i < partStats[slot].Length; i++)
                if (partStats[slot][i][5] <= rank) usable[slot].Add(i);
        }

        foreach (var hull in usable[0])
        foreach (var stern in usable[1])
        foreach (var bow in usable[2])
        foreach (var bridge in usable[3])
        {
            var picks = new[] { hull, stern, bow, bridge };
            var match = true;

            for (var k = 0; k < 5 && match; k++)
            {
                var sum = 0;
                for (var slot = 0; slot < 4; slot++) sum += partStats[slot][picks[slot]][k];
                if (sum != target[k]) match = false;
            }

            if (match)
                return new[] { partNames[0][hull], partNames[1][stern], partNames[2][bow], partNames[3][bridge] };
        }

        return null;
    }

    /// <summary>Yield of the best five-sector single-map voyage for a stat line.</summary>
    public float BestMapTotal(List<SubmarineSector> candidates, int[] stats, int itemIndex) =>
        new ScoringSet(candidates, itemIndex).Score(stats);

    /// <summary>
    /// Sectors that can drop the material at all, ignoring current stats. A
    /// <paramref name="rank"/> above 0 drops the ones the submarine cannot reach.
    /// </summary>
    public List<SubmarineSector> CandidateSectors(int itemIndex, ISet<int>? disabled = null, int rank = 0)
    {
        var result = new List<SubmarineSector>();

        for (var index = 0; index < Data.Sectors.Length; index++)
        {
            if (disabled != null && disabled.Contains(index)) continue;

            var sector = Data.Sectors[index];
            if (IsRankLocked(sector, rank)) continue;

            if (sector.Items.Any(pool => pool?.Any(d => d.ItemIndex == itemIndex) == true))
                result.Add(sector);
        }

        return result;
    }

    /// <summary>
    /// The sectors that can drop one material, grouped by map with their drop
    /// entries already located. The optimiser scores tens of thousands of stat
    /// lines against the same set, so this pre-chewing — and scoring without
    /// allocating — is what keeps the search interactive.
    /// </summary>
    private sealed class ScoringSet
    {
        private readonly struct Hit
        {
            public Hit(int tier, float chance, float[] yield)
            {
                Tier   = tier;
                Chance = chance;
                Yield  = yield;
            }

            public readonly int Tier;
            public readonly float Chance;
            public readonly float[] Yield;
        }

        private readonly SubmarineSector[] sectors;
        private readonly Hit[][] hits;

        /// <summary>Index where each map's run of sectors starts, with a closing terminator.</summary>
        private readonly int[] mapStarts;

        private readonly float[] top = new float[MaxSectorsPerVoyage];

        public ScoringSet(List<SubmarineSector> candidates, int itemIndex)
        {
            sectors = candidates.OrderBy(s => s.Map).ToArray();
            hits    = new Hit[sectors.Length][];

            for (var i = 0; i < sectors.Length; i++)
            {
                var found = new List<Hit>(1);
                for (var tier = 0; tier < sectors[i].Items.Length; tier++)
                {
                    var pool = sectors[i].Items[tier];
                    if (pool == null) continue;

                    foreach (var drop in pool)
                        if (drop.ItemIndex == itemIndex) found.Add(new Hit(tier, drop.Chance, drop.Yield));
                }
                hits[i] = found.ToArray();
            }

            var starts = new List<int> { 0 };
            for (var i = 1; i < sectors.Length; i++)
                if (sectors[i].Map != sectors[i - 1].Map) starts.Add(i);
            starts.Add(sectors.Length);
            mapStarts = starts.ToArray();
        }

        public float Score(int[] stats)
        {
            var best = 0f;

            for (var segment = 0; segment < mapStarts.Length - 1; segment++)
            {
                Array.Clear(top);

                for (var i = mapStarts[segment]; i < mapStarts[segment + 1]; i++)
                {
                    var sector = sectors[i];
                    var (surveillance, retrieval, favor) = TiersFor(sector, stats);

                    var block     = sector.BlockFor(surveillance);
                    var favorProc = favor ? block.FavorProc : 0f;

                    var expected = 0f;
                    foreach (var hit in hits[i])
                        expected += (block.FirstDip[hit.Tier] + favorProc * block.FavorDip[hit.Tier])
                                  * hit.Chance * hit.Yield[retrieval];

                    if (expected <= top[MaxSectorsPerVoyage - 1]) continue;

                    // Keep the five best by insertion; the array is tiny.
                    var slot = MaxSectorsPerVoyage - 1;
                    while (slot > 0 && top[slot - 1] < expected)
                    {
                        top[slot] = top[slot - 1];
                        slot--;
                    }
                    top[slot] = expected;
                }

                var total = 0f;
                foreach (var value in top) total += value;
                if (total > best) best = total;
            }

            return best;
        }
    }
}

/// <summary>What one sector is worth for the selected material.</summary>
public sealed class SectorEstimate
{
    public int Index { get; set; }
    public SubmarineSector Sector { get; set; } = null!;

    public float Expected { get; set; }
    public bool  Found { get; set; }

    /// <summary>Loot tier the material sits in, 0-based.</summary>
    public int Tier { get; set; }

    /// <summary>Chance a voyage pulls the material at all.</summary>
    public float DropChance { get; set; }

    public SubmarineDrop? Drop { get; set; }

    public int  Surveillance { get; set; }
    public int  Retrieval { get; set; }
    public bool Favor { get; set; }

    /// <summary>The yield behind this estimate is a placeholder, not measured data.</summary>
    public bool Estimated { get; set; }

    /// <summary>True when the submarine's rank is too low to plot this sector.</summary>
    public bool RankLocked { get; set; }

    /// <summary>Retrieval needed for the next tier and what it would add.</summary>
    public (int Need, float Gain)? RetrievalUpgrade { get; set; }

    /// <summary>Surveillance needed before this tier can be reached at all.</summary>
    public int? SurveillanceUpgrade { get; set; }

    /// <summary>Favor needed for the second pull and what it would add.</summary>
    public (int Need, float Gain)? FavorUpgrade { get; set; }

    public float AverageYield => Drop?.Yield[Retrieval] ?? 0f;
    public int MinYield => Drop?.Range[Retrieval * 2] ?? 0;
    public int MaxYield => Drop?.Range[Retrieval * 2 + 1] ?? 0;
}

/// <summary>An ordered voyage and the range it costs.</summary>
public sealed class VoyagePlan
{
    public List<SubmarineSector> Order { get; init; } = new();
    public int Distance { get; init; }
}

public sealed class RouteOptions
{
    public ISet<int> Disabled { get; init; } = new HashSet<int>();

    /// <summary>Orders a set of sectors into the cheapest voyage, when game data is available.</summary>
    public Func<IReadOnlyList<SubmarineSector>, VoyagePlan?>? Planner { get; init; }

    /// <summary>Submarine range; 0 leaves voyages unconstrained.</summary>
    public int Range { get; init; }
}

public sealed class MapRoute
{
    public int Map { get; set; }
    public List<SectorEstimate> Sectors { get; set; } = new();
    public float Total { get; set; }

    /// <summary>Range the voyage costs, 0 when distances are unavailable.</summary>
    public int Distance { get; set; }

    /// <summary>Sectors were dropped to keep the voyage inside the range.</summary>
    public bool Trimmed { get; set; }

    /// <summary>Even a single-sector voyage would not fit the range.</summary>
    public bool OverRange { get; set; }
}

public sealed class BuildSuggestion
{
    public float Score { get; set; }
    public int[] Stats { get; set; } = new int[5];
    public string[] Parts { get; set; } = Array.Empty<string>();
}
