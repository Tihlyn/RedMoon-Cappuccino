using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Rmc.Server;

/// <summary>
/// Results by request: the same gear and options always give the same answer for a given data
/// version, so a request is keyed on its canonical JSON (object keys sorted) plus the endpoint
/// and data version. Identical requests in flight at once share one computation.
/// </summary>
public sealed class ResultCache(IMemoryCache cache, JsonSerializerOptions json, string dataVersion)
{
    private static readonly MemoryCacheEntryOptions Entry = new() { Size = 1, SlidingExpiration = TimeSpan.FromHours(6) };

    public string Key(string endpoint, object request)
    {
        var node = JsonSerializer.SerializeToNode(request, request.GetType(), json);
        var canonical = Canonical(node)?.ToJsonString() ?? "null";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{dataVersion}\n{canonical}"));
        return Convert.ToHexStringLower(hash);
    }

    public async Task<T> GetOrCompute<T>(string key, Func<Task<T>> compute)
    {
        var lazy = cache.GetOrCreate(key, e =>
        {
            e.SetOptions(Entry);
            return new Lazy<Task<T>>(compute, LazyThreadSafetyMode.ExecutionAndPublication);
        })!;
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed or cancelled computation isn't an answer: let the next request retry.
            cache.Remove(key);
            throw;
        }
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
