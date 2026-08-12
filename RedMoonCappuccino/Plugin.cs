using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.Windows;
using RedMoonCappuccino.RotationRecorder;

namespace RedMoonCappuccino;

public sealed class Plugin : IDalamudPlugin
{
    // ── Service injection ────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface     { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager      { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState         { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState         { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable         { get; private set; } = null!;
    [PluginService] internal static IGameInventory          GameInventory       { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider     { get; private set; } = null!;
    [PluginService] internal static INotificationManager    NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log                 { get; private set; } = null!;
    [PluginService] internal static IContextMenu            ContextMenu         { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager         { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui             { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    GameInterop         { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework           { get; private set; } = null!;

    private const string CommandName    = "/rmcap";
    private const string CommandDpsCheck = "/dpscheck";
    private const string CommandLiveChat = "/livechat";
    private const string CommandRoute    = "/route";

    public Configuration Configuration { get; init; }
    /// <summary>Alias used by RotationRecorder components.</summary>
    public Configuration Config => Configuration;

    public readonly DataService       DataService;
    public readonly GearPlannerService GearPlannerService;
    public readonly WebSocketService  WsService;
    public readonly ChatService       ChatService;
    public readonly NotificationService EventNotifications;
    public readonly SubmarineRouteService SubmarineRoutes;
    public readonly SubmarineGameData     SubmarineGame;
    public readonly WindowSystem      WindowSystem = new("RedMoonCappuccino");
    private readonly MainWindow   mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly AcquisitionWindow acquisitionWindow;
    private readonly ChatWindow   chatWindow;
    private readonly RouteWindow  routeWindow;

    private ActionRecorder  _actionRecorder  = null!;
    private GeminiAnalyzer  _geminiAnalyzer  = null!;
    private RecorderWindow  _recorderWindow  = null!;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // One-time migration: the WS host moved off the decommissioned box.
        // Only rewrites the exact old address, so custom addresses are left
        // untouched and the condition is false on every subsequent load.
        if (Configuration.WsServerAddress == "ws://78.116.140.30:3100")
        {
            Configuration.WsServerAddress = "ws://51.75.117.220:3100";
            Configuration.Save();
        }

        // Services
        DataService = new DataService(PluginInterface, Log);
        GearPlannerService = new GearPlannerService(PluginInterface, Log);
        WsService   = new WebSocketService(DataService, Log, Configuration);
        ChatService = new ChatService(Log, Configuration);

        // Wire image-fetch callback before starting the WS connection so no
        // snapshot is missed.
        DataService.OnImageNeeded = eventId => WsService.RequestImage(eventId);
        WsService.Start();

        EventNotifications = new NotificationService(Configuration, DataService, NotificationManager, Framework, Log);

        // Submersible route planner: the dataset works offline, the game data
        // layer only enriches it when a character is logged in.
        SubmarineRoutes = new SubmarineRouteService(PluginInterface, Log);
        SubmarineGame   = new SubmarineGameData(SubmarineRoutes, Configuration, Framework, Log);

        // Windows
        mainWindow   = new MainWindow(this, DataService, GearPlannerService);
        configWindow = new ConfigWindow(this);
        acquisitionWindow = new AcquisitionWindow(WsService, DataManager);
        chatWindow   = new ChatWindow(ChatService, Configuration);
        routeWindow  = new RouteWindow(SubmarineRoutes, SubmarineGame);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(acquisitionWindow);
        WindowSystem.AddWindow(chatWindow);
        WindowSystem.AddWindow(routeWindow);

        // DM arrival cues (chime / pop-up) while the chat window is not being watched.
        ChatService.DmReceived += OnDmReceived;

        // Rotation Recorder
        _actionRecorder = new ActionRecorder(GameInterop, ObjectTable, DataManager, ClientState, Log);
        _geminiAnalyzer = new GeminiAnalyzer();
        _recorderWindow = new RecorderWindow(this, _actionRecorder, _geminiAnalyzer);
        WindowSystem.AddWindow(_recorderWindow);

        // Context menu
        ContextMenu.OnMenuOpened += OnContextMenuOpened;

        // Command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Red Moon Cappuccino main window.",
        });
        CommandManager.AddHandler(CommandDpsCheck, new CommandInfo(OnDpsCheckCommand)
        {
            HelpMessage = "Toggle the rotation recorder window.",
        });
        CommandManager.AddHandler(CommandLiveChat, new CommandInfo(OnLiveChatCommand)
        {
            HelpMessage = "Toggle the live chat window.",
        });
        CommandManager.AddHandler(CommandRoute, new CommandInfo(OnRouteCommand)
        {
            HelpMessage = "Toggle the submersible route planner.",
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
        ChatService.DmReceived -= OnDmReceived;
        ClientState.Login -= OnLogin;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;

        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandDpsCheck);
        CommandManager.RemoveHandler(CommandLiveChat);
        CommandManager.RemoveHandler(CommandRoute);

        _recorderWindow.Dispose();
        _geminiAnalyzer.Dispose();
        _actionRecorder.Dispose();
        WindowSystem.RemoveWindow(_recorderWindow);

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        acquisitionWindow.Dispose();
        chatWindow.Dispose();
        routeWindow.Dispose();

        SubmarineGame.Dispose();
        EventNotifications.Dispose();
        ChatService.Dispose();
        WsService.Dispose();
        DataService.Dispose();
    }

    private void OnLogin()
    {
        if (Configuration.ShowOnLogin)
            mainWindow.IsOpen = true;
    }

    // ── DM arrival cues ───────────────────────────────────────────────────────

    /// <summary>In-game chat sound (&lt;se.11&gt;) played when a DM arrives unseen. Valid ids: 1–16.</summary>
    private const uint DmChimeSoundId = 5;

    // Cooldowns so a burst of DMs doesn't stack toasts or spam the chime.
    private const long DmChimeCooldownMs = 2000;
    private const long DmToastCooldownMs = 8000;
    private long lastDmChimeTick;
    private long lastDmToastTick;

    /// <summary>
    /// Raised on the chat WS receive thread for every incoming DM; marshalled to the
    /// framework thread before touching game/UI state. Nothing fires while the chat
    /// window is visible and focused. A collapsed/unfocused window gets a chime; a
    /// closed window additionally gets a pop-up notification with a message preview.
    /// </summary>
    private void OnDmReceived(RedMoonCappuccino.Models.ChatMessage dm)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            if (chatWindow.IsActivelyViewed) return;

            var now = System.Environment.TickCount64;

            if (!chatWindow.IsOpen && now - lastDmToastTick >= DmToastCooldownMs)
            {
                lastDmToastTick = now;
                EventNotifications.NotifyDm(dm.Username, dm.Text);
            }

            if (now - lastDmChimeTick >= DmChimeCooldownMs)
            {
                lastDmChimeTick = now;
                FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect(DmChimeSoundId);
            }
        });
    }

