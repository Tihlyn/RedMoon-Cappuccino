using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace RedMoonCappuccino.UI;

/// <summary>
/// Central visual theme for every plugin window.
///
/// Palette is built around four anchor colours:
///   Navy      #131A2E — background family (window / child / frame surfaces)
///   SlateBlue #6A5ACD — primary interactive (buttons, headers, tabs, sliders)
///   Cornflower#6495ED — highlight / active state, links, section accents
///   LightSteel#B0C4DE — text family and hairline borders
///
/// Push() applies the full style (rounding, padding, colours) and returns a
/// scope that must be disposed after the window is drawn — see ThemedWindow.
/// </summary>
public static class RmcTheme
{
    // ── Anchor palette ────────────────────────────────────────────────────────

    public static readonly Vector4 SlateBlue  = new(0.416f, 0.353f, 0.804f, 1f); // #6A5ACD
    public static readonly Vector4 Cornflower = new(0.392f, 0.584f, 0.929f, 1f); // #6495ED
    public static readonly Vector4 LightSteel = new(0.690f, 0.769f, 0.871f, 1f); // #B0C4DE

    // Navy surface ramp (dark → light)
    private static readonly Vector4 NavyTitle   = new(0.051f, 0.078f, 0.141f, 1f);     // #0D1424
    private static readonly Vector4 NavyWindow  = new(0.071f, 0.102f, 0.180f, 0.985f); // #121A2E
    private static readonly Vector4 NavyChild   = new(0.090f, 0.125f, 0.227f, 1f);     // #17203A
    private static readonly Vector4 NavyPopup   = new(0.106f, 0.145f, 0.251f, 0.98f);  // #1B2540
    private static readonly Vector4 NavyFrame   = new(0.141f, 0.188f, 0.353f, 1f);     // #24305A
    private static readonly Vector4 NavyFrameHi = new(0.173f, 0.231f, 0.431f, 1f);     // #2C3B6E
    private static readonly Vector4 NavyFrameOn = new(0.208f, 0.278f, 0.541f, 1f);     // #35478A

    private static readonly Vector4 SlateButton = new(0.312f, 0.265f, 0.603f, 1f);     // slate, dimmed

    // ── Text ──────────────────────────────────────────────────────────────────

    public static readonly Vector4 Text      = new(0.863f, 0.894f, 0.961f, 1f); // steel-tinted white
    public static readonly Vector4 TextMuted = new(0.545f, 0.608f, 0.753f, 1f); // muted steel

    // ── Semantic status colours (harmonised for the navy surfaces) ────────────

    public static readonly Vector4 Success = new(0.360f, 0.780f, 0.545f, 1f);
    public static readonly Vector4 Warning = new(0.949f, 0.769f, 0.361f, 1f);
    public static readonly Vector4 Error   = new(0.929f, 0.427f, 0.467f, 1f);

    // Button bases for stateful actions (hover/active variants are derived).
    public static readonly Vector4 SuccessButton = new(0.129f, 0.380f, 0.243f, 1f);
    public static readonly Vector4 WarningButton = new(0.471f, 0.353f, 0.102f, 1f);
    public static readonly Vector4 DangerButton  = new(0.620f, 0.200f, 0.243f, 1f);

    // ── Scope ─────────────────────────────────────────────────────────────────

    public readonly struct ThemeScope : IDisposable
    {
        private readonly int varCount;
        private readonly int colorCount;

        internal ThemeScope(int varCount, int colorCount)
        {
            this.varCount   = varCount;
            this.colorCount = colorCount;
        }

        public void Dispose()
        {
            ImGui.PopStyleColor(colorCount);
            ImGui.PopStyleVar(varCount);
        }
    }

