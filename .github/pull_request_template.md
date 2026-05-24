<!--
Thanks for the PR. A quick summary + how you verified it goes a long way.
See CONTRIBUTING.md and RELEASING.md for the full workflow.
-->

## Summary
<!-- 1–3 sentences: what changes and why? -->

## Changes
<!-- Optional bullet list if the summary doesn't cover details -->

## Area
<!-- Where does this PR touch? -->
- [ ] UI (windows, tabs, widgets)
- [ ] Composition / DI wiring
- [ ] Notifications / chat output
- [ ] Settings / filters
- [ ] Localization
- [ ] Build / CI

## Test plan
- [ ] `dotnet build PlayerNexusTracker.sln -c Debug -p:Platform=x64` is green
- [ ] (if UI/runtime change) Loaded in XIVLauncher dev-plugin, smoke-tested the affected feature in-game
- [ ] (if DB schema change) Tested with an existing DB — migration runs cleanly without data loss

## Notes for reviewer
<!-- Optional: anything that needs special attention -->

## Upstream dependency bump?
<!-- Did you bump PackageReference versions for NexusKit or NexusKit.Modules? Were those releases shipped first? -->
