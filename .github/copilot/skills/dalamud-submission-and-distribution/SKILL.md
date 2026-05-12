---
name: dalamud-submission-and-distribution
description: Submit a Dalamud plugin for FFXIV / XIVLauncher to the official DalamudPluginsD17 repository via the Plogon CI pipeline — manifest.toml authoring, the testing/live → stable promotion workflow, the pre-submission checklist (Release build, deterministic versions, packages.lock.json, AGPL license, icon dimensions), and the official restrictions list (no automation, no DPS parsers / FFLogs, no PvP, no AOE markers for non-telegraphed mechanics, no friend-list login alerts, no storing other players' content IDs). Also covers local dev install via Dev Plugin Locations, hot-reload with /xlreload, debug attach, and the AI-disclosure requirement (undisclosed AI-generated code is rejection-grade and may risk a ban). Use whenever the user wants to publish, submit, distribute, ship, release, or open a PR for their Dalamud plugin, mentions Plogon / DalamudPluginsD17 / manifest.toml / testing channel / stable channel / bleatbot, or asks about plugin restrictions, the review process, or how to get the plugin into /xlplugins.
---

# Dalamud Plugin Submission and Distribution

This skill covers the path from "the plugin builds locally" to "it appears in `/xlplugins` for everyone". The official repo is `goatcorp/DalamudPluginsD17`; the build CI is **Plogon**; submission is a regular GitHub PR.

## When this skill applies

Trigger phrases include "submit my plugin", "publish", "ship", "release", "distribute", "DalamudPluginsD17", "Plogon", "manifest.toml", "testing channel", "stable channel", "bleatbot rebuild", "PR review", "plugin restrictions", "what's allowed", "icon size", "dev plugin location", "/xlreload", "/xldev". If the user is moving from "it works on my machine" to "other people can install it", this skill applies.

## Pre-submission checklist

Before opening the PR, run through this list:

1. **Plugin uses `Dalamud.NET.Sdk`** (not legacy `targets/` directory or `<HintPath>` references). The csproj should be a `<Project Sdk="Dalamud.NET.Sdk/15.0.0">` (or 14.0.2, whichever you're targeting).
2. **Public Git repo** — GitHub, GitLab, or self-hosted with anonymous HTTP clone. No auth required.
3. **Built in Release mode** — the `Plogon` action uses Release. Make sure your code path doesn't depend on `#if DEBUG`.
4. **`packages.lock.json` is committed.** Plogon requires reproducible restores. Enable with `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` in the csproj if it isn't already.
5. **`images/icon.png` exists**, between **64×64 and 512×512**. Smaller is rejected; larger is rejected. PNG only.
6. **Versions are deterministic.** No `$([System.DateTime]::Now)` or build-counter macros in `<Version>`. Same commit → same version → same build.
7. **AGPL-3.0** (or another OSI-approved compatible license) declared in the manifest.
8. **Reviewed against `dalamud.dev/plugin-publishing/restrictions`.** See the forbidden-features table below.
9. **Combat-adjacent plugins were pre-cleared in `#plugin-dev` on the Dalamud Discord** before submission. The review team explicitly does not want surprise combat-adjacent submissions.
10. **AI usage disclosed in the PR description** if any portion is AI-generated. See the dedicated section below.

If any of these are off, fix them before opening the PR — Plogon will block the build, and re-pushing fixes adds review-cycle latency.

## The repo layout — `testing/live` vs `stable`

`DalamudPluginsD17` has two top-level state directories:

- **`testing/live/`** — where new plugin submissions land. Users have to opt in to the testing channel (`/xlsettings` → Experimental → "Get plugin testing builds") to install from here.
- **`stable/`** — promoted plugins. The default `/xlplugins` listing.

A new submission lives at `testing/live/<InternalName>/` and stays there until it's been observed working in the wild — usually a few weeks of user testing. Promotion to stable is a follow-up PR that just moves the directory; no version bump or commit-hash change is required.

```
testing/live/
└── MyPlugin/
    ├── manifest.toml
    └── images/
        ├── icon.png        (64×64 to 512×512, mandatory)
        ├── image1.png      (optional, shown in /xlplugins description)
        ├── image2.png      (optional)
        └── image3.png      (optional)
```

## `manifest.toml` — the actual submission file

```toml
[plugin]
repository = "https://github.com/USER/MyPlugin.git"
commit     = "<full 40-char SHA>"
owners     = ["USER"]
maintainers = ["coowner1", "coowner2"]
project_path = "MyPlugin"
changelog  = "Initial submission."
```

Field reference:

- **`repository`** — the canonical Git URL. Must clone without authentication. HTTPS only; no `git@` URLs.
- **`commit`** — the **full 40-character SHA** that Plogon will check out and build from. Tags don't work; short SHAs don't work. Bumping the plugin to a new build means a PR that updates this single line.
- **`owners`** — list of GitHub usernames considered the authority on the plugin. Owners can transfer ownership, change maintainer list, etc.
- **`maintainers`** (optional) — additional GitHub usernames who can push updates without owner sign-off, but cannot change ownership-level fields.
- **`project_path`** — the directory inside the cloned repo that contains the `.csproj`. Required even if it's just the repo name (i.e. when the csproj is at the repo root, set this to `"."` or to the project's folder name as appropriate — check existing submissions for your structure).
- **`changelog`** (optional) — the message shown to users in the installer when they update. Falls back to the manifest's `Changelog` field, then to the PR description.

## PR workflow

- **One plugin per PR; one branch per submission.** Don't bundle multiple plugins or multiple feature changes in one PR.
- **Plogon's "Validate Manifest" GitHub Action** triggers on PR open. It clones the repo at the pinned commit, runs `dotnet build`, and posts results as a comment.
- **To re-trigger Plogon** after pushing fixes (or if a build was flaky), comment **`bleatbot, rebuild`** on the PR. The bot picks it up.
- **Promotion to stable** is a separate PR that moves the directory from `testing/live/MyPlugin/` to `stable/MyPlugin/`. Same `manifest.toml`, no version bump, no commit-hash change required.
- **`nofranz` prefix** — to suppress the auto-Discord changelog post that fires when a plugin update lands, prefix the PR description with `nofranz`. Useful for trivial fixes you don't want to spam.

## Forbidden features (the rejection table)

The official restrictions list is non-exhaustive — review is described in the docs as "a subjective process" — but the following categories are firm rejections. Don't write them, and if the user describes a feature that fits one of these, push back early:

| Anti-pattern | Why it's rejected |
|---|---|
| **Friend-list login alerts** | Technically impossible without external infrastructure (the game doesn't push friend events in a way the client sees) |
| **AOE markers for non-telegraphed mechanics** | Considered cheating — gives information the player shouldn't have |
| **AOE recoloring to highlight specific mechanics** | Same — gameplay advantage |
| **Camera zoom unlock beyond game limits** | PvP advantage; breaks cutscenes |
| **FFLogs integration / DPS parsers** | Use ACT (a separate, sanctioned external tool) instead |
| **Auto-roll / auto-craft / auto-skip / auto-loot** | Automation is the bright-line rule for the entire repo |
| **Auto-replies to /tells, auto-broadcasts on chat channels** | Chat automation crosses into spam / harassment territory |
| **Anything PvP-related** | The team avoids PvP arms-race scenarios entirely |
| **Out-of-bounds exploitation** | "Tacitly encourages behavior we cannot support" |
| **Storing other players' content IDs** | Privacy / security boundary |
| **Bypassing Mog Station purchases** (e.g. avoiding Fantasia) | Affects Square Enix's revenue |
| **Hard dependencies on rule-violating plugins** | If your plugin only works because *another* banned plugin is loaded, also rejected |
| **Plugins only useful in out-of-spec scenarios** | If the only use case is exploits or cheats, rejected even if technically narrow |

**Combat plugins must show only information the player would already have, and must be pre-cleared with the approval team before submission.** This is non-negotiable. Damage indicators, status timers, debuff trackers, gauge displays — these can be fine; ask first.

**Party / HP / overlay / UI / utility / chat-cosmetic / glamour / texture-replacement / linkshell-tooling / RP-tooling plugins are unambiguously in scope.** That's the bulk of the repo.

## AI disclosure — non-optional

The official policy: **AI-generated submissions are not accepted; partial AI use must be disclosed in the PR description.** Undisclosed AI usage risks being banned from the plugin repo entirely.

If any portion of the plugin was written with help from Claude, Copilot, Cursor, ChatGPT, or any similar tool, include a note like this near the top of the PR description:

> **AI usage disclosure:** Portions of this plugin were drafted with the help of [tool name(s)]. All code was reviewed and tested by [author handle] before submission.

This applies to scaffolds too. If you used `dalamud-plugin-scaffold` to generate the project shell and then wrote the feature logic by hand, that still counts as partial AI use — disclose it. The disclosure costs you nothing; the omission can cost you the account.

## Local development workflow (covered for context)

This is what the user has been doing while building; including it here so the submission step is the only thing they're learning fresh.

1. **Build locally:** `dotnet build` produces `bin/x64/Debug/MyPlugin/` — a packed plugin folder, not a single DLL.
2. **Register as a dev plugin:** in-game, `/xlsettings` → Experimental → "Dev Plugin Locations" → add the full path to `MyPlugin.dll` inside that folder.
3. **Enable it:** `/xlplugins` → Dev Tools → Installed Dev Plugins → toggle on.
4. **Hot-reload:** since API 4, `/xlreload <PluginInternalName>` reloads without restarting XIVLauncher.
5. **Manual install drop:** `%AppData%\XIVLauncher\devPlugins\<InternalName>\` — copying a build folder here also makes the plugin available.

### Debugging

1. `/xldev` → enable AntiDebug **once per session** (this disables FFXIV's anti-debug measures so you can attach a debugger).
2. In Visual Studio / Rider: `Debug → Attach to Process`, pick `ffxiv_dx11.exe`, and **select both Native and Managed (.NET) debugger types** in the attach dialog. .NET-only attach won't catch native crashes; native-only attach won't see your plugin's frames.
3. Set breakpoints in your `Plugin.cs` / `MainWindow.cs` etc. as normal.

If you hit weird "managed code can't find the runtime" errors when attaching, it's almost always the .NET runtime mismatch — XIVLauncher pulls a specific runtime version (currently .NET 10), and a partial OS update can leave you with a newer SDK that the launcher's bundled runtime doesn't trust. Reinstall XIVLauncher's runtime via its launcher to fix.

## Pitfalls (in priority order)

1. **Undisclosed AI usage is the fastest way to a ban.** Disclose it in the PR description — every time, even for trivial AI assistance.
2. **A non-deterministic `<Version>` will fail Plogon's reproducibility check.** No timestamps, no build counters, no `[$$DateTime]::Now` macros.
3. **`packages.lock.json` not committed → restore failure** in the Plogon build. Enable lockfile generation in the csproj.
4. **Combat-adjacent plugins submitted without pre-clearance** get bounced even if the implementation is fine. Ask in `#plugin-dev` before opening the PR.
5. **Wrong icon dimensions.** Anything outside 64×64..512×512 is rejected. PNG only; no SVG, no JPEG.
6. **InternalName change between submissions.** The InternalName cannot be changed after first submission to the official repo without a full re-submission. Pick carefully on day one.
7. **Submitting from a private repo or one that requires auth.** Plogon clones anonymously over HTTPS — both must be public.
8. **Forgetting to update `commit` in `manifest.toml`** when you push a new version. The PR is a no-op if the SHA didn't change.
9. **Don't talk about plugins in-game.** The Dalamud team explicitly asks plugin developers not to mention XIVLauncher / Dalamud / third-party tools in `/say`, `/yell`, `/sh`, party chat, etc. This isn't enforced in code but is a community norm; bake the corresponding disclaimer into the plugin's README.
10. **The plugin restrictions list is subjective.** If you're proposing anything combat-adjacent, social-engineering-adjacent, or with novel server interactions, **ask in the Dalamud Discord first** rather than writing the whole plugin and learning at PR review time that it's a no-go.

## Cross-references

- For the manifest fields (`Name`, `Author`, `Punchline`, etc.) that live inside the plugin itself rather than in `manifest.toml`, see `dalamud-plugin-scaffold`.
- For the third-party / "I am not Square Enix" disclaimer that should appear in the README, see `dalamud-plugin-scaffold`.
- For the restriction context that constrains what a plugin's hooks may do, see `dalamud-hooking-and-signatures`.
- For the auto-reply / auto-broadcast restrictions specifically, see `dalamud-chat-commands-ipc`.
