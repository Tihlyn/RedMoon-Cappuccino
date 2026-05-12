---
name: dalamud-plugin-scaffold
description: Scaffold a complete buildable Dalamud plugin project for XIVLauncher / Final Fantasy XIV using the modern Dalamud.NET.Sdk/15.0.0 toolchain. Use whenever the user wants to start a new Dalamud plugin, create a new XIVLauncher addon, build a custom FFXIV overlay, or describes a feature idea (party HP tracker, chat helper, dtr bar widget, etc.) without an existing project to put it in. Generates the full project tree (.csproj using the SDK approach, Plugin.cs with [PluginService] static-property service injection, Configuration implementing IPluginConfiguration, MainWindow + ConfigWindow backed by WindowSystem, manifest .json). Apply this even when the user only describes a feature without explicitly asking for a "new project" — they need a scaffold first. Do NOT use for FFXIVMinion / MMOMinion plugins (different framework, see ffxivminion-plugin-dev) or for non-FFXIV game modding.
---

# Dalamud Plugin Scaffold

Generate a complete, buildable Dalamud plugin project on a single prompt. The Dalamud ecosystem moved away from "drop a Dalamud.dll reference and hand-write everything" — as of API 14/15 the recommended path is the **Dalamud.NET.Sdk** MSBuild SDK, which auto-references `DalamudPackager`, `Dalamud.dll`, `Dalamud.Bindings.ImGui`, `FFXIVClientStructs`, `Lumina`, and `InteropGenerator.Runtime.dll`, and pins the API level. Generated projects MUST use this SDK approach, not the legacy `targets/` or `<HintPath>` patterns.

## When this skill applies

Trigger phrases include "build me a Dalamud plugin", "scaffold a new XIVLauncher plugin", "create a Dalamud project", "FFXIV plugin from scratch", or anything where the user describes an FFXIV plugin feature without referencing an existing codebase. Default API target is **15.0.0** (released April 2026, .NET 10 runtime); fall back to 14.0.2 only if the user explicitly says they need v14 compatibility.

## Required canonical patterns (non-negotiable)

These three are the biggest "AI teaches deprecated code" traps. Bake them into every scaffold:

1. **`IDalamudPluginInterface`** (with the `I` prefix) for the constructor parameter and any field. The unprefixed `DalamudPluginInterface` was renamed in v10 — any AI-written sample referencing it is out of date.
2. **All service interfaces from `Dalamud.Plugin.Services`** — `IClientState`, `IObjectTable`, `IPartyList`, `IChatGui`, etc. Always prefix with `I`.
3. **`IObjectTable.LocalPlayer` / `IPlayerState.ContentId`** for player identity. `IClientState.LocalPlayer` was REMOVED in v15. Do not write code that reads it.

If you ever find yourself about to write `DalamudPluginInterface` (no I) or `ClientState.LocalPlayer`, stop — those compile against v9 and earlier, not v15.

## What to emit

Default project tree:

```
MyPlugin/
├── MyPlugin.sln
├── Data/icon.png                  (placeholder, 64×64 minimum, 512×512 preferred)
├── MyPlugin/
│   ├── MyPlugin.csproj
│   ├── MyPlugin.json              (DalamudPackager manifest; optional in v14+ but kept for clarity)
│   ├── Plugin.cs                  (entry point, static-property service injection)
│   ├── Configuration.cs           (IPluginConfiguration)
│   └── Windows/
│       ├── MainWindow.cs
│       └── ConfigWindow.cs
├── .gitignore                     (.NET + bin/obj + .vs)
├── .editorconfig
└── README.md
```

The literal file contents live in `assets/templates/` next to this SKILL.md. Read each template and substitute placeholders for the user's plugin name. Placeholders use the form `__MYPLUGIN__` (for the C#/csproj name, e.g. `PartyHPOverlay`) and `__USER__` (the GitHub username/org, e.g. `goatcorp`). Treat the `MyPlugin` namespace, `MyPlugin.csproj`, the assembly name, and the `InternalName` as the same value — they MUST match (the InternalName cannot be changed after first submission to the official repo, so pick carefully with the user up front).

Templates available in `assets/templates/`:

- `MyPlugin.csproj` — SDK-style csproj, manifest fields embedded inline (v14+ supports this)
- `MyPlugin.json` — fallback DalamudPackager JSON manifest
- `Plugin.cs` — entry point with the most commonly needed services pre-injected
- `Configuration.cs` — minimal `IPluginConfiguration` with `Save()` helper
- `MainWindow.cs` — `Window`-derived class with size constraints and a Draw stub
- `ConfigWindow.cs` — settings window backed by the same `Configuration` instance
- `gitignore` — standard .NET ignores plus `bin/`, `obj/`, `.vs/`, `*.user`
- `editorconfig` — 4-space indent, `file_header_template` slot, common .NET conventions
- `README.md` — includes the "I am not Square Enix; this is third-party software" disclaimer

If the user has only specified a feature ("a plugin that highlights the lowest-HP party member"), still emit the full scaffold and add a brief note in `Plugin.cs`'s constructor (as a TODO comment) about where their feature logic should hook in — usually `IFramework.Update += ...` or inside `MainWindow.Draw()`.

