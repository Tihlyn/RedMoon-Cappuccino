---
name: dalamud-chat-commands-ipc
description: Add slash commands, print rich chat output (with map / item / Party Finder / player links), listen passively to chat events, and talk between Dalamud plugins via CallGate IPC for FFXIV / XIVLauncher. Use whenever a Dalamud plugin registers a /command, prints to chat with SeStringBuilder, listens to ChatGui.ChatMessage, or calls into another plugin like Penumbra, Glamourer, Honorific, or SimpleTweaks via GetIpcProvider / GetIpcSubscriber. Includes the auto-reply anti-pattern warning — automating responses to /tells, broadcasting on /yell or /sh, or spoofing chat messages all get plugins rejected from the official DalamudPluginsD17 repo. Also covers the SeString / Lumina.Text.SeStringBuilder modernisation path and the standard SeString factory methods (CreateMapLink, CreateItemLink, CreatePartyFinderLink, CreatePlayerLink) plus the DalamudLinkPayload pattern for clickable custom chat links.
---

# Dalamud Chat, Commands, and IPC

This skill covers everything that flows through chat or between plugins: registering slash commands, printing styled / linked output, listening to chat events without crossing into automation territory, and inter-plugin RPC via Dalamud's CallGate.

## When this skill applies

Trigger phrases include "slash command", "register /foo", "print to chat", "rich chat link", "chat handler", "respond to a tell", "chat event", "talk to another plugin", "Penumbra IPC", "Glamourer IPC", "Honorific IPC", "broadcast", "callgate", "InvokeFunc", "RegisterFunc". If the user is editing the `OnCommand` method, hooking `IChatGui`, or building any cross-plugin integration, this is the right skill.

## Slash commands — `ICommandManager`

```csharp
private const string CommandName = "/myplug";

public Plugin()
{
    Plugin.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
    {
        HelpMessage = "/myplug [show|hide|toggle] - Control the main window.",
        ShowInHelp  = true,    // include in /xlhelp output (default: true)
    });
}

public void Dispose()
    => Plugin.CommandManager.RemoveHandler(CommandName);

private void OnCommand(string cmd, string args)
{
    var trimmed = args.Trim().ToLowerInvariant();
    switch (trimmed)
    {
        case "show":   MainWindow.IsOpen = true;  break;
        case "hide":   MainWindow.IsOpen = false; break;
        case "toggle":
        default:       MainWindow.Toggle();        break;
    }
}
```

The `cmd` argument is whatever the user typed (always starts with `/`); `args` is everything after the first space. **Always remove the handler in `Dispose()`** — orphan handlers leak across `/xlreload`.

For multiple sub-command shapes, parse with `args.Split(' ', StringSplitOptions.RemoveEmptyEntries)` and dispatch on the first token. For commands with persistent state (e.g. `/myplug add <preset name>`), validate the input *before* mutating Configuration, and surface errors via `Plugin.ChatGui.PrintError(...)` so the user sees them inline.

Aliases: register multiple `AddHandler` calls pointing at the same delegate. By convention, plugins register one or two: a primary `/<plugin-name>` and an optional short alias.

## Reading chat — listen-only patterns

```csharp
public Plugin()
{
    Plugin.ChatGui.ChatMessage += OnChatMessage;
}

private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender,
                           ref SeString message, ref bool isHandled)
{
    // Filter aggressively — this fires for every line.
    if (type != XivChatType.TellIncoming) return;

    Plugin.Log.Info($"Tell from {sender.TextValue}: {message.TextValue}");
}

public void Dispose()
    => Plugin.ChatGui.ChatMessage -= OnChatMessage;
```

`ChatMessage` fires for every chat line the client renders. The `ref bool isHandled` flag is for filter plugins to suppress the line from being shown (set it to `true`). For inspection-only handlers, leave it alone.

`XivChatType` covers everything: `Say`, `Shout`, `Yell`, `Party`, `CrossParty`, `Alliance`, `TellIncoming`, `TellOutgoing`, `FreeCompany`, `LinkShell1` through `LinkShell8`, `CrossLinkShell1` through `8`, `NoviceNetwork`, `Echo`, `SystemMessage`, `SystemError`, `GatheringSystemMessage`, `ErrorMessage`, plus combat-log subtypes you almost never want to subscribe to in a community plugin.

### What you must not do here

The official repo rejects plugins that:

