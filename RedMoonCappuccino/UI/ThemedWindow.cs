using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RedMoonCappuccino.UI;

/// <summary>
/// Window base that wraps the draw in the shared <see cref="RmcTheme"/> scope.
/// PreDraw/PostDraw run symmetrically around ImGui.Begin/End, so styles pushed
/// here also cover the title bar and any popups opened from the window.
/// Overrides of PreDraw must call base.PreDraw() first.
/// </summary>
public abstract class ThemedWindow : Window
{
    private RmcTheme.ThemeScope themeScope;
    private bool themePushed;

    protected ThemedWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None, bool forceMainWindow = false)
        : base(name, flags, forceMainWindow)
    {
    }

    public override void PreDraw()
    {
        themeScope  = RmcTheme.Push();
        themePushed = true;
        base.PreDraw();
    }

    public override void PostDraw()
    {
        if (themePushed)
        {
            themeScope.Dispose();
            themePushed = false;
        }

        base.PostDraw();
    }
}
