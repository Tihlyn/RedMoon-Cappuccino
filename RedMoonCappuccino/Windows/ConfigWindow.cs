using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RedMoonCappuccino.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("RedMoonCappuccino Configuration",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size          = new Vector2(260, 110);
        SizeCondition = ImGuiCond.Always;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |=  ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable config window", ref movable))
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
