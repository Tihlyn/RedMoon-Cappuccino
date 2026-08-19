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
/// <para>Pinned directly above the game's own Synthesis window rather than floating free, because
/// the advice is read in the same glance as the durability and the condition it is about. A panel
/// the player has to look away to consult is one they stop consulting.</para>
///
/// <para>The judgement leads and everything else is subordinate to it. What a player cannot work
/// out unaided is whether <em>this</em> window is worth spending on — not which action class a
/// Sturdy calls for, which is already common knowledge. So the numbers behind the call live in a
/// tooltip, available to anyone who wants them and in nobody's way.</para>
/// </summary>
public sealed class CraftAdvisorWindow : ThemedWindow
{
    private readonly LiveCraftAdvisor advisor;

    public CraftAdvisorWindow(LiveCraftAdvisor advisor)
        : base("Craft Advisor##rmc-craft-advisor",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
             | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus)
    {
        this.advisor = advisor;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 120),
            MaximumSize = new Vector2(720, 520),
        };
    }

    /// <summary>Gap between the panel and the craft window it sits on.</summary>
    private const float Gap = 6f;

    private bool docked;

    public override void PreDraw()
    {
        base.PreDraw();

        // Pinned only while there is something to pin to. With no craft on screen the panel goes
        // back to being an ordinary movable window, so it can still be found and read.
        var bounds = advisor.CraftWindow;
        docked = advisor.CraftOpen && bounds.Z > 1 && bounds.W > 1;

        if (!docked)
        {
            Flags &= ~(ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar);
            return;
        }

        Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar;

        var s = ImGuiHelpers.GlobalScale;
        var width = MathF.Max(bounds.Z, 340f * s);
        var height = MeasuredHeight * s;

        ImGui.SetNextWindowPos(new Vector2(bounds.X, bounds.Y - height - Gap * s), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
    }

    /// <summary>Height the docked panel reserves. Fixed so the pin does not jitter as text rewraps.</summary>
    private const float MeasuredHeight = 176f;

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
                ImGui.TextWrapped("No craft in progress. Start an expert recipe and this will pin "
                                + "itself above the synthesis window.");
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
            DrawFooter(advice);
            return;
        }

        var color = PostureColor(advice.Posture);

        // ── the action, and the verdict beside it ──
        using (ImRaii.Group())
        {
            var icon = advisor.Actions?.Icon(advice.Recommended) ?? 0;
            var side = 44f * s;

            if (advice.Recommended != CraftAction.None && icon != 0
                && Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var texture)
                && texture.TryGetWrap(out var wrap, out _) && wrap != null)
            {
                var origin = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddRectFilled(
                    origin - new Vector2(2f * s, 2f * s),
                    origin + new Vector2(side + 2f * s, side + 2f * s),
                    ImGui.GetColorU32(RmcTheme.Fade(color, 0.22f)), 6f * s);

                ImGui.Image(wrap.Handle, new Vector2(side, side));
            }
            else
            {
                ImGui.Dummy(new Vector2(side, side));
            }

            ImGui.SameLine(0, 10f * s);

            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, color))
                    ImGui.TextWrapped(advice.Verdict);

                if (advice.CostsDelineation)
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Warning))
                        ImGui.TextUnformatted("Spends a Crafter's Delineation");
                else
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                        ImGui.TextUnformatted(advice.Because);
            }
        }

        ImGui.Dummy(new Vector2(0, 6f * s));

        // ── the craft at a glance ──
        var recipe = advisor.Recipe;
        if (recipe is { } spec && advisor.Tracking)
        {
            var state = advisor.State;

            Meter("Quality", state.Quality, spec.RequiredQuality, spec.MaxQuality,
                  state.Quality >= spec.RequiredQuality ? RmcTheme.Success : color);

            Meter("Progress", state.Progress, spec.Difficulty, spec.Difficulty, RmcTheme.LightSteel);

            ImGui.Dummy(new Vector2(0, 2f * s));

            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"{PostureLabel(advice.Posture)}  ·  "
                                    + $"{advice.ClearChance * 100:0}% to clear  ·  "
                                    + $"{state.Durability} durability  ·  {state.Cp} CP  ·  "
                                    + $"step {state.Step}");
        }

        DrawFooter(advice);
        DrawInternals(advice);
    }

    /// <summary>
    /// A labelled bar with the number that matters marked on it.
    ///
    /// <para>Quality is drawn against the <em>requirement</em> rather than the maximum, with the
    /// maximum only setting the scale. On a recipe asking for 31,500 of 31,520 those are nearly the
    /// same line, but on any other they are not, and the requirement is the one that decides whether
    /// the craft was worth making.</para>
    /// </summary>
    private static void Meter(string label, int value, int threshold, int scale, Vector4 fill)
    {
        var s = ImGuiHelpers.GlobalScale;
        var height = 14f * s;
        var labelWidth = 62f * s;

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
            ImGui.TextUnformatted(label);

        ImGui.SameLine(labelWidth);

        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(60f * s, ImGui.GetContentRegionAvail().X - 4f * s);
        var draw = ImGui.GetWindowDrawList();
        var rounding = height * 0.35f;

        draw.AddRectFilled(origin, origin + new Vector2(width, height),
                           ImGui.GetColorU32(RmcTheme.Fade(RmcTheme.LightSteel, 0.12f)), rounding);

        var filled = scale <= 0 ? 0 : Math.Clamp(value / (float)scale, 0f, 1f);
        if (filled > 0)
            draw.AddRectFilled(origin, origin + new Vector2(width * filled, height),
                               ImGui.GetColorU32(fill), rounding);

        if (threshold > 0 && threshold < scale)
        {
            var x = origin.X + width * Math.Clamp(threshold / (float)scale, 0f, 1f);
            draw.AddLine(new Vector2(x, origin.Y - 1f * s), new Vector2(x, origin.Y + height + 1f * s),
                         ImGui.GetColorU32(RmcTheme.Text), 1.5f * s);
        }

        ImGui.Dummy(new Vector2(width, height));
        ImGui.SameLine(labelWidth + 6f * s);

        using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.Text))
            ImGui.TextUnformatted($"{value:N0} / {threshold:N0}");

        ImGui.Dummy(new Vector2(0, 2f * s));
    }

    /// <summary>
    /// The stats being solved for, and the auto-play toggle.
    ///
    /// <para>The stats are shown because the advice is only as good as them. A solver quietly working
    /// from the wrong control value produces confident wrong answers, and this project has already
    /// spent ten changes on exactly that failure in its own benchmark.</para>
    /// </summary>
    private void DrawFooter(CraftAdvice advice)
    {
        var s = ImGuiHelpers.GlobalScale;
        ImGui.Dummy(new Vector2(0, 3f * s));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 3f * s));

        if (advisor.Player is { } player)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                ImGui.TextUnformatted($"{player.Craftsmanship} craftsmanship  ·  {player.Control} control  "
                                    + $"·  {player.MaxCp} CP");

            if (ImGui.IsItemHovered())
                using (ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted("Read from your character when the craft started, so gear and "
                                        + "food changes are picked up by crafting again.");
                    using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                        ImGui.TextUnformatted("Good-condition multiplier is assumed to be 1.75, the relic "
                                            + "tool value, and is not yet read from the equipped tool.");
                }

            ImGui.SameLine();
        }

        var running = advisor.AutoPlay;
        var label = running ? $"Stop auto ({advisor.AutoActions})" : "Auto-play";
        var width = 108f * s;

        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - width - 12f * s));

        using (RmcTheme.PushButtonColors(running ? RmcTheme.DangerButton : RmcTheme.WarningButton))
            if (ImGui.Button(label, new Vector2(width, 0)))
                advisor.AutoPlay = !advisor.AutoPlay;

        if (ImGui.IsItemHovered())
            using (ImRaii.Tooltip())
            {
                ImGui.TextUnformatted("Plays the craft on the advisor's own recommendations.");
                using (ImRaii.PushColor(ImGuiCol.Text, RmcTheme.TextMuted))
                    ImGui.TextWrapped("A testing aid, not the product — it exists so a whole sequence can "
                                    + "be watched end to end and compared against the simulated clear "
                                    + "rate. It stops on its own when the craft ends, when the advisor "
                                    + "refuses, and the moment the simulated craft stops matching the "
                                    + "real one.");
            }
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
        // Not while a control has its own tooltip up, or the two stack on each other.
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.None) || ImGui.IsAnyItemHovered()) return;

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
