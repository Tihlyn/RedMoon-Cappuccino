using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models.Crafting;
using RedMoonCappuccino.Services.Crafting;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

/// <summary>
/// The advisor, as a player sees it: one verdict, one action, and how the craft is going.
///
/// <para>The judgement leads and everything else is subordinate to it. What a player cannot work
/// out unaided is whether <em>this</em> window is worth spending on — not which action class a
/// Sturdy calls for, which is already common knowledge. So the numbers behind the call live in a
/// tooltip, where they are available to anyone who wants them and in nobody's way.</para>
/// </summary>
public sealed class CraftAdvisorWindow : ThemedWindow
{
    private readonly LiveCraftAdvisor advisor;

    public CraftAdvisorWindow(LiveCraftAdvisor advisor)
        : base("Craft Advisor##rmc-craft-advisor",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.advisor = advisor;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 90),
            MaximumSize = new Vector2(560, 400),
        };
    }

    private static Vector4 PostureColor(CraftPosture posture) => posture switch
    {
        CraftPosture.Dead => RmcTheme.Error,
        CraftPosture.Behind => RmcTheme.Warning,
        CraftPosture.Ahead => RmcTheme.Success,
        _ => RmcTheme.Cornflower,
    };

    private static string PostureLabel(CraftPosture posture) => posture switch
    {
        CraftPosture.Dead => "Lost",
        CraftPosture.Behind => "Behind",
        CraftPosture.Ahead => "Ahead",
        _ => "On pace",
    };

    public override void Draw()
    {
        var advice = advisor.Advice;
        var s = ImGuiHelpers.GlobalScale;

        if (!advisor.CraftOpen)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted("No craft in progress.");
            return;
        }

        if (advice.IsRefusing)
        {
            // Refusing is a real answer, not an error state. Said plainly, with the reason, because
            // the alternative — advising from a state that may be wrong — costs a craft of materials.
            RmcTheme.StatusDot(RmcTheme.Warning, "Not advising", RmcTheme.Warning);
            ImGui.Dummy(new Vector2(0, 3f * s));
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped(advice.Refusal);
            return;
        }

        // ── the verdict ──
        var color = PostureColor(advice.Posture);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(advice.Verdict);

        ImGui.Dummy(new Vector2(0, 4f * s));

        // ── the action ──
        if (advice.Recommended != CraftAction.None)
        {
            var icon = advisor.Actions?.Icon(advice.Recommended) ?? 0;
            if (icon != 0
                && Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var texture)
                && texture.TryGetWrap(out var wrap, out _) && wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(32f * s, 32f * s));
                ImGui.SameLine();
            }

            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(CraftActions.DisplayName(advice.Recommended));

                if (advice.CostsDelineation)
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                        ImGui.TextUnformatted("Costs a Crafter's Delineation");
                else
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                        ImGui.TextUnformatted(PostureLabel(advice.Posture)
                                            + $"  ·  {advice.ClearChance * 100:0}% to clear");
            }

            ImGui.Dummy(new Vector2(0, 2f * s));
        }
        else
        {
            RmcTheme.StatusDot(color, PostureLabel(advice.Posture), color);
            ImGui.Dummy(new Vector2(0, 2f * s));
        }

        // ── the evidence ──
        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextWrapped(advice.Because);

        DrawInternals(advice);
    }

    /// <summary>
    /// Everything behind the call, on hover.
    ///
    /// <para>Hidden by default rather than absent: the confidence is a measured quantity with a
    /// method worth being able to check, and a player who wants to know why the advice is taking a
    /// risk should be able to find out without the answer crowding the answer.</para>
    /// </summary>
    private static void DrawInternals(CraftAdvice advice)
    {
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)) return;

        using var tooltip = ImRaii.Tooltip();

        RmcTheme.SectionHeader("Behind the call");

        Row("Chance to clear", $"{advice.ClearChance * 100:0.#}%  "
                             + $"({CraftAdvisor.DefaultSamples} continuations played out)");
        Row("Quality still owed", advice.Shortfall == 0 ? "none" : $"{advice.Shortfall:N0}");
        Row("Posture", PostureLabel(advice.Posture));

        if (advice.Runner != CraftAction.None)
            Row("Next best", $"{CraftActions.DisplayName(advice.Runner)}  "
                           + $"(behind by {advice.Margin:0.###})");

        if (advice.Posture == CraftPosture.Behind)
        {
            ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextWrapped(
                    "Behind the requirement, a safe finish and a ruined craft are worth the same, "
                    + "so the advice prefers actions that could still reach it over ones that "
                    + "reliably fall short. That is deliberate, not a misread.");
        }

        static void Row(string key, string value)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"{key}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(value);
        }
    }
}