    private void OnCommand(string command, string args) => ToggleMainUI();
    private void OnDpsCheckCommand(string command, string args) => _recorderWindow.Toggle();
    private void OnLiveChatCommand(string command, string args) => chatWindow.Toggle();
    private void OnRouteCommand(string command, string args) => routeWindow.Toggle();
    private void DrawUI() => WindowSystem.Draw();
    public void ToggleConfigUI() => configWindow.Toggle();
    public void ToggleMainUI()   => mainWindow.Toggle();

    // Addons beyond the inventory family that surface item context menus.
    // HoveredItem is reliable here because the game sets it whenever an item
    // tooltip is visible, which is always the case just before right-click.
    private static readonly System.Collections.Generic.HashSet<string> ItemLinkAddons =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "ChatLog",          // item links in chat
            "ItemSearch",       // marketboard search panel
            "ItemSearchResult", // marketboard results list
        };

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        uint baseId;

        if (args.Target is MenuTargetInventory inventoryTarget)
        {
            var targetItem = inventoryTarget.TargetItem;
            if (targetItem == null || targetItem.Value.ItemId == 0) return;
            var rawId = targetItem.Value.ItemId;
            baseId = rawId >= 1_000_000u ? rawId - 1_000_000u : rawId;
        }
        else if (args.AddonName != null && ItemLinkAddons.Contains(args.AddonName))
        {
            var hovered = (uint)GameGui.HoveredItem;
            if (hovered == 0) return;
            baseId = hovered >= 1_000_000u ? hovered - 1_000_000u : hovered;
        }
        else return;

        args.AddMenuItem(new MenuItem
        {
            Name       = "Where do I find that",
            PrefixChar = 'R',
            OnClicked  = _ =>
            {
                WsService.RequestAcquisition(baseId);
                acquisitionWindow.ShowForItem(baseId);
            },
        });
    }
}
