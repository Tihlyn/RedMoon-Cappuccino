// ============================================================
// ADD THESE MEMBERS TO YOUR EXISTING Configuration.cs
// They handle API key storage and the daily grounded query
// limit (500 RPD free tier; plugin enforces 450 hard limit
// with a 400-query soft warning).
// ============================================================

// using System;   ← already present in your Configuration.cs

namespace YourPlugin;

// Partial class so you can keep your existing file untouched
// and drop this into a new file, OR just copy the members across.
public partial class Configuration
{
    // ── Gemini settings ───────────────────────────────────────────────────────

    /// <summary>Google AI Studio API key. Never log or display in plain text.</summary>
    public string GeminiApiKey { get; set; } = "";

    // ── Daily grounded query tracking ─────────────────────────────────────────

    /// <summary>Number of grounded queries sent today (UTC date).</summary>
    public int    DailyGroundedCount { get; set; } = 0;

    /// <summary>UTC date string "yyyy-MM-dd" of the last recorded query.</summary>
    public string GroundedCountDate  { get; set; } = "";

    // ── Limits (change here to adjust globally) ───────────────────────────────

    /// <summary>Show a warning in the UI when remaining drops to this many.</summary>
    public const int GroundedSoftWarnAt = 50;   // warn when 50 left

    /// <summary>
    /// Hard limit enforced by the plugin. Google's free tier is 500 RPD
    /// (shared between Flash and Flash-Lite). We cap at 450 to leave a
    /// 50-query buffer against simultaneous use or clock skew.
    /// </summary>
    public const int GroundedHardLimit = 450;

    // ── Computed helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Remaining grounded queries for today.
    /// Automatically resets DailyGroundedCount when the UTC date changes.
    /// </summary>
    public int GroundedRemaining
    {
        get
        {
            EnsureDateReset();
            return GroundedHardLimit - DailyGroundedCount;
        }
    }

    /// <summary>True when at least one grounded query is available today.</summary>
    public bool CanGroundedQuery => GroundedRemaining > 0;

    /// <summary>
    /// Increments the daily counter and saves. Call ONLY after a successful
    /// API response — do not charge the quota on auth or network failures.
    /// </summary>
    public void RecordGroundedQuery()
    {
        EnsureDateReset();
        DailyGroundedCount++;
        Save();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void EnsureDateReset()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (GroundedCountDate == today) return;

        DailyGroundedCount = 0;
        GroundedCountDate  = today;
        Save();
    }
}
