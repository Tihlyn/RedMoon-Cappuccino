using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Talks to the gear planner compute server: DPS of the equipped set, meld solves and DPS-ranked
/// upgrade plans. Requests carry item ids, melds and stats only, never character names.
/// Answers are cached by request, so asking again with unchanged gear doesn't leave the machine.
/// Every call returns an error instead of throwing; callers fall back to the local planner.
/// </summary>
public sealed class PlannerServerClient : IDisposable
{
    private const int CacheEntries = 32;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly Func<string> address;
    private readonly Dictionary<string, object> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> cacheOrder = new();
    private readonly object cacheLock = new();
    private PlannerMeta? meta;

    public PlannerServerClient(Func<string> address) => this.address = address;

    /// <summary>The configured address, or null while it's empty or still the build placeholder.</summary>
    public string? BaseAddress => IsUsableAddress(address()) ? address().Trim().TrimEnd('/') : null;

    public bool IsConfigured => BaseAddress != null;

    /// <summary>A full http(s) URL (scheme, host, port if not the default), not the build placeholder.</summary>
    public static bool IsUsableAddress(string? value)
    {
        value = value?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(value) && !value.Contains("YOUR_", StringComparison.Ordinal) &&
               Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>Startup check: the server's health line, or why it can't be reached.</summary>
    public async Task<PlannerCall<PlannerHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await SendAsync<PlannerHealth>(HttpMethod.Get, "/healthz", null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PlannerCall<PlannerHealth>(null, "Planner server timed out.");
        }
    }

    public async Task<PlannerCall<PlannerMeta>> GetMetaAsync(CancellationToken ct = default)
    {
        if (meta != null)
            return new PlannerCall<PlannerMeta>(meta, null);
        var result = await SendAsync<PlannerMeta>(HttpMethod.Get, "/v1/meta", null, ct).ConfigureAwait(false);
        meta = result.Value;
        return result;
    }

    public Task<PlannerCall<PlannerEvaluateResponse>> EvaluateAsync(PlannerEvaluateRequest request, CancellationToken ct = default)
        => SendAsync<PlannerEvaluateResponse>(HttpMethod.Post, "/v1/evaluate", request, ct);

    public Task<PlannerCall<PlannerMeldSolveResponse>> SolveMeldsAsync(PlannerMeldSolveRequest request, CancellationToken ct = default)
        => SendAsync<PlannerMeldSolveResponse>(HttpMethod.Post, "/v1/meld-solve", request, ct);

    public Task<PlannerCall<PlannerUpgradePlanResponse>> PlanUpgradesAsync(PlannerUpgradePlanRequest request, CancellationToken ct = default)
        => SendAsync<PlannerUpgradePlanResponse>(HttpMethod.Post, "/v1/upgrade-plan", request, ct);

