using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;  // ICharacter
using Dalamud.Game.Inventory;  

namespace RedMoonCappuccino.Services;

public sealed class GearPlannerService
{
    private static readonly IReadOnlyDictionary<string, int> EmptyStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // Maps EquippedItems container slot indices to the slot-key strings used in BiS JSON.
    // Slot 5 (waist/belt) was removed in Shadowbringers and is intentionally absent.
    private static readonly IReadOnlyDictionary<int, string> EquipSlotIndexToKey =
        new Dictionary<int, string>
        {
            [0]  = "WEAPON",
            [1]  = "OFFHAND",
            [2]  = "HEAD",
            [3]  = "BODY",
            [4]  = "HANDS",
            [6]  = "LEGS",
            [7]  = "FEET",
            [8]  = "EARS",
            [9]  = "NECK",
            [10] = "WRISTS",
            [11] = "RING_L",
            [12] = "RING_R",
        };
    private readonly PlannerData data;

    public GearPlannerService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        data = PlannerData.Load(pluginInterface, log);
    }

    public IReadOnlyList<string> AvailableJobs => data.AvailableJobs;

    /// Returns the abbreviation (e.g. "WAR") of the job the local player currently has equipped,
    /// or null when not logged in or the data is unavailable.
    public string? GetCurrentJob() => TryReadCurrentPlayerJob();

    public PlannerRunResult Solve(string? requestedJob)
    {
        if (!data.IsReady)
            return PlannerRunResult.FromError("Planner data is not available. Check local JSON resources.");

        var job = SelectJob(requestedJob);
        if (job == null)
            return PlannerRunResult.FromError("No BiS target is available for the selected job.");

        if (!data.BisTargets.TryGetValue(job, out var target))
            return PlannerRunResult.FromError($"No BiS target is available for job {job}.");

        var snapshot = BuildSnapshot(target);
        if (snapshot.TotalTargetSlots > 64)
            return PlannerRunResult.FromError($"Planner currently supports up to 64 tracked slots; found {snapshot.TotalTargetSlots}.");

        var pathResult = ComputeBestPaths(snapshot);

        return new PlannerRunResult
        {
            SelectedJob = job,
            IsReady = true,
            DataVersion = data.DataVersion,
            GamePatch = data.GamePatch,
            BisPatch = target.Patch,
            Snapshot = snapshot,
            RecommendedPaths = pathResult,
            GeneratedAtUtc = DateTime.UtcNow,
            SupportsBranching = true,
        };
    }

    private string? SelectJob(string? requestedJob)
    {
        if (!string.IsNullOrWhiteSpace(requestedJob) &&
            data.BisTargets.ContainsKey(requestedJob.Trim().ToUpperInvariant()))
            return requestedJob.Trim().ToUpperInvariant();

        var currentJob = TryReadCurrentPlayerJob();
        if (currentJob != null && data.BisTargets.ContainsKey(currentJob))
            return currentJob;

        return data.AvailableJobs.FirstOrDefault();
    }

    private PlannerSnapshot BuildSnapshot(BisTarget target)
    {
        var current = new Dictionary<string, GearItem?>(StringComparer.Ordinal);
        var targetGear = new Dictionary<string, GearItem>(StringComparer.Ordinal);

        var equipped = BuildEquippedMap();

        foreach (var slotTarget in target.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var slot = slotTarget.Key;
            var desiredItemId = slotTarget.Value;
            var desiredItem = data.GetItemById(desiredItemId);
            if (desiredItem == null)
                continue;

            targetGear[slot] = desiredItem;
            equipped.TryGetValue(slot, out var equippedItem);
            current[slot] = equippedItem;
        }

        NormalizeRingSlots(current, targetGear);

        var matchingSlots = current.Count(kvp =>
            kvp.Value != null &&
            targetGear.TryGetValue(kvp.Key, out var tgt) &&
            kvp.Value.Id == tgt.Id);

        var currentStats = SumStats(current.Values.Where(x => x != null).Cast<GearItem>());
        var targetStats = SumStats(targetGear.Values);

        return new PlannerSnapshot
        {
            Job = target.Job,
            CurrentGear = current,
            TargetGear = targetGear,
            CurrentStats = currentStats,
            TargetStats = targetStats,
            MatchingSlots = matchingSlots,
            TotalTargetSlots = targetGear.Count,
            HasKnownCurrentGear = Plugin.ClientState.IsLoggedIn && equipped.Count > 0,
        };
    }

    // Rings are interchangeable between RING_L and RING_R.
    // Swap the equipped assignment if doing so produces more matches against the target.
    private static void NormalizeRingSlots(Dictionary<string, GearItem?> current, Dictionary<string, GearItem> target)
    {
        if (!target.TryGetValue("RING_L", out var targetL) || !target.TryGetValue("RING_R", out var targetR))
            return;

        current.TryGetValue("RING_L", out var equippedL);
        current.TryGetValue("RING_R", out var equippedR);

        var matchesNormal  = (equippedL?.Id == targetL.Id ? 1 : 0) + (equippedR?.Id == targetR.Id ? 1 : 0);
        var matchesSwapped = (equippedR?.Id == targetL.Id ? 1 : 0) + (equippedL?.Id == targetR.Id ? 1 : 0);

        if (matchesSwapped > matchesNormal)
        {
            current["RING_L"] = equippedR;
            current["RING_R"] = equippedL;
        }
    }

    /// <summary>
    /// Reads the player's currently equipped items from the game inventory and returns
    /// a map of slot-key -> GearItem (null value means slot is occupied but item is not in the gear DB).
    /// Returns an empty dictionary when the player is not logged in or the container is unavailable.
    /// </summary>
    private Dictionary<string, GearItem?> BuildEquippedMap()
    {
        var result = new Dictionary<string, GearItem?>(StringComparer.Ordinal);
        try
        {
            var items = Plugin.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
            foreach (var invItem in items)
            {
                if (invItem.IsEmpty || invItem.ItemId == 0)
                    continue;

                var slotIndex = (int)invItem.InventorySlot;
                if (EquipSlotIndexToKey.TryGetValue(slotIndex, out var slotKey))
                {
                    var rawId = (int)invItem.ItemId;
                    var baseId = rawId >= 1_000_000 ? rawId - 1_000_000 : rawId;
                    result[slotKey] = data.GetItemById(baseId);
                }
            }
        }
        catch { /* Best-effort: return whatever was collected before the failure. */ }
        return result;
    }

    private List<PlannerPathRecommendation> ComputeBestPaths(PlannerSnapshot snapshot)
    {
        var orderedSlots = snapshot.TargetGear.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (orderedSlots.Length == 0)
            return [];

        ulong initialMask = 0;
        for (var i = 0; i < orderedSlots.Length; i++)
        {
            var slot = orderedSlots[i];
            if (snapshot.CurrentGear.TryGetValue(slot, out var current) &&
                snapshot.TargetGear.TryGetValue(slot, out var target) &&
                current?.Id == target.Id)
                initialMask |= 1UL << i;
        }

        var allMask = orderedSlots.Length >= 64 ? ulong.MaxValue : (1UL << orderedSlots.Length) - 1;
        var bestByMask = new Dictionary<ulong, double> { [initialMask] = 0.0 };
        var queue = new PriorityQueue<SearchNode, double>();
        queue.Enqueue(new SearchNode(initialMask, 0.0, [], new Dictionary<string, int>(snapshot.CurrentStats, StringComparer.OrdinalIgnoreCase)), 0.0);

        var completePaths = new List<SearchNode>();
        var expansions = 0;
        const int maxExpansions = 12000;

        while (queue.Count > 0 && expansions < maxExpansions)
        {
            expansions++;
            var node = queue.Dequeue();

            if (node.Mask == allMask)
            {
                completePaths.Add(node);
                if (completePaths.Count >= 8)
                    break;
                continue;
            }

            for (var i = 0; i < orderedSlots.Length; i++)
            {
                var bit = 1UL << i;
                if ((node.Mask & bit) != 0)
                    continue;

                var slot = orderedSlots[i];
                if (!snapshot.TargetGear.TryGetValue(slot, out var targetItem) ||
                    !snapshot.CurrentGear.TryGetValue(slot, out var currentItem))
                    continue;

                var action = BuildAction(slot, currentItem, targetItem, node.CurrentStats);
                var nextScore = node.Score + action.UtilityScore;
                var nextMask = node.Mask | bit;

                if (bestByMask.TryGetValue(nextMask, out var knownBest) && nextScore <= knownBest)
                    continue;

                bestByMask[nextMask] = nextScore;

                var nextActions = new List<PlannerUpgradeAction>(node.Actions.Count + 1);
                nextActions.AddRange(node.Actions);
                nextActions.Add(action);
                var nextStats = BuildNextStats(node.CurrentStats, currentItem, targetItem);

                var heuristic = RemainingPotential(snapshot, orderedSlots, nextMask, nextStats);
                var priority = -(nextScore + heuristic);
                queue.Enqueue(new SearchNode(nextMask, nextScore, nextActions, nextStats), priority);
            }
        }

        if (completePaths.Count == 0)
            return [];

        return completePaths
            .OrderByDescending(p => p.Score)
            .Take(3)
            .Select((path, index) => new PlannerPathRecommendation
            {
                Rank = index + 1,
                TotalUtility = path.Score,
                Upgrades = path.Actions,
                Summary = BuildPathSummary(path),
            })
            .ToList();
    }

    private PlannerUpgradeAction BuildAction(string slot, GearItem? current, GearItem target, IReadOnlyDictionary<string, int> baselineStats)
    {
        var statGain = SumPrimaryCombatStats(target.Stats) - SumPrimaryCombatStats(current?.Stats ?? EmptyStats);
        var itemLevelGain = target.ItemLevel - (current?.ItemLevel ?? 0);

        var tomeCost = EstimateTomeCost(slot, target.SourceType);
        var bookCost = EstimateBookCost(slot, target.SourceType);
        var sourcePenalty = EstimateSourcePenalty(target.SourceType);

        var baselineSpeed = baselineStats.GetValueOrDefault("sks") + baselineStats.GetValueOrDefault("sps");
        var targetSpeed = baselineSpeed - (current?.Stats.GetValueOrDefault("sks") ?? 0) - (current?.Stats.GetValueOrDefault("sps") ?? 0)
                          + target.Stats.GetValueOrDefault("sks") + target.Stats.GetValueOrDefault("sps");

        var breakpointBonus = EstimateBreakpointBonus(baselineSpeed, targetSpeed);

        var utility = (statGain * 1.0)
                    + (itemLevelGain * 0.8)
                    + breakpointBonus
                    - (tomeCost / 30.0)
                    - (bookCost * 6.0)
                    - sourcePenalty;

        var reasons = new List<string>
        {
            $"{slot}: +{itemLevelGain} item level, +{statGain} weighted stat gain",
            breakpointBonus > 0 ? "Crosses a speed breakpoint" : "No speed breakpoint crossed",
            tomeCost > 0 ? $"Estimated tome spend: {tomeCost}" : "No tome spend required",
            bookCost > 0 ? $"Estimated savage books: {bookCost}" : "No savage book requirement",
        };

        if (target.SourceType == "AllianceRaid" && target.ItemLevel == 780)
        {
            var (rain, certs) = AllianceRaidAugmentCost(slot);
            reasons.Add($"Augment from i770 Courtly Lover: {rain}\u00d7 Treno Rain + {certs}\u00d7 Everkeep Certificate");
        }
        else if (current?.SourceType == "AllianceRaid" && current.ItemLevel == 770)
        {
            var (rain, certs) = AllianceRaidAugmentCost(slot);
            reasons.Add($"Current piece upgradeable to i780: {rain}\u00d7 Treno Rain + {certs}\u00d7 Everkeep Certificate");
        }

        if (target.SourceType == "Tome" && target.ItemLevel == 790)
        {
            reasons.Add(TomeAugmentNote(slot));
        }

        return new PlannerUpgradeAction
        {
            Slot = slot,
            CurrentItemName = current?.Name ?? "Unknown equipped item",
            CurrentItemLevel = current?.ItemLevel ?? 0,
            TargetItemName = target.Name,
            TargetItemLevel = target.ItemLevel,
            SourceType = target.SourceType,
            EstimatedTomeCost = tomeCost,
            EstimatedBookCost = bookCost,
            BreakpointBonus = breakpointBonus,
            UtilityScore = utility,
            Reasons = reasons,
        };
    }

    private static double RemainingPotential(PlannerSnapshot snapshot, IReadOnlyList<string> orderedSlots, ulong mask, IReadOnlyDictionary<string, int> currentStats)
    {
        double potential = 0;
        var speed = currentStats.GetValueOrDefault("sks") + currentStats.GetValueOrDefault("sps");

        for (var i = 0; i < orderedSlots.Count; i++)
        {
            var bit = 1UL << i;
            if ((mask & bit) != 0)
                continue;

            var slot = orderedSlots[i];
            if (!snapshot.TargetGear.TryGetValue(slot, out var target) ||
                !snapshot.CurrentGear.TryGetValue(slot, out var current))
                continue;

            var delta = SumPrimaryCombatStats(target.Stats) - SumPrimaryCombatStats(current?.Stats ?? EmptyStats);
            if (delta > 0)
                potential += delta + Math.Max(0, target.Stats.GetValueOrDefault("sks") + target.Stats.GetValueOrDefault("sps") - speed) * 0.05;
        }

        return potential;
    }

    private static Dictionary<string, int> BuildNextStats(IReadOnlyDictionary<string, int> currentStats, GearItem? current, GearItem target)
    {
        var next = new Dictionary<string, int>(currentStats, StringComparer.OrdinalIgnoreCase);
        if (current != null)
            AddStats(next, current.Stats, -1);
        AddStats(next, target.Stats, +1);
        return next;
    }

    private static void AddStats(Dictionary<string, int> target, IReadOnlyDictionary<string, int> delta, int sign)
    {
        foreach (var stat in delta)
            target[stat.Key] = target.GetValueOrDefault(stat.Key) + (stat.Value * sign);
    }

    private static string BuildPathSummary(SearchNode path)
    {
        if (path.Actions.Count == 0)
            return "Already at BiS for tracked slots.";

        var first = path.Actions[0];
        return $"Start with {first.Slot} ({first.TargetItemName}) to maximize immediate utility and preserve future upgrade flexibility.";
    }

    private static int SumPrimaryCombatStats(IReadOnlyDictionary<string, int> stats)
        => stats.GetValueOrDefault("crit")
         + stats.GetValueOrDefault("det")
         + stats.GetValueOrDefault("dh")
         + stats.GetValueOrDefault("sks")
         + stats.GetValueOrDefault("sps")
         + stats.GetValueOrDefault("ten");

    private static Dictionary<string, int> SumStats(IEnumerable<GearItem> items)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var stat in item.Stats)
                totals[stat.Key] = totals.GetValueOrDefault(stat.Key) + stat.Value;
        }

        return totals;
    }

    private static (int Rain, int Certs) AllianceRaidAugmentCost(string slot) => slot switch
    {
        "WEAPON"                                     => (7, 17),
        "BODY" or "LEGS"                             => (5, 17),
        "HEAD" or "HANDS" or "FEET" or "OFFHAND"     => (3, 11),
        _                                            => (2, 7),  // EARS, NECK, WRISTS, RING_L, RING_R
    };

    private static string TomeAugmentNote(string slot) => slot switch
    {
        "WEAPON" =>
            "Direct augment: Thundersteeped Solvent (4 M11S books or M12S loot)",
        "BODY" or "HEAD" or "HANDS" or "LEGS" or "FEET" or "OFFHAND" =>
            "Direct augment: Thundersteeped Twine (4 M11S books, 3000 Nuts, or M11S loot)",
        _ =>
            "Direct augment: Thundersteeped Glaze (3 M10S books, 2000 Nuts, or M10S loot)",
    };

    private int EstimateBreakpointBonus(int baselineSpeed, int upgradedSpeed)
    {
        var step = data.BreakpointStep;
        if (step <= 0)
            return 0;

        var baseTier = baselineSpeed / step;
        var upgradedTier = upgradedSpeed / step;
        if (upgradedTier <= baseTier)
            return 0;

        return (upgradedTier - baseTier) * 20;
    }

    private static int EstimateSourcePenalty(string sourceType)
    {
        return sourceType switch
        {
            "Tome" => 4,
            "Savage" => 12,
            "Raid" => 8,
            "Trial" => 9,
            "AllianceRaid" => 7,
            "Ultimate" => 16,
            _ => 10,
        };
    }

    private static int EstimateTomeCost(string slot, string sourceType)
    {
        if (!string.Equals(sourceType, "Tome", StringComparison.OrdinalIgnoreCase))
            return 0;

        return slot switch
        {
            "WEAPON" => 1000,
            "BODY" or "LEGS" => 825,
            "HEAD" or "HANDS" or "FEET" => 495,
            "OFFHAND" => 400,
            _ => 375,
        };
    }

    private static int EstimateBookCost(string slot, string sourceType)
    {
        if (!string.Equals(sourceType, "Savage", StringComparison.OrdinalIgnoreCase))
            return 0;

        return slot switch
        {
            "WEAPON" => 8,
            "BODY" or "LEGS" => 6,
            "HEAD" or "HANDS" or "FEET" => 4,
            _ => 2,
        };
    }

    private static string? TryReadCurrentPlayerJob()
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
                return null;

            var player = Plugin.ObjectTable.LocalPlayer as ICharacter;
            if (player == null)
                return null;

            var abbreviation = player.ClassJob.ValueNullable?.Abbreviation.ExtractText();
            if (string.IsNullOrWhiteSpace(abbreviation))
                return null;

            return abbreviation.Trim().ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    private sealed class PlannerData
    {
        private readonly Dictionary<int, GearItem> itemsById;
        public bool IsReady { get; }
        public string DataVersion { get; }
        public string GamePatch { get; }
        public int BreakpointStep { get; }
        public Dictionary<string, BisTarget> BisTargets { get; }
        public IReadOnlyList<string> AvailableJobs { get; }

        private PlannerData(
            bool isReady,
            string dataVersion,
            string gamePatch,
            int breakpointStep,
            Dictionary<int, GearItem> itemsById,
            Dictionary<string, BisTarget> bisTargets,
            IReadOnlyList<string> availableJobs)
        {
            IsReady = isReady;
            DataVersion = dataVersion;
            GamePatch = gamePatch;
            BreakpointStep = breakpointStep;
            this.itemsById = itemsById;
            BisTargets = bisTargets;
            AvailableJobs = availableJobs;
        }

        public static PlannerData Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            try
            {
                var pluginDirectory = Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName);
                if (string.IsNullOrWhiteSpace(pluginDirectory))
                    return Empty();

                var plannerRoot = Path.Combine(pluginDirectory, "Resources", "planner");
                var gearPath = Path.Combine(plannerRoot, "gear_db", "gear_database.json");
                var mathPath = Path.Combine(plannerRoot, "math", "math.json");
                var bisPath = Path.Combine(plannerRoot, "bis");

                if (!File.Exists(gearPath) || !File.Exists(mathPath) || !Directory.Exists(bisPath))
                {
                    log.Warning("[RedMoonCappuccino] Gear planner data files are missing from Resources/planner.");
                    return Empty();
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                };

                var gearRoot = JsonSerializer.Deserialize<GearDatabaseRoot>(File.ReadAllText(gearPath), jsonOptions);
                var mathRoot = JsonSerializer.Deserialize<MathRoot>(File.ReadAllText(mathPath), jsonOptions);

                if (gearRoot?.Items == null || gearRoot.Items.Count == 0)
                    return Empty();

                var items = gearRoot.Items
                    .Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Slot))
                    .Select(x => new GearItem
                    {
                        Id = x.Id,
                        Name = x.Name ?? $"Unknown ({x.Id.ToString(CultureInfo.InvariantCulture)})",
                        Slot = x.Slot?.Trim().ToUpperInvariant() ?? string.Empty,
                        ItemLevel = x.ItemLevel,
                        Jobs = x.Jobs?.Select(j => j.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                               ?? [],
                        SourceType = x.Source?.Type?.Trim() ?? "Unknown",
                        Stats = x.Stats ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    })
                    .ToDictionary(i => i.Id, i => i);

                var bisTargets = new Dictionary<string, BisTarget>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(bisPath, "*.JSON", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var bis = JsonSerializer.Deserialize<BisRoot>(File.ReadAllText(file), jsonOptions);
                    if (bis?.Meta?.Job == null || bis.Slots == null)
                        continue;

                    var job = bis.Meta.Job.Trim().ToUpperInvariant();
                    var mappedSlots = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var slot in bis.Slots)
                    {
                        if (slot.Value?.ItemId is int itemId and > 0)
                            mappedSlots[slot.Key.Trim().ToUpperInvariant()] = itemId;
                    }

                    bisTargets[job] = new BisTarget
                    {
                        Job = job,
                        Patch = bis.Meta.Patch ?? string.Empty,
                        Slots = mappedSlots,
                    };
                }

                var breakStep = Math.Max(1, (mathRoot?.LevelStats?.Level100?.LevelDiv ?? 2780) / 130);

                var dataVersion = gearRoot.Metadata?.DataVersion ?? "unknown";
                var gamePatch = gearRoot.Metadata?.GameVersion ?? "unknown";

                var availableJobs = bisTargets.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

                return new PlannerData(
                    isReady: items.Count > 0 && bisTargets.Count > 0,
                    dataVersion: dataVersion,
                    gamePatch: gamePatch,
                    breakpointStep: breakStep,
                    itemsById: items,
                    bisTargets: bisTargets,
                    availableJobs: availableJobs);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[RedMoonCappuccino] Failed to load gear planner data.");
                return Empty();
            }
        }

        public GearItem? GetItemById(int id)
            => itemsById.GetValueOrDefault(id);

        private static PlannerData Empty()
            => new(
                isReady: false,
                dataVersion: "unknown",
                gamePatch: "unknown",
                breakpointStep: 21,
                itemsById: new Dictionary<int, GearItem>(),
                bisTargets: new Dictionary<string, BisTarget>(StringComparer.OrdinalIgnoreCase),
                availableJobs: []);
    }

    private sealed class SearchNode(ulong mask, double score, List<PlannerUpgradeAction> actions, Dictionary<string, int> currentStats)
    {
        public ulong Mask { get; } = mask;
        public double Score { get; } = score;
        public List<PlannerUpgradeAction> Actions { get; } = actions;
        public Dictionary<string, int> CurrentStats { get; } = currentStats;
    }

    private sealed class GearDatabaseRoot
    {
        [JsonPropertyName("metadata")]
        public GearMetadata? Metadata { get; set; }

        [JsonPropertyName("items")]
        public List<GearJsonItem>? Items { get; set; }
    }

    private sealed class GearMetadata
    {
        [JsonPropertyName("dataVersion")]
        public string? DataVersion { get; set; }

        [JsonPropertyName("gameVersion")]
        public string? GameVersion { get; set; }
    }

    private sealed class GearJsonItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("slot")]
        public string? Slot { get; set; }

        [JsonPropertyName("itemLevel")]
        public int ItemLevel { get; set; }

        [JsonPropertyName("jobs")]
        public List<string>? Jobs { get; set; }

        [JsonPropertyName("source")]
        public GearSource? Source { get; set; }

        [JsonPropertyName("stats")]
        public Dictionary<string, int>? Stats { get; set; }
    }

    private sealed class GearSource
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private sealed class BisRoot
    {
        [JsonPropertyName("_meta")]
        public BisMeta? Meta { get; set; }

        [JsonPropertyName("slots")]
        public Dictionary<string, BisSlotItem?>? Slots { get; set; }
    }

    private sealed class BisMeta
    {
        [JsonPropertyName("job")]
        public string? Job { get; set; }

        [JsonPropertyName("patch")]
        public string? Patch { get; set; }
    }

    private sealed class BisSlotItem
    {
        [JsonPropertyName("item_id")]
        public int ItemId { get; set; }
    }

    private sealed class MathRoot
    {
        [JsonPropertyName("levelStats")]
        public MathLevelStats? LevelStats { get; set; }
    }

    private sealed class MathLevelStats
    {
        [JsonPropertyName("100")]
        public Level100Stats? Level100 { get; set; }
    }

    private sealed class Level100Stats
    {
        [JsonPropertyName("levelDiv")]
        public int LevelDiv { get; set; }
    }
}

