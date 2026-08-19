using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models.Crafting;
using SheetCraftAction = Lumina.Excel.Sheets.CraftAction;
using SolverAction = RedMoonCappuccino.Models.Crafting.CraftAction;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Ties the solver's actions to the client's, both ways, for the job being played.
///
/// <para>Every crafting job has its own action ids for the same action, so this has to be built per
/// job rather than written down. Matching is by name with punctuation and spacing removed, which
/// makes it an English-client assumption — the same one the condition model already rests on, since
/// the fitted weights are keyed on the condition strings the client renders. Worth stating plainly
/// rather than leaving implicit.</para>
///
/// <para>Incompleteness is reported rather than tolerated. An advisor that silently cannot name
/// half its recommendations is worse than one that says so and stops.</para>
/// </summary>
public sealed class CraftActionMap
{
    private readonly Dictionary<uint, SolverAction> byGameId = new();
    private readonly Dictionary<SolverAction, uint> toGameId = new();
    private readonly Dictionary<SolverAction, uint> icons = new();
    private readonly List<SolverAction> unresolved = new();
    private readonly List<(uint Id, string Name, int Level, bool Specialist)> offered = new();

    public uint JobId { get; }

    /// <summary>Actions the solver can recommend but this job's sheet did not yield.</summary>
    public IReadOnlyList<SolverAction> Unresolved => unresolved;

    public bool IsComplete => unresolved.Count == 0;

    /// <summary>How many of the solver's actions this job's sheets did yield.</summary>
    public int ResolvedCount => toGameId.Count;

    public CraftActionMap(IDataManager data, uint jobId, int level)
    {
        JobId = jobId;

        var sheet = data.GetExcelSheet<SheetCraftAction>();
        if (sheet == null) return;

        // Name to solver action, once, so the sheet scan is a single pass.
        var wanted = new Dictionary<string, SolverAction>(StringComparer.Ordinal);
        foreach (SolverAction action in Enum.GetValues<SolverAction>())
        {
            if (action == SolverAction.None) continue;
            wanted[Normalise(CraftActions.DisplayName(action))] = action;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (row.ClassJob.RowId != jobId) continue;
            if (row.ClassJobLevel > level) continue;

            var raw = row.Name.ExtractText();
            offered.Add((row.RowId, raw, row.ClassJobLevel, row.Specialist));

            var name = Normalise(raw);
            if (name.Length == 0 || !wanted.TryGetValue(name, out var action)) continue;

            // A higher-level row for the same name supersedes: upgraded actions share a name.
            if (toGameId.ContainsKey(action)) continue;

            byGameId[row.RowId] = action;
            toGameId[action] = row.RowId;
            icons[action] = row.Icon;
        }

        // Second pass over the Action sheet for anything CraftAction did not yield. The two sheets
        // do not partition the crafting actions cleanly, and which one holds a given action is not
        // something worth encoding as a list — asking both and taking whichever answers is both
        // shorter and less likely to rot across a patch.
        var stillMissing = new Dictionary<string, SolverAction>(StringComparer.Ordinal);
        foreach (var (name, action) in wanted)
            if (!toGameId.ContainsKey(action))
                stillMissing[name] = action;

        if (stillMissing.Count > 0)
        {
            var general = data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (general != null)
            {
                foreach (var row in general)
                {
                    if (row.RowId == 0 || !row.IsPlayerAction) continue;
                    if (row.ClassJob.RowId != jobId) continue;
                    if (row.ClassJobLevel > level) continue;

                    var name = Normalise(row.Name.ExtractText());
                    if (name.Length == 0 || !stillMissing.TryGetValue(name, out var action)) continue;
                    if (toGameId.ContainsKey(action)) continue;

                    offered.Add((row.RowId, row.Name.ExtractText() + " (Action sheet)", row.ClassJobLevel, false));
                    byGameId[row.RowId] = action;
                    toGameId[action] = row.RowId;
                    icons[action] = row.Icon;
                }
            }
        }

        foreach (var (_, action) in wanted)
            if (!toGameId.ContainsKey(action))
                unresolved.Add(action);
    }

    public bool TryResolve(uint gameActionId, out SolverAction action) =>
        byGameId.TryGetValue(gameActionId, out action);

    public bool TryGameId(SolverAction action, out uint gameActionId) =>
        toGameId.TryGetValue(action, out gameActionId);

    /// <summary>Icon id for an action, or 0 when it is not on this job's list.</summary>
    public uint Icon(SolverAction action) => icons.GetValueOrDefault(action);

    /// <summary>
    /// The full resolution attempt, for working out why an action did not resolve.
    ///
    /// <para>Prints what the solver wanted and did not get, then every craft action the sheet
    /// actually offered for this job. A count of failures says nothing useful; the names on both
    /// sides say immediately whether the problem is a spelling, a level filter, or an action that
    /// simply is not on this job's list.</para>
    /// </summary>
    public string Describe()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Craft actions for job {JobId}: {toGameId.Count} resolved, {unresolved.Count} not.");

        if (unresolved.Count > 0)
        {
            report.AppendLine("Unresolved (the solver knows these, the sheet did not yield them):");
            foreach (var action in unresolved)
                report.AppendLine($"  {CraftActions.DisplayName(action)}");
        }

        report.AppendLine($"Sheet offered {offered.Count} rows for this job and level:");
        foreach (var (id, name, level, specialist) in offered)
            report.AppendLine($"  {id,7}  lv{level,-4} {name}{(specialist ? "  [specialist]" : "")}");

        return report.ToString();
    }

    /// <summary>Lowercases and drops everything that is not a letter or digit, so "Byregot's Blessing" meets ByregotsBlessing.</summary>
    private static string Normalise(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));

        return builder.ToString();
    }
}
