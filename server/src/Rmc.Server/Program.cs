using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Rmc.Engine;
using Rmc.Engine.Melds;
using Rmc.Engine.Upgrades;
using Rmc.GameData;
using Rmc.Server;

// Settings live next to the binary, whatever the working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var settings = builder.Configuration.GetSection("Rmc");

// The data folder holds everything computed from; the rotation sims also read data/potencies
// relative to the working directory, so run from the folder that contains it.
var dataRoot = FindDataRoot(settings["DataRoot"] ?? "data", builder.Environment.ContentRootPath);
Directory.SetCurrentDirectory(Path.GetDirectoryName(Path.GetFullPath(dataRoot))!);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = settings.GetValue("MaxRequestBytes", 64 * 1024));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddMemoryCache(o => o.SizeLimit = settings.GetValue("CacheEntries", 20_000));

var app = builder.Build();
var log = app.Logger;

var watch = System.Diagnostics.Stopwatch.StartNew();
var data = new ServerData(dataRoot);
var threads = settings.GetValue("TotalThreads", 0) is > 0 and var t ? t : Environment.ProcessorCount;
var pool = new ComputePool(threads, settings.GetValue("MaxConcurrentJobs", 4));
var maxQueued = settings.GetValue("MaxQueuedJobs", 64);
var json = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
var cache = new ResultCache(app.Services.GetRequiredService<IMemoryCache>(), json, data.DataVersion);
log.LogInformation("Loaded patch {Patch} (data {Version}): {Jobs} jobs in {Ms} ms; {Concurrent} heavy jobs at once x {Threads} threads",
    data.Patch, data.DataVersion, data.Jobs.Count(), watch.ElapsedMilliseconds, pool.MaxConcurrentJobs, pool.ThreadsPerJob);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", data.Patch, data.DataVersion, queued = pool.Waiting }));

app.MapGet("/v1/meta", () => new MetaResponse
{
    Patch = data.Patch,
    DataVersion = data.DataVersion,
    Tier = data.Weekly.Tier,
    WeeklyIncome = data.Weekly.PerWeek,
    HuntCurrency = data.Weekly.HuntCurrency,
    Jobs = data.Jobs.Select(j => new MetaJob
    {
        Job = j,
        FightLengths = data.FightLengths(j).ToList(),
        BisSets = data.Bis.TryGetValue(j, out var sheet) ? sheet.Sets.Select(s => s.Name.Trim()).ToList() : [],
    }).ToList(),
    Currencies = data.Data.Snapshot.Currencies.Select(c => new MetaCurrency(c.Name, c.ItemId, c.Type)).ToList(),
});

// Light: microseconds of arithmetic, so it runs inline rather than through the pool.
app.MapPost("/v1/evaluate", (EvaluateRequest request) => Guarded(() =>
{
    var asWorn = Normalize(request.Gear);
    var gear = WithFood(asWorn, request.Options);
    var model = data.Model(gear.Job, request.Options.FightSeconds);
    var (stats, estimate) = data.Evaluate(gear, model);
    var shownStats = ReferenceEquals(gear, asWorn) ? stats : data.Engine.Compute(asWorn);
    var response = new EvaluateResponse
    {
        Patch = data.Patch,
        DataVersion = data.DataVersion,
        FightSeconds = model.Profile.FightSeconds.DefaultIfEmpty(510).Average(),
        Dps = estimate.Dps,
        OutsideFittedRange = estimate.OutsideFittedRange,
        Stats = stats.Final,
        Derived = DerivedStats.From(stats, estimate.GcdCs),
        Breakpoints = stats.Breakpoints,
        Slots = stats.Slots,
        Issues = stats.Issues.Select(i => i.Message).ToList(),
        UnknownSlots = gear.Slots.Where(kv => data.Data.Item(kv.Value.ItemId) == null).Select(kv => kv.Key).ToList(),
    };
    // Parity on substats only: the panel's main stat depends on the party's roles right now,
    // which the request doesn't describe; substats don't.
    if (request.Panel != null)
        foreach (var stat in new[] { Stats.Critical, Stats.DirectHit, Stats.Determination, Stats.SkillSpeed, Stats.SpellSpeed, Stats.Tenacity, Stats.Piety })
            if (request.Panel.TryGetValue(stat, out var shown) && shownStats.Final.GetValueOrDefault(stat) - shown is var diff && diff != 0)
                response.PanelDiff[stat] = diff;
    if (data.BisSet(gear.Job, request.Options.BisSet, gear.ClanId, gear.PartyBonus) is { } bis)
    {
        var (_, bisEstimate) = data.Evaluate(bis, model);
        response.Bis = new BisSummary { Name = BisName(gear.Job, request.Options.BisSet), Dps = bisEstimate.Dps, Gcd = bisEstimate.GcdCs / 100.0 };
    }
    return Results.Ok(response);
}));

