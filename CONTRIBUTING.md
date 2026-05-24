# Contributing to PlayerNexusTracker

Thanks for considering a contribution. This doc covers the workflow; the release process lives in [RELEASING.md](RELEASING.md).

PlayerNexusTracker is a Dalamud plugin built on [NexusKit](https://github.com/NexusFFXIV/NexusKit) + [NexusKit.Modules](https://github.com/NexusFFXIV/NexusKit.Modules). The plugin itself is intentionally thin — most of the heavy lifting lives upstream.

## Branch & PR workflow

All changes go through a Pull Request. Direct pushes to `main` are blocked by branch protection.

1. **Branch off `main`** with a descriptive name:
   ```powershell
   git checkout main; git pull
   git checkout -b <scope>/<short-summary>
   ```
   Suggested scopes: `feat/`, `fix/`, `chore/`, `docs/`, `refactor/`, `ui/`. Example: `feat/player-notes-tab`.

2. **Commit** with clear imperative messages:
   ```
   feat(ui): add player notes tab to detail window
   ```

3. **Push the branch and open a PR**:
   ```powershell
   git push -u origin <branch>
   gh pr create
   ```

4. **CI runs on the PR** — the `build` check must be green. Merge via squash once approved + green.

## Local development

### Recommended layout (NexusFFXIV workspace)

Clone all three NexusFFXIV repos into a common parent folder:

```
NexusFFXIV/
├── NexusFFXIV.sln              ← umbrella solution (opens all 16 projects)
├── Directory.Build.targets     ← swaps PackageRef→ProjectRef when sibling source exists
├── NexusKit/
├── NexusKit.Modules/
└── PlayerNexusTracker/         ← this repo
```

The workspace's `Directory.Build.targets` rewires `PackageReference Include="NexusKit.*"` and `NexusKit.Modules.*` to `ProjectReference` against the sibling source. Edits in NexusKit or NexusKit.Modules are picked up by the Plugin instantly — no NuGet publish needed during development.

### Standalone clone

If you only clone this repo, set up a GitHub Packages PAT:

1. Create a classic PAT at https://github.com/settings/tokens with scope `read:packages`
2. Store it as a User env var:
   ```powershell
   [System.Environment]::SetEnvironmentVariable("GITHUB_PACKAGES_PAT", "<token>", "User")
   ```
   (restart your PowerShell session so the var is visible)

The `nuget.config` in this repo references `%GITHUB_PACKAGES_PAT%` to authenticate.

### Build + run

Default build (matches the dev-plugin path XIVLauncher expects):

```powershell
dotnet build PlayerNexusTracker.sln -c Debug -p:Platform=x64
```

Output: `PlayerNexusTracker.Plugin/bin/x64/Debug/PlayerNexusTracker.dll`. Target framework is `net10.0-windows`; the Dalamud SDK pulls `Dalamud.dll` from `%APPDATA%\XIVLauncher\addon\Hooks\dev\`, so the dev hook must be installed.

## Cutting a testing build

For changes that need real-world validation before reaching all users (risky refactor, new feature, behaviour change), publish a **testing release** first:

1. Land the work on `main` via the normal PR flow.
2. From `main`, tag with a pre-release suffix:
   ```powershell
   git tag -a v0.2.0-rc.1 -m "v0.2.0-rc.1 — testing build for <reason>"
   git push origin v0.2.0-rc.1
   ```
3. CI builds the plugin, packages a `.zip`, and creates a GitHub Release **marked Pre-release** because the tag contains `-`.
4. In the NexusFFXIV Dalamud repo (`https://raw.githubusercontent.com/NexusFFXIV/DalamudRepo/main/pluginmaster.json`) the pre-release fills `DownloadLinkTesting` and `TestingAssemblyVersion`. Players see it **only** when they enable **Settings → Experimental → Get plugin testing builds** in Dalamud.
5. After validation, cut the stable version (`v0.2.0`) — no separate code change, just the tag. The DalamudRepo workflow updates `DownloadLinkInstall` and all users get the new build.

Suffix conventions (descending stability): `-rc.N`, `-beta.N`, `-preview.N`.

The PR does **not** need to know whether it will ship as testing or stable — that's a tag-time decision.

Point XIVLauncher → Settings → Dev → Plugin Locations → add the above bin folder. XIVLauncher hot-reloads on rebuild.

## Code style

Follows [NexusKit's coding-conventions.md](https://github.com/NexusFFXIV/NexusKit/blob/main/docs/coding-conventions.md). Highlights:

- `m`-prefix on private instance fields (`mHttpFactory`, `mLog`)
- `ConfigureAwait(false)` on every background-I/O `await`
- File-scoped namespaces, one public type per file
- `Nullable` enabled — never `!` to silence the compiler unless proven non-null
- Localization: every user-visible string goes through `ILocalizer` — see `PlayerNexusTracker.Plugin/Resources/Language.resx`

## License

By contributing, you agree your contribution is licensed under **AGPL-3.0-only**.