public sealed class PlannerRunResult
{
    public bool IsReady { get; init; }
    public string? Error { get; init; }
    public string SelectedJob { get; init; } = string.Empty;
    public string DataVersion { get; init; } = string.Empty;
    public string GamePatch { get; init; } = string.Empty;
    public string BisPatch { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public PlannerSnapshot Snapshot { get; init; } = new();
    public List<PlannerPathRecommendation> RecommendedPaths { get; init; } = [];
    public bool SupportsBranching { get; init; }

    public static PlannerRunResult FromError(string message)
        => new()
        {
            IsReady = false,
            Error = message,
            Snapshot = new PlannerSnapshot(),
            RecommendedPaths = [],
            GeneratedAtUtc = DateTime.UtcNow,
        };
}

public sealed class PlannerSnapshot
{
    public string Job { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, GearItem?> CurrentGear { get; init; } = new Dictionary<string, GearItem?>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, GearItem> TargetGear { get; init; } = new Dictionary<string, GearItem>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> CurrentStats { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> TargetStats { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int MatchingSlots { get; init; }
    public int TotalTargetSlots { get; init; }
    public bool HasKnownCurrentGear { get; init; }
}

public sealed class PlannerPathRecommendation
{
    public int Rank { get; init; }
    public double TotalUtility { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<PlannerUpgradeAction> Upgrades { get; init; } = [];
}

public sealed class PlannerUpgradeAction
{
    public string Slot { get; init; } = string.Empty;
    public string CurrentItemName { get; init; } = string.Empty;
    public int CurrentItemLevel { get; init; }
    public string TargetItemName { get; init; } = string.Empty;
    public int TargetItemLevel { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int EstimatedTomeCost { get; init; }
    public int EstimatedBookCost { get; init; }
    public int BreakpointBonus { get; init; }
    public double UtilityScore { get; init; }
    public List<string> Reasons { get; init; } = [];
}

public sealed class BisTarget
{
    public string Job { get; init; } = string.Empty;
    public string Patch { get; init; } = string.Empty;
    public Dictionary<string, int> Slots { get; init; } = new(StringComparer.Ordinal);
}

public sealed class GearItem
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slot { get; init; } = string.Empty;
    public int ItemLevel { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public HashSet<string> Jobs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Stats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
