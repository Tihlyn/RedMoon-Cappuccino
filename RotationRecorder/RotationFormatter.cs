using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RedMoonCappuccino.RotationRecorder;

/// <summary>
/// Converts a recorded session into structured plain-text suitable for
/// the Gemini prompt. Computes derived stats (GCD uptime, weave density,
/// GCD gaps) so the model has concrete numbers to reason from.
/// </summary>
public static class RotationFormatter
{
    public static string FormatForPrompt(
        string                     job,
        IReadOnlyList<ActionEvent> events,
        DateTimeOffset             sessionStart,
        DateTimeOffset             sessionEnd)
    {
        if (events.Count == 0)
            return "(no actions recorded)";

        var sb      = new StringBuilder();
        var elapsed = (sessionEnd - sessionStart).TotalSeconds;

        // ── Header ────────────────────────────────────────────────────────────
        sb.AppendLine($"Job: {job}");
        sb.AppendLine($"Recording duration: {elapsed:F1}s");
        sb.AppendLine($"Total actions: {events.Count}");

        var gcds  = events.Where(e => e.IsGcd).ToList();
        var ogcds = events.Where(e => !e.IsGcd).ToList();
        sb.AppendLine($"GCDs: {gcds.Count}  |  oGCDs: {ogcds.Count}");

        // GCD uptime approximation: GCD count × 2.5s assumed GCD, capped at elapsed
        var gcdCoverage = Math.Min(gcds.Count * 2.5, elapsed);
        var uptime      = elapsed > 0 ? gcdCoverage / elapsed * 100.0 : 0.0;
        sb.AppendLine($"Estimated GCD uptime: {uptime:F1}%");

        var weaveRatio = gcds.Count > 0 ? (double)ogcds.Count / gcds.Count : 0.0;
        sb.AppendLine($"oGCDs per GCD window: {weaveRatio:F2}");
        sb.AppendLine();

        // ── Timeline ──────────────────────────────────────────────────────────
        sb.AppendLine("ACTION TIMELINE:");
        sb.AppendLine("format: [timestamp] [GCD/oGCD] [action name] [mp] [dmg if known] [flags]");
        sb.AppendLine();

        var origin  = events[0].Timestamp;
        ActionEvent? lastGcd = null;

        foreach (var ev in events)
        {
            var t    = (ev.Timestamp - origin).TotalSeconds;
            var kind = ev.IsGcd ? " GCD" : "oGCD";
            var mp   = ev.MaxMp > 0 ? $"  mp={ev.Mp}/{ev.MaxMp}" : "";
            var dmg  = ev.DamageDealt.HasValue ? $"  dmg={ev.DamageDealt}" : "";
            var crit = ev.WasCrit == true       ? " CRIT"                   : "";
            var dh   = ev.WasDh   == true       ? " DH"                     : "";

            var gapFlag = "";
            if (ev.IsGcd && lastGcd != null)
            {
                var gap = (ev.Timestamp - lastGcd.Timestamp).TotalSeconds;
                if (gap > 3.2) gapFlag = $"  ⚠ GCD GAP {gap:F1}s";
            }
            if (ev.IsGcd) lastGcd = ev;

            sb.AppendLine($"  {t,6:F2}s  [{kind}]  {ev.ActionName,-32}{mp}{dmg}{crit}{dh}{gapFlag}");
        }

        // ── Derived stats ─────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("DERIVED STATS:");

        var gapsBetweenGcds = new List<double>();
        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].IsGcd && events[i - 1].IsGcd)
                gapsBetweenGcds.Add(
                    (events[i].Timestamp - events[i - 1].Timestamp).TotalSeconds);
        }

        if (gapsBetweenGcds.Any())
        {
            sb.AppendLine($"  Avg GCD gap: {gapsBetweenGcds.Average():F2}s  " +
                          $"(min {gapsBetweenGcds.Min():F2}s, max {gapsBetweenGcds.Max():F2}s)");
            var clips = gapsBetweenGcds.Count(g => g < 2.3);
            if (clips > 0)
                sb.AppendLine($"  Possible GCD clips (<2.3s gap): {clips}");
        }

        var bigGaps = gapsBetweenGcds.Count(g => g > 3.2);
        if (bigGaps > 0)
            sb.AppendLine($"  GCD gaps >3.2s (dropped uptime): {bigGaps}");

        // Weave density per GCD window
        int doubleWeaves = 0, singleWeaves = 0, bareGcds = 0;
        for (int i = 0; i < events.Count - 1; i++)
        {
            if (!events[i].IsGcd) continue;
            int next = i + 1;
            while (next < events.Count && !events[next].IsGcd) next++;
            var weavedCount = next - i - 1;
            if (weavedCount >= 2)      doubleWeaves++;
            else if (weavedCount == 1) singleWeaves++;
            else                       bareGcds++;
        }

        sb.AppendLine($"  GCD windows — double-weave: {doubleWeaves}, " +
                      $"single-weave: {singleWeaves}, bare: {bareGcds}");

        return sb.ToString();
    }
}
