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

    public uint JobId { get; }

    /// <summary>Actions the solver can recommend but this job's sheet did not yield.</summary>
    public IReadOnlyList<SolverAction> Unresolved => unresolved;

    public bool IsComplete => unresolved.Count == 0;

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

            var name = Normalise(row.Name.ExtractText());
            if (name.Length == 0 || !wanted.TryGetValue(name, out var action)) continue;

            // A higher-level row for the same name supersedes: upgraded actions share a name.
            if (toGameId.ContainsKey(action)) continue;

            byGameId[row.RowId] = action;
            toGameId[action] = row.RowId;
            icons[action] = row.Icon;
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
