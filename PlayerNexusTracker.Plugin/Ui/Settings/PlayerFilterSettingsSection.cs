using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.AutoSettings;
using PlayerNexusTracker.Settings.Filters;

namespace PlayerNexusTracker.Ui.Settings;

/// <summary>
/// Settings tab that lets the user manage the list of player-list filters.
/// Two-view UX: the master view shows a compact list (name + criterion count
/// + Edit / Delete) so the section stays readable as the filter count grows;
/// clicking Edit (or creating a new filter) drops into a detail view that
/// focuses on a single filter's criterion editor.
/// <para>The editor doesn't drive the dropdown — that lives in
/// <c>PlayerListPanel</c>; here we only edit the persisted collection in
/// <see cref="PlayerFilterRegistry"/>. Edits propagate immediately because
/// both the editor and the list panel read from the same singleton's
/// cached <c>Filters</c> list.</para>
/// </summary>
internal sealed class PlayerFilterSettingsSection : IAutoSettingsSection
{
    /// <summary>Placed between the schema-driven plugin groups (sort 10ish)
    /// and the Notifications section (sort 100) so filters land in a logical
    /// "list-related settings" slot.</summary>
    public int Order => 50;

    public string NavTitleKey => "ui.pntracker.filter.section.nav";

    private readonly PlayerFilterRegistry mRegistry;
    private readonly ILocalizer mLoc;
    private readonly IGameDataLookups mLookups;
    private readonly IPlayerFilterPreviewService mPreview;
    private readonly EncounterCategoryResolver mCategoryResolver;

    // Per-filter preview state. Resets on filter swap; debounced so we don't
    // fire a DB query on every keystroke. Tooltip surfaces the
    // volatile-fields caveat (see PlayerFilterPreviewService).
    private Guid? mPreviewFilterId;
    private int mPreviewRevision = -1;
    private int? mPreviewMatchCount;
    private DateTime mPreviewKickAt;
    private Task? mPreviewTask;
    private CancellationTokenSource? mPreviewCts;
    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>Null = master view (filter list). Non-null = detail view of
    /// that filter's criterion editor. Snaps back to null if the active id no
    /// longer resolves (defensive against deletion from elsewhere).</summary>
    private Guid? mEditingFilterId;

    /// <summary>Cached list of valid Lumina race row ids — populated on first
    /// render and reused across frames. Computed by walking the first
    /// <see cref="RaceProbeUpper"/> row ids and keeping those with a non-empty
    /// name, so a future expansion patch lights up automatically without a
    /// code change here.</summary>
    private uint[]? mRaceIdsCache;
    private const uint RaceProbeUpper = 16;

    /// <summary>Cached list of valid Lumina OnlineStatus row ids — populated
    /// on first render. The sheet has gaps (some rows are placeholders /
    /// retired statuses with no name), so we probe up to
    /// <see cref="OnlineStatusProbeUpper"/> and keep those that resolve to a
    /// non-empty display name.</summary>
    private uint[]? mOnlineStatusIdsCache;
    private const uint OnlineStatusProbeUpper = 64;

    // Export / import modal state. Set when the user clicks the respective
    // button; the modal renders unconditionally and looks at these to decide
    // what to show. Modals close via the Close/Cancel button, which clears
    // the "open this frame" flag and resets per-modal scratch state.
    private bool mOpenExportPopup;
    private string mExportContent = string.Empty;
    private string mExportTitle = string.Empty;

    private bool mOpenImportPopup;
    private string mImportInput = string.Empty;
    private FilterImportResult? mImportResult;

    private const string ExportPopupId = "##pnt_filter_export";
    private const string ImportPopupId = "##pnt_filter_import";

    public PlayerFilterSettingsSection(
        PlayerFilterRegistry registry,
        ILocalizer localizer,
        IGameDataLookups lookups,
        IPlayerFilterPreviewService preview,
        EncounterCategoryResolver categoryResolver)
    {
        mRegistry = registry;
        mLoc = localizer;
        mLookups = lookups;
        mPreview = preview;
        mCategoryResolver = categoryResolver;
    }

    public void Render(ISettingsStore store)
    {
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.section.description"));
        ImGui.Spacing();

        RenderHelpBlock();

        // If we have an editing target, render the detail view — but defend
        // against the filter having disappeared (settings file edit, etc.):
        // fall back to the master view rather than crashing on a null lookup.
        if (mEditingFilterId is { } editId)
        {
            var editing = mRegistry.FindById(editId);
            if (editing is null)
            {
                mEditingFilterId = null;
            }
            else
            {
                RenderDetailView(editing);
                // Modals are owned by the section, not the view — they have
                // to render in both master and detail so a deferred "show
                // export popup" flag from one frame doesn't drop on the
                // floor when the user happens to navigate at the same time.
                RenderExportPopup();
                RenderImportPopup();
                return;
            }
        }

        RenderMasterView();
        RenderExportPopup();
        RenderImportPopup();
    }

