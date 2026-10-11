# Changelog

## Unreleased

- Added refresh diagnostics with per-source status, retry metadata, automatic
  run boundaries, shutdown completion, bounded run logs and a support export.
- Added selectable Auto, English and German UI language modes. The language
  resources are checked for missing keys during the build.
- Added filtered, paged player history and persistent snapshot comparisons,
  including a clear "since last scan" view.
- Added persistent notification history, event deduplication, quiet hours and
  digest delivery. Pending digests are flushed at session end and plugin
  shutdown instead of being silently lost.
- Added refresh metrics, category-aware stale refresh planning and maintenance
  hooks that keep queue, run and snapshot data bounded.
- Added additive database migrations for refresh runs and player snapshots;
  existing player data remains usable after the update.
- Improved release safety: the shipped version is derived from the release tag,
  and CI checks the tag, manifest and assembly version before publishing.
- Fixed plugin startup by registering the shared notification digest service in
  the dependency-injection container.

This is the development summary for the next release. When a tag creates a
GitHub Release, its release notes are copied into DalamudRepo's `Changelog`
field and shown in the plugin installer's in-game changelog.

See the repository release notes for user-facing changes in tagged versions.
