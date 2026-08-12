using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Everything the route planner borrows from the running game: sector rows,
/// travel distances, unlock state and the free company's actual submarines.
///
/// All of it is optional. Without a logged-in character, or when a sheet lookup
/// fails, the planner still works off the offline dataset — every accessor here
/// may report "unavailable".
///
/// Game functions are only ever called from the framework thread. Distances are
/// pulled into a lookup table up front so the route search, which asks for tens
/// of thousands of them, never re-enters game code.
/// </summary>
public sealed class SubmarineGameData : IDisposable
{
    private readonly SubmarineRouteService routes;
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // Dataset sector (map index + letter) → SubmarineExploration row id.
    private readonly Dictionary<(int Map, string Letter), uint> sectorRows = new();
    private readonly Dictionary<uint, string> rowLetters = new();
    private readonly Dictionary<int, uint> startPoints = new();
    private readonly Dictionary<uint, int> surveyDistance = new();
    private readonly ConcurrentDictionary<(uint, uint), int> distances = new();

    // Material name → item row id, resolved against the English sheet so the
    // dataset's English names match on every client language.
    private readonly Dictionary<string, uint> itemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> localisedNames = new();
    private readonly Dictionary<int, uint> iconIds = new();
    private readonly Dictionary<(int Map, string Letter), string> sectorNames = new();

    private readonly ConcurrentDictionary<string, TimeSpan> durations = new();
    private readonly HashSet<string> durationsRequested = new();

    private HashSet<uint>? unlockedSectors;
    private long lastPollTick;
    private bool sheetsLoaded;

    private const long PollIntervalMs = 4000;

    /// <summary>Cache ceiling so a player in many free companies cannot grow the config without bound.</summary>
    private const int MaxSavedSubmarines = 24;

    public SubmarineGameData(SubmarineRouteService routes, Configuration configuration,
                             IFramework framework, IPluginLog log)
    {
        this.routes        = routes;
        this.configuration = configuration;
        this.framework     = framework;
        this.log           = log;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;

    /// <summary>Submarines last seen in the free company workshop.</summary>
    public IReadOnlyList<SavedSubmarine> Submarines => configuration.KnownSubmarines;

    /// <summary>True once sector distances are known and voyages can be planned.</summary>
    public bool HasVoyageData { get; private set; }

    /// <summary>
    /// Bumped when game data is mapped, so views can rebuild anything they
    /// cached from the offline names.
    /// </summary>
    public int DataRevision { get; private set; }

    /// <summary>True while the player stands in the workshop, where live data is readable.</summary>
    public bool WorkshopVisible { get; private set; }

    // ── Framework polling ─────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            WorkshopVisible = false;
            return;
        }

        if (!sheetsLoaded)
        {
            sheetsLoaded = true;
            try
            {
                LoadSheets();
                PrecomputeDistances();
                DataRevision++;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[RedMoonCappuccino] Submersible game data mapping failed.");
            }
        }

