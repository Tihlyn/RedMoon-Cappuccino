using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rmc.GameData.Extractor;

/// <summary>
/// Converts xivgear sheet exports into our <see cref="BisSheet"/> format. Dev-time only: the
/// output is committed and the server never fetches BiS itself.
/// </summary>
/// <remarks>
/// The same parser handles the static-bis-sets repo (raw GitHub file), api.xivgear.app
/// /basedata (for sheets only published as links, e.g. healers) and a local file.
/// </remarks>
public sealed class BisImporter(GameDataIndex data, TextWriter log)
{
    private const string StaticRepoRaw = "https://raw.githubusercontent.com/xiv-gear-planner/static-bis-sets/main/";
    private const string ApiBaseData = "https://api.xivgear.app/basedata?url=";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly IReadOnlyDictionary<string, string> SlotMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Weapon"] = Slots.Weapon, ["OffHand"] = Slots.OffHand, ["Head"] = Slots.Head, ["Body"] = Slots.Body,
        ["Hand"] = Slots.Hands, ["Legs"] = Slots.Legs, ["Feet"] = Slots.Feet, ["Ears"] = Slots.Ears,
        ["Neck"] = Slots.Neck, ["Wrist"] = Slots.Wrists, ["RingLeft"] = Slots.RingLeft, ["RingRight"] = Slots.RingRight,
    };

    private static readonly IReadOnlyDictionary<string, string> RelicStatMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["crit"] = Stats.Critical, ["dhit"] = Stats.DirectHit, ["determination"] = Stats.Determination,
        ["skillspeed"] = Stats.SkillSpeed, ["spellspeed"] = Stats.SpellSpeed, ["tenacity"] = Stats.Tenacity,
        ["piety"] = Stats.Piety,
    };

    /// <summary>Imports <c>&lt;job&gt;/&lt;sheet&gt;.json</c> from the static-bis-sets repo.</summary>
    public async Task<BisSheet?> ImportStaticAsync(string job, string sheet)
    {
        var reference = $"{job.ToLowerInvariant()}/{sheet}.json";
        var json = await FetchAsync(StaticRepoRaw + reference);
        if (json == null)
        {
            log.WriteLine($"{job}: no '{sheet}' sheet in static-bis-sets; import it from its xivgear link instead (import-bis-url).");
            return null;
        }
        return Convert(job, json, "xivgear-static", reference);
    }

    /// <summary>
    /// Imports xivgear links (sheets or single sets) through api.xivgear.app/basedata. Several links
    /// become one sheet: some jobs publish one link per GCD tier instead of a single sheet.
    /// </summary>
    public async Task<BisSheet?> ImportUrlsAsync(string job, IReadOnlyList<string> xivgearUrls)
    {
        BisSheet? merged = null;
        foreach (var url in xivgearUrls)
        {
            var json = await FetchAsync(ApiBaseData + Uri.EscapeDataString(url));
            if (json == null)
            {
                log.WriteLine($"{job}: xivgear returned nothing for {url}.");
                return null;
            }
            var sheet = Convert(job, json, "xivgear-api", url);
            if (sheet == null)
                return null;
            if (merged == null)
            {
                merged = sheet;
                continue;
            }
            merged.Sets.AddRange(sheet.Sets);
            merged.Meta.Source.Reference += "," + url;
            if (sheet.Meta.Source.UpstreamTimestamp > merged.Meta.Source.UpstreamTimestamp)
                merged.Meta.Source.UpstreamTimestamp = sheet.Meta.Source.UpstreamTimestamp;
        }
        return merged;
    }

    public BisSheet? ImportFile(string job, string path)
        => Convert(job, File.ReadAllText(path), "manual", Path.GetFileName(path));

    private static async Task<string?> FetchAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
    }

    private BisSheet? Convert(string job, string json, string kind, string reference)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Sheet JSON is empty.");
        var sheetJob = root["job"]?.GetValue<string>() ?? job;
        if (!string.Equals(sheetJob, job, StringComparison.OrdinalIgnoreCase))
        {
            log.WriteLine($"{job}: sheet is for {sheetJob}, refusing to import it as {job}.");
            return null;
        }

        var result = new BisSheet
        {
            Meta = new BisMeta
            {
                Job = job.ToUpperInvariant(),
                Level = root["level"]?.GetValue<int>() ?? data.Snapshot.Meta.MaxLevel,
                SheetName = root["name"]?.GetValue<string>() ?? string.Empty,
                PartyBonus = root["partyBonus"]?.GetValue<int>() ?? 5,
                DataPatch = data.Snapshot.Meta.Patch,
                Source = new BisImportSource
                {
                    Kind = kind,
                    Reference = reference,
                    ImportedAtUtc = DateTime.UtcNow,
                    UpstreamTimestamp = root["timestamp"]?.GetValue<long>(),
                },
            },
        };

        // xivgear keeps the race on the sheet; we keep it per set so merged sheets stay exact.
        int? clanId = null;
        if (root["race"]?.GetValue<string>() is { Length: > 0 } race)
        {
            clanId = data.Snapshot.Clans.FirstOrDefault(c => NormalizeClan(c.Name) == NormalizeClan(race))?.Id;
            if (clanId == null)
            {
                log.WriteLine($"{job}: unknown race '{race}' in the sheet.");
                return null;
            }
        }

        // A single-set export has the set's fields at the top level.
        var sets = root["sets"] is JsonArray arr ? arr.OfType<JsonObject>() : [root];

        var errors = new List<string>();
        foreach (var set in sets)
        {
            if (set["isSeparator"]?.GetValue<bool>() == true || set["items"] is not JsonObject items)
                continue;

            var name = set["name"]?.GetValue<string>() ?? $"Set {result.Sets.Count + 1}";
            var bisSet = new BisSet
            {
                Name = name,
                Description = NullIfBlank(set["description"]?.GetValue<string>()),
                FoodItemId = set["food"]?.GetValue<int>() is int food and > 0 ? food : null,
                ClanId = clanId,
            };

            foreach (var (slotName, node) in items)
            {
                if (node is not JsonObject slotNode || slotNode["id"]?.GetValue<int>() is not int itemId || itemId <= 0)
                    continue;
                if (!SlotMap.TryGetValue(slotName, out var slot))
                {
                    errors.Add($"{name}: unknown xivgear slot '{slotName}'.");
                    continue;
                }

                var bisSlot = new BisSlot { ItemId = itemId };
                if (slotNode["materia"] is JsonArray melds)
                {
                    foreach (var meld in melds.OfType<JsonObject>())
                    {
                        if (meld["id"]?.GetValue<int>() is not int materiaItem || materiaItem <= 0)
                            continue;
                        if (data.MateriaByItemId(materiaItem) is not { } m)
                        {
                            errors.Add($"{name} {slot}: materia item {materiaItem} isn't a combat materia in the snapshot.");
                            continue;
                        }
                        bisSlot.Materia.Add(new BisMeld { Stat = m.Materia.Stat, Grade = m.Grade.Grade, ItemId = materiaItem });
                    }
                }

                if (slotNode["relicStats"] is JsonObject relic)
                {
                    var stats = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (k, v) in relic)
                    {
                        if (v?.GetValue<int>() is int value && value > 0 && RelicStatMap.TryGetValue(k, out var key))
                            stats[key] = value;
                    }
                    if (stats.Count > 0)
                        bisSlot.RelicStats = stats;
                }

                bisSet.Slots[slot] = bisSlot;
            }

            foreach (var issue in BisValidator.Validate(data, result.Meta.Job, bisSet))
            {
                if (issue.IsError)
                    errors.Add(issue.Message);
                else
                    log.WriteLine($"{job} warning: {issue.Message}");
            }
            result.Sets.Add(bisSet);
        }

        if (errors.Count > 0)
        {
            log.WriteLine($"{job}: import rejected ({errors.Count} problems):");
            foreach (var e in errors.Distinct())
                log.WriteLine($"  - {e}");
            return null;
        }

        if (result.Sets.Count == 0)
        {
            log.WriteLine($"{job}: sheet has no sets.");
            return null;
        }

        return result;
    }

    /// <summary>xivgear writes clans in the plural ("Seekers of the Sun"), the game mostly doesn't.</summary>
    private static string NormalizeClan(string name)
        => string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd('s')));

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