## The csproj is intentionally tiny — don't bloat it

The canonical `goatcorp/SamplePlugin` master csproj is 16 lines because the SDK supplies everything else. Specifically:

- **Never set `<TargetFramework>` manually** — the SDK does that. v14/v15 = `net10.0-windows`.
- **Never reference `Dalamud.dll` with `<HintPath>`** — that's legacy v6-era. The SDK pulls it from `$(AppData)\XIVLauncher\addon\Hooks\dev\` automatically.
- **Never add `<PackageReference Include="DalamudPackager">`** — the SDK supplies it.
- **Versions must be deterministic.** Do not write `$([System.DateTime]::Now.ToString(...))` inside `<Version>`. Plogon (the official build CI) rejects timestamped versions.

If the user asks for additional NuGet packages, those go in a `<ItemGroup>` with `<PackageReference>` as normal — that is fine, just don't override what the SDK already provides.

## Service injection idiom: static properties (Idiom A)

Every real-world community plugin (Honorific, MacroChain, NoClippy, LMeter, ChatTranslated) uses static `[PluginService]` properties on the plugin class so services are accessible from any other class via `Plugin.ChatGui.Print(...)`. The official Dalamud docs show constructor injection in some samples (Idiom B), but Idiom A is the de facto community standard and is what the bundled `Plugin.cs` template uses.

Pre-injected in the template (these cover ~80% of party/social/overlay plugin needs):

`IDalamudPluginInterface`, `ICommandManager`, `IChatGui`, `IClientState`, `IPlayerState`, `IDataManager`, `IFramework`, `IObjectTable`, `IPartyList`, `ITargetManager`, `ICondition`, `IDutyState`, `ITextureProvider`, `INotificationManager`, `IPluginLog`.

Add or remove based on the user's feature. If they want hooking, add `IGameInteropProvider` and read the `dalamud-hooking-and-signatures` skill. If they want to read native UI addons, add `IAddonLifecycle` + `IAddonEventManager`. Don't pre-inject everything — unused services still trigger the IoC container and clutter the file.

## Manifest fields and what's required

In v14+, the `.csproj` `<PropertyGroup>` can carry the manifest directly, making `MyPlugin.json` optional. Both forms work; the JSON file is more discoverable for someone reading the repo, so the bundled template emits both with matching values.

Required at minimum: `Name`, `Author`, `Punchline`, `Description`, `RepoUrl`. `InternalName`, `AssemblyVersion`, `DalamudApiLevel` are auto-filled by `DalamudPackager`. YAML/TOML manifests use snake_case (`repo_url`); JSON/csproj use PascalCase (`RepoUrl`).

`Tags` is freeform; `CategoryTags` must be one of the known values (most safe default: `["other"]`; for party/UI plugins, `["other"]` or `["utility"]`).

## Local development workflow (include in README.md)

1. `dotnet build` → outputs `bin/x64/Debug/MyPlugin/` (the SDK creates a packed plugin folder, not just a DLL).
2. In FFXIV: `/xlsettings` → Experimental → "Dev Plugin Locations" → add the full path to `MyPlugin.dll`.
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable.
4. For debugging: `/xldev` → enable AntiDebug once, then attach with both Native and Managed (.NET) debuggers.
5. Hot-reload: `/xlreload <PluginInternalName>`.
6. Manual install drop location: `%AppData%\XIVLauncher\devPlugins\<InternalName>\`.

## Anti-patterns banner

The official `DalamudPluginsD17` repo does not accept AI-generated submissions, and partial AI use must be disclosed in the PR description (or the user risks a ban). The scaffold's README should include a one-liner reminding the user of this. Also forbidden: friend-list login alerts, AOE markers for non-telegraphed mechanics, AOE recoloring, camera zoom unlock, FFLogs/DPS parser integration, automation of any kind, anything PvP, storing other players' content IDs.

If the user describes a feature on this list, push back: "That category is rejected by the official repo; do you want to retarget the plugin or self-distribute?" — don't silently scaffold a plugin destined for rejection.

## Cross-references to other Dalamud skills

After scaffolding, the user usually wants to fill in:

- **UI / windows** → `dalamud-imgui-windowing`
- **Reading game state, party, target** → `dalamud-party-and-game-state`
- **Slash commands, chat output, IPC to other plugins** → `dalamud-chat-commands-ipc`
- **Looking up specific service members** → `dalamud-services-reference`
- **Hooking game functions** → `dalamud-hooking-and-signatures`
- **Publishing the plugin** → `dalamud-submission-and-distribution`

## A note on stale upstream snippets

The `goatcorp/SamplePlugin` master `.csproj` is currently pinned to `Dalamud.NET.Sdk/14.0.2` while the latest published SDK is `15.0.0`. The template lags the SDK by design. The bundled templates here default to `15.0.0`. If the user reports build errors mentioning missing types or namespaces, downgrade the SDK line to `14.0.2` in `MyPlugin.csproj` as a first compatibility check.

The Mintlify-hosted "Services Overview" wiki (`mintlify.wiki/goatcorp/dalamud`) shows pre-v10 examples without the `I` prefix on interface types. Treat anything from that source as out of date. The source of truth is `dalamud.dev` and the `goatcorp/SamplePlugin` repo on `master`.
