# Releasing PlayerNexusTracker

PlayerNexusTracker is a Dalamud plugin and uses tag-driven releases. Unlike the upstream lib repos, this one publishes a **`.zip` to a GitHub Release**, not a NuGet package — Dalamud's plugin loader picks it up from a repo manifest URL.

## The version comes from the tag — do not bump it by hand

`release.yml` passes `-p:Version=<tag>` to the build, so the tag you push *is* the version the plugin reports. The `<Version>0.0.0.0</Version>` in `PlayerNexusTracker.Plugin.csproj` is a development placeholder and must stay that way; a local build reporting `0.0.0.0` is intentional and reads as "not a release build".

This matters more than it looks. Dalamud decides whether an update exists by comparing the `AssemblyVersion` in `pluginmaster.json` against the installed assembly. If the two disagree the failure is silent: the release page looks fine, the download link points at the new zip, and **no user is ever offered the update** because the manifest claims they already have that version.

> That is not hypothetical — v0.2.0 originally shipped with `AssemblyVersion` `0.1.2.0`, because the csproj carried a hardcoded version that nothing tied to the tag and this document never said to bump it. The release reached nobody and had to be recut.

The release workflow now fails rather than publishing a mismatch. After building the zip it checks that **the tag, the manifest's `AssemblyVersion`, and the assembly inside the zip all agree**; any disagreement stops the release. `NexusFFXIV/DalamudRepo`'s build script additionally emits a warning if an entry's version disagrees with the tag its download link points at.

Note for pre-releases: `v0.2.0-rc.1` yields `AssemblyVersion` `0.2.0.0` — the same value the eventual stable `v0.2.0` gets, since assembly versions cannot express a pre-release suffix. Testers moving from `rc.1` to stable are therefore not offered an update by version alone. Use a distinct patch level (`v0.2.1-rc.1` → `v0.2.1`) when that matters.

## Cutting a release

