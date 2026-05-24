# Releasing PlayerNexusTracker

PlayerNexusTracker is a Dalamud plugin and uses tag-driven releases. Unlike the upstream lib repos, this one publishes a **`.zip` to a GitHub Release**, not a NuGet package — Dalamud's plugin loader picks it up from a repo manifest URL.

## Cutting a release

1. **Make sure upstream deps are at the version you expect**. If the Plugin needs new NexusKit or NexusKit.Modules features:
   - NexusKit released first (NexusKit's [RELEASING.md](https://github.com/NexusFFXIV/NexusKit/blob/main/RELEASING.md))
   - NexusKit.Modules released next (NexusKit.Modules' [RELEASING.md](https://github.com/NexusFFXIV/NexusKit.Modules/blob/main/RELEASING.md))
   - Then bump this repo's `PackageReference` constraints if not already covered by floating versions

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
   - Restore (pulls NexusKit + Modules NuGets from GitHub Packages)
   - Build in Release config
   - Pack the plugin into `PlayerNexusTracker-<version>.zip` (DalamudPackager output)
   - Create a GitHub Release with auto-generated notes (PRs since the previous tag) and attach the zip

6. **Verify**:
   - Release page: `https://github.com/NexusFFXIV/PlayerNexusTracker/releases/tag/v0.2.0`
   - `PlayerNexusTracker.zip` is attached as a release asset

## Player distribution

Players install via Dalamud's testing-track URL:

```
https://raw.githubusercontent.com/NexusFFXIV/PlayerNexusTracker/main/repo.json
```

The `repo.json` file (maintained in this repo's `main` branch) points Dalamud at the latest release's zip asset. It must be updated alongside a release if the schema/asset URL convention changes.

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
