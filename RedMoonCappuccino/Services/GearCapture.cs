using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Inventory;
using Dalamud.Game.Player;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace RedMoonCappuccino.Services;

/// <summary>What the planner server needs about the player, read from the game in one go.</summary>
public sealed class GearCaptureResult
{
    public PlannerGear Gear { get; init; } = new();
    /// <summary>The attribute panel's substats, for the server's parity check. Null while level synced.</summary>
    public Dictionary<string, int>? Panel { get; init; }
    /// <summary>Equipped item names by slot, for the meld panel.</summary>
    public Dictionary<string, string> ItemNames { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads the equipped set the way the planner server takes it: item ids, HQ, melds, food, clan,
/// level, plus the in-game attribute panel and the currencies a plan can spend.
/// Game state: call on the framework thread.
/// </summary>
public static class GearCapture
{
    private const uint WellFedStatusId = 48;
    private const uint MealUiCategory = 46;

    // Substats the server compares with its own computation (party bonus only moves main stats).
    private static readonly (PlayerAttribute Attribute, string Stat)[] PanelStats =
    {
        (PlayerAttribute.CriticalHit, "crit"), (PlayerAttribute.DirectHitRate, "dh"), (PlayerAttribute.Determination, "det"),
        (PlayerAttribute.SkillSpeed, "sks"), (PlayerAttribute.SpellSpeed, "sps"), (PlayerAttribute.Tenacity, "ten"), (PlayerAttribute.Piety, "pie"),
    };

    // Substats a relic can carry on top of its sheet stats (BaseParam row ids).
    private static readonly (uint BaseParam, string Stat)[] RelicSubstats =
    {
        (27, "crit"), (22, "dh"), (44, "det"), (45, "sks"), (46, "sps"), (19, "ten"), (6, "pie"),
    };

    private static Dictionary<uint, int>? foodItemByFoodRow;
    private static readonly HashSet<uint> loggedRelics = new();
    private static Dictionary<string, uint>? itemIdByEnglishName;

    public static GearCaptureResult? Capture(string job, int partyBonus)
    {
        var state = Plugin.PlayerState;
        if (!Plugin.ClientState.IsLoggedIn || !state.IsLoaded)
            return null;

        var gear = new PlannerGear
        {
            Job = job,
            Level = state.Level > 0 ? state.Level : 100,
            ClanId = state.Tribe.RowId > 0 ? (int)state.Tribe.RowId : null,
            PartyBonus = partyBonus,
            Food = ReadFood(),
        };
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var materiaSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>();

        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems))
        {
            if (item.IsEmpty || item.ItemId == 0 ||
                !GearPlannerService.EquipSlotIndexToKey.TryGetValue((int)item.InventorySlot, out var slot))
                continue;

            var piece = new PlannerPiece { ItemId = (int)item.BaseItemId, Hq = item.IsHq };
            var sheetRow = itemSheet.GetRowOrDefault(item.BaseItemId);

            // Relics with adjustable substats take no materia: their chosen stats aren't on the
            // item sheet, so ask the game for the item's actual values and send the difference.
            // Only meld-less pieces: older relics with slots carry their stats on the sheet, and
            // the game's value would count their melds twice.
            if (sheetRow is { MateriaSlotCount: 0 })
            {
                piece.RelicStats = ReadRelicStats(item, sheetRow);
                if (piece.RelicStats != null)
                {
                    gear.Slots[slot] = piece;
                    if (sheetRow?.Name.ExtractText() is { Length: > 0 } relicName)
                        names[slot] = relicName;
                    continue;
                }
            }

            var materia = item.Materia;
            var grades = item.MateriaGrade;
            for (var i = 0; i < materia.Length && i < grades.Length; i++)
            {
                if (materia[i] == 0)
                    continue;
                var row = materiaSheet.GetRowOrDefault(materia[i]);
                if (row == null || !GearPlannerService.BaseParamToStatKey.TryGetValue(row.Value.BaseParam.RowId, out var stat))
                    continue;
                piece.Materia.Add(new PlannerMeld { Stat = stat, Grade = grades[i] + 1 });
            }
            gear.Slots[slot] = piece;

            var name = itemSheet.GetRowOrDefault(item.BaseItemId)?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                names[slot] = name;
        }

        Dictionary<string, int>? panel = null;
        if (!state.IsLevelSynced)
        {
            panel = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (attribute, stat) in PanelStats)
                panel[stat] = state.GetAttribute(attribute);
        }