- Auto-reply to incoming `/tells`. Even with a "consent" toggle, this is in-scope-of-rejection because the recipient never consented.
- Broadcast on `/yell`, `/sh`, `/say`, `/p`, `/fc`, or any linkshell automatically. "Auto-summarising in /p when a wipe happens" is not OK.
- Spoof chat messages — synthesise `ChatMessage` events that look like other players said something.
- Persist other players' Lodestone IDs / `ContentId`s linked to chat history. The chat handler can *read* those for one-frame logic, but writing them to disk crosses the privacy line.

If the user describes a feature in any of these directions, push back early — don't quietly write a plugin that will be rejected.

## Rich chat output — `SeStringBuilder` and `XivChatEntry`

Plain string output is fine for trivial cases:

```csharp
Plugin.ChatGui.Print("Hello from MyPlugin!");
Plugin.ChatGui.PrintError("Something went wrong.");
```

For coloured prefixes, links, and structured output, build a `SeString` and print it as part of an `XivChatEntry`:

```csharp
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

var msg = new SeStringBuilder()
    .AddUiForeground("[MyPlugin] ", 58)            // 58 = a teal-ish UI color index
    .Append("Heading to ")
    .Append(SeString.CreateMapLink(146, 23, 195, 175))   // (TerritoryTypeId, MapId, x, y)
    .Append("!")
    .Build();

Plugin.ChatGui.Print(new XivChatEntry
{
    Type    = XivChatType.Echo,
    Message = msg,
});
```

`AddUiForeground(text, colorKey)` and `AddUiForegroundOff()` push and pop a UI color. The numeric color keys come from the game's `UIColor` Excel sheet — common ones: `1` (white), `3` (red), `8` (green), `45` (yellow), `52` (blue), `58` (teal), `567` (gray-out).

### Built-in link factories

```csharp
SeString.CreateMapLink(territoryTypeId, mapId, x, y);
SeString.CreateItemLink(itemId, isHQ);
SeString.CreatePartyFinderLink();      // opens the PF window when clicked
SeString.CreatePlayerLink(playerName, worldId);
```

These produce ready-made interactive payloads — clicking the rendered link in chat does the expected thing (open map, link item to chat input, etc.).

### Custom click handlers — `DalamudLinkPayload`

For your own clickable links, register a handler with `IChatGui.AddChatLinkHandler` and embed the returned payload:

```csharp
private uint commandId;

public Plugin()
{
    var payload = Plugin.ChatGui.AddChatLinkHandler(
        commandId: 1,
        commandHandler: (id, sestring) => {
            Plugin.Log.Info($"Custom link {id} clicked");
            ToggleMainUI();
        });

    var msg = new SeStringBuilder()
        .Add(payload)
        .AddUiForeground("[Click me]", 45)
        .Add(RawPayload.LinkTerminator)        // ends the clickable region
        .Build();

    Plugin.ChatGui.Print(msg);
}

public void Dispose() => Plugin.ChatGui.RemoveChatLinkHandler();
```

The `commandId` is your plugin's own ID space — pick small integers; multiple plugins can register the same numeric ID without conflict (Dalamud keys them by plugin assembly).

### Modern path: `Lumina.Text.SeStringBuilder` + `ISeStringEvaluator`

The newer (still-experimental as of v15) builder lives in Lumina, not Dalamud, and is faster and more correct around macro / parameter expansion. Convert between worlds with `.ToDalamudString()`:

```csharp
using Lumina.Text;

var lumina = new SeStringBuilder()
    .Append("From Lumina: ")
    .Append(/*...*/)
    .ToReadOnlySeString();
var dalamud = lumina.ToDalamudString();
Plugin.ChatGui.Print(dalamud);
```

Until `ISeStringEvaluator` and the Lumina builder are out of experimental, the Dalamud `SeStringBuilder` shown above is the safer default.

## Game-style notifications and toasts

Quick UX summary so you reach for the right tool:

| Want | Use | Where it shows |
|---|---|---|
| Bottom-right glance toast | `Plugin.NotificationManager.AddNotification(...)` | ImGui overlay, stacks |
| Center-screen game toast | `Plugin.ToastGui.ShowNormal("...")` | Native FFXIV toast |
| Quest-style toast | `Plugin.ToastGui.ShowQuest("...")` | Big yellow ribbon |
| Error toast | `Plugin.ToastGui.ShowError("...")` | Red, with sound |
| Inline in chat | `Plugin.ChatGui.Print(...)` / `PrintError(...)` | Chat log |
| Server info bar | `Plugin.DtrBar.Get("title")` → set `.Text` | Top-right server bar |

For passive feedback ("fetched 12 items"), prefer a notification or DTR bar entry over chat output — chat is finite and shared with the player's actual conversations.