    /// <summary>Pushes the full theme. Must be paired with disposing the returned scope.</summary>
    public static ThemeScope Push()
    {
        var s    = ImGuiHelpers.GlobalScale;
        var vars = 0;
        var cols = 0;

        void V(ImGuiStyleVar sv, float value)    { ImGui.PushStyleVar(sv, value); vars++; }
        void V2(ImGuiStyleVar sv, Vector2 value) { ImGui.PushStyleVar(sv, value); vars++; }
        void C(ImGuiCol col, Vector4 value)      { ImGui.PushStyleColor(col, value); cols++; }

        // Shape: generous rounding + breathing room
        V(ImGuiStyleVar.WindowRounding,    9f * s);
        V(ImGuiStyleVar.ChildRounding,     8f * s);
        V(ImGuiStyleVar.FrameRounding,     6f * s);
        V(ImGuiStyleVar.PopupRounding,     8f * s);
        V(ImGuiStyleVar.GrabRounding,      6f * s);
        V(ImGuiStyleVar.TabRounding,       6f * s);
        V(ImGuiStyleVar.ScrollbarRounding, 12f * s);
        V(ImGuiStyleVar.WindowBorderSize,  1f);
        V(ImGuiStyleVar.ChildBorderSize,   1f);
        V(ImGuiStyleVar.PopupBorderSize,   1f);
        V(ImGuiStyleVar.FrameBorderSize,   0f);
        V(ImGuiStyleVar.ScrollbarSize,     12f * s);
        V(ImGuiStyleVar.GrabMinSize,       12f * s);
        V(ImGuiStyleVar.IndentSpacing,     18f * s);
        V(ImGuiStyleVar.DisabledAlpha,     0.55f);
        V2(ImGuiStyleVar.WindowPadding,    new Vector2(14f, 12f) * s);
        V2(ImGuiStyleVar.FramePadding,     new Vector2(10f, 6f) * s);
        V2(ImGuiStyleVar.CellPadding,      new Vector2(8f, 5f) * s);
        V2(ImGuiStyleVar.ItemSpacing,      new Vector2(8f, 7f) * s);
        V2(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6f, 5f) * s);

        // Text
        C(ImGuiCol.Text,         Text);
        C(ImGuiCol.TextDisabled, TextMuted);

        // Surfaces
        C(ImGuiCol.WindowBg,         NavyWindow);
        C(ImGuiCol.ChildBg,          NavyChild);
        C(ImGuiCol.PopupBg,          NavyPopup);
        C(ImGuiCol.MenuBarBg,        NavyChild);
        C(ImGuiCol.Border,           Fade(LightSteel, 0.18f));
        C(ImGuiCol.BorderShadow,     new Vector4(0f, 0f, 0f, 0f));
        C(ImGuiCol.TitleBg,          NavyTitle);
        C(ImGuiCol.TitleBgActive,    new Vector4(0.110f, 0.153f, 0.286f, 1f));
        C(ImGuiCol.TitleBgCollapsed, Fade(NavyTitle, 0.75f));

        // Frames (inputs, combos, checkboxes)
        C(ImGuiCol.FrameBg,        NavyFrame);
        C(ImGuiCol.FrameBgHovered, NavyFrameHi);
        C(ImGuiCol.FrameBgActive,  NavyFrameOn);

        // Interactive accents
        C(ImGuiCol.CheckMark,        Cornflower);
        C(ImGuiCol.SliderGrab,       SlateBlue);
        C(ImGuiCol.SliderGrabActive, Cornflower);
        C(ImGuiCol.Button,           SlateButton);
        C(ImGuiCol.ButtonHovered,    SlateBlue);
        C(ImGuiCol.ButtonActive,     Cornflower);
        C(ImGuiCol.Header,           Fade(SlateBlue, 0.31f));
        C(ImGuiCol.HeaderHovered,    Fade(SlateBlue, 0.48f));
        C(ImGuiCol.HeaderActive,     Fade(SlateBlue, 0.62f));
        C(ImGuiCol.Separator,        Fade(LightSteel, 0.16f));
        C(ImGuiCol.SeparatorHovered, SlateBlue);
        C(ImGuiCol.SeparatorActive,  Cornflower);
        C(ImGuiCol.ResizeGrip,       Fade(SlateBlue, 0.25f));
        C(ImGuiCol.ResizeGripHovered,Fade(SlateBlue, 0.60f));
        C(ImGuiCol.ResizeGripActive, Cornflower);

        // Tabs
        C(ImGuiCol.Tab,                NavyFrame);
        C(ImGuiCol.TabHovered,         Fade(SlateBlue, 0.85f));
        C(ImGuiCol.TabActive,          new Vector4(0.349f, 0.310f, 0.680f, 1f));
        C(ImGuiCol.TabUnfocused,       new Vector4(0.110f, 0.149f, 0.271f, 1f));
        C(ImGuiCol.TabUnfocusedActive, new Vector4(0.239f, 0.220f, 0.478f, 1f));

