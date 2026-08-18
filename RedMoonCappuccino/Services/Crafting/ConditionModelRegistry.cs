using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Holds the fitted condition model for every flag that has been characterised, and is the
/// only route by which the solver may obtain one.
///
/// <para><strong>The gate is the reason this class exists.</strong> Weights belong to the
/// <c>ConditionsFlag</c>, and flags differing by a single bit fit measurably different
/// distributions, so an unmeasured flag is genuinely unknown rather than approximately known.
/// The registry therefore never returns weights for a flag that has not passed
/// <see cref="ConditionModelGate"/>: <see cref="TryGetAdmissible"/> hands back a model or a
/// reason, and there is no accessor that yields raw weights without that check. A solver
/// meeting its third flag gets a refusal it must handle, not an empty array it can silently
/// misuse.</para>
/// </summary>
public sealed class ConditionModelRegistry
{
    private readonly Dictionary<ushort, ConditionModel> models = new();

    /// <summary>Flags that have any data at all, characterised or not.</summary>
    public IReadOnlyCollection<ushort> KnownFlags => models.Keys;

    /// <summary>Flags whose models passed the gate.</summary>
    public IEnumerable<ushort> AdmissibleFlags => models.Where(kv => kv.Value.IsAdmissible).Select(kv => kv.Key);

    /// <summary>
    /// The solver's only entry point.
    ///
    /// Returns false for an unmeasured flag, a thin one, a corrupt one, and one whose
    /// transitions reject the model shape — with <paramref name="reason"/> saying which, so
    /// the advisory surface can tell the player that this recipe's condition set needs
    /// collecting rather than showing them advice computed from nothing.
    /// </summary>
    public bool TryGetAdmissible(ushort flag, out ConditionModel model, out string reason)
    {
        if (!models.TryGetValue(flag, out var candidate))
        {
            model  = Unmeasured(flag);
            reason = model.Explain();
            return false;
        }

        model  = candidate;
        reason = candidate.Explain();
        return candidate.IsAdmissible;
    }

    /// <summary>
    /// The model for a flag whatever its status, for diagnostics and for the collection
    /// progress display. Never call this to obtain weights for a search.
    /// </summary>
    public ConditionModel Describe(ushort flag) =>
        models.TryGetValue(flag, out var model) ? model : Unmeasured(flag);

    /// <summary>
    /// A placeholder carrying zeroed weights and <see cref="ConditionModelStatus.Absent"/>.
    ///
    /// The weights are zero rather than uniform on purpose. A uniform default is the exact
    /// shape of the failure this gate exists to prevent — it looks like a distribution, it
    /// sums to one, and it silently produces confident nonsense. Zeroes are visibly wrong to
    /// anything that inspects them, and the status refuses the model before they are read.
    /// </summary>
    private static ConditionModel Unmeasured(ushort flag)
    {
        var members = ConditionEffects.Decode(flag);
        var telegraphSource = members.FirstOrDefault(
            m => ConditionEffects.Telegraphs(m) != CraftCondition.Unknown,
            CraftCondition.Unknown);

        return new ConditionModel
        {
            Flag            = flag,
            Members         = members,
            TelegraphSource = telegraphSource,
            TelegraphTarget = ConditionEffects.Telegraphs(telegraphSource),
            Weights         = new double[ConditionEffects.TableSize],
            Status          = ConditionModelStatus.Absent,
            Evidence = new ConditionModelEvidence
            {
                FittedTransitions    = 0,
                TelegraphTransitions = 0,
                TelegraphHonoured    = 0,
                ChiSquare            = 0,
                DegreesOfFreedom     = 0,
                PValue               = 1.0,
                MaxHalfWidth         = 1.0,
                DistinctObserved     = 0,
                DeclaredCount        = ConditionEffects.DeclaredConditionCount(flag),
                UnobservedConditions = members,
            },
        };
    }

    /// <summary>Fit every flag present in the supplied transitions and grade each one.</summary>
    public void Rebuild(IReadOnlyList<ConditionTransition> transitions)
    {
        models.Clear();

        foreach (var flag in transitions.Select(t => t.Flag).Distinct())
            models[flag] = ConditionModelFitter.Fit(flag, transitions);
    }

    /// <summary>
    /// Lines that could not be parsed during the last load.
    ///
    /// Surfaced rather than swallowed. A tolerant reader that drops what it cannot understand
    /// silently discarded 98% of this project's own corpus once already — every record written
    /// before the <c>Trigger</c> field was added failed a required-property check and vanished,
    /// leaving a fit that looked plausible and was computed from a fiftieth of the data. A
    /// non-zero count here with a healthy file on disk means the schema has moved.
    /// </summary>
    public int MalformedLines { get; private set; }

    /// <summary>
    /// Load every recorded session from a directory of JSONL files and rebuild the models.
    /// Returns the number of transitions used.
    /// </summary>
    public int LoadFrom(string directory)
    {
        if (!Directory.Exists(directory)) return 0;

        MalformedLines = 0;

        var sessions = new Dictionary<string, CraftSessionHeader>(StringComparer.Ordinal);
        var samples  = new Dictionary<string, List<CraftStepSample>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
            ReadFile(path, sessions, samples);

        var transitions = ConditionModelFitter.ToTransitions(sessions, samples);
        Rebuild(transitions);
        return transitions.Count;
    }

    private void ReadFile(
        string path,
        Dictionary<string, CraftSessionHeader> sessions,
        Dictionary<string, List<CraftStepSample>> samples)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!root.TryGetProperty("type", out var typeElement)) continue;
                if (!root.TryGetProperty("data", out var dataElement)) continue;

                switch (typeElement.GetString())
                {
                    case "session":
                    {
                        var header = dataElement.Deserialize<CraftSessionHeader>();
                        if (header != null) sessions[header.Id] = header;
                        break;
                    }

                    case "step":
                    {
                        var sample = dataElement.Deserialize<CraftStepSample>();
                        if (sample == null) break;

                        if (!samples.TryGetValue(sample.SessionId, out var list))
                            samples[sample.SessionId] = list = new List<CraftStepSample>();

                        list.Add(sample);
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated final line is expected when a recording is interrupted, so one
                // unusable line must not abort the load — but it is counted, because a schema
                // change shows up here as thousands of them and nowhere else.
                MalformedLines++;
            }
        }
    }

    /// <summary>Multi-line status for the command handler, listing every flag and what it still needs.</summary>
    public string Summarise()
    {
        if (models.Count == 0) return "Condition models: none fitted.";

        var lines = new List<string> { $"Condition models: {models.Count} flag(s) fitted." };
        foreach (var (flag, model) in models.OrderBy(kv => kv.Key))
        {
            var mark = model.IsAdmissible ? "OK  " : "HOLD";
            lines.Add($"  [{mark}] {model.Explain()}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