    // ─── Master view ──────────────────────────────────────────────────────

    private void RenderMasterView()
    {
        var atCap = mRegistry.Filters.Count >= PlayerFilterRegistry.SoftCap;
        var noFilters = mRegistry.Filters.Count == 0;

        ImGui.BeginDisabled(atCap);
        var addClicked = ImGui.Button(mLoc.Get("ui.pntracker.filter.button.new"));
        ImGui.EndDisabled();
        if (addClicked)
        {
            // New filter is created blank and the user lands straight in the
            // detail view — the common flow is "create → add criteria", so
            // skipping the extra Edit click saves a step.
            var created = mRegistry.Add(string.Empty);
            if (created is not null)
            {
                mEditingFilterId = created.Id;
                _ = mRegistry.PersistAsync();
                return;
            }
        }

        // Bulk Export — disabled when there's nothing to export. Opens a
        // modal that holds the encoded token + a Copy-to-clipboard button.
        ImGui.SameLine();
        ImGui.BeginDisabled(noFilters);
        if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.export")))
        {
            mExportTitle = mLoc.Get("ui.pntracker.filter.export.title.all");
            mExportContent = mRegistry.ExportToString();
            mOpenExportPopup = true;
        }
        ImGui.EndDisabled();

        // Import is always available — even with zero existing filters the
        // user may want to bring a friend's filter collection in.
        ImGui.SameLine();
        if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.import")))
        {
            mImportInput = string.Empty;
            mImportResult = null;
            mOpenImportPopup = true;
        }

        if (atCap)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(
                CultureInfo.CurrentCulture,
                mLoc.Get("ui.pntracker.filter.cap_warning"),
                PlayerFilterRegistry.SoftCap));
        }
        ImGui.Spacing();

        var filters = mRegistry.Filters;
        if (filters.Count == 0)
        {
            ImGui.TextDisabled(mLoc.Get("ui.pntracker.filter.empty_section_hint"));
            return;
        }

        Guid? pendingDelete = null;
        Guid? pendingEdit = null;
        Guid? pendingClone = null;
        Guid? pendingExport = null;
        (Guid Id, int Delta)? pendingMove = null;
        for (var i = 0; i < filters.Count; i++)
        {
            if (i > 0) ImGui.Separator();
            var f = filters[i];
            var isFirst = i == 0;
            var isLast = i == filters.Count - 1;
            ImGui.PushID(f.Id.ToString());
            try
            {
                RenderMasterRow(f, atCap, isFirst, isLast,
                    out var editThis, out var cloneThis, out var deleteThis,
                    out var exportThis, out var moveDelta);
                if (editThis) pendingEdit = f.Id;
                if (cloneThis) pendingClone = f.Id;
                if (deleteThis) pendingDelete = f.Id;
                if (exportThis) pendingExport = f.Id;
                if (moveDelta != 0) pendingMove = (f.Id, moveDelta);
            }
            finally
            {
                ImGui.PopID();
            }
        }

        // Resolve at-most-one of the three intents this frame. Delete wins
        // over Edit/Clone for the rare case where two buttons land in the
        // same frame (ImGui processes both on a single click only if their
        // bounds overlap — which they don't here, but the priority order
        // makes the intent explicit).
        if (pendingDelete is { } id)
        {
            mRegistry.Remove(id);
            _ = mRegistry.PersistAsync();
        }
        else if (pendingClone is { } cid)
        {
            var source = mRegistry.FindById(cid);
            if (source is not null)
            {
                var cloneName = string.Format(
                    CultureInfo.CurrentCulture,
                    mLoc.Get("ui.pntracker.filter.clone_suffix"),
                    string.IsNullOrWhiteSpace(source.Name)
                        ? mLoc.Get("ui.pntracker.filter.unnamed")
                        : source.Name);
                var clone = mRegistry.Duplicate(cid, cloneName);
                if (clone is not null)
                {
                    // Same flow as "+ New filter": drop straight into the
                    // detail view so the user can tweak the clone right away.
                    mEditingFilterId = clone.Id;
                    _ = mRegistry.PersistAsync();
                }
            }
        }
        else if (pendingMove is { } mv)
        {
            if (mRegistry.Move(mv.Id, mv.Delta))
                _ = mRegistry.PersistAsync();
        }
        else if (pendingExport is { } xid)
        {
            var token = mRegistry.ExportFilter(xid);
            var source = mRegistry.FindById(xid);
            if (token is not null && source is not null)
            {
                var displayName = string.IsNullOrWhiteSpace(source.Name)
                    ? mLoc.Get("ui.pntracker.filter.unnamed")
                    : source.Name;
                mExportTitle = string.Format(
                    CultureInfo.CurrentCulture,
                    mLoc.Get("ui.pntracker.filter.export.title.one"),
                    displayName);
                mExportContent = token;
                mOpenExportPopup = true;
            }
        }
        else if (pendingEdit is { } eid)
        {
            mEditingFilterId = eid;
        }
    }

    private void RenderMasterRow(PlayerFilter f, bool cloneDisabled,
                                 bool isFirst, bool isLast,
                                 out bool edit, out bool clone, out bool delete,
                                 out bool export, out int moveDelta)
    {
        edit = false;
        clone = false;
        delete = false;
        export = false;
        moveDelta = 0;

        var displayName = string.IsNullOrWhiteSpace(f.Name)
            ? mLoc.Get("ui.pntracker.filter.unnamed")
            : f.Name;
        var countLabel = string.Format(
            CultureInfo.CurrentCulture,
            mLoc.Get("ui.pntracker.filter.criteria_count"),
            f.Criteria.Count);

        // Right-align the four action buttons against the section width so
        // the layout stays tidy as names vary in length. The criterion-count
        // grey text hugs the buttons; the filter name takes the remaining
        // left. Order goes non-destructive → constructive → destructive
        // (Edit → Export → Clone → Delete) so the Delete button stays where
        // the user expects it (far right).
        var editLabel = mLoc.Get("ui.pntracker.filter.button.edit");
        var exportLabel = mLoc.Get("ui.pntracker.filter.button.export");
        var cloneLabel = mLoc.Get("ui.pntracker.filter.button.clone");
        var deleteLabel = mLoc.Get("ui.pntracker.filter.button.delete");
        var framePadX = ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var editWidth = ImGui.CalcTextSize(editLabel).X + framePadX;
        var exportWidth = ImGui.CalcTextSize(exportLabel).X + framePadX;
        var cloneWidth = ImGui.CalcTextSize(cloneLabel).X + framePadX;
        var deleteWidth = ImGui.CalcTextSize(deleteLabel).X + framePadX;
        var countWidth = ImGui.CalcTextSize(countLabel).X;
        var rightCluster = countWidth + spacing + editWidth + spacing
                           + exportWidth + spacing + cloneWidth + spacing + deleteWidth;
        var rowStartX = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;

        // Reorder controls sit on the left edge — visual hierarchy is
        // "position controls left, destructive controls right". First row
        // has Up disabled; last row has Down disabled.
        ImGui.BeginDisabled(isFirst);
        if (ImGui.ArrowButton("##move_up", ImGuiDir.Up)) moveDelta = -1;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(isLast);
        if (ImGui.ArrowButton("##move_down", ImGuiDir.Down)) moveDelta = 1;
        ImGui.EndDisabled();
        ImGui.SameLine();

        if (string.IsNullOrWhiteSpace(f.Name))
            ImGui.TextColored(ImGuiColors.DalamudGrey, displayName);
        else
            ImGui.TextUnformatted(displayName);

        // Push the right-cluster onto the same line, at the right edge.
        ImGui.SameLine(rowStartX + available - rightCluster);
        ImGui.TextDisabled(countLabel);
        ImGui.SameLine();
        if (ImGui.Button(editLabel)) edit = true;
        ImGui.SameLine();
        if (ImGui.Button(exportLabel)) export = true;
        ImGui.SameLine();
        ImGui.BeginDisabled(cloneDisabled);
        if (ImGui.Button(cloneLabel)) clone = true;
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(deleteLabel)) delete = true;
    }

    // ─── Detail view ──────────────────────────────────────────────────────

    private void RenderDetailView(PlayerFilter filter)
    {
        var changed = false;

        if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.back")))
        {
            mEditingFilterId = null;
            return;
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Name editor — full width. Persist on any keystroke (matches the
        // ChatNotifications pattern: PersistAsync coalesces overlapping
        // writes so this is cheap even for fast typists).
        var name = filter.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##filter_name",
                                    mLoc.Get("ui.pntracker.filter.placeholder.name"),
                                    ref name, 64))
        {
            filter.Name = name;
            changed = true;
        }

        ImGui.Spacing();

        DrawPreviewCounter(filter);

        ImGui.TextDisabled(mLoc.Get("ui.pntracker.filter.heading.criteria"));

        var removeIdx = -1;
        for (var i = 0; i < filter.Criteria.Count; i++)
        {
            ImGui.PushID(i);
            try
            {
                if (RenderCriterionRow(filter.Criteria[i], out var removeThis))
                    changed = true;
                if (removeThis) removeIdx = i;
            }
            finally
            {
                ImGui.PopID();
            }
        }
        if (removeIdx >= 0)
        {
            filter.Criteria.RemoveAt(removeIdx);
            changed = true;
        }

        if (filter.Criteria.Count == 0)
            ImGui.TextDisabled(mLoc.Get("ui.pntracker.filter.empty_filter_hint"));

        if (ImGui.SmallButton(mLoc.Get("ui.pntracker.filter.button.add_criterion")))
        {
            filter.Criteria.Add(new PlayerFilterCriterion
            {
                Field = FilterField.Name,
                Operator = FilterOperator.Contains,
                Value = string.Empty,
            });
            changed = true;
        }

        if (changed) _ = mRegistry.PersistAsync();
    }

    /// <summary>Renders the "Matches: N" counter above the criteria list.
    /// Recomputes 300 ms after the last criterion edit, using the same
    /// compile + match pipeline the runtime list uses (sans volatile
    /// fields). Tooltip surfaces the caveat.</summary>
    private void DrawPreviewCounter(PlayerFilter filter)
    {
        var revision = PlayerFilterEvaluator.ComputeSourceRevision(filter);

        // Switching filters OR mutating the active filter's criteria both
        // invalidate the cached count: cancel any in-flight task so its
        // stale snapshot can't land back into mPreviewMatchCount, clear
        // the visible count so the UI shows "…" instead of the previous
        // value, and arm the debounce so a quick burst of edits coalesces
        // into one DB hit.
        if (mPreviewFilterId != filter.Id)
        {
            mPreviewFilterId = filter.Id;
            mPreviewRevision = revision;
            mPreviewMatchCount = null;
            mPreviewCts?.Cancel();
            mPreviewCts = null;
            mPreviewTask = null;
            mPreviewKickAt = DateTime.UtcNow + PreviewDebounce;
        }
        else if (mPreviewRevision != revision)
        {
            mPreviewRevision = revision;
            mPreviewMatchCount = null;
            mPreviewCts?.Cancel();
            mPreviewCts = null;
            mPreviewTask = null;
            mPreviewKickAt = DateTime.UtcNow + PreviewDebounce;
        }

        // Kick the actual count when the debounce window has elapsed and we
        // don't already have a fresh result for the current revision. Task
        // is also gated on being idle so the same revision doesn't fan out
        // duplicate DB hits across frames.
        if (mPreviewTask is null
            && DateTime.UtcNow >= mPreviewKickAt
            && mPreviewMatchCount is null)
        {
            var capturedRevision = revision;
            var capturedId = filter.Id;
            // Snapshot the filter for the task. CompileSnapshot makes a
            // shallow copy of the criterion list so a mid-flight edit on the
            // live filter doesn't mutate the criteria the preview is
            // evaluating against.
            var snapshot = CompileSnapshot(filter);
            mPreviewCts = new CancellationTokenSource();
            var token = mPreviewCts.Token;
            mPreviewTask = Task.Run(async () =>
            {
                var n = await mPreview.CountMatchesAsync(snapshot, token).ConfigureAwait(false);
                // Hard gate: ONLY write back when the result still matches
                // the filter id AND the revision it was started against.
                // The revision check is what kept the previous version
                // surfacing 4 from a stale CompanyTag run after the user
                // switched the field to FreeCompanyLodestoneId.
                if (!token.IsCancellationRequested
                    && mPreviewFilterId == capturedId
                    && mPreviewRevision == capturedRevision)
                {
                    mPreviewMatchCount = n;
                }
                mPreviewTask = null;
            });
        }

        var label = mPreviewMatchCount is { } count
            ? string.Format(CultureInfo.CurrentCulture,
                mLoc.Get("ui.pntracker.filter.preview.matches"), count)
            : mLoc.Get("ui.pntracker.filter.preview.loading");
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(mLoc.Get("ui.pntracker.filter.preview.caveat"));
        ImGui.Spacing();
    }

    /// <summary>Cheap deep-copy of the criterion list for the preview task —
    /// avoids the editor mutating criteria mid-evaluation. The
    /// <c>PlayerFilter</c> wrapper points at the same Id so callers that
    /// key on Id still match.</summary>
    private static PlayerFilter CompileSnapshot(PlayerFilter source)
    {
        var copy = new PlayerFilter
        {
            Id = source.Id,
            Name = source.Name,
            Criteria = new List<PlayerFilterCriterion>(source.Criteria.Count),
        };
        foreach (var c in source.Criteria)
            copy.Criteria.Add(new PlayerFilterCriterion
            {
                Field = c.Field,
                Operator = c.Operator,
                Value = c.Value,
            });
        return copy;
    }

    private bool RenderCriterionRow(PlayerFilterCriterion criterion, out bool remove)
    {
        var changed = false;
        remove = false;

        // Field combo
        var fields = FilterFieldMetadata.AllFields;
        var fieldLabels = new string[fields.Length];
        var fieldIdx = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            fieldLabels[i] = FieldLabel(fields[i]);
            if (fields[i] == criterion.Field) fieldIdx = i;
        }
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##field", ref fieldIdx, fieldLabels, fieldLabels.Length))
        {
            criterion.Field = fields[fieldIdx];
            // Clamp the operator to the first allowed for the new field type
            // (otherwise an old Equals on a freshly-switched Bool field would
            // be invisible-and-broken — the editor wouldn't render the option
            // in the combo, but the persisted value would still be Equals).
            var allowed = FilterFieldMetadata.GetAllowedOperators(criterion.Field);
            if (!FilterFieldMetadata.IsOperatorAllowed(criterion.Field, criterion.Operator))
                criterion.Operator = allowed[0];
            // Reset the value too: a string from a Text field doesn't survive
            // the switch to Integer / JobRole semantics.
            criterion.Value = string.Empty;
            changed = true;
        }

        // CompanyTag is the weakest FC match (tags can repeat across FCs on
        // the same world). Surface that fact only when the criterion currently
        // *uses* CompanyTag — putting a generic question-mark icon next to
        // every field combo would be noise for users not filtering by FC.
        if (criterion.Field == FilterField.CompanyTag)
        {
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                FontAwesomeIcon.QuestionCircle.ToIconString());
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mLoc.Get("ui.pntracker.filter.field.companytag.tooltip"));
        }

        ImGui.SameLine();

        // Operator combo (allowlist-driven)
        var ops = FilterFieldMetadata.GetAllowedOperators(criterion.Field);
        var opLabels = new string[ops.Count];
        var opIdx = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            opLabels[i] = OperatorLabel(ops[i]);
            if (ops[i] == criterion.Operator) opIdx = i;
        }
        ImGui.SetNextItemWidth(130f);
        if (ImGui.Combo("##op", ref opIdx, opLabels, opLabels.Length))
        {
            criterion.Operator = ops[opIdx];
            changed = true;
        }
        ImGui.SameLine();

        // Value widget — shape depends on the field's value-kind.
        var kind = FilterFieldMetadata.GetValueKind(criterion.Field);
        switch (kind)
        {
            case FilterValueKind.Text:
            {
                var s = criterion.Value ?? string.Empty;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.InputTextWithHint("##value",
                                            mLoc.Get("ui.pntracker.filter.placeholder.value"),
                                            ref s, 64))
                {
                    criterion.Value = s;
                    changed = true;
                }
                break;
            }

            case FilterValueKind.Integer:
            {
                var n = int.TryParse(criterion.Value, NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : 0;
                ImGui.SetNextItemWidth(140f);
                if (ImGui.InputInt("##value", ref n))
                {
                    criterion.Value = n.ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                break;
            }

            case FilterValueKind.Bool:
                // Bool fields embed the predicate in the operator (IsTrue /
                // IsFalse) so no value widget is needed. Add a spacer so the
                // remove button stays aligned with the integer/text rows.
                ImGui.Dummy(new Vector2(200f, 0f));
                break;

            case FilterValueKind.JobRoleEnum:
            {
                var roles = FilterFieldMetadata.JobRoleNames;
                var roleLabels = new string[roles.Length];
                var roleIdx = -1;
                for (var i = 0; i < roles.Length; i++)
                {
                    roleLabels[i] = mLoc.Get($"ui.pntracker.filter.role.{roles[i].ToLowerInvariant()}");
                    if (string.Equals(roles[i], criterion.Value, StringComparison.OrdinalIgnoreCase))
                        roleIdx = i;
                }
                // Auto-commit the displayed selection when the persisted
                // value doesn't resolve. ImGui.Combo only returns true when
                // the user actively changes the selection — clicking the
                // already-shown entry is a no-op, so leaving the persisted
                // value out of sync with the visible one silently produces
                // a criterion that never matches (compile path returns
                // IsValid=false on empty enum strings).
                if (roleIdx < 0)
                {
                    roleIdx = 0;
                    criterion.Value = roles[0];
                    changed = true;
                }
                ImGui.SetNextItemWidth(160f);
                if (ImGui.Combo("##value", ref roleIdx, roleLabels, roleLabels.Length))
                {
                    criterion.Value = roles[roleIdx];
                    changed = true;
                }
                break;
            }

            case FilterValueKind.RaceEnum:
            {
                var raceIds = GetRaceIds();
                if (raceIds.Length == 0) break;
                var raceLabels = new string[raceIds.Length];
                var raceIdx = -1;
                var parsed = int.TryParse(criterion.Value, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out var selectedId);
                for (var i = 0; i < raceIds.Length; i++)
                {
                    raceLabels[i] = mLookups.GetRaceName(raceIds[i], feminine: false) ?? $"#{raceIds[i]}";
                    if (parsed && (int)raceIds[i] == selectedId) raceIdx = i;
                }
                // Same auto-commit pattern as JobRole — see comment there.
                if (raceIdx < 0)
                {
                    raceIdx = 0;
                    criterion.Value = raceIds[0].ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                ImGui.SetNextItemWidth(180f);
                if (ImGui.Combo("##value", ref raceIdx, raceLabels, raceLabels.Length))
                {
                    criterion.Value = raceIds[raceIdx].ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                break;
            }

            case FilterValueKind.OnlineStatusEnum:
            {
                var statusIds = GetOnlineStatusIds();
                if (statusIds.Length == 0) break;
                var statusLabels = new string[statusIds.Length];
                var statusIdx = -1;
                var statusParsed = int.TryParse(criterion.Value, NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out var selectedStatusId);
                for (var i = 0; i < statusIds.Length; i++)
                {
                    statusLabels[i] = mLookups.GetOnlineStatusName(statusIds[i]) ?? $"#{statusIds[i]}";
                    if (statusParsed && (int)statusIds[i] == selectedStatusId) statusIdx = i;
                }
                // Same auto-commit pattern as JobRole — bring the persisted
                // value in line with the visible default when no valid
                // selection resolves (otherwise the criterion would silently
                // never match).
                if (statusIdx < 0)
                {
                    statusIdx = 0;
                    criterion.Value = statusIds[0].ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                ImGui.SetNextItemWidth(200f);
                if (ImGui.Combo("##value", ref statusIdx, statusLabels, statusLabels.Length))
                {
                    criterion.Value = statusIds[statusIdx].ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                break;
            }

            case FilterValueKind.EncounteredInPicker:
                if (RenderEncounteredInPicker(criterion))
                    changed = true;
                break;

            case FilterValueKind.GenderEnum:
            {
                // Two-entry combo. Labels reuse the observation panel's
                // existing gender strings so we don't double-translate;
                // persisted value mirrors FFXIV's Customize[1] convention
                // (0 = male, 1 = female).
                var genderLabels = new[]
                {
                    mLoc.Get("ui.main.gender.male"),
                    mLoc.Get("ui.main.gender.female"),
                };
                var parsed = int.TryParse(criterion.Value, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out var selectedGender);
                var validSel = parsed && selectedGender is 0 or 1;
                var genderIdx = validSel ? selectedGender : 0;
                // Same auto-commit pattern as JobRole — see comment there.
                // A criterion that was added but never had its gender combo
                // clicked (because the default "Male" already matched the
                // visible state) leaves Value empty and evaluates as
                // never-match. Writing the default here brings persisted
                // state in line with what the user sees.
                if (!validSel)
                {
                    criterion.Value = "0";
                    changed = true;
                }
                ImGui.SetNextItemWidth(140f);
                if (ImGui.Combo("##value", ref genderIdx, genderLabels, genderLabels.Length))
                {
                    criterion.Value = genderIdx.ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                break;
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"{mLoc.Get("ui.pntracker.filter.button.delete_criterion")}##remove"))
            remove = true;

        return changed;
    }

    // Help-block open state owned by this section. We override ImGui's
    // per-window ImGuiStorage every frame so a stale "open" bit cached
    // there can't drift the initial UX.
    // Note: dalamudUI.ini only persists window-level state (position,
    // size, window-collapsed); sub-item open states live exclusively in
    // ImGuiStorage and never reach the ini.
    private bool mHelpOpen;
    // Last ImGui frame this section called RenderHelpBlock. Used to
    // detect "section wasn't active last frame" (user navigated to a
    // sibling settings tab / closed and re-opened the settings window
    // / plugin reloaded) and reset mHelpOpen back to closed on re-entry.
    // -2 ⇒ "never rendered" (initial value picks the reset branch on the
    // very first frame too).
    private int mHelpLastRenderFrame = -2;

    /// <summary>Collapsible help block that explains how the list-panel
    /// filtering stack works. Collapsed by default — both on plugin
    /// reload and on every re-entry of the Filters settings tab. Users
    /// who want it open click to expand; the choice holds while they
    /// stay on the tab.</summary>
    private void RenderHelpBlock()
    {
        var currentFrame = ImGui.GetFrameCount();
        // Gap > 1 means at least one frame went by without this section
        // rendering — the user switched away and came back, the settings
        // window closed and reopened, or this is the very first call.
        // Reset to closed on every such re-entry so the default state is
        // consistent regardless of how long ago the user left.
        if (currentFrame - mHelpLastRenderFrame > 1) mHelpOpen = false;
        mHelpLastRenderFrame = currentFrame;

        // Force-set the open state every frame so ImGui's per-window
        // storage is bypassed entirely — the visible state is always
        // mHelpOpen.
        ImGui.SetNextItemOpen(mHelpOpen, ImGuiCond.Always);
        var open = ImGui.CollapsingHeader(mLoc.Get("ui.pntracker.filter.help.header"));
        // ImGui returns true on the frame the user clicks to open and
        // false on the frame they click to close. Mirror the return into
        // our owned state so it sticks for subsequent frames.
        if (open != mHelpOpen) mHelpOpen = open;
        if (!open) return;

        ImGui.Indent();
        ImGui.PushTextWrapPos();

        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.intro"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow,
            mLoc.Get("ui.pntracker.filter.help.system_filters.heading"));
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.system_filters.body"));
        ImGui.BulletText(mLoc.Get("ui.pntracker.filter.help.system_filters.current"));
        ImGui.BulletText(mLoc.Get("ui.pntracker.filter.help.system_filters.recent"));
        ImGui.BulletText(mLoc.Get("ui.pntracker.filter.help.system_filters.all"));
        ImGui.BulletText(mLoc.Get("ui.pntracker.filter.help.system_filters.unread"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow,
            mLoc.Get("ui.pntracker.filter.help.user_filters.heading"));
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.user_filters.body"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow,
            mLoc.Get("ui.pntracker.filter.help.interaction.heading"));
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.interaction.body"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow,
            mLoc.Get("ui.pntracker.filter.help.example.heading"));
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.example.body"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow,
            mLoc.Get("ui.pntracker.filter.help.troubleshoot.heading"));
        ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.help.troubleshoot.body"));

        ImGui.PopTextWrapPos();
        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Search input value for the EncounteredIn zone picker. Static because
    // only one combo can be open at a time in ImGui — reset to empty when
    // the combo closes.
    private static string sZoneSearchText = string.Empty;

    private static readonly EncounterZoneFilter[] EncounteredInCategories =
    {
        EncounterZoneFilter.OpenWorld,
        EncounterZoneFilter.AnyDuty,
        EncounterZoneFilter.Dungeons,
        EncounterZoneFilter.Trials,
        EncounterZoneFilter.Raids,
        EncounterZoneFilter.Pvp,
        EncounterZoneFilter.Field,
        EncounterZoneFilter.OtherDuty,
    };

    private bool RenderEncounteredInPicker(PlayerFilterCriterion criterion)
    {
        var changed = false;
        var current = EncounteredInValue.Decode(criterion.Value);

        // Auto-commit a sane default — an empty criterion would compile to
        // IsValid=false and silently never match. Default to "Any duty"
        // because it's the broadest useful starting point and the most
        // likely shape the user wants before narrowing.
        if (!current.IsSpecified)
        {
            current = current with { Category = EncounterZoneFilter.AnyDuty };
            criterion.Value = current.Encode();
            changed = true;
        }

        // --- Category combo (narrows the zone picker) ---
        var catLabels = new string[EncounteredInCategories.Length];
        var catIdx = 0;
        for (var i = 0; i < EncounteredInCategories.Length; i++)
        {
            catLabels[i] = mLoc.Get(
                $"ui.pntracker.filter.encounteredin.category.{EncounteredInCategories[i].ToString().ToLowerInvariant()}");
            if (EncounteredInCategories[i] == current.Category) catIdx = i;
        }
        ImGui.SetNextItemWidth(140f);
        if (ImGui.Combo("##value_cat", ref catIdx, catLabels, catLabels.Length))
        {
            // Switching category invalidates any previously-picked zone
            // (it may no longer belong to the new category). Reset to 0
            // = "any in this category".
            current = current with { Category = EncounteredInCategories[catIdx], TerritoryId = 0 };
            criterion.Value = current.Encode();
            changed = true;
        }

        ImGui.SameLine();

        // --- Searchable zone combo ---
        var territories = mCategoryResolver.GetTerritoriesForCategory(current.Category);
        var currentZoneLabel = current.TerritoryId == 0
            ? mLoc.Get("ui.pntracker.filter.encounteredin.zone.any")
            : ResolveZoneLabel(current.TerritoryId);

        ImGui.SetNextItemWidth(240f);
        if (ImGui.BeginCombo("##value_zone", currentZoneLabel))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search",
                mLoc.Get("ui.pntracker.filter.encounteredin.search.hint"),
                ref sZoneSearchText, 64);
            ImGui.Separator();

            // "Any in this category" entry — always available, sits above
            // the concrete territories so it's easy to pick.
            if (ImGui.Selectable(mLoc.Get("ui.pntracker.filter.encounteredin.zone.any"),
                                  current.TerritoryId == 0))
            {
                current = current with { TerritoryId = 0 };
                criterion.Value = current.Encode();
                changed = true;
            }

            foreach (var tid in territories)
            {
                var label = ResolveZoneLabel(tid);
                if (sZoneSearchText.Length > 0
                    && label.IndexOf(sZoneSearchText, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (ImGui.Selectable(label, tid == current.TerritoryId))
                {
                    current = current with { TerritoryId = tid };
                    criterion.Value = current.Encode();
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        else
        {
            // Reset search text when the combo closes — next-open starts
            // fresh. Without this the previous filter would still be in
            // effect on the next selection.
            sZoneSearchText = string.Empty;
        }

        return changed;
    }

    private string ResolveZoneLabel(ushort territoryTypeId)
    {
        var duty = mLookups.GetInstancedContentName(territoryTypeId);
        if (!string.IsNullOrEmpty(duty)) return duty;
        return mLookups.GetTerritoryDisplayName(territoryTypeId) ?? $"#{territoryTypeId}";
    }

    private string FieldLabel(FilterField field)
        => mLoc.Get($"ui.pntracker.filter.field.{field.ToString().ToLowerInvariant()}");

    private string OperatorLabel(FilterOperator op)
        => mLoc.Get($"ui.pntracker.filter.op.{op.ToString().ToLowerInvariant()}");

    private uint[] GetRaceIds()
    {
        if (mRaceIdsCache is not null) return mRaceIdsCache;
        var ids = new List<uint>();
        for (uint i = 1; i <= RaceProbeUpper; i++)
            if (!string.IsNullOrEmpty(mLookups.GetRaceName(i, feminine: false)))
                ids.Add(i);
        mRaceIdsCache = ids.ToArray();
        return mRaceIdsCache;
    }

    private uint[] GetOnlineStatusIds()
    {
        if (mOnlineStatusIdsCache is not null) return mOnlineStatusIdsCache;
        var ids = new List<uint>();
        for (uint i = 1; i <= OnlineStatusProbeUpper; i++)
            if (!string.IsNullOrEmpty(mLookups.GetOnlineStatusName(i)))
                ids.Add(i);
        mOnlineStatusIdsCache = ids.ToArray();
        return mOnlineStatusIdsCache;
    }

    // ─── Export / import modals ───────────────────────────────────────────

    private void RenderExportPopup()
    {
        if (mOpenExportPopup)
        {
            ImGui.OpenPopup(ExportPopupId);
            mOpenExportPopup = false;
        }

        // AlwaysAutoResize lets the popup grow with the title line so the
        // textbox sits comfortably regardless of language. Centred on the
        // viewport on first appear.
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(ExportPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        try
        {
            ImGui.TextUnformatted(mExportTitle);
            ImGui.Separator();
            ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.export.hint"));
            ImGui.Spacing();

            // Read-only textbox — InputTextMultiline so the user can drag-
            // select / Ctrl-C the whole token. The string is opaque base64
            // with a prefix; line wrapping doesn't matter because the
            // importer trims and the format ignores whitespace inside the
            // base64 body would corrupt it (so we explicitly strip on
            // import via .Trim()).
            // Buffer size 65536 mirrors the import-side limit. Read-only so
            // the user can drag-select / Ctrl-C the whole token but can't
            // accidentally mutate the source. The local var matches the
            // ref-string overload picked by Dalamud's binding (passing the
            // field directly trips overload resolution because of the
            // surrounding modal state).
            var copy = mExportContent;
            ImGui.InputTextMultiline("##export_value", ref copy, 65536,
                new Vector2(640f, 120f), ImGuiInputTextFlags.ReadOnly);

            ImGui.Spacing();
            if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.copy")))
                ImGui.SetClipboardText(mExportContent);
            ImGui.SameLine();
            if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.close")))
                ImGui.CloseCurrentPopup();
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void RenderImportPopup()
    {
        if (mOpenImportPopup)
        {
            ImGui.OpenPopup(ImportPopupId);
            mOpenImportPopup = false;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(ImportPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        try
        {
            ImGui.TextUnformatted(mLoc.Get("ui.pntracker.filter.import.title"));
            ImGui.Separator();
            ImGui.TextWrapped(mLoc.Get("ui.pntracker.filter.import.hint"));
            ImGui.Spacing();

            // Multiline so a wrapped paste survives — the importer trims
            // outer whitespace; base64 itself can't contain real newlines
            // inside the data so this is a tolerant input.
            ImGui.InputTextMultiline("##import_value", ref mImportInput, 65536,
                new Vector2(640f, 120f), ImGuiInputTextFlags.None);

            ImGui.Spacing();
            if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.import")))
            {
                mImportResult = mRegistry.ImportFromString(mImportInput);
                if (mImportResult.HasChanges) _ = mRegistry.PersistAsync();
            }
            ImGui.SameLine();
            if (ImGui.Button(mLoc.Get("ui.pntracker.filter.button.close")))
            {
                mImportInput = string.Empty;
                mImportResult = null;
                ImGui.CloseCurrentPopup();
            }

            // Inline result / error summary — stays visible until the user
            // closes the modal so multi-step diagnose-and-retry flows work
            // without a separate notification.
            if (mImportResult is { } r)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                if (r.ParseError)
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed,
                        mLoc.Get("ui.pntracker.filter.import.invalid"));
                }
                else if (!r.HasChanges && r.SkippedAtCap == 0)
                {
                    ImGui.TextDisabled(mLoc.Get("ui.pntracker.filter.import.result.no_changes"));
                }
                else
                {
                    if (r.Added > 0)
                    {
                        ImGui.TextColored(ImGuiColors.HealerGreen, string.Format(
                            CultureInfo.CurrentCulture,
                            mLoc.Get("ui.pntracker.filter.import.result.added"),
                            r.Added));
                    }
                    if (r.Overwritten > 0)
                    {
                        var names = string.Join(", ", r.OverwrittenNames);
                        ImGui.TextColored(ImGuiColors.DalamudYellow, string.Format(
                            CultureInfo.CurrentCulture,
                            mLoc.Get("ui.pntracker.filter.import.result.overwrite_warning"),
                            r.Overwritten, names));
                    }
                    if (r.SkippedAtCap > 0)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed, string.Format(
                            CultureInfo.CurrentCulture,
                            mLoc.Get("ui.pntracker.filter.import.result.cap_truncated"),
                            r.SkippedAtCap, PlayerFilterRegistry.HardCap));
                    }
                }
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }
}