        return new GearCaptureResult { Gear = gear, Panel = panel, ItemNames = names };
    }

    /// <summary>
    /// A relic's substats above its sheet values, from the game's own per-item stat lookup
    /// (what the item tooltip shows). Null when the item carries none, so it's handled as normal gear.
    /// </summary>
    private static unsafe Dictionary<string, int>? ReadRelicStats(GameInventoryItem item, Lumina.Excel.Sheets.Item? row)
    {
        var native = (InventoryItem*)item.Address;
        if (native == null)
            return null;

        var extra = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (baseParam, stat) in RelicSubstats)
        {
            var actual = (int)InventoryItem.GetParameterValue(baseParam, native, true, item.IsHq, false, false);
            var onSheet = row != null ? SheetStat(row.Value, baseParam, item.IsHq) : 0;
            if (actual > onSheet)
                extra[stat] = actual - onSheet;
        }

        // Once per item: what was read, so a relic that comes out wrong can be checked in /xllog.
        if (loggedRelics.Add(item.BaseItemId))
            Plugin.Log.Information($"[RedMoonCappuccino] Relic {item.BaseItemId} substats: " +
                (extra.Count > 0 ? string.Join(", ", extra.Select(kv => $"{kv.Key} {kv.Value}")) : "none above the sheet") +
                $" (materia [{string.Join(",", item.Materia.ToArray())}], grades [{string.Join(",", item.MateriaGrade.ToArray())}]).");

        return extra.Count > 0 ? extra : null;
    }

    private static int SheetStat(Lumina.Excel.Sheets.Item row, uint baseParam, bool hq)
    {
        var total = 0;
        for (var i = 0; i < row.BaseParam.Count; i++)
            if (row.BaseParam[i].RowId == baseParam)
                total += row.BaseParamValue[i];
        if (hq)
            for (var i = 0; i < row.BaseParamSpecial.Count; i++)
                if (row.BaseParamSpecial[i].RowId == baseParam)
                    total += row.BaseParamValueSpecial[i];
        return total;
    }

    /// <summary>How much of each currency the player holds, for the ones a plan can spend.</summary>
    public static unsafe Dictionary<string, int> ReadWallet(IEnumerable<PlannerCurrency> currencies)
    {
        var wallet = new Dictionary<string, int>(StringComparer.Ordinal);
        var inventory = InventoryManager.Instance();
        if (inventory == null || !Plugin.ClientState.IsLoggedIn)
            return wallet;

        foreach (var currency in currencies)
        {
            var itemId = currency.ItemId > 0 ? (uint)currency.ItemId : ItemIdByEnglishName(currency.Name);
            if (itemId == 0)
                continue;
            var count = currency.Type == "Tome"
                ? (int)inventory->GetTomestoneCount(itemId)
                : inventory->GetInventoryItemCount(itemId, false, true, true, 0);
            if (count > 0)
                wallet[currency.Name] = count;
        }
        return wallet;
    }

    /// <summary>The meal the player is fed with right now (Well Fed's param is the ItemFood row, +10000 when HQ).</summary>
    private static PlannerFood? ReadFood()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;
        foreach (var status in player.StatusList)
        {
            if (status.StatusId != WellFedStatusId)
                continue;
            var param = (uint)status.Param;
            var hq = param >= 10000;
            var foodRow = hq ? param - 10000 : param;
            return FoodItemByFoodRow().TryGetValue(foodRow, out var itemId) ? new PlannerFood { ItemId = itemId, Hq = hq } : null;
        }
        return null;
    }

    // Meals point at their ItemFood row through ItemAction.Data[1]; built once on first use.
    private static Dictionary<uint, int> FoodItemByFoodRow()
    {
        if (foodItemByFoodRow != null)
            return foodItemByFoodRow;
        var map = new Dictionary<uint, int>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>())
        {
            if (item.ItemUICategory.RowId != MealUiCategory || item.ItemAction.ValueNullable is not { } action || action.Data.Count < 2)
                continue;
            map.TryAdd(action.Data[1], (int)item.RowId);
        }
        return foodItemByFoodRow = map;
    }

    // Tomestones have no item id in the server's data, only their (English) name.
    private static uint ItemIdByEnglishName(string name)
    {
        if (itemIdByEnglishName == null)
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>(ClientLanguage.English))
            {
                var text = item.Name.ExtractText();
                if (text.StartsWith("Allagan Tomestone of ", StringComparison.Ordinal))
                    map.TryAdd(text, item.RowId);
            }
            itemIdByEnglishName = map;
        }
        return itemIdByEnglishName.GetValueOrDefault(name);
    }
}
