using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
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
        Size          = new Vector2(360, 180);
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

        ImGui.Separator();
        ImGui.TextUnformatted("Rotation Analyser — Gemini API Key");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Free key at aistudio.google.com\n" +
            "Model: gemini-2.5-flash-lite\n" +
            "500 grounded RPD free (plugin caps at 450)");

        var keyBuf = configuration.GeminiApiKey;
        if (ImGui.InputText("##geminikey", ref keyBuf, 200, ImGuiInputTextFlags.Password))
        {
            configuration.GeminiApiKey = keyBuf;
            configuration.Save();
        }
    }
}
