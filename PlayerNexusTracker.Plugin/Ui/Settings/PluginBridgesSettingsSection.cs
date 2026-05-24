using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.PluginBridge;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.AutoSettings;

namespace PlayerNexusTracker.Ui.Settings;

/// <summary>
/// Settings tab that surfaces every <see cref="IExternalPluginAdapter"/> with
/// its current probe status (installed / loaded / required IPCs / version).
/// Mirrors Questionable's Dependencies tab but driven by the adapter
/// registry instead of a duplicate hardcoded plugin list — the same adapter
/// instance that produces this status also exposes the normalized service
/// the plugin's UI code consumes.
/// </summary>
internal sealed class PluginBridgesSettingsSection : IAutoSettingsSection
{
    public int Order => 60;

    public string NavTitleKey => "ui.pntracker.bridges.section.nav";

    private readonly IPluginBridgeRegistry mRegistry;
    private readonly ILocalizer mLoc;

    public PluginBridgesSettingsSection(IPluginBridgeRegistry registry, ILocalizer localizer)
    {
        mRegistry = registry;
        mLoc = localizer;
    }

    public void Render(ISettingsStore store)
    {
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.bridges.section.description"));
        ImGui.Spacing();

        if (ImGui.Button(mLoc.Get("ui.pntracker.bridges.button.recheck_all")))
            mRegistry.RefreshAll();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var adapters = mRegistry.All();
        if (adapters.Count == 0)
        {
            ImGui.TextDisabled(mLoc.Get("ui.pntracker.bridges.empty"));
            return;
        }

        for (var i = 0; i < adapters.Count; i++)
        {
            if (i > 0)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
            ImGui.PushID(adapters[i].AdapterKey);
            try
            {
                RenderAdapter(adapters[i]);
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    private void RenderAdapter(IExternalPluginAdapter adapter)
    {
        var status = adapter.GetStatus();

        // Header row: display name + version (right-aligned with subtle color).
        var displayName = mLoc.Get(adapter.DisplayNameKey);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGold);
        ImGui.TextUnformatted(displayName);
        ImGui.PopStyleColor();
        if (status.PluginVersion is { } v)
        {
            ImGui.SameLine();
            var versionLabel = string.Format(
                CultureInfo.CurrentCulture,
                mLoc.Get("ui.pntracker.bridges.version_label"),
                v);
            ImGui.TextDisabled(versionLabel);
        }

        // Description below header.
        var description = mLoc.Get(adapter.DescriptionKey);
        ImGui.TextWrapped(description);
        ImGui.Spacing();

        // Status table — Installed / Loaded / Required IPCs, each with a
        // colored badge so health is glance-readable.
        DrawStatusRow(
            mLoc.Get("ui.pntracker.bridges.row.installed"),
            status.PluginInstalled);
        DrawStatusRow(
            mLoc.Get("ui.pntracker.bridges.row.loaded"),
            status.PluginLoaded);
        DrawStatusRow(
            mLoc.Get("ui.pntracker.bridges.row.ipcs_available"),
            status.AllRequiredIpcsAvailable);

        if (!status.AllRequiredIpcsAvailable && status.MissingIpcs.Count > 0)
        {
            var missingLabel = string.Format(
                CultureInfo.CurrentCulture,
                mLoc.Get("ui.pntracker.bridges.missing_ipcs"),
                string.Join(", ", status.MissingIpcs));
            ImGui.TextColored(ImGuiColors.DalamudRed, missingLabel);
        }

        ImGui.Spacing();
        if (ImGui.Button(mLoc.Get("ui.pntracker.bridges.button.recheck")))
            adapter.Refresh();
    }

    private void DrawStatusRow(string label, bool ok)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        if (ok)
            ImGui.TextColored(ImGuiColors.HealerGreen, mLoc.Get("ui.pntracker.bridges.status.ok"));
        else
            ImGui.TextColored(ImGuiColors.DalamudRed, mLoc.Get("ui.pntracker.bridges.status.missing"));
    }
}
