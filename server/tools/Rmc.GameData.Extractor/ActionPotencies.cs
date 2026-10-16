using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;
using LuminaGameData = Lumina.GameData;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Rmc.GameData.Extractor;

/// <summary>
/// Reads action potencies from the game's own tooltips. Tooltip text carries level- and
/// job-gated values as macros, e.g. <c>&lt;if([gnum72&gt;=94],460,400)&gt;</c> (gnum72 = level,
/// gnum68 = job); evaluating them for a job at a level gives the exact numbers the game shows.
/// </summary>
public sealed partial class ActionPotencies(LuminaGameData game)
{
    /// <summary>A potency as the tooltip labels it: "base" for "a potency of N", "rear"/"flank" for
    /// "N when executed from a target's rear/flank", otherwise the line's label ("Combo Potency",
    /// "Rear Combo Potency", "Potency" for damage over time, ...).</summary>
    public sealed record LabelledPotency(string Label, int Value)
    {
        public override string ToString() => $"{Label}={Value}";
    }

    public sealed record ActionInfo(uint Id, string Name, int Job, int Level, int Category, string Text, IReadOnlyList<LabelledPotency> Potencies)
    {
        public int CastMs { get; init; }
        public int RecastMs { get; init; }
        public int MaxCharges { get; init; }
        public string Timing => $"cast {CastMs / 1000.0:0.0#}s recast {RecastMs / 1000.0:0.0#}s" + (MaxCharges > 1 ? $" x{MaxCharges}" : "");
    }

    /// <summary>All player actions usable by <paramref name="jobId"/> (or its base class) at or below <paramref name="level"/>.</summary>
    public IEnumerable<ActionInfo> ForJob(int jobId, int parentClassId, int level)
    {
        var tooltips = game.GetExcelSheet<ActionTransient>()!;
        foreach (var a in game.GetExcelSheet<LuminaAction>()!)
        {
            if (a.IsPvP || a.ClassJobLevel == 0 || a.ClassJobLevel > level)
                continue;
            var job = (int)a.ClassJob.RowId;
            var raw = tooltips.GetRowOrDefault(a.RowId)?.Description.ToMacroString() ?? string.Empty;
            // Proc replacements (Raiden Thrust, Drakesbane, ...) belong to job 0 and gate their
            // text on the job instead.
            var jobGated = job == 0 && raw.Contains($"gnum68=={jobId}]", StringComparison.Ordinal);
            if (job != jobId && job != parentClassId && !jobGated)
                continue;
            var text = Evaluate(raw, jobId, level);
            yield return new ActionInfo(a.RowId, a.Name.ExtractText(), job, a.ClassJobLevel, (int)a.ActionCategory.RowId, text, Potencies(text))
            { CastMs = a.Cast100ms * 100, RecastMs = a.Recast100ms * 100, MaxCharges = a.MaxCharges };
        }
    }

    /// <summary>Non-PvP actions with exactly this name, whatever job they're filed under (some replacements aren't).</summary>
    public IEnumerable<ActionInfo> Find(string name, int jobId, int level)
        => game.GetExcelSheet<LuminaAction>()!
            .Where(a => !a.IsPvP && a.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(a => Get(a.RowId, jobId, level)!)
            .Where(i => i.Text.Length > 0);

    public ActionInfo? Get(uint actionId, int jobId, int level)
    {
        var a = game.GetExcelSheet<LuminaAction>()!.GetRowOrDefault(actionId);
        if (a == null)
            return null;
        var raw = game.GetExcelSheet<ActionTransient>()!.GetRowOrDefault(actionId)?.Description.ToMacroString() ?? string.Empty;
        var text = Evaluate(raw, jobId, level);
        return new ActionInfo(actionId, a.Value.Name.ExtractText(), (int)a.Value.ClassJob.RowId, a.Value.ClassJobLevel, (int)a.Value.ActionCategory.RowId, text, Potencies(text))
            { CastMs = a.Value.Cast100ms * 100, RecastMs = a.Value.Recast100ms * 100, MaxCharges = a.Value.MaxCharges };
    }

    /// <summary>Every potency the evaluated tooltip states, labelled, in order.</summary>
    public static IReadOnlyList<LabelledPotency> Potencies(string text)
    {
        var result = new List<LabelledPotency>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            foreach (Match m in BasePotency().Matches(line))
                result.Add(new LabelledPotency(result.Any(p => p.Label == "base") ? "base2" : "base", Number(m.Groups[1].Value)));
            if (PositionalPotency().Match(line) is { Success: true } pos)
                result.Add(new LabelledPotency(pos.Groups[2].Value, Number(pos.Groups[1].Value)));
            if (LabelledLine().Match(line) is { Success: true } lab)
                result.Add(new LabelledPotency(lab.Groups[1].Value.Trim(), Number(lab.Groups[2].Value)));
        }
        return result;
    }

