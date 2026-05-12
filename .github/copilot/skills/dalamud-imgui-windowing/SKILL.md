---
name: dalamud-imgui-windowing
description: Build ImGui windows, overlays, and config UIs in Dalamud plugins for FFXIV / XIVLauncher using the WindowSystem API and ImRaii disposable scopes. Use whenever the user is drawing a window, building an overlay, adding tables / popups / icon buttons / tooltips / collapsing headers, styling ImGui widgets, loading images and game icons, or working on the visual layer of a Dalamud plugin. Covers the Window base class, WindowSystem registration, SizeConstraints, ImRaii.PushColor / Table / Child / Combo / Group / Tooltip / Disabled, ITextureProvider image loading via ISharedImmediateTexture, FontAwesome icon buttons via ImGuiComponents, INotificationManager toasts, ImGuiHelpers.GlobalScale dimension scaling, and v15-specific gotchas like ImRaii.Group / Tooltip / Disabled no longer returning bool because the IEndObject interface was removed.
---

# Dalamud ImGui Windowing

The Dalamud team has converged on a specific shape for plugin UIs: every window is a `Window`-derived class registered with a `WindowSystem`, and ImGui state changes go through `ImRaii` disposable scopes so exceptions can never leak push/pop imbalance into the game's own ImGui frame. Use this skill for any UI work after the project has been scaffolded.

## When this skill applies

Any prompt that involves drawing — "show a window", "make an overlay", "add an ImGui table", "render party HP bars", "config window", "toolbar button", "tooltip", "popup modal", "icon button", "color the text red when X". If the user is editing `MainWindow.cs` / `ConfigWindow.cs` or any file under a `Windows/` folder, this is the right skill.

## Why WindowSystem is mandatory by guideline

`dalamud.dev/plugin-development/technical-considerations` states explicitly: "For regular windows, like settings and utility windows, you should use the Dalamud Windowing API. It enhances windows with a few nice features, like integration into the native UI closing-order, pinning, and opacity controls. If it looks like a window, it should use the windowing API."

The PR review team won't reject *existing* plugins for non-compliance, but new submissions are expected to use it. Concretely, that means: don't call `ImGui.Begin(...)` / `ImGui.End(...)` directly from a top-level draw handler. Subclass `Window` and let `WindowSystem.Draw()` orchestrate.

## Window base class — what to override and what to set

```csharp
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MyPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly string iconPath;

    public MainWindow(Plugin plugin, string iconPath)
        : base("My Plugin##MainWindow",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin   = plugin;
        this.iconPath = iconPath;

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
        ImGui.TextUnformatted($"Logged in: {Plugin.ClientState.IsLoggedIn}");
        // ...your UI...
    }
}
```

