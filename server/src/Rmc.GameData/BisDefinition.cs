namespace Rmc.GameData;

/// <summary>
/// A BiS sheet we keep ourselves: one job, one or more sets, each with items, melds and food.
/// Imported once from a community source and committed. The server never fetches these at runtime.
/// </summary>
public sealed class BisSheet
{
    public BisMeta Meta { get; set; } = new();
    public List<BisSet> Sets { get; set; } = [];
}

public sealed class BisMeta
{
    public string Job { get; set; } = string.Empty;
    public int Level { get; set; }
    public string SheetName { get; set; } = string.Empty;
    public int PartyBonus { get; set; }
    /// <summary>Patch of the game data snapshot the sheet was validated against at import.</summary>
    public string DataPatch { get; set; } = string.Empty;
    public BisImportSource Source { get; set; } = new();
}

public sealed class BisImportSource
{
    /// <summary>"xivgear-static" (static-bis-sets repo), "xivgear-api" or "manual".</summary>
    public string Kind { get; set; } = "manual";
    public string Reference { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; }
    /// <summary>Upstream timestamp, when the source has one.</summary>
    public long? UpstreamTimestamp { get; set; }
}

public sealed class BisSet
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? FoodItemId { get; set; }
    /// <summary>Clan (Tribe row id) the set was built for; racial modifiers shift main stats by a few points.</summary>
    public int? ClanId { get; set; }
    public Dictionary<string, BisSlot> Slots { get; set; } = new(StringComparer.Ordinal);
}

public sealed class BisSlot
{
    public int ItemId { get; set; }
    public List<BisMeld> Materia { get; set; } = [];
    /// <summary>Custom substats for relic weapons whose stats the player assigns.</summary>
    public Dictionary<string, int>? RelicStats { get; set; }
}

public sealed class BisMeld
{
    public string Stat { get; set; } = string.Empty;
    public int Grade { get; set; }
    public int ItemId { get; set; }
}
