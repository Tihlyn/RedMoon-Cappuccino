using Rmc.GameData;

namespace Rmc.Engine;

/// <summary>
/// A character's gear as the plugin reads it (and as the API request carries it): item ids,
/// HQ flags, melds in slot order, food, clan and party bonus. No names.
/// </summary>
public sealed class GearsetInput
{
    public string Job { get; set; } = string.Empty;
    public int Level { get; set; } = 100;
    /// <summary>Tribe row id (clan); null applies no racial modifiers.</summary>
    public int? ClanId { get; set; }
    /// <summary>Unique roles in the party, 0–5 (+1% main stat and vitality each).</summary>
    public int PartyBonus { get; set; } = 5;
    public FoodInput? Food { get; set; }
    public Dictionary<string, PieceInput> Slots { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PieceInput
{
    public int ItemId { get; set; }
    /// <summary>Null means "HQ if the item can be HQ", the community planners' default.</summary>
    public bool? Hq { get; set; }
    /// <summary>Melds in slot order: the game applies each against what's left under the cap.</summary>
    public List<MeldInput> Materia { get; set; } = [];
    /// <summary>Player-assigned substats on customizable relic weapons.</summary>
    public Dictionary<string, int>? RelicStats { get; set; }
}

public sealed class MeldInput
{
    public string Stat { get; set; } = string.Empty;
    public int Grade { get; set; }
}

public sealed class FoodInput
{
    public int ItemId { get; set; }
    public bool Hq { get; set; } = true;
}

public static class GearsetInputs
{
    /// <summary>A BiS set as engine input (HQ wherever possible, HQ food), the way xivgear evaluates it.</summary>
    public static GearsetInput FromBis(BisMeta meta, BisSet set) => new()
    {
        Job = meta.Job,
        Level = meta.Level,
        ClanId = set.ClanId,
        PartyBonus = meta.PartyBonus,
        Food = set.FoodItemId is int food ? new FoodInput { ItemId = food } : null,
        Slots = set.Slots.ToDictionary(
            kv => kv.Key,
            kv => new PieceInput
            {
                ItemId = kv.Value.ItemId,
                Materia = kv.Value.Materia.Select(m => new MeldInput { Stat = m.Stat, Grade = m.Grade }).ToList(),
                RelicStats = kv.Value.RelicStats,
            },
            StringComparer.Ordinal),
    };
}
