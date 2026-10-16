using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

// Gear planner tab, server half: DPS of the equipped set against BiS, the DPS-ranked upgrade
// plan and the meld solver, all from the planner server. The local stat-based planner in
// MainWindow.cs stays as the fallback whenever the server isn't configured or doesn't answer.
public partial class MainWindow
{
    private static readonly (int Seconds, string Label)[] FightLengthChoices =
    {
        (0, "Standard (~8.5 min)"), (300, "Short (~5 min)"), (720, "Long (~12 min)"),
    };

    private static readonly (string Key, string Label)[] BreakpointStats =
    {
        ("crit", "Critical Hit"), ("dh", "Direct Hit"), ("det", "Determination"),
        ("sks", "Skill Speed"), ("sps", "Spell Speed"), ("ten", "Tenacity"), ("pie", "Piety"),
    };

    private readonly PlannerServerClient plannerServer;
    private ServerPlannerState? serverPlanner;

    private sealed class ServerPlannerState
    {
        public required string Job { get; init; }
        public required GearCaptureResult Capture { get; init; }
        public Task? Run { get; set; }
        public PlannerEvaluateResponse? Evaluation { get; set; }
        public PlannerUpgradePlanResponse? Plan { get; set; }
        public string? Error { get; set; }

        public Task<PlannerCall<PlannerMeldSolveResponse>>? MeldRun { get; set; }
        public PlannerMeldSolveResponse? Melds { get; set; }
        public string? MeldError { get; set; }

        public bool Pending => Run is { IsCompleted: false };
        public bool HasResult => Evaluation != null || Plan != null;
    }

    private bool PlannerServerEnabled => plugin.Configuration.PlannerUseServer && plannerServer.IsConfigured;

    private PlannerOptions BuildPlannerOptions() => new()
    {
        FightSeconds = plugin.Configuration.PlannerFightSeconds > 0 ? plugin.Configuration.PlannerFightSeconds : null,
        BisFoodWhenUnfed = true,
    };

    /// <summary>Starts the server requests for the equipped set. Reads the game, so draw thread only.</summary>
    private void StartServerPlan(string job)
    {
        serverPlanner = null;
        if (!PlannerServerEnabled)
            return;

        var current = gearPlannerService.GetCurrentJob();
        if (!string.Equals(current, job, StringComparison.OrdinalIgnoreCase))
            return;

        var capture = GearCapture.Capture(job, Math.Clamp(plugin.Configuration.PlannerPartyBonus, 0, 5));
        if (capture == null || capture.Gear.Slots.Count == 0)
            return;

        var state = new ServerPlannerState { Job = job, Capture = capture };
        var options = BuildPlannerOptions();
        var ignoreLimits = plugin.Configuration.PlannerIgnoreWeeklyLimits;
        var hunts = plugin.Configuration.PlannerMaterialsFromHunts;
        state.Run = Task.Run(async () =>
        {
            // The wallet needs the server's currency list, and reading it touches game memory.
            var wallet = new Dictionary<string, int>(StringComparer.Ordinal);
            var meta = await plannerServer.GetMetaAsync().ConfigureAwait(false);
            if (meta.Value != null)
                wallet = await Plugin.Framework.RunOnFrameworkThread(() => GearCapture.ReadWallet(meta.Value.Currencies)).ConfigureAwait(false);

            var evaluation = plannerServer.EvaluateAsync(new PlannerEvaluateRequest { Gear = capture.Gear, Panel = capture.Panel, Options = options });
            var plan = plannerServer.PlanUpgradesAsync(new PlannerUpgradePlanRequest
            {
                Gear = capture.Gear, Options = options, Wallet = wallet,
                IgnoreWeeklyLimits = ignoreLimits, MaterialsFromHunts = hunts,
            });
            var (e, p) = (await evaluation.ConfigureAwait(false), await plan.ConfigureAwait(false));
            state.Evaluation = e.Value;
            state.Plan = p.Value;
            state.Error = e.Error ?? p.Error ?? meta.Error;
        });
        serverPlanner = state;
    }

