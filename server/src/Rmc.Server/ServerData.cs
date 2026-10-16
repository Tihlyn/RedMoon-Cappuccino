using System.Security.Cryptography;
using Rmc.Engine;
using Rmc.Engine.Damage;
using Rmc.Engine.Sim.Rotations;
using Rmc.GameData;

namespace Rmc.Server;

/// <summary>
/// Everything the server computes from, loaded once at startup from the data folder: the game
/// data snapshot, every job's fitted hit profiles (one set per fight length bucket), the BiS
/// sheets and the weekly income overlay. Immutable afterwards, so requests share it freely.
/// </summary>
public sealed class ServerData
{
    public GameDataIndex Data { get; }
    public StatEngine Engine { get; }
    public string Patch => Data.Snapshot.Meta.Patch;
    /// <summary>Hash of every input file: changes whenever any computed answer could.</summary>
    public string DataVersion { get; }
    public WeeklyIncome Weekly { get; }
    public IReadOnlyDictionary<string, BisSheet> Bis { get; }

    /// <summary>Per job: fitted models by their mean fight length, shortest first.</summary>
    private readonly Dictionary<string, List<(double Seconds, FittedProfileModel Model)>> profiles;
    private readonly Dictionary<string, FoodInfo?> medicines;

    public ServerData(string dataRoot)
    {
        var files = new List<string>();
        var gamedataPath = Directory.EnumerateFiles(Path.Combine(dataRoot, "gamedata"), "*.json").Order(StringComparer.Ordinal).Last();
        files.Add(gamedataPath);
        Data = new GameDataIndex(GameDataJson.ReadSnapshot(gamedataPath));
        Engine = new StatEngine(Data);

        // Profiles: data/profiles/<JOB>.json is the standard length; data/profiles/<name>/<JOB>.json
        // hold other lengths. Each file says which lengths it was fitted over.
        profiles = new Dictionary<string, List<(double, FittedProfileModel)>>(StringComparer.Ordinal);
        var profileRoot = Path.Combine(dataRoot, "profiles");
        foreach (var path in Directory.EnumerateFiles(profileRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var profile = GameDataJson.Read<HitProfile>(path);
            if (string.IsNullOrEmpty(profile.Job) || profile.Tiers.Count == 0)
                continue;
            files.Add(path);
            var seconds = profile.FightSeconds.Count > 0 ? profile.FightSeconds.Average() : 510;
            if (!profiles.TryGetValue(profile.Job, out var list))
                profiles[profile.Job] = list = [];
            list.Add((seconds, new FittedProfileModel(profile)));
        }
        foreach (var list in profiles.Values)
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));

        var bis = new Dictionary<string, BisSheet>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(dataRoot, "bis")).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, "current.json");
            if (!File.Exists(path))
                continue;
            files.Add(path);
            var sheet = GameDataJson.Read<BisSheet>(path);
            bis[sheet.Meta.Job] = sheet;
        }
        Bis = bis;

        var weeklyPath = Path.Combine(dataRoot, "overlays", "weekly.json");
        files.Add(weeklyPath);
        Weekly = GameDataJson.Read<WeeklyIncome>(weeklyPath);

        // Potion use is a property of the rotation; resolve it once per job.
        medicines = new Dictionary<string, FoodInfo?>(StringComparer.Ordinal);
        foreach (var job in profiles.Keys)
            medicines[job] = (RotationRegistry.For(job)?.UsesPotion ?? false) ? Engine.BestMedicine(job) : null;

        using var sha = SHA256.Create();
        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        DataVersion = Convert.ToHexStringLower(sha.Hash!)[..12];
    }

    public IEnumerable<string> Jobs => profiles.Keys.Order(StringComparer.Ordinal);

    public IEnumerable<double> FightLengths(string job)
        => profiles.TryGetValue(job, out var list) ? list.Select(p => p.Seconds) : [];

    /// <summary>The job's profile fitted closest to the asked fight length (the standard one when none is asked).</summary>
    public FittedProfileModel Model(string job, double? fightSeconds)
    {
        if (!profiles.TryGetValue(job, out var list))
            throw new ArgumentException($"No damage profile for job '{job}'.");
        var want = fightSeconds ?? 510;
        return list.MinBy(p => Math.Abs(p.Seconds - want)).Model;
    }

    public FoodInfo? Medicine(string job) => medicines.GetValueOrDefault(job);

    /// <summary>Stats and expected DPS of a set, potion included where the rotation uses one.</summary>
    public (GearsetStats Stats, DamageEstimate Estimate) Evaluate(GearsetInput input, FittedProfileModel model)
    {
        var stats = Engine.Compute(input);
        var medicine = Medicine(stats.Job);
        return (stats, model.Evaluate(stats, medicine != null ? Engine.WithMedicine(stats, medicine) : null));
    }

    /// <summary>A job's BiS set as the player would wear it: their clan and party bonus.</summary>
    public GearsetInput? BisSet(string job, string? setName, int? clanId, int partyBonus)
    {
        if (!Bis.TryGetValue(job, out var sheet) || sheet.Sets.Count == 0)
            return null;
        var set = setName == null ? sheet.Sets[0] : sheet.Sets.FirstOrDefault(s => s.Name.Trim() == setName.Trim());
        if (set == null)
            return null;
        var input = GearsetInputs.FromBis(sheet.Meta, set);
        input.ClanId = clanId ?? input.ClanId;
        input.PartyBonus = partyBonus;
        return input;
    }
}

public sealed class WeeklyIncome
{
    public string Tier { get; set; } = string.Empty;
    public Dictionary<string, int> PerWeek { get; set; } = new(StringComparer.Ordinal);
    public string HuntCurrency { get; set; } = "Sack of Nuts";
}
