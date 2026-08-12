using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMoonCappuccino.Models;

/// <summary>
/// Loot / route dataset for submersible voyages, deserialised from
/// <c>Resources/submarine_routes.json</c>.
///
/// The file is the <c>DATA</c> blob of the community "FFXIV Submersible Route
/// Planner" page kept in <c>subs/</c>; to refresh it, copy the object literal
/// out of that page's script tag verbatim. Field names are the original short
/// keys so the two stay diff-able.
/// </summary>
public sealed class SubmarineRouteData
{
    /// <summary>Map names, indexed by <see cref="SubmarineSector.Map"/>.</summary>
    [JsonPropertyName("maps")]    public string[] Maps { get; set; } = Array.Empty<string>();

    /// <summary>Material names, indexed by <see cref="SubmarineDrop.ItemIndex"/>.</summary>
    [JsonPropertyName("names")]   public string[] Names { get; set; } = Array.Empty<string>();

    [JsonPropertyName("sectors")] public SubmarineSector[] Sectors { get; set; } = Array.Empty<SubmarineSector>();

    /// <summary>Stat bonus granted by submarine rank, keyed by rank ("1".."130").</summary>
    [JsonPropertyName("rank")]    public Dictionary<string, int[]> Rank { get; set; } = new();

    /// <summary>Slot name ("Hull".."Bridge") → part name → [surv, retr, speed, range, favor, minRank].</summary>
    [JsonPropertyName("parts")]   public Dictionary<string, Dictionary<string, int[]>> Parts { get; set; } = new();
}

public sealed class SubmarineSector
{
    [JsonPropertyName("n")]  public string Name   { get; set; } = string.Empty;
    /// <summary>Sector letter within its map ("A".."AD").</summary>
    [JsonPropertyName("L")]  public string Letter { get; set; } = string.Empty;
    [JsonPropertyName("m")]  public int    Map    { get; set; }

    /// <summary>
    /// Stat breakpoints: [surveillance for mid tier, surveillance for high tier,
    /// retrieval for normal, retrieval for optimal, favor].
    /// </summary>
    [JsonPropertyName("bp")] public int[] Breakpoints { get; set; } = Array.Empty<int>();

    [JsonPropertyName("hi")] public SubmarineTierBlock High { get; set; } = SubmarineTierBlock.Empty;
    [JsonPropertyName("md")] public SubmarineTierBlock Mid  { get; set; } = SubmarineTierBlock.Empty;
    [JsonPropertyName("lo")] public SubmarineTierBlock Low  { get; set; } = SubmarineTierBlock.Empty;

    /// <summary>Drop pool per loot tier (always three tiers, T1 first).</summary>
    [JsonPropertyName("it")] public SubmarineDrop[][] Items { get; set; } = Array.Empty<SubmarineDrop[]>();

    public int SurveillanceForMid => Breakpoints[0];
    public int SurveillanceForHigh => Breakpoints[1];
    public int RetrievalForNormal => Breakpoints[2];
    public int RetrievalForOptimal => Breakpoints[3];
    public int FavorRequired => Breakpoints[4];

    public SubmarineTierBlock BlockFor(int surveillanceTier) =>
        surveillanceTier == 2 ? High : surveillanceTier == 1 ? Mid : Low;
}

/// <summary>
/// Tier weights for one surveillance level. <see cref="FirstDip"/> is how the
/// guaranteed pull splits across T1/T2/T3, <see cref="FavorDip"/> is the same
/// split for the extra favor pull, and <see cref="FavorProc"/> is how often
/// that extra pull happens once the favor breakpoint is met.
/// </summary>
[JsonConverter(typeof(SubmarineTierBlockConverter))]
public sealed class SubmarineTierBlock
{
    public static readonly SubmarineTierBlock Empty = new();

    public float[] FirstDip  { get; set; } = new float[3];
    public float[] FavorDip  { get; set; } = new float[3];
    public float   FavorProc { get; set; }
}

public sealed class SubmarineDrop
{
    /// <summary>Index into <see cref="SubmarineRouteData.Names"/>.</summary>
    [JsonPropertyName("i")] public int   ItemIndex { get; set; }

    /// <summary>Share of its tier's pulls that land on this material.</summary>
    [JsonPropertyName("p")] public float Chance { get; set; }

    /// <summary>Average quantity per retrieval tier (poor / normal / optimal).</summary>
    [JsonPropertyName("y")] public float[] Yield { get; set; } = new float[3];

    /// <summary>Min/max quantity per retrieval tier, flattened to six values.</summary>
    [JsonPropertyName("r")] public int[] Range { get; set; } = new int[6];

    /// <summary>Sample size behind the numbers, for confidence display.</summary>
    [JsonPropertyName("N")] public int Samples { get; set; }
}

/// <summary>
/// Accepts whole numbers written with a decimal point. The source dataset
/// exports breakpoints and quantity ranges as <c>20.0</c> rather than
/// <c>20</c>, which the stock reader rejects for an integer field.
/// </summary>
public sealed class TolerantIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TryGetInt32(out var value)) return value;
        return (int)Math.Round(reader.GetDouble());
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>
/// Reads the heterogeneous <c>[[f,f,f],[f,f,f],f]</c> tier-block form used by
/// the source dataset.
/// </summary>
public sealed class SubmarineTierBlockConverter : JsonConverter<SubmarineTierBlock>
{
    public override SubmarineTierBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected an array for a submersible tier block.");

        var block = new SubmarineTierBlock
        {
            FirstDip = ReadTriple(ref reader),
            FavorDip = ReadTriple(ref reader),
        };

        reader.Read();
        block.FavorProc = (float)reader.GetDouble();

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Submersible tier block has more than three elements.");

        return block;
    }

    private static float[] ReadTriple(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a weight triple in a submersible tier block.");

        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            reader.Read();
            values[i] = (float)reader.GetDouble();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Weight triple has more than three elements.");

        return values;
    }

    public override void Write(Utf8JsonWriter writer, SubmarineTierBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteTriple(writer, value.FirstDip);
        WriteTriple(writer, value.FavorDip);
        writer.WriteNumberValue(value.FavorProc);
        writer.WriteEndArray();
    }

    private static void WriteTriple(Utf8JsonWriter writer, float[] values)
    {
        writer.WriteStartArray();
        foreach (var v in values) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }
}

/// <summary>
/// A submarine registered to a free company workshop. Cached per workshop so a
/// player in several free companies keeps every boat on the list without having
/// to revisit each workshop.
/// </summary>
[Serializable]
public sealed class SavedSubmarine
{
    public string Name { get; set; } = string.Empty;
    public int    Rank { get; set; }

    /// <summary>Workshop the submarine belongs to; groups boats across free companies.</summary>
    public ulong HouseId { get; set; }

    /// <summary>Human readable workshop location, for the tooltip.</summary>
    public string Workshop { get; set; } = string.Empty;

    /// <summary>Part names per slot (Hull, Stern, Bow, Bridge); empty when unidentified.</summary>
    public string[] Parts { get; set; } = Array.Empty<string>();

    /// <summary>Live stats read from the game: surv, retr, speed, range, favor.</summary>
    public int[] Stats { get; set; } = new int[5];

    /// <summary>Sector letters of the currently plotted voyage, if any.</summary>
    public string[] Route { get; set; } = Array.Empty<string>();

    public DateTime SeenUtc { get; set; }

    /// <summary>True when every slot was matched back to a known part.</summary>
    public bool HasParts => Parts.Length == 4;
}
