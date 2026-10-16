namespace Rmc.GameData;

/// <summary>Lookups over a snapshot, plus the meld cap formula that needs them.</summary>
public sealed class GameDataIndex
{
    public GameDataSnapshot Snapshot { get; }

    private readonly Dictionary<int, GearItem> items;
    private readonly Dictionary<string, BaseParamInfo> baseParams;
    private readonly Dictionary<int, ItemLevelInfo> itemLevels;
    private readonly Dictionary<int, (MateriaInfo Materia, MateriaGrade Grade)> materiaByItem;
    private readonly Dictionary<int, FoodInfo> foods;
    private readonly Dictionary<string, ClassJobInfo> jobs;

    public GameDataIndex(GameDataSnapshot snapshot)
    {
        Snapshot = snapshot;
        items = snapshot.Items.ToDictionary(i => i.Id);
        baseParams = snapshot.BaseParams.ToDictionary(b => b.Key, StringComparer.Ordinal);
        itemLevels = snapshot.ItemLevels.ToDictionary(l => l.ItemLevel);
        materiaByItem = snapshot.Materia
            .SelectMany(m => m.Grades.Select(g => (m, g)))
            .ToDictionary(x => x.g.ItemId, x => (x.m, x.g));
        foods = snapshot.Foods.ToDictionary(f => f.ItemId);
        jobs = snapshot.ClassJobs.ToDictionary(j => j.Abbreviation, StringComparer.OrdinalIgnoreCase);
    }

    public GearItem? Item(int id) => items.GetValueOrDefault(id);
    public FoodInfo? Food(int itemId) => foods.GetValueOrDefault(itemId);
    public ClassJobInfo? Job(string abbreviation) => jobs.GetValueOrDefault(abbreviation);
    public BaseParamInfo? BaseParam(string stat) => baseParams.GetValueOrDefault(stat);
    public ItemLevelInfo? ItemLevel(int ilvl) => itemLevels.GetValueOrDefault(ilvl);

    public (MateriaInfo Materia, MateriaGrade Grade)? MateriaByItemId(int itemId)
        => materiaByItem.TryGetValue(itemId, out var m) ? m : null;

    /// <summary>
    /// Highest value <paramref name="stat"/> can reach on <paramref name="item"/> through melds,
    /// or null when the stat can't appear on that item at all.
    /// </summary>
    /// <remarks>
    /// cap = round( fl( fl(ilvlMod × meldParam / 100) × slotPct / 100 ) / 10 ), all on
    /// non-negative integers. Off-hands, for stats where 1H% + OH% = 2H%, use cap(2H) − cap(1H)
    /// instead of their own column so the two roundings don't stack (xivgear's finding).
    /// </remarks>
    public int? MeldCap(GearItem item, string stat)
    {
        if (!baseParams.TryGetValue(stat, out var bp) || !itemLevels.TryGetValue(item.ItemLevel, out var il))
            return null;
        var ilvlMod = il.Stats.GetValueOrDefault(stat);
        if (ilvlMod == 0)
            return null;

        var meldParam = item.MeldParamIndex >= 0 && item.MeldParamIndex < bp.MeldParam.Count
            ? bp.MeldParam[item.MeldParamIndex]
            : 100;

        int? CapFor(string capSlot)
            => bp.SlotPercent.TryGetValue(capSlot, out var pct) ? Cap(ilvlMod, meldParam, pct) : null;

        if (item.CapSlot == CapSlots.OffHand)
        {
            var one = bp.SlotPercent.GetValueOrDefault(CapSlots.OneHandWeapon);
            var two = bp.SlotPercent.GetValueOrDefault(CapSlots.TwoHandWeapon);
            var off = bp.SlotPercent.GetValueOrDefault(CapSlots.OffHand);
            if (one + off == two && CapFor(CapSlots.TwoHandWeapon) is { } h2 && CapFor(CapSlots.OneHandWeapon) is { } h1)
                return h2 - h1;
        }

        return CapFor(item.CapSlot);
    }

    public static int Cap(int ilvlModifier, int meldParam, int slotPercent)
    {
        var scaled = ilvlModifier * meldParam / 100 * slotPercent / 100;
        return (scaled + 5) / 10;
    }
}
