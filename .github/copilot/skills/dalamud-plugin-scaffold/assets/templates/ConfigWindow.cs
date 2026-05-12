using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace __MYPLUGIN__.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("__MYPLUGIN__ Configuration",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size          = new Vector2(232, 90);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;

        // Match the runtime-toggleable "movable" setting. Flags are
        // re-evaluated each Draw via PreOpenCheck → Update, but assigning
        // here is fine for the initial state.
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Honour the "is the config window movable" toggle without rebuilding
        // the window. Keep this cheap — runs every frame the window is open.
        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |=  ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        var showOnLogin = configuration.ShowOnLogin;
        if (ImGui.Checkbox("Show main window on login", ref showOnLogin))
        {
            configuration.ShowOnLogin = showOnLogin;
            configuration.Save();
        }
    }
}
