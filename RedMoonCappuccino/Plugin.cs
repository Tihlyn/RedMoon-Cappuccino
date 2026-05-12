using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.Windows;

namespace RedMoonCappuccino;

public sealed class Plugin : IDalamudPlugin
{
    // ── Service injection ────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface     { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager      { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState         { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider     { get; private set; } = null!;
    [PluginService] internal static INotificationManager    NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log                 { get; private set; } = null!;

    private const string CommandName = "/rmcap";

    public Configuration Configuration { get; init; }

    public readonly DataService       DataService;
    public readonly WebSocketService  WsService;
    public readonly WindowSystem      WindowSystem = new("RedMoonCappuccino");
    private readonly MainWindow   mainWindow;
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Services
        DataService = new DataService(PluginInterface, Log);
        WsService   = new WebSocketService(DataService, Log);

        // Wire image-fetch callback before starting the WS connection so no
        // snapshot is missed.
        DataService.OnImageNeeded = eventId => WsService.RequestImage(eventId);
        WsService.Start();

        // Windows
        var iconPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "icon.png");
        mainWindow   = new MainWindow(this, DataService);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);

        // Command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Red Moon Cappuccino main window.",
        });

        // UI hooks
        PluginInterface.UiBuilder.Draw         += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUI;

        // Auto-open on login if configured
        ClientState.Login += OnLogin;

        Log.Information("[RedMoonCappuccino] Plugin loaded.");
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;

        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;

        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();

        WsService.Dispose();
        DataService.Dispose();
    }

    private void OnLogin()
    {
        if (Configuration.ShowOnLogin)
            mainWindow.IsOpen = true;
    }

    private void OnCommand(string command, string args) => ToggleMainUI();
    private void DrawUI() => WindowSystem.Draw();
    public void ToggleConfigUI() => configWindow.Toggle();
    public void ToggleMainUI()   => mainWindow.Toggle();
}
