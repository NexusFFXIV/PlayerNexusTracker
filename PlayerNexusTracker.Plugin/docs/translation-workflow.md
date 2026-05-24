# Translation Workflow (PlayerNexusTracker.Plugin)

How to add or change translations for the plugin's own strings, and how the
plugin's `Language.resx` layers with the framework's bundled translations.

## File map

```
PlayerNexusTracker.Plugin/Resources/
├── Language.resx              English (default culture)
├── Language.<culture>.resx    one per supported language (none yet)
└── Language.Designer.cs       auto-generated companion (ResourceManager)
```

`Language.resx` is empty in the initial scaffold. The infrastructure is
already wired so any keys you add resolve immediately — no code change to
register them.

## Adding a key (Visual Studio resource editor)

1. Open `Resources/Language.resx`. VS auto-launches the resource editor
   (string grid with name / value / comment columns).
2. Add a row: e.g. `tracker.main.title` = `"PlayerNexusTracker"`.
3. Save. VS regenerates `Language.Designer.cs` automatically — a new
   internal static property `tracker_main_title` appears.
4. Use the key in your code via the framework's `ILocalizer`:
   ```csharp
   ImGui.Text(localizer.Get("tracker.main.title"));
   ```

The framework's `ResourceLocalizer<Language>` registration (already in
`AddServices()`) hands the key to the `ResourceManager` exposed by
`Language.Designer.cs`. Adding a key requires zero registration changes.

## Adding a new language

Suppose you want German:

1. In Visual Studio: right-click `Resources/` → Add → New Item → Resource
   File → name it `Language.de.resx`. The culture suffix `.de` tells
   `ResourceManager` this is the German variant.
2. Fill the same keys as `Language.resx` with German translations.
3. Build. The compiler emits `de/PlayerNexusTracker.resources.dll` as a
   satellite assembly. `ResourceManager` picks it up at runtime whenever
   `CultureInfo.CurrentUICulture` indicates German.

You don't need to register the satellite — `ResourceLocalizer<Language>`
already consults `Language.ResourceManager`, which knows about every
satellite assembly built into the same plugin DLL.

For Japanese: `Language.ja.resx`. For French: `Language.fr.resx`. For
Brazilian Portuguese: `Language.pt-BR.resx`. The naming follows
.NET culture codes (BCP 47).

## How the culture gets set

`PluginUiHost` reports Dalamud's UI language to `LocalizationManager` on
construction (`LocalizationManager.ReportHostCulture(PluginInterface.UiLanguage)`)
and on every `IDalamudPluginInterface.LanguageChanged` event. The manager
sets `CultureInfo.CurrentUICulture` accordingly, which `ResourceManager`
honours by default.

If the user wants to override (e.g. force English even though Dalamud is
in German), code can call `localizationManager.SetOverride("en")`. The
override sticks until cleared with `SetOverride(null)`.

## Layered resolution — when our key competes with the framework's

The plugin's `Language.resx` is registered **after** the module localizers
and the framework's `Framework.resx`. The `LayeredLocalizer` consults
sources in reverse-registration order — **plugin wins** for every key it
defines.

Practical implication: the framework ships `nexuskit.module.enabled.label`
as "Enabled" / "Aktiviert". If you'd rather call it "On" (in your plugin
specifically), add a row in `Language.resx`:

```
nexuskit.module.enabled.label = "On"
```

That single override doesn't affect the rest of the framework's keys — the
chain falls back to `Framework.resx` for everything you haven't redefined.

This makes it safe to:
- Override only the keys you care about
- Wholesale replace a module's translations (FFXIVCollect, Lodestone) by
  redefining their `nexuskit.modules.<module>.*` keys
- Leave the rest of the framework / modules at their bundled defaults

## Using keys in settings schemas

The fluent settings schema supports either literals or keys:

```csharp
.Property(x => x.MaxRecentPlayers, p => p
    .LabelKey("tracker.settings.max_recent_players.label")
    .DescriptionKey("tracker.settings.max_recent_players.description")
    .Slider(10, 1000)
    .Order(1))
```

Same for `Group`, `Title`, `Placeholder`. Pair every `…Key` call with a
matching row in `Language.resx` and you get fully translated settings UI.

The current plugin sample (in `Composition/PluginServiceCollectionExtensions.cs`)
uses literals for now — you can leave them as-is or switch to keys as you
add translations.

## Key-naming convention

Recommended pattern for plugin-local keys:

```
<plugin-id>.<area>.<key>
```

Examples:
- `tracker.main.title`
- `tracker.settings.max_recent_players.label`
- `tracker.errors.player_not_found`

This makes plugin keys distinct from framework keys
(`nexuskit.…`) and module keys (`nexuskit.modules.<module>.…`), so accidental
clashes don't happen.

## CI gotchas (forward-looking)

When CI eventually builds release artefacts:

- Satellite assemblies for every `Language.<culture>.resx` ship next to the
  plugin DLL (`de/`, `ja/`, …). They're part of `bin\x64\Release\`.
- A missing translation isn't a build error — `ResourceManager.GetString`
  just falls back to the default `Language.resx`. The `LayeredLocalizer`
  then asks the next source down the chain (modules, framework).
- An empty key returns the key itself as the visible string — handy for
  spotting "did I forget to translate this?" during development.

---

**Maintenance**: when you change the layering rule, the culture-fallback
behaviour, or the recommended key-naming pattern, update this doc.
