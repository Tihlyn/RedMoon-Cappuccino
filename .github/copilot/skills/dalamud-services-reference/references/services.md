# Dalamud service interfaces — full reference

Every service Dalamud injects via `[PluginService]` or constructor injection. Unless noted, the interface lives in `Dalamud.Plugin.Services`.

## Table of contents

- [Root types (not in Services namespace)](#root-types-not-in-services-namespace)
- [Player and session state](#player-and-session-state)
- [Party, alliance, target, NPCs](#party-alliance-target-npcs)
- [Conditions, duty, lifecycle](#conditions-duty-lifecycle)
- [Frame, tick, threading](#frame-tick-threading)
- [Chat and notifications](#chat-and-notifications)
- [UI surface — native UI, dtr bar, nameplates, context menus](#ui-surface--native-ui-dtr-bar-nameplates-context-menus)
- [Commands](#commands)
- [Game data — Lumina, config, files](#game-data--lumina-config-files)
- [Textures and icons](#textures-and-icons)
- [Inventory, market, jobs](#inventory-market-jobs)
- [Hooking and signatures](#hooking-and-signatures)
- [Logging](#logging)
- [Input](#input)
- [Other](#other)
- [Thread safety summary](#thread-safety-summary)

---

## Root types (not in Services namespace)

| Type | Namespace | Purpose | Key members |
|---|---|---|---|
| `IDalamudPluginInterface` | `Dalamud.Plugin` | Root handle to Dalamud, passed by IoC | `UiBuilder`, `Manifest`, `AssemblyLocation`, `SavePluginConfig`, `GetPluginConfig`, `GetPluginConfigDirectory`, `GetIpcProvider<>`, `GetIpcSubscriber<>`, `Create<T>`, `Inject(obj)`, `OpenPluginInstallerTo`, `OpenDalamudSettingsTo`, `LanguageChanged` event |
| `IUiBuilder` | `Dalamud.Interface` | UI lifecycle events, accessed via `pluginInterface.UiBuilder` | `Draw` event (per-frame ImGui render), `OpenConfigUi`, `OpenMainUi`, `BuildFonts`, `RebuildFonts`, `ResizeBuffers`, `ShowUi`, `IconFontHandle`, `DefaultFontHandle`, `MonoFontHandle`, `LoadImage`, `LoadImageRaw` |
| `ICallGateProvider<...>` / `ICallGateSubscriber<...>` | `Dalamud.Plugin.Ipc` | Inter-plugin RPC | Provider: `RegisterAction`, `RegisterFunc`, `UnregisterAction`, `UnregisterFunc`, `SendMessage`. Subscriber: `Subscribe`, `Unsubscribe`, `InvokeAction`, `InvokeFunc`. Generic arity up to 8. |

## Player and session state

| Interface | Purpose | Key members |
|---|---|---|
| `IClientState` | Game session state | `IsLoggedIn`, `IsPvP`, `IsGPosing`, `TerritoryType`, `MapId`, `ClientLanguage`. Events: `Login`, `Logout`, `TerritoryChanged`, `ClassJobChanged`, `LevelChanged`, `EnterPvP`, `LeavePvP`, `CfPop`. **v15: `LocalPlayer` was removed — use `IObjectTable.LocalPlayer`.** |
| `IPlayerState` (v14+) | Player identity that survives GameObject churn | `ContentId`, `CurrentWorld` (`RowRef<World>`), `HomeWorld` (`RowRef<World>`). Use these for values that must survive logout / zone-change / cutscene boundaries. |
| `IObjectTable` | Every spawned game object | Indexer `[i]`, `LocalPlayer` (since v15), `SearchById(uint)`, implements `IEnumerable<IGameObject>`. **Main-thread-only.** |

## Party, alliance, target, NPCs

| Interface | Purpose | Key members |
|---|---|---|
| `IPartyList` | Party + alliance | `Length`, `PartyLeaderIndex`, `IsAlliance`. Indexer returns `IPartyMember`: `Name`, `World`, `ClassJob`, `CurrentHP`, `MaxHP`, `CurrentMP`, `MaxMP`, `Level`, `ObjectId`, `ContentId`, `Statuses` (status-effect list), `Position`. `GetAllianceMember(int)` for alliance slots 8–47. **Main-thread-only.** |
| `ITargetManager` | Target / focus / mouseover / soft target | `Target`, `FocusTarget`, `MouseOverTarget`, `SoftTarget`, `PreviousTarget` |
| `IBuddyList` | Companion / pet / chocobo | `CompanionBuddy`, `PetBuddy`, `BattleBuddies` |
| `IFateTable` | Active FATEs | `Length`, indexer, `IEnumerable<IFate>`, `GetFateById` |

## Conditions, duty, lifecycle

| Interface | Purpose | Key members |
|---|---|---|
| `ICondition` | Player condition flags | Indexer `[ConditionFlag]` returning bool. `Any()` returns true if any flag is set. `ConditionChange` event. Common flags: `InCombat`, `Mounted`, `Casting`, `BetweenAreas`, `BoundByDuty`, `Crafting`, `Gathering`, `OccupiedInQuestEvent`, `WatchingCutscene`, `Performing`, `Disguised` |
| `IDutyState` | Duty progress | `IsDutyStarted`, `IsBoundByDuty`. Events: `DutyStarted`, `DutyWiped`, `DutyRecommenced`, `DutyCompleted` (all pass `(sender, ushort territoryId)`) |
| `IGameLifecycle` | Game-process lifecycle | `DalamudUnloadingTask`, `GameShuttingDownTask` — useful for graceful shutdown logic |
| `IAgentLifecycle` | Native UI agent open/close events | `RegisterListener(AgentLifecycleEvent, AgentId, handler)` |
| `IUnlockState` | Track unlocks (mounts, minions, recipes, achievements) | `IsUnlocked(uint)` |
| `ISelfTestRegistry` | Plugin self-tests for Dalamud's diagnostic harness | `Add(ISelfTestStep)` |

## Frame, tick, threading

| Interface | Purpose | Key members |
|---|---|---|
| `IFramework` | Game thread tick & dispatch | `Update` event (per-frame), `RunOnFrameworkThread(Action)`, `RunOnFrameworkThread<T>(Func<T>)`, `RunOnTick(Action, TimeSpan delay = default, int delayTicks = 0, CancellationToken = default)`, `IsInFrameworkUpdateThread`, `LastUpdate`, `LastUpdateUTC` |

## Chat and notifications

| Interface | Purpose | Key members |
|---|---|---|
| `IChatGui` | In-game chat I/O | `Print(SeString or string)`, `Print(XivChatEntry)`, `PrintError`. Events: `ChatMessage`, `ChatMessageHandled`, `ChatMessageUnhandled`, `CheckMessageHandled`. `AddChatLinkHandler(commandId, (id, sestring) => ...)` returning a `DalamudLinkPayload`, `RemoveChatLinkHandler` |
| `IToastGui` | Game-style toast popups | `ShowNormal(string, ToastOptions?)`, `ShowError`, `ShowQuest`. `Toast` event |
| `IFlyTextGui` | Damage-popup overlay | `FlyText` event (with cancel), `AddFlyText(...)` |
| `INotificationManager` | Bottom-right ImGui notification toasts | `AddNotification(Notification)` returning `IActiveNotification` (lets you mutate it later — change content, dismiss programmatically) |

## UI surface — native UI, dtr bar, nameplates, context menus

| Interface | Purpose | Key members |
|---|---|---|
| `IGameGui` | Native UI queries | `GameUiHidden`, `WorldToScreen(Vector3, out Vector2)`, `ScreenToWorld`, `GetAddonByName(string, int = 1)`, `HoveredItem`, `HoveredAction`, `OpenMapWithMapLink`, `SetCursor` |
| `IDtrBar` | Server info-bar entries | `Get(string title)` returns `IDtrBarEntry` with `Text` (SeString), `Tooltip`, `OnClick` (Action), `Shown`, `UserHidden`, `Remove()` |
| `INamePlateGui` | Per-frame nameplate edits | `OnNamePlateUpdate` event giving an `INamePlateUpdateContext` and per-handler `INamePlateUpdateHandler[]` |
| `IContextMenu` | Right-click context menu items | `OnMenuOpened` event, `AddMenuItem`, `RemoveMenuItem` |
| `IAddonLifecycle` | Native UI addon lifecycle hooks | `RegisterListener(AddonEvent.PreSetup or PostSetup or PreFinalize or PostFinalize or PreUpdate or PostUpdate or PreDraw or PostDraw or PreReceiveEvent or PostReceiveEvent or PreRequestedUpdate or PostRequestedUpdate, "AddonName", handler)`, `UnregisterListener` |
| `IAddonEventManager` | Lower-level addon event subscriptions | `AddEvent`, `RemoveEvent`, `SetCursor`, `ResetCursor` |

## Commands

| Interface | Purpose | Key members |
|---|---|---|
| `ICommandManager` | Slash commands | `AddHandler(string command, CommandInfo info)` (info: `HelpMessage`, `ShowInHelp`), `RemoveHandler(string)`, `Commands` IDictionary |

## Game data — Lumina, config, files

| Interface | Purpose | Key members |
|---|---|---|
| `IDataManager` | Lumina/Excel access | `GetExcelSheet<T>(language? = null, name? = null)` returning `ExcelSheet<T>`. `GetSubrowExcelSheet<T>` for sub-rowed sheets (e.g. `Item`'s sub-row variants). `GetFile<T>(string path)`. Properties: `Excel`, `GameData`. Common sheets: `ClassJob`, `World`, `TerritoryType`, `Action`, `Status`, `Item`, `ContentFinderCondition`, `BNpcName`, `Mount`, `Companion`, `Achievement`, `Quest`, `OnlineStatus`. v11+ uses Lumina 5: rows are `struct` types implementing `IExcelRow<T>`, and string fields are `ReadOnlySeString` — call `.ExtractText()` to get a `string`. |
| `IGameConfig` | Game options access | `System` / `UiConfig` / `UiControl` namespaced sub-stores; `TryGet`, `Get`, `Set`. `Changed` event. Useful for reading the user's keybinds, mouse settings, UI scale, language, etc. |
| `IReliableFileStorage` | Crash-safe file writes for plugin data | `WriteAllText(path, contents)`, `ReadAllText(path)`. Wraps writes so a power loss doesn't truncate the file. Use for non-Configuration persistent state (preset libraries, cached JSON, etc.) |
| `IGameInventory` | Player inventory snapshot + change events | `GetInventoryContainer`, `GetInventoryItems`, `InventoryChanged`, `ItemChanged`, `ItemAdded`, `ItemRemoved`, `ItemMoved`, `ItemMerged`, `ItemSplit`, `ItemChangedQuantity` |

## Textures and icons

| Interface | Purpose | Key members |
|---|---|---|
| `ITextureProvider` | Load any texture (game icon, file, embedded resource, web image, clipboard) | `GetFromGameIcon(GameIconLookup lookup)` (lookup carries `IconId`, optional `ItemHq`, language). `GetFromGame(string path)` for raw game-resource paths like `ui/uld/IconA_Frame_hr1.tex`. `GetFromFile(string path)`. `GetFromManifestResource(Assembly, string resourceName)` for embedded resources (DalamudPackager bundles them with `<EmbeddedResource>`). `CreateFromImageAsync(byte[])`, `CreateFromTexFileAsync(TexFile)`, `HasClipboardImage`, `CreateFromClipboardAsync`. All return `ISharedImmediateTexture` — call `.GetWrapOrEmpty()` or `.TryGetWrap(out var wrap, out _)` inside `Draw()`. |
| `ITextureReadbackProvider` | CPU readback (read pixels back from GPU textures) | Specialised — most plugins don't need this |
| `ITextureSubstitutionProvider` | Override textures the game requests | Used by skinning / glamour plugins |

**Don't cache `IDalamudTextureWrap` long-term.** The provider's 2-second auto-unload handles lifetime; explicit caching just fights it.

## Inventory, market, jobs

| Interface | Purpose | Key members |
|---|---|---|
| `IJobGauges` | Per-job gauge values | `Get<T>()` where `T` is a job-specific gauge struct (e.g. `WHMGauge`, `BLMGauge`, `MNKGauge`). Returns a snapshot. |
| `IMarketBoard` | Market board hooks | `HistoryReceived`, `OfferingsReceived`, `PurchaseRequested`, `ItemPurchased` events |

## Hooking and signatures

| Interface | Purpose | Key members |
|---|---|---|
| `IGameInteropProvider` | Modern hooking entry point (replaces v8's `Hook.FromSignature`) | `HookFromAddress<T>(nint address, T detour)`, `HookFromSignature<T>(string sig, T detour)`, `HookFromSymbol<T>(string moduleName, string exportName, T detour)`, `InitializeFromAttributes(object)` for `[Signature]`-decorated fields |
| `ISigScanner` | Pattern scanning | `ScanText(string)`, `ScanModule(string)`, `ScanData(string)`, `TryScanText(string, out nint)`, `Resolve...` helpers, `Module` |

## Logging

| Interface | Purpose | Key members |
|---|---|---|
| `IPluginLog` | Structured logging (Serilog-style) | `Verbose(string template, params object[] args)`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. Supports `{Placeholder}` template interpolation. Includes `(Exception ex, string template, params object[])` overloads. Output goes to `%AppData%\XIVLauncher\dalamud.log` and the Dalamud Log window. |

## Input

| Interface | Purpose | Key members |
|---|---|---|
| `IKeyState` | Keyboard polling | Indexer `[VirtualKey]` returns bool, `[VirtualKey] = true/false` to consume/synthesise (use sparingly), `IndexOfKey`, `GetValidVirtualKeys`. **Polling-only — for hotkey plugins, prefer the `Dalamud.Game.Keystate` events.** |
| `IGamepadState` | Gamepad polling | Similar shape; raw axis + button state |

## Other

| Interface | Purpose | Key members |
|---|---|---|
| `ITitleScreenMenu` | Add buttons to the title-screen menu | `AddEntry(string text, IDalamudTextureWrap icon, Action onClick, ulong priority = 0, ushort flags = 0)` returns `ITitleScreenMenuEntry`. Used by plugins that want to show a launcher button before login. |
| `IPartyFinderGui` | Party Finder hooks | `ReceiveListing` event giving `IPartyFinderListing` (party leader info, slots, conditions, world, datacenter) |
| `ISeStringEvaluator` (experimental) | Evaluate `Lumina.Text.SeString` to its rendered form | `EvaluateMacroString`, `EvaluateFromAddon` |
| `IDalamudService` | Marker interface — empty, used as a type constraint by Dalamud internals | Don't use directly |
| `IConsole` | Programmatic access to the in-Dalamud console | Less commonly used by plugins |

## Thread safety summary

These services / properties throw `InvalidOperationException` (or behave incorrectly) when accessed off the framework thread:

- `IObjectTable` — indexer, enumeration, `LocalPlayer`, `SearchById`
- `IPartyList` — indexer, `Length`, enumeration, `GetAllianceMember`
- `IClientState` — `TerritoryType`, `MapId`, `IsLoggedIn` (v12+ semantics tightened these)
- `ITargetManager` — `Target`, `FocusTarget`, `MouseOverTarget`, etc.
- All FFXIVClientStructs pointers (`PlayerState.Instance()`, `GroupManager.Instance()`, etc.)

To safely consume these from a non-main thread:

```csharp
await Plugin.Framework.RunOnFrameworkThread(() => {
    var player = Plugin.ObjectTable.LocalPlayer;
    // ...read what you need, copy to a value type, return that...
});
```

Or check the gate before doing the work:

```csharp
if (!Plugin.Framework.IsInFrameworkUpdateThread) {
    Plugin.Framework.RunOnFrameworkThread(DoTheWork);
    return;
}
DoTheWork();
```

A common pattern for one-shot delayed reads: `IFramework.RunOnTick(action, delayTicks: 5)`.