1. **Make sure upstream deps are at the version you expect, and bump the constraint floor**. If the Plugin needs new NexusKit or NexusKit.Modules features:
   - NexusKit released first (NexusKit's [RELEASING.md](https://github.com/NexusFFXIV/NexusKit/blob/main/RELEASING.md))
   - NexusKit.Modules released next (NexusKit.Modules' [RELEASING.md](https://github.com/NexusFFXIV/NexusKit.Modules/blob/main/RELEASING.md))
   - **Bump the constraint floor in `PlayerNexusTracker.Plugin.csproj`** for every `PackageReference` that should pick up a new version, e.g. `[0.1.0,)` → `[0.1.1,)`. NuGet's `PackageReference` resolves to the **lowest** version satisfying the range, so without raising the floor the build will keep resolving the old release even after a new one is on the feed. Verifiable in CI under "List outdated NuGet dependencies" — if Resolved < Latest for any NexusKit package, the floor still needs bumping.
   - **Refresh `packages.lock.json`** for the bumped PackageReferences. Locally the sibling-clone swap in `Directory.Build.targets` rewrites NexusKit refs to ProjectReferences and the lock file drops their NuGet entries — that is fine, CI doesn't have the sibling clones and re-evaluates from the csproj on every restore. You can verify by deleting the lock file and running `dotnet restore --force-evaluate` on a clean checkout (or simply rely on CI to surface a mismatch).

2. **Verify `main` is green**:
   ```powershell
   git fetch origin
   git checkout main; git pull
   gh run list --limit 1
   ```

3. **Pick the version** per SemVer:
   ```powershell
   git describe --tags --abbrev=0
   ```
   - `vX.Y.(Z+1)` — patch: bugfix only, no API surface change
   - `vX.(Y+1).0` — minor: new feature, no settings-migration required
   - `v(X+1).0.0` — major: breaking change for users (e.g. database migration that's not backwards-compatible)

4. **Tag with annotation**. First line becomes the Release title:
   ```powershell
   git tag -a v0.2.0 -m "v0.2.0 — adds player notes, fixes encounter dedup"
   git push origin v0.2.0
   ```

5. **CI auto-builds + releases** via `.github/workflows/release.yml`:
   - Resolve the version from the tag (`v0.2.0` → `Version=0.2.0`, `AssemblyVersion=0.2.0.0`)
   - Restore (pulls NexusKit + Modules NuGets from GitHub Packages)
   - Build in Release config with `-p:Version=<tag>`
   - Pack the plugin into `PlayerNexusTracker-<version>.zip` (DalamudPackager output)
   - **Verify tag, manifest `AssemblyVersion` and the assembly inside the zip all match** — the release fails here rather than shipping a version users would never be offered
   - Create a GitHub Release with auto-generated notes (PRs since the previous tag) and attach the zip

6. **Verify**:
   - Release page: `https://github.com/NexusFFXIV/PlayerNexusTracker/releases/tag/v0.2.0`
   - `PlayerNexusTracker.zip` is attached as a release asset
   - `AssemblyVersion` in the attached `PlayerNexusTracker.json` matches the tag (the workflow already gated on this; this is the human-readable confirmation)
   - After DalamudRepo rebuilds, `pluginmaster.json`'s `AssemblyVersion` / `TestingAssemblyVersion` match the versions in `DownloadLinkInstall` / `DownloadLinkTesting`

## Player distribution

Players install by adding this custom Dalamud repo URL (Settings → Experimental → Custom Plugin Repositories):

```
https://raw.githubusercontent.com/NexusFFXIV/DalamudRepo/main/pluginmaster.json
```

The `pluginmaster.json` file lives in the [NexusFFXIV/DalamudRepo](https://github.com/NexusFFXIV/DalamudRepo) repo and is rebuilt automatically by its `update.yml` workflow on every release (triggered by the `plugin-release` dispatch this repo's `release.yml` sends, with a 6-hourly cron as fallback). It points Dalamud at the latest release's zip asset — stable users get `DownloadLinkInstall`/`AssemblyVersion`, while testers who enable testing builds get `DownloadLinkTesting`/`TestingAssemblyVersion`. No manual edit is required here when cutting a release.

> The standard Dalamud repo channel will follow once the plugin matures and gets accepted into the official repo.

## Hotfix releases

Same pattern as the lib repos. Branch from the latest release tag, apply the fix, tag a patch version:

```powershell
git checkout -b hotfix/<thing> v0.2.0
# fix + commit + push, open separate PR against main for the long-term fix
git tag -a v0.2.1 -m "v0.2.1 — hotfix: <description>"
git push origin v0.2.1
```

Make sure the same fix lands on `main` so v0.3.0 doesn't regress.

## Pre-release versions (testing builds)

**Same constraint-floor + lock-file discipline applies as for stable releases.** If the testing build is meant to pick up an upstream lib update (e.g. a fresh NexusKit.Modules patch), step 1 of "Cutting a release" still has to happen first — otherwise the testing zip ships with the same old NuGets as the previous stable tag and the change you wanted to test is missing.

Tag with a suffix containing `-` to publish a testing build instead of a stable release:

```powershell
git tag -a v0.2.0-rc.1 -m "v0.2.0-rc.1 — testing build for <reason>"
git push origin v0.2.0-rc.1
```

What CI does differently from a stable tag:

| Step | Stable tag (`v0.2.0`) | Pre-release tag (`v0.2.0-rc.1`) |
|---|---|---|
| Build + zip | same | same |
| GitHub Release | normal | **Pre-release** (set automatically because the tag contains `-`) |
| DalamudRepo `pluginmaster.json` | updates `DownloadLinkInstall` + `AssemblyVersion` | updates `DownloadLinkTesting` + `TestingAssemblyVersion`; stable fields stay on the previous stable tag |

### What testers see in Dalamud

Pre-release builds are surfaced **only** to players who enable **Settings → Experimental → Get plugin testing builds** in Dalamud. Everyone else continues to see the latest stable version. This way you can ship a `-rc.1` to validate behaviour in real conditions without disrupting non-testers.

### Stable vs. testing pointer behaviour

The DalamudRepo build script keeps the two pointers consistent:
- **No pre-release exists** → testing pointer mirrors the stable pointer (testers and everyone else see the same build).
- **Pre-release newer than stable** → testing pointer advances; stable pointer stays put until you tag the next stable version.
- **Pre-release older than stable** (e.g. you cut stable after a quick fix) → testing pointer is pulled up to the stable version; no downgrade for testers.

When you cut the next stable version (`v0.2.0` after `v0.2.0-rc.1`/`-rc.2`), both pointers re-sync and the cycle starts over.

Suffix conventions (descending stability): `-rc.N`, `-beta.N`, `-preview.N`.