        // Scrollbars
        C(ImGuiCol.ScrollbarBg,          new Vector4(0.051f, 0.071f, 0.129f, 0.55f));
        C(ImGuiCol.ScrollbarGrab,        new Vector4(0.290f, 0.349f, 0.561f, 1f));
        C(ImGuiCol.ScrollbarGrabHovered, SlateBlue);
        C(ImGuiCol.ScrollbarGrabActive,  Cornflower);

        // Plots / progress bars
        C(ImGuiCol.PlotLines,            LightSteel);
        C(ImGuiCol.PlotLinesHovered,     Cornflower);
        C(ImGuiCol.PlotHistogram,        Cornflower);
        C(ImGuiCol.PlotHistogramHovered, Lighten(Cornflower, 0.15f));

        // Tables
        C(ImGuiCol.TableHeaderBg,     new Vector4(0.129f, 0.173f, 0.322f, 1f));
        C(ImGuiCol.TableBorderStrong, Fade(LightSteel, 0.26f));
        C(ImGuiCol.TableBorderLight,  Fade(LightSteel, 0.10f));
        C(ImGuiCol.TableRowBg,        new Vector4(0f, 0f, 0f, 0f));
        C(ImGuiCol.TableRowBgAlt,     new Vector4(1f, 1f, 1f, 0.026f));

        // Misc
        C(ImGuiCol.TextSelectedBg,   Fade(Cornflower, 0.35f));
        C(ImGuiCol.DragDropTarget,   Cornflower);
        C(ImGuiCol.NavHighlight,     Cornflower);
        C(ImGuiCol.ModalWindowDimBg, new Vector4(0.031f, 0.051f, 0.102f, 0.55f));

        return new ThemeScope(vars, cols);
    }

    // ── Colour helpers ────────────────────────────────────────────────────────

    /// <summary>Same colour with a different alpha.</summary>
    public static Vector4 Fade(Vector4 color, float alpha) => color with { W = alpha };

    /// <summary>Lerps the colour towards white by <paramref name="amount"/> (0–1).</summary>
    public static Vector4 Lighten(Vector4 color, float amount) => new(
        color.X + (1f - color.X) * amount,
        color.Y + (1f - color.Y) * amount,
        color.Z + (1f - color.Z) * amount,
        color.W);

    // ── Widget helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Cornflower section title with a short slate accent bar underneath —
    /// replaces the old gold ad-hoc headers.
    /// </summary>
    public static void SectionHeader(string label)
    {
        var s = ImGuiHelpers.GlobalScale;

        using (ImRaii.PushColor(ImGuiCol.Text, Cornflower))
            ImGui.TextUnformatted(label);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var y   = max.Y + 2f * s;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(min.X, y),
            new Vector2(min.X + 26f * s, y + 2.5f * s),
            ImGui.GetColorU32(SlateBlue),
            2f * s);

        ImGui.Dummy(new Vector2(0, 5f * s));
    }

    /// <summary>Draws a soft-glow status dot followed by text.</summary>
    public static void StatusDot(Vector4 dotColor, string text, Vector4? textColor = null)
    {
        var s      = ImGuiHelpers.GlobalScale;
        var radius = 3.5f * s;
        var lineH  = ImGui.GetTextLineHeight();
        var pos    = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + radius, pos.Y + lineH / 2f);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, radius + 2f * s, ImGui.GetColorU32(Fade(dotColor, 0.25f)));
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(dotColor));

        ImGui.Dummy(new Vector2(radius * 2f + 4f * s, lineH));
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, textColor ?? Text))
            ImGui.TextUnformatted(text);
    }

    /// <summary>
    /// Pushes Button / ButtonHovered / ButtonActive derived from one base colour.
    /// Use with the stateful bases (SuccessButton, WarningButton, DangerButton).
    /// </summary>
    public static ButtonColorScope PushButtonColors(Vector4 baseColor) => new(baseColor);

    public readonly struct ButtonColorScope : IDisposable
    {
        internal ButtonColorScope(Vector4 baseColor)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(baseColor, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(baseColor, 0.24f));
        }

        public void Dispose() => ImGui.PopStyleColor(3);
    }
}