app.MapPost("/v1/meld-solve", (MeldSolveRequest request, CancellationToken ct) => GuardedAsync(async () =>
{
    var gear = WithFood(Normalize(request.Gear), request.Options);
    var model = data.Model(gear.Job, request.Options.FightSeconds);
    var key = cache.Key("meld-solve", request);
    return Results.Ok(await cache.GetOrCompute(key, () => Heavy(degree =>
    {
        var solution = new MeldSolver(data.Engine, model).Solve(gear, new MeldSolveOptions
        {
            KeepExistingMelds = request.KeepExistingMelds,
            LockedSlots = new HashSet<string>(request.LockedSlots, StringComparer.Ordinal),
            TargetGcdCs = request.TargetGcd is double gcd ? (int)Math.Round(gcd * 100) : null,
            MinPiety = request.MinPiety,
            SolveFood = request.SolveFood,
            MaxDegreeOfParallelism = degree,
        });
        return new MeldSolveResponse
        {
            Patch = data.Patch,
            DataVersion = data.DataVersion,
            FightSeconds = model.Profile.FightSeconds.DefaultIfEmpty(510).Average(),
            CurrentDps = solution.CurrentDps,
            Dps = solution.Dps,
            Gain = solution.Gain,
            Gcd = solution.GcdCs / 100.0,
            Infeasible = solution.Infeasible,
            Gear = solution.Gear,
            Stats = solution.Stats.Final,
            Breakpoints = solution.Stats.Breakpoints,
            ElapsedMs = solution.Elapsed.TotalMilliseconds,
        };
    }, ct)));
}));

app.MapPost("/v1/upgrade-plan", (UpgradePlanRequest request, CancellationToken ct) => GuardedAsync(async () =>
{
    var gear = WithFood(Normalize(request.Gear), request.Options);
    var model = data.Model(gear.Job, request.Options.FightSeconds);
    var target = data.BisSet(gear.Job, request.Options.BisSet, gear.ClanId, gear.PartyBonus)
                 ?? throw new ArgumentException($"No BiS set '{request.Options.BisSet ?? "(default)"}' for {gear.Job}.");
    var key = cache.Key("upgrade-plan", request);
    return Results.Ok(await cache.GetOrCompute(key, () => Heavy(degree =>
    {
        var options = new UpgradeOptions
        {
            WeeklyIncome = data.Weekly.PerWeek,
            HuntCurrency = data.Weekly.HuntCurrency,
            Wallet = new Dictionary<string, int>(request.Wallet, StringComparer.Ordinal),
            IncludeCrafted = request.IncludeCrafted,
            IgnoreWeeklyLimits = request.IgnoreWeeklyLimits,
            MaterialsFromHunts = request.MaterialsFromHunts,
            MaxDegreeOfParallelism = degree,
        };
        if (request.MaxWeeks is int weeks)
            options.MaxWeeks = Math.Clamp(weeks, 1, 52);
        var plan = new UpgradePlanner(data.Engine, model).Plan(gear, target, options);
        return new UpgradePlanResponse
        {
            Patch = data.Patch,
            DataVersion = data.DataVersion,
            FightSeconds = model.Profile.FightSeconds.DefaultIfEmpty(510).Average(),
            BisSet = BisName(gear.Job, request.Options.BisSet),
            CurrentDps = plan.CurrentDps,
            CurrentRemeldedDps = plan.CurrentRemeldedDps,
            BisDps = plan.BisDps,
            FinalDps = plan.FinalDps,
            WeeksToFinish = plan.WeeksToFinish,
            Steps = plan.Steps,
            FinalRemeldSlots = plan.FinalRemeldSlots,
            FinalRemeldGain = plan.FinalRemeldGain,
            Drops = plan.Drops,
            Unreached = plan.Unreached,
            NoGain = plan.NoGain,
            FinalGear = plan.FinalGear,
            ElapsedMs = plan.Elapsed.TotalMilliseconds,
        };
    }, ct)));
}));

