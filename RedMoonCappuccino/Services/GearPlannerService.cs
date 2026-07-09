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
                    var isHq = rawId >= 1_000_000;
                    var baseId = isHq ? rawId - 1_000_000 : rawId;
                    result[slotKey] = data.GetItemById(baseId) ?? ResolveEquippedFromGameData(baseId, isHq, slotKey);
                }
            }
        }
        catch { /* Best-effort: return whatever was collected before the failure. */ }
        return result;
    }

    // Maps Lumina BaseParam row IDs to the stat keys used by the planner gear DB.
    private static readonly IReadOnlyDictionary<uint, string> BaseParamToStatKey =
        new Dictionary<uint, string>
        {
            [1] = "str", [2] = "dex", [3] = "vit", [4] = "int", [5] = "mnd", [6] = "pie",
            [19] = "ten", [22] = "dh", [27] = "crit", [44] = "det", [45] = "sks", [46] = "sps",
        };

    /// <summary>
    /// Fallback for equipped items that are not in the planner gear DB (crafted, older content, ...):
    /// resolves name, item level and stats from the game's own data so the plan can show what is
    /// actually being replaced instead of "unknown item".
    /// </summary>
    private static GearItem? ResolveEquippedFromGameData(int itemId, bool isHq, string slotKey)
    {
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault((uint)itemId);
            if (row == null || row.Value.RowId == 0)
                return null;

            var item = row.Value;
            var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < item.BaseParam.Count; i++)
            {
                if (BaseParamToStatKey.TryGetValue(item.BaseParam[i].RowId, out var key))
                    stats[key] = stats.GetValueOrDefault(key) + item.BaseParamValue[i];
            }

            if (isHq)
            {
                for (var i = 0; i < item.BaseParamSpecial.Count; i++)
                {
                    if (BaseParamToStatKey.TryGetValue(item.BaseParamSpecial[i].RowId, out var key))
                        stats[key] = stats.GetValueOrDefault(key) + item.BaseParamValueSpecial[i];
                }
            }

            var name = item.Name.ExtractText();
            return new GearItem
            {
                Id = itemId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Item #{itemId}" : name,
                Slot = slotKey,
                ItemLevel = (int)item.LevelItem.RowId,
                SourceType = "Equipped",
                Stats = stats,
            };
        }
        catch
        {
            return null;
        }
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

        // The solver often finds several orderings of the same purchases with (near-)identical
        // scores; only surface paths that begin with a different purchase so alternatives are
        // meaningful rather than shuffled duplicates.
        var ranked = new List<PlannerPathRecommendation>();
        var seenFirstSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in completePaths.OrderByDescending(p => p.Score))
        {
            var firstSlot = path.Actions.Count > 0 ? path.Actions[0].Slot : string.Empty;
            if (!seenFirstSlots.Add(firstSlot))
                continue;

            ranked.Add(new PlannerPathRecommendation
            {
                Rank = ranked.Count + 1,
                TotalUtility = path.Score,
                Upgrades = path.Actions,
                Summary = BuildPathSummary(path),
            });

            if (ranked.Count >= 3)
                break;
        }

        return ranked;
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

        var (costText, detailNote) = DescribeAcquisition(slot, current, target, tomeCost, bookCost);

        return new PlannerUpgradeAction
        {
            Slot = slot,
            SlotName = PlannerDisplay.SlotName(slot),
            CurrentItemId = current?.Id ?? 0,
            CurrentItemName = current?.Name,
            CurrentItemLevel = current?.ItemLevel ?? 0,
            TargetItemId = target.Id,
            TargetItemName = target.Name,
            TargetItemLevel = target.ItemLevel,
            SourceType = target.SourceType,
            CostText = costText,
            DetailNote = detailNote,
            CrossesSpeedBreakpoint = breakpointBonus > 0,
            StatGains = PlannerDisplay.StatDelta(current?.Stats ?? EmptyStats, target.Stats),
            EstimatedTomeCost = tomeCost,
            EstimatedBookCost = bookCost,
            UtilityScore = utility,
        };
    }

    /// <summary>
    /// Composes the single-line acquisition cost shown next to an upgrade step, plus an
    /// optional longer note for the step tooltip (augment currency details, alternatives).
    /// </summary>
    private static (string CostText, string? DetailNote) DescribeAcquisition(string slot, GearItem? current, GearItem target, int tomeCost, int bookCost)
    {
        string costText;
        var notes = new List<string>();

        if (target.SourceType == "AllianceRaid" && target.ItemLevel == 780)
        {
            var (rain, certs) = AllianceRaidAugmentCost(slot);
            var mats = $"{rain}\u00d7 Treno Rain + {certs}\u00d7 Everkeep Cert.";
            var ownsBasePiece = current is { SourceType: "AllianceRaid", ItemLevel: 770 };
            costText = ownsBasePiece ? $"Augment: {mats}" : $"i770 alliance piece + {mats}";
            if (!ownsBasePiece)
                notes.Add("The i770 base piece drops in the alliance raid and is then augmented to i780.");
        }
        else if (target.SourceType == "Tome" && target.ItemLevel == 790)
        {
            var augmentItem = TomeAugmentItemName(slot);
            var ownsBasePiece = current is { SourceType: "Tome", ItemLevel: 780 };
            costText = ownsBasePiece
                ? $"Augment: 1\u00d7 {augmentItem}"
                : $"{tomeCost:N0} tomes + 1\u00d7 {augmentItem}";
            notes.Add(TomeAugmentDetail(slot));
        }
        else
        {
            costText = target.SourceType switch
            {
                "Tome"         => $"{tomeCost:N0} tomes",
                "Savage"       => $"{bookCost} savage books",
                "AllianceRaid" => "Alliance raid drop",
                "Raid"         => "Normal raid drop",
                "Trial"        => "Trial drop",
                "Ultimate"     => "Ultimate drop",
                _              => target.SourceType,
            };
        }

        // The equipped piece has its own augment route; surface it as an alternative
        // instead of mixing it into the recommendation itself.
        if (current is { SourceType: "AllianceRaid", ItemLevel: 770 } &&
            !(target.SourceType == "AllianceRaid" && target.ItemLevel == 780))
        {
            var (rain, certs) = AllianceRaidAugmentCost(slot);
            notes.Add($"Alternative: augment your current {current.Name} to i780 for {rain}\u00d7 Treno Rain + {certs}\u00d7 Everkeep Certificate.");
        }

        return (costText, notes.Count > 0 ? string.Join("\n", notes) : null);
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
            return "Already at BiS for all tracked slots.";

        var parts = new List<string>
        {
            path.Actions.Count == 1 ? "1 upgrade" : $"{path.Actions.Count} upgrades",
        };

        var tomes = path.Actions.Sum(a => a.EstimatedTomeCost);
        if (tomes > 0)
            parts.Add($"{tomes:N0} tomes");

        var books = path.Actions.Sum(a => a.EstimatedBookCost);
        if (books > 0)
            parts.Add($"{books} savage books");

        var augments = path.Actions.Count(a => a.CostText.StartsWith("Augment", StringComparison.Ordinal));
        if (augments > 0)
            parts.Add(augments == 1 ? "1 augment" : $"{augments} augments");

        return string.Join(" · ", parts);
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

    private static string TomeAugmentItemName(string slot) => slot switch
    {
        "WEAPON"                                                    => "Thundersteeped Solvent",
        "BODY" or "HEAD" or "HANDS" or "LEGS" or "FEET" or "OFFHAND" => "Thundersteeped Twine",
        _                                                           => "Thundersteeped Glaze",
    };

    private static string TomeAugmentDetail(string slot) => slot switch
    {
        "WEAPON" =>
            "Thundersteeped Solvent: 4 M11S books or M12S loot.",
        "BODY" or "HEAD" or "HANDS" or "LEGS" or "FEET" or "OFFHAND" =>
            "Thundersteeped Twine: 4 M11S books, 3,000 Nuts, or M11S loot.",
        _ =>
            "Thundersteeped Glaze: 3 M10S books, 2,000 Nuts, or M10S loot.",
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
    public string SlotName { get; init; } = string.Empty;
    public int CurrentItemId { get; init; }
    /// <summary>Null when the slot is empty.</summary>
    public string? CurrentItemName { get; init; }
    public int CurrentItemLevel { get; init; }
    public int TargetItemId { get; init; }
    public string TargetItemName { get; init; } = string.Empty;
    public int TargetItemLevel { get; init; }
    public string SourceType { get; init; } = string.Empty;
    /// <summary>Single-line acquisition cost, e.g. "825 tomes + 1× Thundersteeped Twine".</summary>
    public string CostText { get; init; } = string.Empty;
    /// <summary>Optional longer note for tooltips (augment currency details, alternatives).</summary>
    public string? DetailNote { get; init; }
    public bool CrossesSpeedBreakpoint { get; init; }
    /// <summary>Per-stat gain of this step (displayable stats only, zero entries omitted).</summary>
    public IReadOnlyDictionary<string, int> StatGains { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int EstimatedTomeCost { get; init; }
    public int EstimatedBookCost { get; init; }
    public double UtilityScore { get; init; }
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

/// <summary>Shared presentation helpers for planner output: slot names, stat labels, stat deltas.</summary>
public static class PlannerDisplay
{
    private static readonly IReadOnlyDictionary<string, string> SlotNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WEAPON"]  = "Weapon",
            ["OFFHAND"] = "Off Hand",
            ["HEAD"]    = "Head",
            ["BODY"]    = "Body",
            ["HANDS"]   = "Hands",
            ["LEGS"]    = "Legs",
            ["FEET"]    = "Feet",
            ["EARS"]    = "Earrings",
            ["NECK"]    = "Necklace",
            ["WRISTS"]  = "Bracelet",
            ["RING_L"]  = "Left Ring",
            ["RING_R"]  = "Right Ring",
        };

    // Display order: major stats first, then substats. Keys match the gear DB.
    private static readonly (string Key, string Label)[] StatOrder =
    {
        ("str", "STR"), ("dex", "DEX"), ("int", "INT"), ("mnd", "MND"), ("vit", "VIT"),
        ("crit", "Crit"), ("det", "Det"), ("dh", "Direct Hit"),
        ("sks", "Skill Speed"), ("sps", "Spell Speed"), ("ten", "Tenacity"), ("pie", "Piety"),
    };

    public static string SlotName(string slotKey)
        => SlotNames.TryGetValue(slotKey, out var name) ? name : slotKey;

    /// <summary>Per-stat difference (to − from), restricted to displayable stats, zero entries omitted.</summary>
    public static IReadOnlyDictionary<string, int> StatDelta(IReadOnlyDictionary<string, int> from, IReadOnlyDictionary<string, int> to)
    {
        var delta = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _) in StatOrder)
        {
            var diff = to.GetValueOrDefault(key) - from.GetValueOrDefault(key);
            if (diff != 0)
                delta[key] = diff;
        }

        return delta;
    }

    /// <summary>Formats a stat delta as "+205 STR · +378 Crit". Empty string when nothing changes.</summary>
    public static string FormatStatDelta(IReadOnlyDictionary<string, int> delta)
    {
        var parts = new List<string>();
        foreach (var (key, label) in StatOrder)
        {
            if (delta.TryGetValue(key, out var value) && value != 0)
                parts.Add($"{(value > 0 ? "+" : "−")}{Math.Abs(value):N0} {label}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Adds a step's stat delta onto a running cumulative total.</summary>
    public static void Accumulate(Dictionary<string, int> total, IReadOnlyDictionary<string, int> delta)
    {
        foreach (var (key, value) in delta)
            total[key] = total.GetValueOrDefault(key) + value;
    }

    public static string SourceLabel(string sourceType) => sourceType switch
    {
        "Tome"         => "Tomestone gear",
        "Savage"       => "Savage raid",
        "AllianceRaid" => "Alliance raid",
        "Raid"         => "Normal raid",
        "Trial"        => "Trial",
        "Ultimate"     => "Ultimate",
        _              => sourceType,
    };
}
