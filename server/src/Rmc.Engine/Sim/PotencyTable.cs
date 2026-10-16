using Rmc.GameData;

namespace Rmc.Engine.Sim;

/// <summary>
/// A job's potencies as the game's tooltips state them (data/potencies/&lt;JOB&gt;.json), keyed by
/// action id and tooltip label. Written by "rmc-extract refresh-potencies" on patch day; the
/// rotations' in-code values are only defaults.
/// </summary>
public sealed class PotencyTable
{
    public string Job { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public List<PotencyEntry> Entries { get; set; } = [];

    public static string Key(uint actionId, string label) => $"{actionId}|{label}";

    public static PotencyTable? Load(string directory, string job)
    {
        var path = Path.Combine(directory, $"{job.ToUpperInvariant()}.json");
        return File.Exists(path) ? GameDataJson.Read<PotencyTable>(path) : null;
    }

    /// <summary>Overwrites the rotation's potencies with the table's; returns entries it had no ability for.</summary>
    public IReadOnlyList<PotencyEntry> ApplyTo(Rotation rotation)
    {
        var byKey = Entries.ToDictionary(e => Key(e.ActionId, e.Label));
        var used = new HashSet<string>();
        foreach (var a in rotation.Abilities)
        {
            if (a.ActionId != 0 && byKey.TryGetValue(Key(a.ActionId, a.PotencyLabel), out var e))
            {
                a.Potency = e.Potency;
                used.Add(e.Key);
            }
            if (a.Dot is { ActionId: not 0 } dot && byKey.TryGetValue(Key(dot.ActionId, dot.PotencyLabel), out var d))
            {
                dot.Potency = d.Potency;
                used.Add(d.Key);
            }
        }
        return Entries.Where(e => !used.Contains(e.Key)).ToList();
    }
}

public sealed class PotencyEntry
{
    public string Name { get; set; } = string.Empty;
    public uint ActionId { get; set; }
    public string Label { get; set; } = "base";
    public int Potency { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => PotencyTable.Key(ActionId, Label);
}