    private void StartMeldSolve(ServerPlannerState state)
    {
        var targetGcd = plugin.Configuration.PlannerTargetGcd;
        state.Melds = null;
        state.MeldError = null;
        state.MeldRun = plannerServer.SolveMeldsAsync(new PlannerMeldSolveRequest
        {
            Gear = state.Capture.Gear,
            Options = BuildPlannerOptions(),
            TargetGcd = targetGcd > 0 ? Math.Round(targetGcd, 2) : null,
        });
    }

    private void DrawPlannerOptions()
    {
        if (!ImGui.CollapsingHeader("Planner options###plannerOptions"))
            return;

        using var indent = ImRaii.PushIndent(8f);
        var config = plugin.Configuration;
        var changed = false;

        var useServer = config.PlannerUseServer;
        if (ImGui.Checkbox("DPS figures from the planner server", ref useServer))
        {
            config.PlannerUseServer = useServer;
            changed = true;
        }
        if (!plannerServer.IsConfigured)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("(no server set up in this build)");
        }

        var lengthIndex = Math.Max(0, Array.FindIndex(FightLengthChoices, c => c.Seconds == config.PlannerFightSeconds));
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Fight length", FightLengthChoices[lengthIndex].Label))
        {
            foreach (var (seconds, label) in FightLengthChoices)
            {
                if (ImGui.Selectable(label, seconds == config.PlannerFightSeconds))
                {
                    config.PlannerFightSeconds = seconds;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        var partyBonus = config.PlannerPartyBonus;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Party bonus %", ref partyBonus, 0, 5))
        {
            config.PlannerPartyBonus = partyBonus;
            changed = true;
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("+1% main stat per role in the party (tank, healer, melee, ranged, caster). 5 in a usual raid party.");

        var gcd = config.PlannerTargetGcd;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Target GCD", ref gcd, 0.01f, 0.05f, gcd > 0 ? "%.2f s" : "any"))
        {
            config.PlannerTargetGcd = gcd < 1.5f ? 0f : Math.Min(gcd, 2.5f);
            changed = true;
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("The meld solver keeps this GCD. 0 lets it pick whatever does the most damage.");

        var ignoreLimits = config.PlannerIgnoreWeeklyLimits;
        if (ImGui.Checkbox("Ignore weekly limits", ref ignoreLimits))
        {
            config.PlannerIgnoreWeeklyLimits = ignoreLimits;
            changed = true;
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Late in a tier, once weekly lockouts lift: plan by biggest gain first, without spacing purchases by week.");

        var hunts = config.PlannerMaterialsFromHunts;
        if (ImGui.Checkbox("Upgrade materials from hunts", ref hunts))
        {
            config.PlannerMaterialsFromHunts = hunts;
            changed = true;
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Buy twine, glaze and solvent with Sacks of Nuts, so books only go to gear.");

        if (changed)
            config.Save();
        ImGui.Spacing();
    }

    /// <summary>Server results for the solved job. False when there are none, so the local planner draws instead.</summary>
    private bool DrawServerPlanner(string job)
    {
        var state = serverPlanner;
        if (state == null || !string.Equals(state.Job, job, StringComparison.OrdinalIgnoreCase))
        {
            if (PlannerServerEnabled && !string.Equals(gearPlannerService.GetCurrentJob(), job, StringComparison.OrdinalIgnoreCase))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                    ImGui.TextWrapped($"Switch to {job} in game and press Solve again for DPS figures.");
            }
            return false;
        }

        if (state.Pending)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("Asking the planner server…");
            return false;
        }

        if (!state.HasResult)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped("Planner server unavailable — showing the offline estimate.");
            if (state.Error != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(state.Error);
            return false;
        }

        DrawServerHeader(state);
        ImGui.Spacing();

        using var child = ImRaii.Child("##gearPlannerServerResults", new Vector2(0, 0), false);
        if (!child) return true;

        if (state.Plan != null)
            DrawServerPlan(state.Plan);
        ImGui.Spacing();
        DrawMeldPanel(state);
        return true;
    }

    private void DrawServerHeader(ServerPlannerState state)
    {
        var evaluation = state.Evaluation;
        var currentDps = evaluation?.Dps ?? state.Plan?.CurrentDps ?? 0;
        var bisDps = evaluation?.Bis?.Dps ?? state.Plan?.BisDps ?? 0;

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Cornflower))
            ImGui.TextUnformatted($"{currentDps:N0} DPS now");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted("·");
        ImGui.SameLine();
        ImGui.TextUnformatted(bisDps > 0 ? $"BiS {bisDps:N0} DPS ({currentDps / bisDps - 1:+0.0%;−0.0%})" : "BiS unknown");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(BuildHeaderTooltip(state));

        if (bisDps > 0)
            ImGui.ProgressBar((float)Math.Clamp(currentDps / bisDps, 0, 1), new Vector2(-1, 0), $"{currentDps / bisDps:0.0%} of BiS damage");

        if (evaluation is { PanelDiff.Count: > 0 })
        {
            RmcTheme.StatusDot(RmcTheme.Warning, "Your stats differ slightly from what the planner computes.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Computed minus in-game:\n" + string.Join("\n",
                    evaluation.PanelDiff.Select(kv => $"{StatLabel(kv.Key)}: {kv.Value:+#;−#}")) +
                    "\n\nUsually a buff or level sync; otherwise the planner data may be out of date.");
        }
        if (evaluation is { UnknownSlots.Count: > 0 })
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped($"{evaluation.UnknownSlots.Count} equipped piece(s) are older than the planner's data and count as empty.");
        }
    }

    private static string BuildHeaderTooltip(ServerPlannerState state)
    {
        var lines = new List<string>();
        if (state.Evaluation is { } e)
        {
            lines.Add($"GCD {e.Derived.Gcd:0.00}s · Crit {e.Derived.CritRate:0.0%} (×{e.Derived.CritDamage:0.000}) · DH {e.Derived.DirectHitRate:0.0%} · Det ×{e.Derived.Determination:0.000}");
            if (e.Bis != null)
                lines.Add($"BiS: {e.Bis.Name} (GCD {e.Bis.Gcd:0.00}s)");
            lines.Add($"Fight length {e.FightSeconds / 60:0.0} min, expected damage, no party buffs but your own.");
            if (state.Capture.Gear.Food == null)
                lines.Add("Not fed right now: counted with the BiS set's food.");
            if (e.OutsideFittedRange)
                lines.Add("Your speed is outside the fitted range; the DPS is approximate.");
            lines.Add($"Patch {e.Patch} · data {e.DataVersion}");
        }
        if (state.Error != null)
            lines.Add(state.Error);
        return string.Join("\n", lines);
    }

    private void DrawServerPlan(PlannerUpgradePlanResponse plan)
    {
        if (plan.Steps.Count == 0)
        {
            RmcTheme.StatusDot(RmcTheme.Success, plan.Drops.Count > 0
                ? "Nothing left to buy — the remaining upgrades only drop."
                : "Nothing left to buy for this BiS set.");
        }
        else
        {
            RmcTheme.SectionHeader("Upgrade order");
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(plan.WeeksToFinish is int weeks
                    ? $"{plan.Steps.Count} steps, done {(weeks == 0 ? "right away" : $"in week {weeks}")}: {plan.CurrentDps:N0} → {plan.FinalDps:N0} DPS"
                    : $"{plan.Steps.Count} steps: {plan.CurrentDps:N0} → {plan.FinalDps:N0} DPS");
            DrawServerSteps(plan);
        }

        if (plan.FinalRemeldSlots.Count > 0 && plan.FinalRemeldGain >= 1)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped($"Finally re-meld {string.Join(", ", plan.FinalRemeldSlots.Select(PlannerDisplay.SlotName))}: +{plan.FinalRemeldGain:N0} DPS");
        }

        if (plan.Drops.Count > 0 && ImGui.CollapsingHeader($"Drop-only upgrades ({plan.Drops.Count})###plannerDrops"))
        {
            using var indent = ImRaii.PushIndent(8f);
            foreach (var drop in plan.Drops.OrderByDescending(d => d.GainIfObtained))
            {
                DrawItemIcon(drop.ItemId, ImGui.GetTextLineHeight());
                ImGui.SameLine();
                ImGui.TextUnformatted($"{drop.Item} — +{drop.GainIfObtained:N0} DPS");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{PlannerDisplay.SlotName(drop.Slot)} · {PlannerDisplay.SourceLabel(drop.SourceType)}");
            }
        }

        if (plan.NoGain.Count > 0 && ImGui.CollapsingHeader($"No DPS gain ({plan.NoGain.Count})###plannerNoGain"))
        {
            using var indent = ImRaii.PushIndent(8f);
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                foreach (var entry in plan.NoGain)
                    ImGui.TextWrapped(entry);
        }
    }

