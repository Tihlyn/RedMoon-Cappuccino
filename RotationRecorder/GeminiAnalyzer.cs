using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RedMoonCappuccino.RotationRecorder;

/// <summary>
/// Sends a recorded rotation to Gemini 2.5 Flash-Lite with Google Search
/// grounding enabled. The 500 RPD free grounded quota is enforced upstream
/// by the plugin's ConfigurationExtensions.RecordGroundedQuery() guard.
///
/// Model: gemini-2.5-flash-lite
///   - Free tier: tokens + 500 grounded RPD (shared with 2.5 Flash)
///   - Grounding always on — lets the model fetch current Balance/IcyVeins guides
///   - Temperature 1.0 per Google's recommendation when grounding is active
/// </summary>
public sealed class GeminiAnalyzer : IDisposable
{
    // Static HttpClient — intentional, one instance for the plugin lifetime
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string Endpoint =
        "https://generativelanguage.googleapis.com/v1beta/models/" +
        "gemini-2.5-flash-lite:generateContent";

    private const string SystemPrompt = """
        You are an FFXIV rotation coach. The player will provide a recorded
        action timeline from a training dummy session, with derived stats.

        Using Google Search, look up the current recommended rotation for this
        job from The Balance (thebalanceffxiv.com) and Icy Veins. Use those
        sources as your reference for what is correct.

        Structure your response exactly as follows — no other sections:

        GRADE: [S / A / B / C / D] — one letter, one sentence justification

        WHAT YOU DID WELL:
        • [specific strength, reference action names where relevant]
        • ...

        KEY ISSUES (ranked by impact, worst first):
        1. [issue] — [timestamp or pattern from the timeline] — [why it matters]
        2. ...

        FIXES:
        For each issue above, one concrete actionable change the player can make.

        OPENER (first 15 seconds):
        Assess the opener specifically — correct order, buffs before damage, etc.

        RESOURCE USAGE:
        Comment on MP (only for caster and healer jobs), job gauge, or ability-specific resources if visible in the data.

        Be direct. Name specific actions. If the recording is under 30 seconds, say so
        and give a partial assessment only. The recording may be lengthy or incomplete and provide no context on encounter, be lenient in your assessment. Focus on the rotation quality, not the raw numbers.
        """;

    public async Task<string> AnalyzeAsync(
        string            rotationText,
        string            apiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Gemini API key is not configured. Add it in the plugin settings.");

        var url  = $"{Endpoint}?key={Uri.EscapeDataString(apiKey)}";
        var body = new GeminiRequest
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = SystemPrompt }]
            },
            Contents =
            [
                new Content
                {
                    Role  = "user",
                    Parts = [new Part { Text = rotationText }]
                }
            ],
            // google_search tool — enables grounding on all current Gemini 2.x models
            Tools = [new Tool { GoogleSearch = new GoogleSearchConfig() }],
            GenerationConfig = new GenerationConfig
            {
                // Google recommends temperature 1.0 when grounding is enabled
                Temperature     = 1.0f,
                MaxOutputTokens = 1200,
            }
        };

        using var response = await Http.PostAsJsonAsync(url, body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Gemini API returned {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        // Response path: candidates[0].content.parts[0].text
        return doc.RootElement
                  .GetProperty("candidates")[0]
                  .GetProperty("content")
                  .GetProperty("parts")[0]
                  .GetProperty("text")
                  .GetString()
               ?? "(empty response)";
    }

    public void Dispose() { /* HttpClient is static — managed by GC */ }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    private sealed class GeminiRequest
    {
        [JsonPropertyName("system_instruction")]
        public Content? SystemInstruction { get; init; }

        [JsonPropertyName("contents")]
        public Content[] Contents { get; init; } = [];

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Tool[]? Tools { get; init; }

        [JsonPropertyName("generationConfig")]
        public GenerationConfig? GenerationConfig { get; init; }
    }

    private sealed class Content
    {
        [JsonPropertyName("role")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Role { get; init; }

        [JsonPropertyName("parts")]
        public Part[] Parts { get; init; } = [];
    }

    private sealed class Part
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = "";
    }

    private sealed class Tool
    {
        // Empty object = enable Google Search with defaults
        [JsonPropertyName("google_search")]
        public GoogleSearchConfig? GoogleSearch { get; init; }
    }

    // Empty class serialises as {} which is the correct tool config
    private sealed class GoogleSearchConfig { }

    private sealed class GenerationConfig
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; init; }

        [JsonPropertyName("maxOutputTokens")]
        public int MaxOutputTokens { get; init; }
    }
}
