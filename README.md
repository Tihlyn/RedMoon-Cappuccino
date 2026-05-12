# RedMoonCappuccino

A Dalamud plugin for Final Fantasy XIV via [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher).

> **This is third-party software.** I am not affiliated with Square Enix.
> Please do not discuss this plugin in-game.

## What it does

Connects to a WebSocket server at `ws://78.116.140.30:3100` and displays:

- **Overview tab** — lowest market tax rate and the city offering it.
- **Events tab** — upcoming FC events with expandable details and event images.
- **Past Events tab** — FC events that ended within the last 24 hours, with their cached images.

Event images are downloaded from the server and stored locally for up to 24 hours, after which they are deleted automatically.

## Commands

- `/rmcap` — Toggle the main window.

## Building locally

```sh
dotnet build
```

This produces `bin/x64/Debug/RedMoonCappuccino/`. To load in-game:

1. `/xlsettings` → Experimental → "Dev Plugin Locations" → add the path to `RedMoonCappuccino.dll`.
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable.
3. Hot-reload: `/xlreload RedMoonCappuccino`.

## Contributing

If you submit a PR that includes AI-generated code, please disclose it in the PR description.
The official [DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17) repository does not accept undisclosed AI-generated submissions.

## License

AGPL-3.0-or-later.