    private static int Number(string s) => int.Parse(s.Replace(",", string.Empty));

    /// <summary>
    /// Evaluates the tooltip's macros for the given job and level: <c>&lt;if([cond],a,b)&gt;</c>
    /// picks a branch (recursively), <c>&lt;br&gt;</c> becomes a newline, every other macro
    /// (colours, formatting) is dropped.
    /// </summary>
    public static string Evaluate(string text, int jobId, int level)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
            {
                var end = MacroEnd(text, i);
                var macro = text[(i + 1)..(end - 1)];
                var paren = macro.IndexOf('(');
                var name = paren < 0 ? macro : macro[..paren];
                if (name == "if" && paren >= 0)
                {
                    var args = SplitTopLevel(macro[(paren + 1)..^1]);
                    var cond = args[0].Trim('[', ']');
                    var branch = Condition(cond, jobId, level) ? args.ElementAtOrDefault(1) : args.ElementAtOrDefault(2);
                    sb.Append(Evaluate(branch ?? string.Empty, jobId, level));
                }
                else if (name == "br")
                {
                    sb.Append('\n');
                }
                i = end;
                continue;
            }
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // Index just past the macro starting at text[start] == '<', honouring nested macros,
    // parentheses and [conditions] (which contain '>' characters of their own).
    private static int MacroEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0) return text.Length;
                i = close;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '<' && i > start && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
            {
                i = MacroEnd(text, i) - 1;
                continue;
            }
            else if (c == '>' && depth == 0)
                return i + 1;
        }
        return text.Length;
    }

    private static bool Condition(string cond, int jobId, int level)
    {
        var m = ConditionRegex().Match(cond);
        if (!m.Success)
            return false;
        var value = m.Groups["var"].Value switch
        {
            "gnum72" => level,
            "gnum68" => jobId,
            _ => 0,
        };
        var rhs = int.Parse(m.Groups["rhs"].Value);
        return m.Groups["op"].Value switch
        {
            ">=" => value >= rhs,
            "<=" => value <= rhs,
            ">" => value > rhs,
            "<" => value < rhs,
            "==" => value == rhs,
            "!=" => value != rhs,
            _ => false,
        };
    }

    // Splits macro arguments at commas outside nested macros, parentheses and [conditions].
    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            // "\," is a literal comma (thousands separators: 1\,400).
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '<' && i + 1 < body.Length && (char.IsLetter(body[i + 1]) || body[i + 1] == '/'))
            {
                i = MacroEnd(body, i) - 1;
                continue;
            }
            switch (c)
            {
                case '(' or '[': depth++; break;
                case ')' or ']': depth--; break;
                case ',' when depth == 0:
                    parts.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(body[start..]);
        return parts;
    }

    [GeneratedRegex(@"(?<var>gnum\d+)\s*(?<op>>=|<=|==|!=|>|<)\s*(?<rhs>-?\d+)")]
    private static partial Regex ConditionRegex();

    [GeneratedRegex(@"potency of (\d[\d,]*)")]
    private static partial Regex BasePotency();

    [GeneratedRegex(@"^(\d[\d,]*) when executed (?:from|in front of) a target's (rear|flank)")]
    private static partial Regex PositionalPotency();

    // "Combo Potency: 300", "Opo-opo's Fury Potency: 460", dance finishers' "2 Steps: 850",
    // Pitch Perfect's "3 Repertoire Stacks: 360".
    [GeneratedRegex(@"^([A-Za-z' -]*Potency|\d Steps?|\d Repertoire Stacks?):\s*(\d[\d,]*)$")]
    private static partial Regex LabelledLine();
}
