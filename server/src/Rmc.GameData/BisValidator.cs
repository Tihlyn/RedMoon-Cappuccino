namespace Rmc.GameData;

public sealed record ValidationIssue(bool IsError, string Message);

/// <summary>
/// Checks a BiS set against the game data: items exist and fit their slot and job, melds are
/// legal for the item, food exists. Overcapped melds are only warnings: they're legal, just wasteful.
/// </summary>
public static class BisValidator
{
    /// <summary>A meld losing more than this to the stat cap is reported (xivgear's threshold).</summary>
    public const int AcceptableOvercapLoss = 2;

    public static IEnumerable<ValidationIssue> Validate(GameDataIndex data, string job, BisSet set)
    {
        var prefix = set.Name;

        if (set.FoodItemId is int foodId && data.Food(foodId) == null)
            yield return new(true, $"{prefix}: food {foodId} is not in the snapshot.");

        var uniqueSeen = new HashSet<int>();
        foreach (var (slot, bisSlot) in set.Slots)
        {
            var item = data.Item(bisSlot.ItemId);
            if (item == null)
            {
                yield return new(true, $"{prefix} {slot}: item {bisSlot.ItemId} is not in the snapshot.");
                continue;
            }

            if (!Slots.Fits(item.Slot, slot))
                yield return new(true, $"{prefix} {slot}: {item.Name} is a {item.Slot} item.");
            if (!item.Jobs.Contains(job, StringComparer.OrdinalIgnoreCase))
                yield return new(true, $"{prefix} {slot}: {job} can't equip {item.Name}.");
            if (item.Unique && !uniqueSeen.Add(item.Id))
                yield return new(true, $"{prefix} {slot}: {item.Name} is unique and already equipped.");

            var allowed = item.AdvancedMelding ? GearRules.MaxMateriaSlots : item.MateriaSlots;
            if (bisSlot.Materia.Count > allowed)
                yield return new(true, $"{prefix} {slot}: {bisSlot.Materia.Count} melds on {item.Name}, which takes {allowed}.");

            // Running stat total so each meld is checked against what's left under the cap.
            var stats = new Dictionary<string, int>(item.CanBeHq ? item.StatsHq : item.Stats, StringComparer.Ordinal);
            if (bisSlot.RelicStats != null)
                foreach (var (k, v) in bisSlot.RelicStats)
                    stats[k] = stats.GetValueOrDefault(k) + v;

            for (var i = 0; i < bisSlot.Materia.Count; i++)
            {
                var meld = bisSlot.Materia[i];
                if (!GearRules.GradeAllowedInSlot(meld.Grade, i, item.MateriaSlots))
                    yield return new(true, $"{prefix} {slot}: grade {meld.Grade} {meld.Stat} can't go in overmeld slot {i + 1}.");

                var value = data.MateriaByItemId(meld.ItemId)?.Grade.Value ?? 0;
                var cap = data.MeldCap(item, meld.Stat);
                var current = stats.GetValueOrDefault(meld.Stat);
                var applied = cap is int c ? Math.Clamp(c - current, 0, value) : value;
                stats[meld.Stat] = current + applied;
                if (value - applied > AcceptableOvercapLoss)
                    yield return new(false, $"{prefix} {slot}: {meld.Stat} meld on {item.Name} loses {value - applied} of {value} to the cap.");
            }
        }
    }
}
