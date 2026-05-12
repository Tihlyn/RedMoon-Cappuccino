# __MYPLUGIN__

A Dalamud plugin for Final Fantasy XIV via [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher).

> **This is third-party software.** I am not affiliated with Square Enix.
> Please do not discuss this plugin in-game. Reports about plugin behaviour
> belong here on GitHub or in the Dalamud Discord — never in `/sh`, `/yell`,
> or party chat where it puts other players at unnecessary risk.

## What it does

(One-paragraph summary — this is what shows up in `/xlplugins` if your
manifest's `Description` field references this README.)

## Commands

- `/__myplugin__` — Toggles the main window.

## Building locally

This project uses the `Dalamud.NET.Sdk`, which auto-references everything
needed (`Dalamud.dll`, `DalamudPackager`, `Dalamud.Bindings.ImGui`,
`FFXIVClientStructs`, `Lumina`, `InteropGenerator.Runtime`).

```sh
dotnet build
```

That produces `bin/x64/Debug/__MYPLUGIN__/` — a packed plugin folder, not
just a DLL. To load it in-game:

1. `/xlsettings` → Experimental → "Dev Plugin Locations" → add the full path
   to `__MYPLUGIN__.dll` inside that folder.
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable.
3. Hot-reload while iterating: `/xlreload __MYPLUGIN__`.

## Contributing

Issues and PRs welcome. If you submit a PR that includes AI-generated code
(any tool — Claude, Copilot, etc.), please disclose this in the PR
description: the official Dalamud plugin repository
([DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17)) does
not accept undisclosed AI-generated submissions, and downstream submission
to that repo will be blocked otherwise.

## License

AGPL-3.0-or-later. See `LICENSE`.