    private async Task<PlannerCall<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) where T : class
    {
        var baseAddress = BaseAddress;
        if (baseAddress == null)
            return new PlannerCall<T>(null, "No planner server is configured.");

        var payload = body != null ? JsonSerializer.Serialize(body, body.GetType(), Json) : null;
        var key = payload != null ? $"{path}\n{Hash(payload)}" : null;
        if (key != null)
            lock (cacheLock)
                if (cache.TryGetValue(key, out var hit))
                    return new PlannerCall<T>((T)hit, null);

        try
        {
            using var request = new HttpRequestMessage(method, baseAddress + path);
            if (payload != null)
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = TryReadError(text) ?? $"HTTP {(int)response.StatusCode}";
                return new PlannerCall<T>(null, $"Planner server: {message}");
            }
            var value = JsonSerializer.Deserialize<T>(text, Json);
            if (value == null)
                return new PlannerCall<T>(null, "Planner server sent an empty answer.");
            if (key != null)
                Remember(key, value);
            return new PlannerCall<T>(value, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PlannerCall<T>(null, "Planner server timed out.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            return new PlannerCall<T>(null, $"Planner server unreachable ({e.GetType().Name}).");
        }
    }

    private void Remember(string key, object value)
    {
        lock (cacheLock)
        {
            if (cache.ContainsKey(key))
                return;
            cache[key] = value;
            cacheOrder.AddLast(key);
            while (cacheOrder.Count > CacheEntries)
            {
                cache.Remove(cacheOrder.First!.Value);
                cacheOrder.RemoveFirst();
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? TryReadError(string text)
    {
        try { return JsonSerializer.Deserialize<PlannerError>(text, Json)?.Error; }
        catch (JsonException) { return null; }
    }

    public void Dispose() => http.Dispose();

    private sealed class PlannerError
    {
        public string? Error { get; set; }
    }
}

public sealed record PlannerCall<T>(T? Value, string? Error) where T : class;

// ── Request shapes (match the server's v1 API) ───────────────────────────────

public sealed class PlannerGear
{
    public string Job { get; set; } = string.Empty;
    public int Level { get; set; } = 100;
    public int? ClanId { get; set; }
    public int PartyBonus { get; set; } = 5;
    public PlannerFood? Food { get; set; }
    public Dictionary<string, PlannerPiece> Slots { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PlannerPiece
{
    public int ItemId { get; set; }
    public bool? Hq { get; set; }
    public List<PlannerMeld> Materia { get; set; } = [];
}

public sealed class PlannerMeld
{
    public string Stat { get; set; } = string.Empty;
    /// <summary>1-based materia grade (XII = 12).</summary>
    public int Grade { get; set; }
}

public sealed class PlannerFood
{
    public int ItemId { get; set; }
    public bool Hq { get; set; } = true;
}

public sealed class PlannerOptions
{
    public double? FightSeconds { get; set; }
    public string? BisSet { get; set; }
    public bool BisFoodWhenUnfed { get; set; } = true;
}

public sealed class PlannerEvaluateRequest
{
    public PlannerGear Gear { get; set; } = new();
    public Dictionary<string, int>? Panel { get; set; }
    public PlannerOptions Options { get; set; } = new();
}

public sealed class PlannerMeldSolveRequest
{
    public PlannerGear Gear { get; set; } = new();
    public PlannerOptions Options { get; set; } = new();
    public double? TargetGcd { get; set; }
    public List<string> LockedSlots { get; set; } = [];
    public bool KeepExistingMelds { get; set; }
    public bool SolveFood { get; set; }
}

public sealed class PlannerUpgradePlanRequest
{
    public PlannerGear Gear { get; set; } = new();
    public PlannerOptions Options { get; set; } = new();
    public Dictionary<string, int> Wallet { get; set; } = new(StringComparer.Ordinal);
    public bool IgnoreWeeklyLimits { get; set; }
    public bool MaterialsFromHunts { get; set; }
}

// ── Response shapes ──────────────────────────────────────────────────────────

public sealed class PlannerHealth
{
    public string Status { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public int Queued { get; set; }
}

public sealed class PlannerMeta
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public List<PlannerMetaJob> Jobs { get; set; } = [];
    public List<PlannerCurrency> Currencies { get; set; } = [];
}

public sealed class PlannerMetaJob
{
    public string Job { get; set; } = string.Empty;
    public List<double> FightLengths { get; set; } = [];
    public List<string> BisSets { get; set; } = [];
}

public sealed class PlannerCurrency
{
    public string Name { get; set; } = string.Empty;
    /// <summary>0 for tomestones, which the snapshot knows by name only.</summary>
    public int ItemId { get; set; }
    public string Type { get; set; } = string.Empty;
}

public sealed class PlannerBreakpoint
{
    public string Stat { get; set; } = string.Empty;
    public int Value { get; set; }
    public int TierStart { get; set; }
    public int NextTier { get; set; }
    public int? GcdCs { get; set; }
    public int? NextGcdCs { get; set; }
}

public sealed class PlannerDerived
{
    public double CritRate { get; set; }
    public double CritDamage { get; set; }
    public double DirectHitRate { get; set; }
    public double Determination { get; set; }
    public double Tenacity { get; set; }
    public double Gcd { get; set; }
}

public sealed class PlannerBisSummary
{
    public string Name { get; set; } = string.Empty;
    public double Dps { get; set; }
    public double Gcd { get; set; }
}

public sealed class PlannerEvaluateResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public double FightSeconds { get; set; }
    public double Dps { get; set; }
    public bool OutsideFittedRange { get; set; }
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    public PlannerDerived Derived { get; set; } = new();
    public List<PlannerBreakpoint> Breakpoints { get; set; } = [];
    public Dictionary<string, int> PanelDiff { get; set; } = new(StringComparer.Ordinal);
    public List<string> Issues { get; set; } = [];
    public List<string> UnknownSlots { get; set; } = [];
    public PlannerBisSummary? Bis { get; set; }
}

public sealed class PlannerMeldSolveResponse
{
    public double FightSeconds { get; set; }
    public double CurrentDps { get; set; }
    public double Dps { get; set; }
    public double Gain { get; set; }
    public double Gcd { get; set; }
    public bool Infeasible { get; set; }
    public PlannerGear Gear { get; set; } = new();
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.Ordinal);
    public List<PlannerBreakpoint> Breakpoints { get; set; } = [];
    public double ElapsedMs { get; set; }
}

public sealed class PlannerUpgradePlanResponse
{
    public string Patch { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public double FightSeconds { get; set; }
    public string BisSet { get; set; } = string.Empty;
    public double CurrentDps { get; set; }
    public double CurrentRemeldedDps { get; set; }
    public double BisDps { get; set; }
    public double FinalDps { get; set; }
    public int? WeeksToFinish { get; set; }
    public List<PlannerStep> Steps { get; set; } = [];
    public List<string> FinalRemeldSlots { get; set; } = [];
    public double FinalRemeldGain { get; set; }
    public List<PlannerDrop> Drops { get; set; } = [];
    public List<string> Unreached { get; set; } = [];
    public List<string> NoGain { get; set; } = [];
    public double ElapsedMs { get; set; }
}

public sealed class PlannerStep
{
    public int Week { get; set; }
    public string Slot { get; set; } = string.Empty;
    /// <summary>Buy, Augment, Craft, ...</summary>
    public string Kind { get; set; } = string.Empty;
    public int FromItemId { get; set; }
    public string FromItem { get; set; } = string.Empty;
    public int ToItemId { get; set; }
    public string ToItem { get; set; } = string.Empty;
    public int ToItemLevel { get; set; }
    public string CostText { get; set; } = string.Empty;
    public double DpsBefore { get; set; }
    public double DpsAfter { get; set; }
    public List<PlannerMeld> Melds { get; set; } = [];
    public List<string> RemeldSlots { get; set; } = [];
    public double RemeldGain { get; set; }

    public double Gain => DpsAfter - DpsBefore;
}

public sealed class PlannerDrop
{
    public string Slot { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string Item { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public double GainIfObtained { get; set; }
}
