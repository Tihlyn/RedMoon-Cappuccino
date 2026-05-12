using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using __MYPLUGIN__.Windows;

namespace __MYPLUGIN__;

public sealed class Plugin : IDalamudPlugin
{
    // ------------------------------------------------------------------
    // Service injection (Idiom A: static [PluginService] properties).
    // Accessible from anywhere via Plugin.ChatGui, Plugin.PartyList, etc.
    // Add or remove based on the plugin's actual needs — unused services
    // still go through the IoC container and clutter the file.
    // ------------------------------------------------------------------
    [PluginService] internal static IDalamudPluginInterface PluginInterface     { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager      { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui             { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState         { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState         { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager         { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework           { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable         { get; private set; } = null!;
    [PluginService] internal static IPartyList              PartyList           { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager       { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition           { get; private set; } = null!;
    [PluginService] internal static IDutyState              DutyState           { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider     { get; private set; } = null!;
    [PluginService] internal static INotificationManager    NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log                 { get; private set; } = null!;

    private const string CommandName = "/__myplugin__";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("__MYPLUGIN__");
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Resolve the icon path relative to the plugin's assembly location so
        // the path is correct in dev-plugin and packaged-install layouts alike.
        var iconPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "icon.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow   = new MainWindow(this, iconPath);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the __MYPLUGIN__ main window."
        });

        PluginInterface.UiBuilder.Draw         += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUI;

        // TODO: wire feature-specific handlers here, e.g.
        //   Framework.Update += OnFrameworkTick;
        //   ChatGui.ChatMessage += OnChatMessage;
        //   DutyState.DutyStarted += OnDutyStarted;
        // Always unsubscribe in Dispose() to avoid lingering handlers.

        Log.Information($"{PluginInterface.Manifest.Name} loaded.");
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;
    }

    private void OnCommand(string command, string args) => ToggleMainUI();
    private void DrawUI() => WindowSystem.Draw();
    public  void ToggleConfigUI() => ConfigWindow.Toggle();
    public  void ToggleMainUI()   => MainWindow.Toggle();
}