The constructor's first argument is the window's title with a trailing `##StableId` so the title can change at runtime without ImGui treating it as a new window. The `Window` properties most commonly set: `Size`, `SizeCondition`, `SizeConstraints`, `Flags`, `IsOpen`, `RespectCloseHotkey` (default `true` — set `false` for windows that should not close on Escape), `AllowPinning`, `AllowClickthrough` (for click-through overlays), `BgAlpha`, `ShowCloseButton`, `TitleBarButtons` (extra ❓/⚙ buttons next to the X). Overrides available: `Draw()` (required), `OnOpen()`, `OnClose()`, `PreOpenCheck()` (return `false` to suppress this frame's draw), `Update()` (every frame whether or not the window is open — use sparingly), `PreDraw()`, `PostDraw()`.

## Registering with the WindowSystem

The plugin owns one `WindowSystem` keyed by a namespace string, adds windows during construction, and routes `UiBuilder.Draw` to it:

```csharp
public readonly WindowSystem WindowSystem = new("MyPlugin");

public Plugin() {
    var main   = new MainWindow(this, iconPath);
    var config = new ConfigWindow(this);
    WindowSystem.AddWindow(main);
    WindowSystem.AddWindow(config);

    PluginInterface.UiBuilder.Draw += () => WindowSystem.Draw();
}

public void Dispose() {
    WindowSystem.RemoveAllWindows();
    PluginInterface.UiBuilder.Draw -= () => WindowSystem.Draw();
    // ...also Dispose() each window if it implements IDisposable.
}
```

Toggle visibility with `window.IsOpen = true/false` or `window.Toggle()`.

## ImRaii — the modern way to push ImGui state

The Dalamud v9 release notes (Sept 2023) explicitly endorsed `Dalamud.Interface.Utility.Raii.ImRaii`: "They make it a lot easier to prevent crashes your plugin may cause due to e.g. unhandled exceptions … You also will never again forget to pop a color, or that children always need to be ended."

The pattern: `using var x = ImRaii.PushSomething(...);` — the corresponding `Pop` runs when the scope ends, even on exception.

```csharp
using (ImRaii.PushColor(ImGuiCol.Text, 0xFFAA55FFu))
    ImGui.TextUnformatted("Colored line via ImRaii");

using var indent = ImRaii.PushIndent(20f);

using (var child = ImRaii.Child("##scroll", new Vector2(0, 200), border: true)) {
    if (child) {
        // render scrollable content
    }
}
```

Equivalent helpers: `Table`, `Child`, `TabItem`, `Combo`, `Group`, `Tooltip`, `Disabled`, `PushStyle`, `PushIndent`, `PushFont`, `PopupModal`, `PopupContextItem`, `Header` (collapsing), `MenuBar`, `Menu`, `TreeNode`.

### v15 breaking change you will hit

In v15 the `IEndObject` interface was removed. `ImRaii.Group`, `ImRaii.Tooltip`, and `ImRaii.Disabled` no longer return a value with a bool conversion. Pre-v15 code looked like:

```csharp
// v14 and earlier — DOES NOT COMPILE on v15
using (var t = ImRaii.Tooltip()) {
    if (t) ImGui.TextUnformatted("hint");
}
```

In v15, just use the scope directly without the `if (t)` guard:

```csharp
using (ImRaii.Tooltip())
    ImGui.TextUnformatted("hint");

using (ImRaii.Disabled(condition))
    ImGui.Button("Disabled when condition is true");

using (ImRaii.Group()) {
    ImGui.TextUnformatted("Line 1");
    ImGui.TextUnformatted("Line 2");
}
```

`ImRaii.Table`, `ImRaii.Child`, `ImRaii.TabItem`, `ImRaii.Combo`, `ImRaii.PopupModal` *still* return a wrapper that converts to `bool` — those didn't change. The pattern `if (table) { … }` continues to work for them.

## Tables (the workhorse for party / list UIs)

```csharp
using (var table = ImRaii.Table("##party", 3,
                                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
{
    if (table) {
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Job");
        ImGui.TableSetupColumn("HP");
        ImGui.TableHeadersRow();

        foreach (var member in Plugin.PartyList) {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(member.Name.TextValue);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(member.ClassJob.Value.Abbreviation.ExtractText());
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{member.CurrentHP}/{member.MaxHP}");
        }
    }
}
```

Useful flags to mix in: `Sortable`, `Resizable`, `Reorderable`, `ScrollY`, `BordersInnerH`, `BordersOuterH`, `SizingFixedFit`, `SizingStretchSame`, `Hideable`, `ContextMenuInBody`. Set fixed-width columns with `ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale)`.

## Loading textures via ITextureProvider

v10 refactored texture loading: `ITextureProvider` now returns an `ISharedImmediateTexture` — a *handle*, because the texture may load asynchronously. Inside `Draw()` you call `.GetWrapOrEmpty()` (returns a transparent placeholder while loading) or `.TryGetWrap(out var wrap, out _)`.

```csharp
// File on disk (your plugin's bundled icon, etc.)
var image = Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrEmpty();
ImGui.Image(image.Handle, new Vector2(64, 64) * ImGuiHelpers.GlobalScale);

// Game icon by ID
var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
ImGui.Image(icon.Handle, new Vector2(32, 32) * ImGuiHelpers.GlobalScale);

// Game resource path (look these up in TexTools / FFXIV-Modding-Tools)
var ui = Plugin.TextureProvider.GetFromGame("ui/uld/IconA_Frame_hr1.tex").GetWrapOrEmpty();

// Embedded resource (bundle via <EmbeddedResource> in csproj)
var embedded = Plugin.TextureProvider
    .GetFromManifestResource(typeof(Plugin).Assembly, "MyPlugin.Resources.logo.png")
    .GetWrapOrEmpty();

// Async byte[] (e.g. from a downloaded PNG)
var fromBytes = await Plugin.TextureProvider.CreateFromImageAsync(pngBytes);
```

**Caching `IDalamudTextureWrap` is explicitly discouraged** — the provider's 2-second auto-unload handles lifetime, and caching just fights it. Just call `GetFromX(...)` every frame. Don't `Dispose()` provider-returned wraps either; they're shared.

## FontAwesome icon buttons and icons-in-text

```csharp
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

// Simple icon-only button.
if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
    plugin.ToggleConfigUI();

// Icon mixed into text.
using (ImRaii.PushFont(UiBuilder.IconFont))
    ImGui.TextUnformatted(FontAwesomeIcon.Heart.ToIconString());
ImGui.SameLine();
ImGui.TextUnformatted("Healer");
```

`ImGuiComponents` (in `Dalamud.Interface.Components`) also exposes: `IconButtonWithText`, `ColorPickerWithPalette`, `HelpMarker(text)` (the standard "(?)" hover-for-help indicator), `ToggleButton`, `TextWithLabel`.

## Notifications — bottom-right toasts

These are ImGui-rendered, non-modal, and stack. Use them for "something happened, glance at it" feedback:

```csharp
Plugin.NotificationManager.AddNotification(new Notification
{
    Title           = "Hello",
    Content         = "Something happened.",
    Type            = NotificationType.Info,           // Info, Success, Warning, Error, None
    InitialDuration = TimeSpan.FromSeconds(5),
});
```

`AddNotification` returns an `IActiveNotification` — you can mutate `Content`, `Title`, `Type`, or call `DismissNow()` later.

For game-style center-screen toasts, use `IToastGui.ShowNormal` / `ShowError` / `ShowQuest` instead.

## Always scale by GlobalScale

The user's UI scale is in `ImGuiHelpers.GlobalScale`. Multiply any pixel-sized dimension (image sizes, fixed column widths, button sizes, child heights) by it:

```csharp
ImGui.Image(wrap.Handle, new Vector2(64, 64) * ImGuiHelpers.GlobalScale);
ImGui.Button("OK", new Vector2(80f * ImGuiHelpers.GlobalScale, 0f));
```

`Window.Size` is auto-scaled by `WindowSystem`, so leave that as logical units. Same for `SizeConstraints`.

## Common pitfalls (in priority order)

1. **Never call `ImGui.*` outside a `Draw` context.** There is no current ImGui frame, and the game will crash. If you need to compute state per-frame, do it in `IFramework.Update`, store the result in a field, and consume it inside `Draw()`.
2. **Never `Dispose()` Dalamud-provided textures.** `ITextureProvider` returns shared handles. Disposing them poisons the cache for every other consumer.
3. **v15: `ImRaii.Group()` / `Tooltip()` / `Disabled()` no longer return bool.** Don't write `if (t) ...` for these. Just `using (ImRaii.X()) { ... }`.
4. **Always scale by `ImGuiHelpers.GlobalScale`.** Forgetting this makes plugins look comically small or massive on non-default UI scales.
5. **v14+: exceptions inside `Draw()` stop the window from rendering** and show an error message overlay. Wrap user-facing errors gracefully — try/catch around state reads, render an error string instead of throwing.
6. **Don't push too much state without scope.** A bare `ImGui.PushStyleColor(...)` without the matching `Pop` corrupts every later frame's rendering. ImRaii fixes this for almost every case.
7. **Don't iterate `Plugin.PartyList` or `Plugin.ObjectTable` from outside `Draw` / `Framework.Update`.** They're main-thread-only (see `dalamud-services-reference`). Inside `Draw`, you're fine — the framework holds the lock.

## Cross-references

- For state to read inside `Draw()` (party, target, conditions, Lumina sheets), see `dalamud-party-and-game-state`.
- For the full service catalogue (which interface exposes what), see `dalamud-services-reference`.
- For new-project setup with a working `MainWindow` + `ConfigWindow`, see `dalamud-plugin-scaffold`.