        try { PollWorkshop(); }
        catch (Exception ex)
        {
            WorkshopVisible = false;
            log.Warning(ex, "[RedMoonCappuccino] Reading workshop submarines failed.");
        }
    }

    // ── Sheet mapping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Matches every dataset sector to its SubmarineExploration row. Sectors are
    /// keyed by map plus the short letter shown in game ("A".."AD"), which is
    /// the same in every client language.
    /// </summary>
    private void LoadSheets()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<SubmarineExploration>();
        if (sheet == null || !routes.IsReady) return;

        // The dataset lists maps in the sheet's own map order, but the sheet is
        // 1-based and could gain rows later, so bind by ordinal position of the
        // maps that exploration rows actually reference.
        var mapOrder = sheet
            .Where(r => r.RowId != 0 && r.Map.RowId != 0)
            .Select(r => r.Map.RowId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Map.RowId == 0) continue;

            var mapIndex = mapOrder.IndexOf(row.Map.RowId);
            if (mapIndex < 0 || mapIndex >= routes.Data.Maps.Length) continue;

            if (row.StartingPoint)
            {
                if (row.RowId <= byte.MaxValue) startPoints[mapIndex] = row.RowId;
                continue;
            }

            var location    = row.Location.ExtractText();
            var destination = row.Destination.ExtractText();

            // One column holds the sector letter and the other its name; which
            // is which has moved between game versions, so go by length.
            var letter = location.Length is > 0 and <= 2 ? location
                       : destination.Length is > 0 and <= 2 ? destination
                       : string.Empty;
            if (letter.Length == 0) continue;

            // The game's voyage helpers take the row id as a byte; anything
            // beyond that is a future sector this build cannot reason about.
            if (row.RowId > byte.MaxValue) continue;

            var name = location.Length > 2 ? location : destination.Length > 2 ? destination : string.Empty;

            sectorRows[(mapIndex, letter)] = row.RowId;
            rowLetters[row.RowId]          = letter;
            surveyDistance[row.RowId]      = row.SurveyDistance;
            if (name.Length > 0) sectorNames[(mapIndex, letter)] = name;
        }

        var matched = routes.Data.Sectors.Count(s => sectorRows.ContainsKey((s.Map, s.Letter)));
        if (matched < routes.Data.Sectors.Length)
            log.Information($"[RedMoonCappuccino] Submersible sectors matched to game data: {matched}/{routes.Data.Sectors.Length}.");

        LoadItemIds();
    }

    private void LoadItemIds()
    {
        var english = Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English);
        if (english == null) return;

        var wanted = new HashSet<string>(routes.Data.Names, StringComparer.OrdinalIgnoreCase);
        foreach (var item in english)
        {
            if (item.RowId == 0) continue;
            var name = item.Name.ExtractText();
            if (name.Length == 0 || !wanted.Contains(name)) continue;
            itemIds.TryAdd(name, item.RowId);
        }
    }

    /// <summary>
    /// Fills the distance table for every pair of sectors that could share a
    /// voyage — same map, plus that map's starting point.
    /// </summary>
    private void PrecomputeDistances()
    {
        if (sectorRows.Count == 0) return;

        try
        {
            foreach (var mapIndex in routes.Data.Sectors.Select(s => s.Map).Distinct())
            {
                var nodes = routes.Data.Sectors
                    .Where(s => s.Map == mapIndex)
                    .Select(s => sectorRows.TryGetValue((s.Map, s.Letter), out var row) ? row : 0u)
                    .Where(row => row != 0)
                    .ToList();

                if (startPoints.TryGetValue(mapIndex, out var start)) nodes.Add(start);

                for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var value = HousingManager.GetSubmarineVoyageDistance((byte)nodes[i], (byte)nodes[j]);
                    distances[Key(nodes[i], nodes[j])] = (int)value;
                }
            }

            HasVoyageData = distances.Count > 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[RedMoonCappuccino] Submersible distance table failed to build.");
        }
    }

    private static (uint, uint) Key(uint a, uint b) => a < b ? (a, b) : (b, a);

    // ── Lookups ───────────────────────────────────────────────────────────────

    /// <summary>Item row id for a dataset material name, or 0 when unmatched.</summary>
    public uint GetItemId(string materialName) =>
        itemIds.TryGetValue(materialName, out var id) ? id : 0u;

    /// <summary>Icon id for a material, or 0 when it has no game item.</summary>
    public uint GetIconId(int itemIndex, string materialName)
    {
        if (iconIds.TryGetValue(itemIndex, out var cached)) return cached;

        // Before the sheets are mapped there is nothing to cache; answering 0
        // now and retrying later is what makes icons appear after login.
        if (itemIds.Count == 0) return 0;

        uint icon = 0;
        var id = GetItemId(materialName);
        if (id != 0)
            icon = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(id)?.Icon ?? 0;

        iconIds[itemIndex] = icon;
        return icon;
    }

    /// <summary>The material's name in the client's language, or the dataset name.</summary>
    public string GetDisplayName(int itemIndex, string fallback)
    {
        if (localisedNames.TryGetValue(itemIndex, out var cached)) return cached;
        if (itemIds.Count == 0) return fallback;

        var name = fallback;
        var id   = GetItemId(fallback);
        if (id != 0)
        {
            var row = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(id);
            if (row is { } value)
            {
                var localised = value.Name.ExtractText();
                if (localised.Length > 0) name = localised;
            }
        }

        localisedNames[itemIndex] = name;
        return name;
    }

    /// <summary>Sector name in the client's language, or the dataset name.</summary>
    public string GetSectorName(SubmarineSector sector) =>
        sectorNames.TryGetValue((sector.Map, sector.Letter), out var name) ? name : sector.Name;

    /// <summary>Submarine rank a sector needs, or null when unknown.</summary>
    public int? GetRankRequirement(SubmarineSector sector)
    {
        if (!sectorRows.TryGetValue((sector.Map, sector.Letter), out var rowId)) return null;
        return Plugin.DataManager.GetExcelSheet<SubmarineExploration>()?.GetRowOrDefault(rowId)?.RankReq;
    }

    /// <summary>
    /// Whether the free company has unlocked a sector, or null while the unlock
    /// table has not been read.
    /// </summary>
    public bool? IsUnlocked(SubmarineSector sector)
    {
        if (unlockedSectors == null) return null;
        if (!sectorRows.TryGetValue((sector.Map, sector.Letter), out var rowId)) return null;
        return unlockedSectors.Contains(rowId);
    }

    // ── Voyage planning ───────────────────────────────────────────────────────

    /// <summary>
    /// Cheapest visiting order for a set of sectors and the range it costs, in
    /// the same units as the submarine's Range stat. Null when the sectors are
    /// not mapped to game data.
    /// </summary>
    public VoyagePlan? PlanVoyage(IReadOnlyList<SubmarineSector> sectors)
    {
        if (sectors.Count == 0 || !HasVoyageData) return null;
        if (!startPoints.TryGetValue(sectors[0].Map, out var start)) return null;

        var rows = new uint[sectors.Count];
        for (var i = 0; i < sectors.Count; i++)
        {
            if (!sectorRows.TryGetValue((sectors[i].Map, sectors[i].Letter), out var row)) return null;
            rows[i] = row;
        }

        var order     = Enumerable.Range(0, rows.Length).ToArray();
        var bestOrder = (int[])order.Clone();
        var bestCost  = int.MaxValue;
        var failed    = false;

        Permute(order, 0, perm =>
        {
            var cost = 0;
            var prev = start;
            foreach (var i in perm)
            {
                if (!distances.TryGetValue(Key(prev, rows[i]), out var leg)) { failed = true; return; }
                cost += leg;
                prev  = rows[i];
            }
            if (!distances.TryGetValue(Key(prev, start), out var home)) { failed = true; return; }
            cost += home;

            if (cost >= bestCost) return;
            bestCost  = cost;
            bestOrder = (int[])perm.Clone();
        });

        if (failed || bestCost == int.MaxValue) return null;

        var survey = rows.Sum(r => surveyDistance.TryGetValue(r, out var d) ? d : 0);

        return new VoyagePlan
        {
            Order    = bestOrder.Select(i => sectors[i]).ToList(),
            Distance = bestCost + survey,
        };
    }

    /// <summary>Runs <paramref name="action"/> over every permutation of <paramref name="items"/>.</summary>
    private static void Permute(int[] items, int index, Action<int[]> action)
    {
        if (items.Length == 0) return;

        if (index == items.Length - 1)
        {
            action(items);
            return;
        }

        for (var i = index; i < items.Length; i++)
        {
            (items[index], items[i]) = (items[i], items[index]);
            Permute(items, index + 1, action);
            (items[index], items[i]) = (items[i], items[index]);
        }
    }

    /// <summary>
    /// How long a plotted voyage takes. The first call for a route schedules the
    /// lookup on the framework thread and returns null; the answer is cached for
    /// the next frame.
    /// </summary>
    public TimeSpan? GetDuration(IReadOnlyList<SubmarineSector> order, int speed)
    {
        if (order.Count == 0 || speed <= 0 || !HasVoyageData) return null;

        var key = $"{speed}|{string.Join(",", order.Select(s => $"{s.Map}{s.Letter}"))}";
        if (durations.TryGetValue(key, out var cached)) return cached;

        lock (durationsRequested)
        {
            if (!durationsRequested.Add(key)) return null;
        }

        if (!startPoints.TryGetValue(order[0].Map, out var start)) return null;

        var rows = new List<uint>();
        foreach (var sector in order)
        {
            if (!sectorRows.TryGetValue((sector.Map, sector.Letter), out var row)) return null;
            rows.Add(row);
        }

        framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var seconds = 0u;
                var prev    = start;
                foreach (var row in rows)
                {
                    seconds += HousingManager.GetSubmarineVoyageTime((byte)prev, (byte)row, (short)speed);
                    seconds += HousingManager.GetSubmarineSurveyDuration((byte)row, (short)speed);
                    prev     = row;
                }
                seconds += HousingManager.GetSubmarineVoyageTime((byte)prev, (byte)start, (short)speed);
                durations[key] = TimeSpan.FromSeconds(seconds);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[RedMoonCappuccino] Submersible voyage time lookup failed.");
            }
        });

        return null;
    }

    // ── Live submarines ───────────────────────────────────────────────────────

    private unsafe void PollWorkshop()
    {
        var manager = HousingManager.Instance();
        if (manager == null || manager->WorkshopTerritory == null)
        {
            WorkshopVisible = false;
            return;
        }

        WorkshopVisible = true;

        var houseId  = manager->WorkshopTerritory->HouseId;
        var workshop = DescribeWorkshop(houseId);

        var found = new List<SavedSubmarine>();

        // Bind by reference so the span points at the game's own memory rather
        // than a copy of the struct.
        ref var submersible = ref manager->WorkshopTerritory->Submersible;
        var data = submersible.Data;

        for (var i = 0; i < data.Length; i++)
        {
            ref var sub = ref data[i];
            if (sub.RankId == 0 || sub.RegisterTime == 0) continue;

            var name = ReadCString(sub.Name);
            if (name.Length == 0) name = $"Submarine {i + 1}";

            found.Add(new SavedSubmarine
            {
                Name     = name,
                Rank     = sub.RankId,
                HouseId  = houseId.Id,
                Workshop = workshop,
                Stats = new[]
                {
                    sub.SurveillanceBase + sub.SurveillanceBonus,
                    sub.RetrievalBase    + sub.RetrievalBonus,
                    sub.SpeedBase        + sub.SpeedBonus,
                    sub.RangeBase        + sub.RangeBonus,
                    sub.FavorBase        + sub.FavorBonus,
                },
                Parts = IdentifyParts(
                    sub.HullId, sub.SternId, sub.BowId, sub.BridgeId, sub.RankId,
                    new[] { (int)sub.SurveillanceBase, sub.RetrievalBase, sub.SpeedBase, sub.RangeBase, sub.FavorBase }),
                Route   = ReadRoute(sub.CurrentExplorationPoints),
                SeenUtc = DateTime.UtcNow,
            });
        }

        ReadUnlockedSectors();

        if (found.Count == 0 || SameAsStored(houseId.Id, found)) return;

        // Only this workshop's boats are replaced; submarines cached from other
        // free companies stay on the list.
        var merged = configuration.KnownSubmarines.Where(s => s.HouseId != houseId.Id).ToList();
        merged.AddRange(found);

        configuration.KnownSubmarines = merged.TakeLast(MaxSavedSubmarines).ToList();
        configuration.Save();
    }

    private bool SameAsStored(ulong houseId, List<SavedSubmarine> found)
    {
        var stored = configuration.KnownSubmarines.Where(s => s.HouseId == houseId).ToList();
        if (stored.Count != found.Count) return false;

        for (var i = 0; i < found.Count; i++)
        {
            if (stored[i].Name != found[i].Name || stored[i].Rank != found[i].Rank) return false;
            if (!stored[i].Stats.SequenceEqual(found[i].Stats)) return false;
            if (!stored[i].Route.SequenceEqual(found[i].Route)) return false;
        }

        return true;
    }

    private static string DescribeWorkshop(HouseId houseId)
    {
        var world = Plugin.DataManager.GetExcelSheet<World>()?
            .GetRowOrDefault(houseId.WorldId)?.Name.ExtractText() ?? string.Empty;

        var plot = $"Ward {houseId.WardIndex + 1}, Plot {houseId.PlotIndex + 1}";
        return world.Length > 0 ? $"{world} · {plot}" : plot;
    }

    private void ReadUnlockedSectors()
    {
        if (unlockedSectors is { Count: > 0 }) return;

        try
        {
            var unlocked = new HashSet<uint>();
            foreach (var rowId in sectorRows.Values)
            {
                if (HousingManager.IsSubmarineExplorationUnlocked((byte)rowId))
                    unlocked.Add(rowId);
            }

            // Nothing unlocked means the table was not readable here — keep it
            // unknown rather than telling the player every sector is locked.
            if (unlocked.Count > 0) unlockedSectors = unlocked;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[RedMoonCappuccino] Submersible unlock lookup failed.");
        }
    }

    /// <summary>
    /// Maps the part ids of a live submarine back to dataset part names. When
    /// the ids do not resolve, the part stats the game reports are solved back
    /// into a build instead, so a sheet change cannot break the feature.
    /// </summary>
    private string[] IdentifyParts(ushort hull, ushort stern, ushort bow, ushort bridge,
                                   byte rank, int[] baseStats)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<SubmarinePart>();
        if (sheet != null)
        {
            var ids   = new[] { hull, stern, bow, bridge };
            var names = new string[4];
            var ok    = true;

            for (var slot = 0; slot < 4 && ok; slot++)
            {
                var row = sheet.GetRowOrDefault(ids[slot]);
                if (row is not { } part) { ok = false; break; }

                var match = routes.FindPart(slot, part.Surveillance, part.Retrieval, part.Speed, part.Range, part.Favor);
                if (match == null) ok = false;
                else names[slot] = match;
            }

            if (ok) return names;
        }

        // The base stats are the four parts added up; the rank bonus is carried
        // separately, but older builds folded it in, so try both readings.
        var solved = routes.SolveParts(baseStats, rank);
        if (solved != null) return solved;

        var bonus   = routes.RankBonus(rank);
        var without = new int[5];
        for (var k = 0; k < 5; k++) without[k] = baseStats[k] - bonus[k];

        return routes.SolveParts(without, rank) ?? Array.Empty<string>();
    }

    private string[] ReadRoute(Span<byte> points)
    {
        var letters = new List<string>();
        foreach (var point in points)
        {
            if (point == 0) continue;
            if (rowLetters.TryGetValue(point, out var letter)) letters.Add(letter);
        }
        return letters.ToArray();
    }

    private static string ReadCString(Span<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.UTF8.GetString(bytes[..end]).Trim();
    }
}
