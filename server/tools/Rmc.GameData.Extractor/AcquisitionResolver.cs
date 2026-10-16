using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;
using LuminaGameData = Lumina.GameData;

namespace Rmc.GameData.Extractor;

/// <summary>
/// Derives each item's <see cref="AcquisitionSource"/> from the game files:
/// recipes → Crafted; exchange shops (SpecialShop) → the type of the currency that buys it;
/// augment exchanges → the base item's type plus the augment materials.
/// </summary>
/// <remarks>
/// Only currencies need naming (overlays/currencies.json, regexes on the currency's item name),
/// and names repeat tier to tier within an expansion, so a new tier usually needs no edits.
/// Currencies no rule matches are collected in <see cref="UnclassifiedCurrencies"/> so patch day
/// fails loudly instead of quietly producing "Unknown".
/// </remarks>
public sealed class AcquisitionResolver
{
    private const string AugmentedPrefix = "Augmented ";
    private const byte ScripCurrencyType = 16;

    private readonly LuminaGameData game;
    private readonly HashSet<uint> crafted;
    private readonly Dictionary<uint, List<Offer>> offersByItem = [];
    private readonly Dictionary<string, uint> itemIdByName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> tomestoneNameByIndex = [];
    private readonly List<(Regex Match, string Type)> currencyRules;
    private readonly List<(Regex Match, string Type)> itemRules;
    private readonly Dictionary<int, AcquisitionSource> overrides;
    private readonly Dictionary<uint, AcquisitionSource> memo = [];

    /// <summary>Currency name → number of exported items it buys, for currencies no rule matched.</summary>
    public Dictionary<string, int> UnclassifiedCurrencies { get; } = new(StringComparer.Ordinal);

    private sealed record Offer(string ShopName, byte CurrencyType, List<(uint ItemId, uint Amount)> Costs);

    public AcquisitionResolver(LuminaGameData game, IEnumerable<uint> craftedItems, string overlayDirectory)
    {
        this.game = game;
        crafted = craftedItems.ToHashSet();

        var rules = GameDataJson.Read<CurrencyRules>(Path.Combine(overlayDirectory, "currencies.json"));
        currencyRules = rules.Currencies.Select(r => (new Regex(r.Match, RegexOptions.CultureInvariant), r.Type)).ToList();
        itemRules = rules.Items.Select(r => (new Regex(r.Match, RegexOptions.CultureInvariant), r.Type)).ToList();

        var overridesPath = Path.Combine(overlayDirectory, "overrides.json");
        overrides = File.Exists(overridesPath)
            ? GameDataJson.Read<Dictionary<string, AcquisitionSource>>(overridesPath).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value)
            : [];

        foreach (var t in game.GetExcelSheet<TomestonesItem>()!)
        {
            if (t.Tomestones.RowId != 0 && t.Item.ValueNullable is { } item)
                tomestoneNameByIndex[t.Tomestones.RowId] = item.Name.ExtractText();
        }

        foreach (var item in game.GetExcelSheet<Item>()!)
        {
            if (item.EquipSlotCategory.RowId != 0)
                itemIdByName.TryAdd(item.Name.ExtractText(), item.RowId);
        }

