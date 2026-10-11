using Dalamud.Bindings.ImGui;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.AutoSettings;

namespace PlayerNexusTracker.Ui.Settings;

internal sealed class DiagnosticsSettingsSection : IAutoSettingsSection
{
    public int Order => 70;
    public string NavTitleKey => "ui.pntracker.diagnostics.section.nav";

    private readonly IRefreshRunSummaryService mRuns;
    private readonly NotificationHistoryStore mNotifications;
    private readonly ILocalizer mLoc;

    public DiagnosticsSettingsSection(IRefreshRunSummaryService runs,
        NotificationHistoryStore notifications, ILocalizer localizer)
    {
        mRuns = runs;
        mNotifications = notifications;
        mLoc = localizer;
    }

    public void Render(ISettingsStore store)
    {
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.diagnostics.section.description"));
        ImGui.Spacing();
        var run = mRuns.Latest;
        if (run is null)
            ImGui.TextDisabled(mLoc.Get("ui.pntracker.diagnostics.no_run"));
        else
        {
            ImGui.TextUnformatted(string.Format(mLoc.Get("ui.pntracker.diagnostics.run_summary"),
                run.Total, run.Succeeded, run.Failed, run.Skipped, run.Exhausted));
            ImGui.SameLine();
            if (ImGui.Button(mLoc.Get("ui.pntracker.diagnostics.copy_export")))
            {
                var json = mRuns.ExportLatestJson();
                if (!string.IsNullOrEmpty(json)) ImGui.SetClipboardText(json);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted(mLoc.Get("ui.pntracker.diagnostics.notifications"));
        if (ImGui.Button(mLoc.Get("ui.pntracker.diagnostics.mark_read")))
            mNotifications.MarkAllRead();
        foreach (var entry in mNotifications.GetLatest())
        {
            ImGui.TextDisabled(entry.CreatedAtUtc.ToLocalTime().ToString("g"));
            ImGui.SameLine();
            ImGui.TextUnformatted($"[{entry.Kind}] {entry.Message}");
        }
    }
}