app.Run();

// Heavy work goes through the pool; when too many are already waiting the caller is told to
// come back later rather than queueing without bound.
Task<T> Heavy<T>(Func<int, T> work, CancellationToken ct)
{
    if (pool.Waiting >= maxQueued)
        throw new ServerBusyException();
    return pool.RunAsync(work, ct);
}

GearsetInput Normalize(GearsetInput gear)
{
    gear.Job = gear.Job.Trim().ToUpperInvariant();
    if (data.Data.Job(gear.Job) == null)
        throw new ArgumentException($"Unknown job '{gear.Job}'.");
    gear.PartyBonus = Math.Clamp(gear.PartyBonus, 0, 5);
    gear.Slots = gear.Slots
        .Where(kv => Slots.All.Contains(kv.Key.ToUpperInvariant()) && kv.Value.ItemId > 0)
        .ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value, StringComparer.Ordinal);
    foreach (var piece in gear.Slots.Values)
        piece.Materia = piece.Materia.Where(m => !string.IsNullOrEmpty(m.Stat) && m.Grade > 0).Take(5).ToList();
    return gear;
}

// The BiS set's food for a player who isn't fed right now (a copy; the gear as sent is kept for parity).
GearsetInput WithFood(GearsetInput gear, ComputeOptions options)
{
    if (gear.Food != null || !options.BisFoodWhenUnfed || data.BisSet(gear.Job, options.BisSet, gear.ClanId, gear.PartyBonus)?.Food is not { } food)
        return gear;
    return new GearsetInput { Job = gear.Job, Level = gear.Level, ClanId = gear.ClanId, PartyBonus = gear.PartyBonus, Food = food, Slots = gear.Slots };
}

string BisName(string job, string? setName)
    => setName?.Trim() ?? (data.Bis.TryGetValue(job, out var sheet) && sheet.Sets.Count > 0 ? sheet.Sets[0].Name.Trim() : string.Empty);

IResult Guarded(Func<IResult> handler)
{
    try { return handler(); }
    catch (ArgumentException e) { return Results.BadRequest(new ErrorResponse(e.Message)); }
}

async Task<IResult> GuardedAsync(Func<Task<IResult>> handler)
{
    try { return await handler(); }
    catch (ArgumentException e) { return Results.BadRequest(new ErrorResponse(e.Message)); }
    catch (ServerBusyException) { return Results.Json(new ErrorResponse("Server busy, retry shortly."), statusCode: StatusCodes.Status503ServiceUnavailable); }
}

static string FindDataRoot(string configured, string contentRoot)
{
    if (Path.IsPathRooted(configured))
        return configured;
    for (var dir = new DirectoryInfo(contentRoot); dir != null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, configured);
        if (Directory.Exists(Path.Combine(candidate, "gamedata")))
            return candidate;
    }
    throw new DirectoryNotFoundException($"No '{configured}' folder with gamedata/ above {contentRoot}.");
}

internal sealed class ServerBusyException : Exception;