        foreach (var shop in game.GetExcelSheet<SpecialShop>()!)
        {
            var shopName = shop.Name.ExtractText();
            foreach (var entry in shop.Item)
            {
                var costs = entry.ItemCosts.Where(c => c.ItemCost.RowId != 0).Select(c => (c.ItemCost.RowId, c.CurrencyCost)).ToList();
                if (costs.Count == 0)
                    continue;
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId == 0)
                        continue;
                    if (!offersByItem.TryGetValue(receive.Item.RowId, out var list))
                        offersByItem[receive.Item.RowId] = list = [];
                    list.Add(new Offer(shopName, shop.UseCurrencyType, costs));
                }
            }
        }
    }

    public AcquisitionSource Resolve(uint itemId) => Resolve(itemId, depth: 0);

    private AcquisitionSource Resolve(uint itemId, int depth)
    {
        if (memo.TryGetValue(itemId, out var known))
            return known;
        var result = ResolveUncached(itemId, depth);
        memo[itemId] = result;
        return result;
    }

    private AcquisitionSource ResolveUncached(uint itemId, int depth)
    {
        if (overrides.TryGetValue((int)itemId, out var pinned))
            return pinned;
        if (crafted.Contains(itemId))
            return new AcquisitionSource { Type = SourceTypes.Crafted };

        var item = game.GetExcelSheet<Item>()!.GetRow(itemId);
        var name = item.Name.ExtractText();
        var offers = offersByItem.GetValueOrDefault(itemId) ?? [];

        // Augments: the exchange takes the base item (or, for crafted bases, only the materials,
        // with the base named "Augmented <base>").
        if (depth < 8)
        {
            foreach (var offer in offers)
            {
                var gearCost = offer.Costs.FirstOrDefault(c => IsGear(c.ItemId));
                uint baseId = gearCost.ItemId;
                if (baseId == 0 && name.StartsWith(AugmentedPrefix, StringComparison.Ordinal))
                    baseId = itemIdByName.GetValueOrDefault(name[AugmentedPrefix.Length..]);
                if (baseId == 0 || baseId == itemId)
                    continue;

                var baseSource = Resolve(baseId, depth + 1);
                return new AcquisitionSource
                {
                    Type = baseSource.Type,
                    Detail = offer.ShopName.Length > 0 ? offer.ShopName : baseSource.Detail,
                    Augment = new AugmentInfo
                    {
                        BaseItemId = (int)baseId,
                        Materials = offer.Costs.Where(c => c.ItemId != baseId && !IsGear(c.ItemId))
                            .Select(c => Cost(c.ItemId, c.Amount, offer.CurrencyType)).ToList(),
                    },
                };
            }
        }

        // Plain exchanges: the currency's type is the item's type.
        foreach (var offer in offers)
        {
            var costs = offer.Costs.Select(c => Cost(c.ItemId, c.Amount, offer.CurrencyType)).ToList();
            var typed = costs.FirstOrDefault(c => c.Type != SourceTypes.Unknown);
            if (typed != null)
            {
                // Ultimate weapons are bought with one totem each; trial weapons cost ten.
                var type = typed.Type == SourceTypes.Trial && typed.Amount == 1 && IsWeaponSlot(item)
                    ? SourceTypes.Ultimate
                    : typed.Type;
                return new AcquisitionSource { Type = type, Detail = NullIfEmpty(offer.ShopName) ?? typed.Name, Costs = costs };
            }
            foreach (var c in costs)
                UnclassifiedCurrencies[c.Name] = UnclassifiedCurrencies.GetValueOrDefault(c.Name) + 1;
        }

        foreach (var (match, type) in itemRules)
        {
            if (match.IsMatch(name))
                return new AcquisitionSource { Type = type, Detail = "name rule" };
        }

        // Nothing sells it: infer from what kind of drop it is. Dungeon gear is green (rarity 2);
        // alliance raid gear is blue (rarity 3) left-side armor that no exchange offers. Savage,
        // normal raid and trial gear never get here: their tokens buy it.
        if (offers.Count == 0)
        {
            if (item.Rarity == 2)
                return new AcquisitionSource { Type = SourceTypes.Dungeon, Detail = "inferred: green drop, not sold" };
            if (item.Rarity == 3 && item.EquipSlotCategory.ValueNullable is { } esc &&
                (esc.Head == 1 || esc.Body == 1 || esc.Gloves == 1 || esc.Legs == 1 || esc.Feet == 1))
                return new AcquisitionSource { Type = SourceTypes.AllianceRaid, Detail = "inferred: blue armor drop, not sold" };
        }

        return new AcquisitionSource();
    }

    /// <summary>A currency or material: its type, and every exchange that gives one (no gear-for-gear trades).</summary>
    public CurrencyInfo DescribeCurrency(CurrencyCost cost)
    {
        var info = new CurrencyInfo { ItemId = cost.ItemId, Name = cost.Name, Type = cost.Type };
        if (cost.ItemId == 0)
            return info;
        foreach (var offer in offersByItem.GetValueOrDefault((uint)cost.ItemId) ?? [])
        {
            if (offer.Costs.Any(c => IsGear(c.ItemId)))
                continue;
            var costs = offer.Costs.Select(c => Cost(c.ItemId, c.Amount, offer.CurrencyType)).ToList();
            if (!info.Exchanges.Any(e => e.Count == costs.Count && e.Zip(costs).All(x => x.First.Name == x.Second.Name && x.First.Amount == x.Second.Amount)))
                info.Exchanges.Add(costs);
        }
        return info;
    }

    private static bool IsWeaponSlot(Item item)
        => item.EquipSlotCategory.ValueNullable is { } esc && (esc.MainHand == 1 || esc.OffHand == 1);

    private bool IsGear(uint itemId)
        => itemId >= 100 && game.GetExcelSheet<Item>()!.GetRowOrDefault(itemId) is { } i && i.EquipSlotCategory.RowId != 0;

    private CurrencyCost Cost(uint itemId, uint amount, byte currencyType)
    {
        // Small ids are currency indices, not items: scrips in scrip shops, tomestones elsewhere.
        if (itemId < 100)
        {
            if (currencyType == ScripCurrencyType)
                return new CurrencyCost { Name = "Scrip", Amount = (int)amount, Type = SourceTypes.Scrip };
            return tomestoneNameByIndex.TryGetValue(itemId, out var tome)
                ? new CurrencyCost { Name = tome, Amount = (int)amount, Type = SourceTypes.Tome }
                : new CurrencyCost { Name = $"currency #{itemId}", Amount = (int)amount };
        }

        var name = game.GetExcelSheet<Item>()!.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item #{itemId}";
        var type = currencyRules.FirstOrDefault(r => r.Match.IsMatch(name)).Type ?? SourceTypes.Unknown;
        return new CurrencyCost { ItemId = (int)itemId, Name = name, Amount = (int)amount, Type = type };
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private sealed class CurrencyRules
    {
        public List<Rule> Currencies { get; set; } = [];
        public List<Rule> Items { get; set; } = [];
    }

    private sealed class Rule
    {
        public string Match { get; set; } = string.Empty;
        public string Type { get; set; } = SourceTypes.Unknown;
    }
}