    private void DrawServerSteps(PlannerUpgradePlanResponse plan)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var table = ImRaii.Table("##plannerServerSteps", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
        if (!table) return;

        ImGui.TableSetupColumn("Week", ImGuiTableColumnFlags.WidthFixed, 40f * scale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthFixed, 70f * scale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableHeadersRow();

        foreach (var step in plan.Steps)
        {
            var tooltip = BuildServerStepTooltip(step);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(step.Week == 0 ? "now" : step.Week.ToString());

            ImGui.TableNextColumn();
            var iconSize = ImGui.GetTextLineHeight() * 2f + ImGui.GetStyle().ItemSpacing.Y;
            DrawItemIcon(step.ToItemId, iconSize);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted($"{step.ToItem} (i{step.ToItemLevel})");
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(step.Kind == "Augment"
                    ? $"augment · {PlannerDisplay.SlotName(step.Slot)}"
                    : string.IsNullOrEmpty(step.FromItem) ? $"{PlannerDisplay.SlotName(step.Slot)} · fills an empty slot" : $"{PlannerDisplay.SlotName(step.Slot)} · replaces {step.FromItem}");
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, step.Gain > 0 ? RmcTheme.Success : RmcTheme.TextMuted))
                ImGui.TextUnformatted($"{step.Gain:+#,0;−#,0;0}");
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(step.DpsBefore > 0 ? $"{step.Gain / step.DpsBefore:+0.0%;−0.0%}" : string.Empty);

            ImGui.TableNextColumn();
            ImGui.TextWrapped(step.CostText);
        }
    }

    private static string BuildServerStepTooltip(PlannerStep step)
    {
        var lines = new List<string> { $"{step.ToItem} (i{step.ToItemLevel}) — {PlannerDisplay.SlotName(step.Slot)}" };
        lines.Add($"{step.DpsBefore:N0} → {step.DpsAfter:N0} DPS");
        if (step.Melds.Count > 0)
            lines.Add($"Meld: {FormatMelds(step.Melds)}");
        if (step.RemeldSlots.Count > 0)
            lines.Add($"Then re-meld {string.Join(", ", step.RemeldSlots.Select(PlannerDisplay.SlotName))} (+{step.RemeldGain:N0} DPS)");
        if (step.Kind == "Augment")
            lines.Add("Losing damage before the augment is expected: the base piece is only a step to it.");
        return string.Join("\n", lines);
    }

    private void DrawMeldPanel(ServerPlannerState state)
    {
        if (!ImGui.CollapsingHeader("Melds###plannerMelds"))
            return;

        using var indent = ImRaii.PushIndent(8f);
        if (state.MeldRun is { IsCompleted: true } run)
        {
            state.Melds = run.Result.Value;
            state.MeldError = run.Result.Error;
            state.MeldRun = null;
        }

        using (ImRaii.Disabled(state.MeldRun != null))
        {
            if (ImGui.Button(state.MeldRun != null ? "Solving…" : "Solve melds"))
                StartMeldSolve(state);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Best materia for your current gear (same pieces, any melds).");

        var solved = state.Melds;
        if (state.MeldError != null)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Error))
                ImGui.TextUnformatted("The meld solver didn't answer.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(state.MeldError);
        }
        else if (solved != null)
        {
            ImGui.SameLine();
            if (solved.Infeasible)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                    ImGui.TextUnformatted("That GCD can't be reached with this gear.");
            }
            else
            {
                using (ImRaii.PushColor(ImGuiCol.Text, solved.Gain >= 1 ? RmcTheme.Success : RmcTheme.TextMuted))
                    ImGui.TextUnformatted(solved.Gain >= 1
                        ? $"+{solved.Gain:N0} DPS ({solved.Gain / Math.Max(solved.CurrentDps, 1):+0.00%}), GCD {solved.Gcd:0.00}s"
                        : "Your melds are already the best for this gear.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Solved in {solved.ElapsedMs:N0} ms · {solved.CurrentDps:N0} → {solved.Dps:N0} DPS");
        }

        if (solved is { Infeasible: false })
            DrawMeldTable(state.Capture, solved);

        var breakpoints = solved is { Infeasible: false } ? solved.Breakpoints : state.Evaluation?.Breakpoints;
        if (breakpoints is { Count: > 0 })
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted(solved is { Infeasible: false } ? "Stat tiers with the suggested melds" : "Stat tiers now");
            DrawBreakpointBars(breakpoints);
        }
    }

    private void DrawMeldTable(GearCaptureResult capture, PlannerMeldSolveResponse solved)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var table = ImRaii.Table("##plannerMeldTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
        if (!table) return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 74f * scale);
        ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("Suggested", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableHeadersRow();

        foreach (var (slot, piece) in solved.Gear.Slots.OrderBy(kv => SlotOrder(kv.Key)))
        {
            var now = capture.Gear.Slots.TryGetValue(slot, out var current) ? FormatMelds(current.Materia) : string.Empty;
            var suggested = FormatMelds(piece.Materia);
            if (now.Length == 0 && suggested.Length == 0)
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(PlannerDisplay.SlotName(slot));
            if (ImGui.IsItemHovered() && capture.ItemNames.TryGetValue(slot, out var itemName))
                ImGui.SetTooltip(itemName);

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped(now.Length > 0 ? now : "—");

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, now == suggested ? RmcTheme.TextMuted : RmcTheme.Success))
                ImGui.TextWrapped(now == suggested ? "keep" : suggested);
        }
    }

    private static void DrawBreakpointBars(IReadOnlyList<PlannerBreakpoint> breakpoints)
    {
        var labelWidth = 110f * ImGuiHelpers.GlobalScale;
        foreach (var (key, label) in BreakpointStats)
        {
            var bp = breakpoints.FirstOrDefault(b => b.Stat == key);
            if (bp == null || bp.NextTier <= bp.TierStart)
                continue;

            ImGui.TextUnformatted($"{label} {bp.Value:N0}");
            ImGui.SameLine(labelWidth);
            var span = bp.NextTier - bp.TierStart;
            var toNext = bp.NextTier - bp.Value;
            var overlay = bp.GcdCs is int gcd && bp.NextGcdCs is int nextGcd
                ? $"GCD {gcd / 100.0:0.00}s · {nextGcd / 100.0:0.00}s at +{toNext}"
                : $"+{toNext} to next tier";
            ImGui.ProgressBar((float)(bp.Value - bp.TierStart) / span, new Vector2(-1, 0), overlay);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{bp.Value - bp.TierStart} point(s) past the tier start do nothing until {bp.NextTier:N0}.");
        }
    }

    private static string FormatMelds(IEnumerable<PlannerMeld> melds)
        => string.Join(" · ", melds.Select(m => $"{StatLabel(m.Stat)} {ToRoman(m.Grade)}"));

    private static string StatLabel(string key)
        => BreakpointStats.FirstOrDefault(s => s.Key == key).Label is { } label
            ? label switch { "Critical Hit" => "Crit", "Direct Hit" => "DH", "Determination" => "Det", "Skill Speed" => "SkS", "Spell Speed" => "SpS", "Tenacity" => "Ten", _ => label }
            : key;

    private static int SlotOrder(string slot)
        => Array.IndexOf(new[] { "WEAPON", "OFFHAND", "HEAD", "BODY", "HANDS", "LEGS", "FEET", "EARS", "NECK", "WRISTS", "RING_L", "RING_R" }, slot);

    private static string ToRoman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI",
        7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X", 11 => "XI", 12 => "XII", _ => n.ToString(),
    };
}
