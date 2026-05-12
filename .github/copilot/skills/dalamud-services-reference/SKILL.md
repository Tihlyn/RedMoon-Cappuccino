---
name: dalamud-services-reference
description: Reference for Dalamud's 45 service interfaces in the Dalamud.Plugin.Services namespace (IClientState, IPlayerState, IObjectTable, IPartyList, IChatGui, IFramework, IDataManager, ITextureProvider, IGameInteropProvider, ICondition, IDutyState, IDtrBar, INotificationManager, etc.) used by FFXIV / XIVLauncher plugins. Use this skill whenever a Dalamud plugin needs to look up which service exposes a particular game-state property, send chat, register commands, hook framework events, query Lumina/Excel sheets, load textures, show notifications, or whenever the user asks "how do I get the current X" / "which service has Y" in a Dalamud plugin context. Critical for avoiding deprecated patterns — v15 removed IClientState.LocalPlayer (now IObjectTable.LocalPlayer), several services throw InvalidOperationException on non-main-thread access, and IPlayerState (added v14) is the right place to read ContentId / HomeWorld / CurrentWorld values that need to survive logout and zone-transition boundaries.
---

# Dalamud Services Reference

Dalamud exposes its game-side capabilities through a set of service interfaces in `Dalamud.Plugin.Services`. Plugins receive them either via constructor injection or via static `[PluginService]` properties (the latter is the de facto community standard — see `dalamud-plugin-scaffold` for the canonical pattern). This skill is the lookup table — when you don't remember which service has the property you want, find it here first.

## How to use this skill

1. Identify the kind of thing the user is asking for: game state? UI? hooking? data sheets? chat? IPC?
2. Open `references/services.md` and find the matching service.
3. Use the listed members directly — `Plugin.ChatGui.Print(...)`, `Plugin.PartyList[i]`, etc.
4. If the user is asking for something that crosses multiple services (e.g. "show party HP in a window"), pull from the relevant rows and combine; don't try to memorise everything.

## Critical correctness rules (these come up constantly)

These three rules account for most "AI taught me deprecated code" complaints. Treat them as load-bearing.

**1. The `I`-prefix on every service interface is non-negotiable.** v10 renamed every Dalamud-exposed type to add the `I` prefix: `DalamudPluginInterface → IDalamudPluginInterface`, `DtrBarEntry → IDtrBarEntry`, `PartyFinderListing → IPartyFinderListing`, `TitleScreenMenuEntry → ITitleScreenMenuEntry`. Pre-v10 samples (including the Mintlify-hosted "Services Overview" wiki) show the unprefixed forms. Those are out of date. The official `dalamud.dev` API reference is the source of truth.

**2. `IClientState.LocalPlayer` was removed in v15.** Use `IObjectTable.LocalPlayer` instead. For player identity that needs to survive across the local player's GameObject lifetime (pre-login, zone transitions, between cutscenes), use the v14+ `IPlayerState` service: `IPlayerState.ContentId`, `IPlayerState.CurrentWorld` (a `RowRef<World>`), `IPlayerState.HomeWorld`. These don't blink out the way `LocalPlayer` does during transitions.

**3. Several services are main-thread-only.** v12 made `IObjectTable`, `IPartyList`, and several `IClientState` properties throw `InvalidOperationException` when accessed from non-main threads. If you're inside a `Task`, a `Thread`, an async continuation that may have escaped the framework thread, or a chat-message handler that the user is calling from outside the tick — wrap with `Plugin.Framework.RunOnFrameworkThread(() => ...)` or `RunOnTick(...)`. You can also check `Plugin.Framework.IsInFrameworkUpdateThread` to gate code paths.

## What goes where (quick mental map)

A short shape of the namespace before opening the table:

- **Player & session state** → `IClientState`, `IPlayerState`, `IObjectTable`
- **Party / alliance / target** → `IPartyList`, `ITargetManager`, `IBuddyList`, `IFateTable`
- **Conditions & duty** → `ICondition`, `IDutyState`, `IUnlockState`
- **Frame / tick / threading** → `IFramework`
- **Chat** → `IChatGui`, `IToastGui`, `IFlyTextGui`, `INotificationManager`
- **UI surface (overlays, native UI, menus)** → `IGameGui`, `IDtrBar`, `INamePlateGui`, `IContextMenu`, `IAddonLifecycle`, `IAddonEventManager`
- **Slash commands** → `ICommandManager`
- **Game data (Lumina/Excel, files)** → `IDataManager`, `IGameConfig`, `IReliableFileStorage`
- **Textures / icons** → `ITextureProvider`, `ITextureReadbackProvider`, `ITextureSubstitutionProvider`
- **Job / gauge / market / inventory** → `IJobGauges`, `IMarketBoard`, `IGameInventory`
- **Hooking / signatures** → `IGameInteropProvider`, `ISigScanner`
- **Logging** → `IPluginLog`
- **Input** → `IKeyState`, `IGamepadState`
- **Self-test / diagnostic** → `ISelfTestRegistry`
- **String evaluation** → `ISeStringEvaluator` (experimental)

The full table — every interface, the namespace it lives in, what it's for, and its key members — is in **`references/services.md`**. Read that file when you need to look up specifics; don't try to inline-recite the whole thing.

## Note on root types that aren't in the Services namespace

A few types you'll use constantly are not in `Dalamud.Plugin.Services`:

- **`IDalamudPluginInterface`** lives in `Dalamud.Plugin`. It's the root handle: `UiBuilder`, `SavePluginConfig`, `GetPluginConfig`, `GetPluginConfigDirectory`, `Manifest`, `AssemblyLocation`, `GetIpcProvider<>`, `GetIpcSubscriber<>`, `Create<T>`, `Inject(obj)`, `OpenPluginInstallerTo`, `OpenDalamudSettingsTo`, the `LanguageChanged` event.
- **`IUiBuilder`** lives in `Dalamud.Interface`. You access it as `pluginInterface.UiBuilder`. It carries the `Draw`, `OpenConfigUi`, `OpenMainUi`, `BuildFonts`, `RebuildFonts`, `ResizeBuffers`, and `ShowUi` events that drive your plugin's render lifecycle.
- **`ICallGateProvider<...>` / `ICallGateSubscriber<...>`** live in `Dalamud.Plugin.Ipc`. These are the IPC primitives — see `dalamud-chat-commands-ipc` for the full pattern.

## Cross-references

- For the actual scaffolding of a plugin that uses these services, see `dalamud-plugin-scaffold`.
- For ImGui / window patterns that read state from these services, see `dalamud-imgui-windowing`.
- For pulling concrete game-state values (party HP, current target, in-combat checks), see `dalamud-party-and-game-state`.
- For chat I/O and slash commands, see `dalamud-chat-commands-ipc`.
- For setting up game-function hooks via `IGameInteropProvider`, see `dalamud-hooking-and-signatures`.