## IPC between plugins — `CallGate`

Dalamud's IPC primitive is `CallGate`. The publishing plugin registers either an `Action`-shaped or `Func`-shaped gate at a string name; consumers subscribe to the same name and `InvokeAction` / `InvokeFunc`.

Naming convention is `<PluginInternalName>.<MethodName>`. Generic arity goes up to 8 type parameters.

### Provider (publishing side)

```csharp
using Dalamud.Plugin.Ipc;

public class HelloProvider : IDisposable
{
    private readonly ICallGateProvider<string, string> gate;

    public HelloProvider(IDalamudPluginInterface pi)
    {
        gate = pi.GetIpcProvider<string, string>("MyPlugin.Greet");
        gate.RegisterFunc(name => $"Hello, {name}!");
    }

    public void Dispose() => gate.UnregisterFunc();
}
```

`RegisterAction` for void methods, `RegisterFunc` for return-bearing. There's also `SendMessage(args...)` on the provider that broadcasts to all subscribers (event-style fan-out — neither side waits on the other).

### Subscriber (calling another plugin)

```csharp
using Dalamud.Plugin.Ipc.Exceptions;

var sub = pluginInterface.GetIpcSubscriber<string, string>("Glamourer.GetState");

try
{
    var state = sub.InvokeFunc(characterName);
    // ...use state...
}
catch (IpcNotReadyError)
{
    // Other plugin isn't loaded, or hasn't registered yet this session.
}
catch (IpcVersionMismatch)
{
    // Generic argument types don't match what the provider registered.
}
```

For event-style notifications (the provider calls `SendMessage` and you want to listen):

```csharp
var sub = pluginInterface.GetIpcSubscriber<int>("OtherPlugin.SomethingHappened");
sub.Subscribe(value => Plugin.Log.Info($"Got value: {value}"));
// In Dispose:
sub.Unsubscribe(theSameLambdaReference);
```

### Picking gate names that won't collide

If you're consuming someone else's gate, use the documented name from their README (Penumbra, Glamourer, Honorific all maintain IPC docs). If you're publishing your own, use `<YourInternalName>.<Method>` so collisions are impossible. Don't publish under another plugin's namespace.

### Common community IPC gates (for context — confirm names against the target plugin's README)

- **Penumbra**: `Penumbra.GetCollectionForObject`, `Penumbra.RedrawObject`, `Penumbra.SetCollectionForObject`, `Penumbra.GetMods`
- **Glamourer**: `Glamourer.GetState`, `Glamourer.ApplyState`, `Glamourer.RevertState`
- **Honorific**: `Honorific.GetCharacterTitle`, `Honorific.SetCharacterTitle`, `Honorific.ClearCharacterTitle`
- **SimpleTweaks**: various per-tweak gates

These names change between major versions; always check the consumer plugin's docs at consume time.

## Pitfalls (in priority order)

1. **Auto-reply / auto-broadcast to other players is a hard rejection trigger.** Don't write it. If the user asks for it, surface this risk in the first response, not after they've finished the plugin.
2. **Always remove command handlers and unsubscribe events in `Dispose()`.** Orphaned handlers from old plugin loads stack up across `/xlreload`s and double-fire.
3. **Don't iterate `Plugin.PartyList` or `Plugin.ObjectTable` inside a `ChatMessage` handler off-thread.** Chat handlers fire on the framework thread, so it's actually safe — but if you spawn a `Task.Run(...)` from inside one, the inner task is *not* on the framework thread. Marshal back via `RunOnFrameworkThread`.
4. **`IpcNotReadyError` is normal at startup.** Don't crash the plugin if a downstream IPC isn't there yet; treat it as "feature unavailable" and retry on demand.
5. **Don't store gate references across plugin reloads** of either side. Re-fetch on each plugin construction.
6. **`AddUiForeground` / `AddUiForegroundOff` must be balanced.** SeStringBuilder doesn't enforce it; an unbalanced color push will leak into the next chat line. Test by adding an obnoxiously bright color and confirming the next line renders normally.

## Cross-references

- For where to put the `OnCommand` handler in a fresh plugin, see `dalamud-plugin-scaffold`.
- For service properties referenced here (`IChatGui`, `ICommandManager`, `IDalamudPluginInterface`), see `dalamud-services-reference`.
- For UI alternatives to chat output (notifications, toasts, DTR bar), see `dalamud-imgui-windowing`.
- For reacting to game state inside a chat handler (current target, party HP), see `dalamud-party-and-game-state`.
