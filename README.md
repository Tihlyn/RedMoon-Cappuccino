# RedMoonCappuccino

A Dalamud plugin for Final Fantasy XIV via [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher).

> **This is third-party software.** I am not affiliated with Square Enix.
> Please do not discuss this plugin in-game.

## What it does

Connects to a WebSocket server at `ws://IP:3100` and displays:

- **Overview tab** — lowest market tax rate and the city offering it, plus upcoming patch highlights for 7.4–8.0.
- **gear planner tab** — manually triggered progression solver (`solve`) that loads offline JSON gear/math/BiS data and recommends deterministic next upgrades with explanations and alternate paths.
- **Useful Links tab** — mount guide links in a table layout, plus a Visual Plans link section.
- **Events tab** — upcoming FC events with expandable details and event images.
- **Past Events tab** — FC events that ended within the last 24 hours, with their cached images.

Event images are downloaded from the server and stored locally for up to 24 hours, after which they are deleted automatically.

## Commands

- `/rmcap` — Toggle the main window.

## Installing via Dalamud (custom repository)

1. In-game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, paste:
   ```
   https://raw.githubusercontent.com/Tihlyn/RedMoon-Cappuccino/main/pluginmaster.json
   ```
3. Click **Save and Close**, then install **RedMoonCappuccino** from `/xlplugins`.

## Publishing a new release

1. **Bump the version** in `RedMoonCappuccino/RedMoonCappuccino.csproj`:
   ```xml
   <Version>1.2.3.0</Version>
   ```
2. Commit and push the version bump to `main`.
3. **Tag the commit** using the `v<major>.<minor>.<patch>` format and push the tag:
   ```sh
   git tag v1.2.3
   git push origin v1.2.3
   ```
4. GitHub Actions (`.github/workflows/build.yml`) will automatically:
   - Build the plugin in Release mode.
   - Package it as `RedMoonCappuccino.zip`.
   - Create a GitHub Release with the ZIP attached.
   - Update `AssemblyVersion` and `LastUpdate` in `pluginmaster.json` and commit the change to `main`.

Users who have the custom repository URL already added will see the update in `/xlplugins` after Dalamud refreshes.

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
