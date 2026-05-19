using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RedMoonCappuccino.Services;

public sealed class GearPlannerService
{
    private readonly PlannerData data;

    public GearPlannerService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        data = PlannerData.Load(pluginInterface, log);
    }

    public IReadOnlyList<string> AvailableJobs => data.AvailableJobs;

    public PlannerRunResult Solve(string? requestedJob)
    {
        if (!data.IsReady)
            return PlannerRunResult.FromError("Planner data is not available. Check local JSON resources.");

        var job = SelectJob(requestedJob);
        if (job == null)
            return PlannerRunResult.FromError("No BiS target is available for the selected job.");

        if (!data.BisTargets.TryGetValue(job, out var target))
            return PlannerRunResult.FromError($"No BiS target is available for job {job}.");

        var snapshot = BuildSnapshot(job, target);
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

    private PlannerSnapshot BuildSnapshot(string job, BisTarget target)
    {
        var current = new Dictionary<string, GearItem>(StringComparer.Ordinal);
        var targetGear = new Dictionary<string, GearItem>(StringComparer.Ordinal);

        foreach (var slotTarget in target.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var slot = slotTarget.Key;
            var desiredItemId = slotTarget.Value;
            var desiredItem = data.GetItemById(desiredItemId);
            if (desiredItem == null)
                continue;

            targetGear[slot] = desiredItem;

            var fallbackCurrent = data.FindBestCurrentItem(job, slot, desiredItemId);
            current[slot] = fallbackCurrent ?? desiredItem;
        }

        var currentStats = SumStats(current.Values);
        var targetStats = SumStats(targetGear.Values);

        return new PlannerSnapshot
        {
            Job = job,
            CurrentGear = current,
            TargetGear = targetGear,
            CurrentStats = currentStats,
            TargetStats = targetStats,
            MatchingSlots = current.Count(kvp => targetGear.TryGetValue(kvp.Key, out var targetItem) && targetItem.Id == kvp.Value.Id),
            TotalTargetSlots = targetGear.Count,
        };
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
                current.Id == target.Id)
                initialMask |= 1UL << i;
        }

        var allMask = orderedSlots.Length >= 64 ? ulong.MaxValue : (1UL << orderedSlots.Length) - 1;
        var bestByMask = new Dictionary<ulong, double> { [initialMask] = 0.0 };
        var queue = new PriorityQueue<SearchNode, double>();
        queue.Enqueue(new SearchNode(initialMask, 0.0, []), 0.0);

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

                var action = BuildAction(slot, currentItem, targetItem, snapshot.CurrentStats);
                var nextScore = node.Score + action.UtilityScore;
                var nextMask = node.Mask | bit;

                if (bestByMask.TryGetValue(nextMask, out var knownBest) && nextScore <= knownBest)
                    continue;

                bestByMask[nextMask] = nextScore;

                var nextActions = new List<PlannerUpgradeAction>(node.Actions.Count + 1);
                nextActions.AddRange(node.Actions);
                nextActions.Add(action);

                var heuristic = RemainingPotential(snapshot, orderedSlots, nextMask);
                var priority = -(nextScore + heuristic);
                queue.Enqueue(new SearchNode(nextMask, nextScore, nextActions), priority);
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

    private PlannerUpgradeAction BuildAction(string slot, GearItem current, GearItem target, IReadOnlyDictionary<string, int> baselineStats)
    {
        var statGain = SumPrimaryCombatStats(target.Stats) - SumPrimaryCombatStats(current.Stats);
        var itemLevelGain = target.ItemLevel - current.ItemLevel;

        var tomeCost = EstimateTomeCost(slot, target.SourceType);
        var bookCost = EstimateBookCost(slot, target.SourceType);
        var sourcePenalty = EstimateSourcePenalty(target.SourceType);

        var baselineSpeed = baselineStats.GetValueOrDefault("sks") + baselineStats.GetValueOrDefault("sps");
        var targetSpeed = baselineSpeed - current.Stats.GetValueOrDefault("sks") - current.Stats.GetValueOrDefault("sps")
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
            breakpointBonus > 0 ? "Crosses a speed breakpoint tier" : "Maintains current speed tier without regression",
            tomeCost > 0 ? $"Estimated tome spend: {tomeCost}" : "No tome spend required",
            bookCost > 0 ? $"Estimated savage books: {bookCost}" : "No savage book requirement",
            "Targets final BiS slot directly to avoid dead-end purchases",
        };

        return new PlannerUpgradeAction
        {
            Slot = slot,
            CurrentItemName = current.Name,
            CurrentItemLevel = current.ItemLevel,
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

    private static double RemainingPotential(PlannerSnapshot snapshot, IReadOnlyList<string> orderedSlots, ulong mask)
    {
        double potential = 0;
        for (var i = 0; i < orderedSlots.Count; i++)
        {
            var bit = 1UL << i;
            if ((mask & bit) != 0)
                continue;

            var slot = orderedSlots[i];
            if (!snapshot.TargetGear.TryGetValue(slot, out var target) ||
                !snapshot.CurrentGear.TryGetValue(slot, out var current))
                continue;

            var delta = SumPrimaryCombatStats(target.Stats) - SumPrimaryCombatStats(current.Stats);
            if (delta > 0)
                potential += delta;
        }

        return potential;
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
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null)
                return null;

            var classJob = localPlayer.GetType().GetProperty("ClassJob")?.GetValue(localPlayer);
            var value = classJob?.GetType().GetProperty("Value")?.GetValue(classJob);
            var abbreviation = value?.GetType().GetProperty("Abbreviation")?.GetValue(value)?.ToString();

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
        private readonly Dictionary<(string Job, string Slot), List<GearItem>> slotIndex;

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
            Dictionary<(string Job, string Slot), List<GearItem>> slotIndex,
            Dictionary<string, BisTarget> bisTargets,
            IReadOnlyList<string> availableJobs)
        {
            IsReady = isReady;
            DataVersion = dataVersion;
            GamePatch = gamePatch;
            BreakpointStep = breakpointStep;
            this.itemsById = itemsById;
            this.slotIndex = slotIndex;
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

                var index = items.Values
                    .SelectMany(item => item.Jobs.Select(job => (job, item.Slot, item)))
                    .GroupBy(x => (x.job, x.Slot), x => x.item)
                    .ToDictionary(
                        x => (x.Key.job, x.Key.Slot),
                        x => x.OrderByDescending(i => i.ItemLevel).ThenBy(i => i.Id).ToList());

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
                    slotIndex: index,
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

        public GearItem? FindBestCurrentItem(string job, string slot, int targetItemId)
        {
            if (!slotIndex.TryGetValue((job, slot), out var options) || options.Count == 0)
                return null;

            return options.FirstOrDefault(i => i.Id != targetItemId) ?? options[0];
        }

        private static PlannerData Empty()
            => new(
                isReady: false,
                dataVersion: "unknown",
                gamePatch: "unknown",
                breakpointStep: 21,
                itemsById: new Dictionary<int, GearItem>(),
                slotIndex: new Dictionary<(string Job, string Slot), List<GearItem>>(),
                bisTargets: new Dictionary<string, BisTarget>(StringComparer.OrdinalIgnoreCase),
                availableJobs: []);
    }

    private sealed class SearchNode(ulong mask, double score, List<PlannerUpgradeAction> actions)
    {
        public ulong Mask { get; } = mask;
        public double Score { get; } = score;
        public List<PlannerUpgradeAction> Actions { get; } = actions;
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
    public IReadOnlyDictionary<string, GearItem> CurrentGear { get; init; } = new Dictionary<string, GearItem>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, GearItem> TargetGear { get; init; } = new Dictionary<string, GearItem>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> CurrentStats { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> TargetStats { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int MatchingSlots { get; init; }
    public int TotalTargetSlots { get; init; }
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
