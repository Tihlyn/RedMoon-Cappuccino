using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

public class ConfigWindow : ThemedWindow, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("RedMoonCappuccino Configuration",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size          = new Vector2(440, 330);
        SizeCondition = ImGuiCond.Always;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        base.PreDraw();

        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |=  ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        RmcTheme.SectionHeader("General");

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

        ImGui.Spacing();
        RmcTheme.SectionHeader("Notifications");

        var eventNotifications = configuration.EnableEventNotifications;
        if (ImGui.Checkbox("Enable event notifications", ref eventNotifications))
        {
            configuration.EnableEventNotifications = eventNotifications;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Opt-in: shows a pop-up when a new event is detected.\n" +
            "Also enables per-event start reminders\n" +
            "(toggled on each event in the Events tab).");

        ImGui.Spacing();
        RmcTheme.SectionHeader("Free Company");

        var rosterSync = configuration.FcRosterSync;
        if (ImGui.Checkbox($"Report {configuration.FcRosterName} roster", ref rosterSync))
        {
            configuration.FcRosterSync = rosterSync;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Keeps the free company member list on the server in sync.\n" +
            "Only member names are sent, and only while you are logged in\n" +
            $"as a member of {configuration.FcRosterName}.\n\n" +
            "The roster is sent once when it changes; every other heartbeat\n" +
            "carries just a short fingerprint of it.");

        ImGui.Spacing();
        RmcTheme.SectionHeader("Rotation Analyser");

        ImGui.TextUnformatted("Gemini API Key");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Free key at aistudio.google.com\n" +
            "Model: gemini-2.5-flash-lite\n" +
            "500 grounded RPD free (plugin caps at 450)");

        var keyBuf = configuration.GeminiApiKey;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##geminikey", ref keyBuf, 200, ImGuiInputTextFlags.Password))
        {
            configuration.GeminiApiKey = keyBuf;
            configuration.Save();
        }
    }
}
