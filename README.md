# PlayerNexusTracker

**A Dalamud plugin for FINAL FANTASY XIV, built on [NexusKit](https://github.com/NexusFFXIV/NexusKit) + [NexusKit.Modules](https://github.com/NexusFFXIV/NexusKit.Modules).**

[![Build](https://github.com/NexusFFXIV/PlayerNexusTracker/actions/workflows/release.yml/badge.svg)](https://github.com/NexusFFXIV/PlayerNexusTracker/actions/workflows/release.yml)
[![CodeQL](https://github.com/NexusFFXIV/PlayerNexusTracker/actions/workflows/codeql.yml/badge.svg)](https://github.com/NexusFFXIV/PlayerNexusTracker/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/NexusFFXIV/PlayerNexusTracker?label=release&logo=github)](https://github.com/NexusFFXIV/PlayerNexusTracker/releases)
[![Dalamud API](https://img.shields.io/badge/Dalamud_API-15-9D5BFF)](https://github.com/goatcorp/Dalamud)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

## Overview

PlayerNexusTracker tracks players you meet in FFXIV — locally observed sessions plus optional enrichment from public sources (Lodestone, FFXIVCollect). It is the reference consumer of the NexusKit framework: see how the framework's hosting, persistence, IPC, UI, and module systems get wired together in a real plugin.

## Install (as a player)

1. Open Dalamud's **Settings → Experimental** tab.
2. Under **Custom Plugin Repositories**, paste the NexusFFXIV repo URL and hit Save:
   ```
   https://raw.githubusercontent.com/NexusFFXIV/DalamudRepo/main/pluginmaster.json
   ```
3. Reopen **Settings → Experimental** if needed and tick the new entry as **Enabled**.
4. Open `/xlplugins`, switch to **All Plugins**, search **PlayerNexusTracker**, Install.

### Testing builds (opt-in)

Tick **Settings → Experimental → Get plugin testing builds** to surface pre-release versions when available. Testing builds are tagged `vX.Y.Z-rc.N` in this repo and ship through the same Dalamud repo URL — non-testers continue to see only stable releases.

> The standard Dalamud repo channel will follow once the plugin matures.

## Build from source

```powershell
git clone https://github.com/NexusFFXIV/PlayerNexusTracker.git
cd PlayerNexusTracker

# Configure access to NexusKit packages on GitHub Packages
# 1. Create a classic PAT at https://github.com/settings/tokens with scope: read:packages
# 2. Set it as a User env var:
[System.Environment]::SetEnvironmentVariable("GITHUB_PACKAGES_PAT", "<your_pat>", "User")
# (restart PowerShell so the var is visible)

dotnet build PlayerNexusTracker.sln -c Debug -p:Platform=x64
```

Output lands at `PlayerNexusTracker.Plugin/bin/x64/Debug/PlayerNexusTracker.dll` — the path XIVLauncher's dev-plugin loader expects.

## Architecture

PlayerNexusTracker is intentionally thin: most of the heavy lifting lives in NexusKit + NexusKit.Modules. The plugin itself contains:

- Composition wiring (`Composition/PluginServiceCollectionExtensions.cs`) — `services.AddNexusKitX()` calls
- Plugin-specific notification producers (FC events, history events, …)
- Plugin-specific UI windows
- Localization resources (English + German)

For the framework architecture, see [NexusKit/docs/architecture.md](https://github.com/NexusFFXIV/NexusKit/blob/main/docs/architecture.md).

## Contributing

Issue reports welcome. Code contributions accepted under AGPL-3.0-only.

## License

[AGPL-3.0-only](LICENSE). This plugin and its derivative works must stay open under the same license.
