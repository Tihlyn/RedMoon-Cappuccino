using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace __MYPLUGIN__.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly string iconPath;

    public MainWindow(Plugin plugin, string iconPath)
        : base("__MYPLUGIN__##MainWindow",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin   = plugin;
        this.iconPath = iconPath;

        // Sizes are in logical units; multiply by ImGuiHelpers.GlobalScale
        // when you draw things sized in pixels (images, fixed columns, etc).
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(420, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Header — quick example readout. Replace with your plugin's UI.
        ImGui.TextUnformatted($"Logged in: {Plugin.ClientState.IsLoggedIn}");

        // Loading the icon every frame is fine — ITextureProvider returns a
        // shared, ref-counted handle. Do NOT cache the IDalamudTextureWrap;
        // let the provider's auto-unload manage lifetime.
        var image = Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrEmpty();
        ImGui.Image(image.Handle, new Vector2(64, 64) * ImGuiHelpers.GlobalScale);

        ImGui.Spacing();

        if (ImGui.Button("Open Settings"))
            plugin.ToggleConfigUI();

        // ImRaii is the modern, exception-safe way to push ImGui state.
        // The using-block guarantees the corresponding Pop runs even on
        // exception, so plugin draw bugs don't leak style state into the
        // game's own UI rendering.
        using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAA55FFu))
            ImGui.TextUnformatted("Colored line via ImRaii");

        // Example party-list table. v15 note: ImRaii.Table still returns a
        // wrapper that supports `if (table)` — only Group/Tooltip/Disabled
        // lost their bool conversion in v15.
        using (var table = ImRaii.Table("##party", 3,
                                        ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Job");
                ImGui.TableSetupColumn("HP");
                ImGui.TableHeadersRow();

                foreach (var member in Plugin.PartyList)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(member.Name.TextValue);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(member.ClassJob.Value.Abbreviation.ExtractText());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{member.CurrentHP}/{member.MaxHP}");
                }
            }
        }
    }
}
