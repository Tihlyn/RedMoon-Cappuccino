using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rmc.GameData;

public static class GameDataJson
{
    /// <summary>Bump when a consumer would misread an older snapshot.</summary>
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Item names carry apostrophes and accented letters; keep them readable in diffs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
           ?? throw new InvalidDataException($"{path} is empty.");

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options) + "\n");
    }

    public static GameDataSnapshot ReadSnapshot(string path)
    {
        var snapshot = Read<GameDataSnapshot>(path);
        if (snapshot.Meta.SchemaVersion != SchemaVersion)
            throw new InvalidDataException(
                $"{path} has schema version {snapshot.Meta.SchemaVersion}; this build reads {SchemaVersion}. Re-run the extractor.");
        return snapshot;
    }
}
