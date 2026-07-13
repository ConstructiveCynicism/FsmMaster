using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FsmMaster;

// Actions / Events / Variables sub-tabs for whichever state is currently selected. The
// data-gathering logic here is FsmMaster's own existing code, ported (not copied from
// agent-context) from FsmGraphOverlay.RebuildSidePanelCache/FormatActionField: FsmStateInfo.Actions/
// Fields (already collected generically by FsmDataCollector via reflection - no hardcoded action
// types) for the Actions tab, the raw fsm.Fsm.Variables typed arrays for Variables (FsmUtil has no
// bulk-enumeration helper for this - confirmed directly against
// agent-context/Silksong.FsmUtil-main/src/FsmUtil.cs), and fsm.Fsm.Events for Events.
//
// Editable values (Actions/Variables) route through FsmEditManager.SetActionField/SetVariable - the
// same override/pristine-snapshot backend the Save/Load buttons and preset system already use, so an
// edit made here is indistinguishable from one applied by loading a saved edit set. Editability and
// display formatting both come from FsmEditManager.TryFormatValue, so this panel never has to
// duplicate FsmEditManager's own notion of which types are safely editable. Section header/type-value
// color-coding is a concept ported from FSMExpress's own type/value color split
// (agent-context/FSMExpress-master/FSMExpress/Controls/Sidebar/TypeColorConverter.cs); the literal
// colors themselves live in UICommon, FsmMaster's own palette. Each action (Actions tab) and each
// variable-type group (Variables tab) is drawn inside its own outlined, clickable CanvasSectionBlock -
// see BeginBlock/EndBlock/SelectBlock below.
internal sealed class FsmActiveStatePanel : CanvasPanel
{
    // Design-reference pixel values at 1920x1080 - every actual usage site reads the scaled
    // *Height/*Indent/*Width properties below instead, matching UICommon.FontSize/HeaderFontSize's own
    // ScaleHeight-at-read-time convention. These used to be raw `const float`s consumed directly, which
    // meant row/divider/indent sizes never scaled with resolution while UICommon's own font sizes did -
    // at any Screen.height other than 1080, the actual rendered line height of a row's text (and an
    // active CanvasTextField's caret/selection quads, which are sized from that same line metric) could
    // exceed a row's fixed, unscaled height and bleed into the next row, which then visually painted
    // over it (later-added siblings render in front - see FsmRightPanel's own comment on this
    // convention), making a focused field's caret/highlight look clipped, faint, or entirely covered
    // depending on how much of the overflow the next row's own background happened to hide.
    private const float HeaderHeightDesign = 18f;
    private const float SubTabButtonHeightDesign = 24f;
    private const float RowHeightDesign = 18f;
    private const float HeaderRowHeightDesign = 22f;
    private const float ScrollbarWidthDesign = 10f;
    private const float RowIndentDesign = 4f;
    private const float FieldIndentDesign = 14f;

    // Inner top/bottom padding within a section block (CanvasSectionBlock) and the vertical gap left
    // between one block and the next - see BeginBlock/EndBlock. Blocks span the section's full width
    // (no horizontal inset), matching the divider they replace, which used to do the same.
    private const float BlockPaddingDesign = 4f;
    private const float BlockGapDesign = 6f;

    // Sits to the right of a Random-event-family action's header, in the same row - see
    // AddActionHeaderRow.
    private const float SequencerButtonWidthDesign = 80f;

    // Sequencer tab layout - see BuildSequencerRows/BuildSequencerBlock. Blocks grow to fit their
    // content rather than scrolling internally (only the tab's own outer _scrollView ever scrolls),
    // per this panel's existing single-outer-scroll-view convention.
    private const float ColumnGapDesign = 10f;
    private const float CloseButtonSizeDesign = 16f;
    private const float SeqDeleteButtonSizeDesign = 18f;
    private const float SeqRowGapDesign = 2f;
    private const float LoopToggleWidthDesign = 70f;
    private const float DragGhostWidthDesign = 140f;

    private static float HeaderHeight => UICommon.ScaleHeight(HeaderHeightDesign);
    private static float SubTabButtonHeight => UICommon.ScaleHeight(SubTabButtonHeightDesign);
    private static float RowHeight => UICommon.ScaleHeight(RowHeightDesign);
    private static float HeaderRowHeight => UICommon.ScaleHeight(HeaderRowHeightDesign);
    private static float ScrollbarWidth => UICommon.ScaleWidth(ScrollbarWidthDesign);
    private static float RowIndent => UICommon.ScaleWidth(RowIndentDesign);
    private static float FieldIndent => UICommon.ScaleWidth(FieldIndentDesign);
    private static float ArrayElementIndent => FieldIndent + UICommon.ScaleWidth(12f);
    private static float BlockPadding => UICommon.ScaleHeight(BlockPaddingDesign);
    private static float BlockGap => UICommon.ScaleHeight(BlockGapDesign);
    private static float SequencerButtonWidth => UICommon.ScaleWidth(SequencerButtonWidthDesign);
    private static float ColumnGap => UICommon.ScaleWidth(ColumnGapDesign);
    private static float CloseButtonSize => UICommon.ScaleWidth(CloseButtonSizeDesign);
    private static float SeqDeleteButtonSize => UICommon.ScaleWidth(SeqDeleteButtonSizeDesign);
    private static float SeqRowGap => UICommon.ScaleHeight(SeqRowGapDesign);
    private static float LoopToggleWidth => UICommon.ScaleWidth(LoopToggleWidthDesign);
    private static float DragGhostWidth => UICommon.ScaleWidth(DragGhostWidthDesign);

    private enum SubTabKind
    {
        Actions,
        Events,
        Variables,
        Sequencer,
    }

    private readonly UICommon _ui;
    private readonly FsmEditManager _editManager;
    private readonly FsmVariableTracker _tracker;
    private readonly ManualLogSource _logger;
    private readonly Action<string> _showStatus;
    private readonly CanvasText _header;
    private readonly CanvasToggleDot _stateHeaderDot;
    private readonly CanvasButton _actionsTab;
    private readonly CanvasButton _eventsTab;
    private readonly CanvasButton _variablesTab;
    private readonly CanvasButton _sequencerTab;
    private readonly CanvasScrollView _scrollView;
    private readonly CanvasScrollbar _scrollbar;

    private SubTabKind _activeSubTab = SubTabKind.Actions;
    private FsmInfo? _cachedFsm;
    private string? _cachedStateName;
    private SubTabKind? _cachedSubTab;

    // Matches the current body font size (see UICommon.DotSize) rather than an arbitrary fixed pixel
    // value, so every toggle dot in this panel reads as part of the same line of text it sits beside.
    private float DotWidth => _ui.DotSize;
    private static float DotGap => UICommon.ScaleWidth(4f);

    // Refreshed every frame alongside _valueRefreshers (see RefreshLiveValues) but never cleared by
    // RebuildContent - the state header dot itself persists across every rebuild (only _header.Text
    // changes), unlike the row dots below which are recreated from scratch each time.
    private readonly List<Action> _headerRefreshers = new();

    // Re-reads and redisplays every currently-built value row's live current value - repopulated by
    // RebuildContent, not by Refresh's identity/name cache above. That cache only guards the row
    // *structure* (which fields/rows exist for this fsm/state/subtab) - it must never gate the
    // *values* those rows display, or an external mutation (Load, Reset, the FSM's own running code)
    // shows stale text until the user happens to switch away and back. Never rebuilds a widget, only
    // pushes text into ones that already exist, so it's safe to run every frame without disturbing an
    // in-progress edit (CanvasTextField.UpdateDefaultText already no-ops while focused).
    private readonly List<Action> _valueRefreshers = new();

    // Each action's own outline block (header + fields), keyed by action index - rebuilt every
    // RebuildContent call alongside the rows themselves, so ScrollToAction always has an up-to-date
    // block to scroll to and select even after a state/tab switch changes every row's position.
    private readonly Dictionary<int, CanvasSectionBlock> _actionBlocks = new();

    // Currently-selected section block, if any - cleared (without a Selected=false, since RebuildContent
    // already destroyed the old block along with every other row) on every rebuild, since a rebuild
    // tears down and recreates every block from scratch (see RebuildContent's ClearChildren).
    private CanvasSectionBlock? _selectedBlock;

    // ---- Sequencer tab state ----
    // See BuildSequencerRows/BuildSequencerBlock. A block's key is (StateName, ActionRank) - ActionRank
    // is the rank among Random-event-family actions in that state (see
    // FsmActionSequencer.IndexRandomEventAction), not a raw action array index, so a state with more
    // than one such action gets independently addressable blocks.

    // Blocks the user has explicitly opened via a "Sequencer" button but that may not have an installed
    // pattern yet (an empty block, still being built) - keyed by fsmKey since this panel is reused
    // across whichever FSM tab is active. Merged with whatever SequencerOverrides are actually installed
    // (which persists independently, e.g. after Save/Load) when the tab's rows are built - see
    // CollectOpenSequencerKeys. A block leaves this set (and the live sequencer, if any, is uninstalled)
    // only via its own close button - emptying its pattern alone does not close it.
    private readonly Dictionary<string, HashSet<(string StateName, int ActionRank)>> _openSequencerBlocks = new();

    // Rebuilt every RebuildContent pass for the Sequencer subtab alongside the rows themselves - lets a
    // click on the Actions tab's own Sequencer button scroll to/select the block it just opened.
    private readonly Dictionary<(string StateName, int ActionRank), CanvasSectionBlock> _sequencerBlockWidgets = new();

    // A block's own Sequence-column bounding rect (content-space, matching cursor.Y's own coordinate
    // system) and its individual row Y-ranges within that rect - used by EndDrag to find which block (if
    // any) a drag was dropped into and where. Cross-block drops are rejected: only a drop landing inside
    // the SAME block the drag originated from (_dragBlockKey) is accepted.
    private readonly Dictionary<(string StateName, int ActionRank), Rect> _sequenceColumnRects = new();
    private readonly Dictionary<(string StateName, int ActionRank), List<(float Y, float Height)>> _sequenceRowRanges = new();

    private (string StateName, int ActionRank)? _dragBlockKey;
    private string? _dragEventName;
    private int? _dragSourceIndex;

    // Small floating label that follows the pointer during a drag - added directly to this panel (not
    // into _scrollView.Content, so it survives RebuildContent and is never clipped by the scroll view's
    // own RectMask2D) and last among this panel's own children, so its whole subtree renders on top of
    // the scroll view/scrollbar regardless of which block's row started the drag.
    private readonly CanvasPanel _dragGhost;
    private readonly CanvasText _dragGhostText;

    public FsmActiveStatePanel(UICommon ui, FsmEditManager editManager, FsmVariableTracker tracker, ManualLogSource logger, Action<string> showStatus) : base("ActiveStatePanel")
    {
        _ui = ui;
        _editManager = editManager;
        _tracker = tracker;
        _logger = logger;
        _showStatus = showStatus;

        _header = Add(new CanvasText("Header", ui) { Text = "No state selected" });
        _header.Font = ui.HeaderFont;
        _header.FontStyle = FontStyle.Bold;

        // Tracks/displays the FSM's live active state, not whichever state happens to be selected in
        // the panel (that's what the header text above still shows) - so toggling this dot on and then
        // clicking through other states in the graph keeps watching whatever state that FSM is actually
        // running, rather than silently re-targeting to each newly-selected state.
        _stateHeaderDot = Add(new CanvasToggleDot("StateHeaderDot", ui));
        _stateHeaderDot.ActiveSelf = false;
        _stateHeaderDot.OnClicked += () =>
        {
            if (_cachedFsm?.Component == null)
            {
                return;
            }

            string fsmKey = FsmIdentity.GetFsmKey(_cachedFsm.Component);
            string activeStateName = _cachedFsm.Fsm.ActiveStateName;
            if (_tracker.IsStateTracked(fsmKey, activeStateName))
            {
                _tracker.UntrackState(fsmKey, activeStateName);
            }
            else
            {
                _tracker.TrackState(fsmKey, activeStateName);
            }
        };
        _headerRefreshers.Add(() =>
        {
            if (_cachedFsm?.Component == null)
            {
                return;
            }

            string fsmKey = FsmIdentity.GetFsmKey(_cachedFsm.Component);
            _stateHeaderDot.On = _tracker.IsStateTracked(fsmKey, _cachedFsm.Fsm.ActiveStateName);
        });

        _actionsTab = Add(new CanvasButton("ActionsTab", ui));
        _actionsTab.Text.Text = "Actions";
        _actionsTab.OnClicked += () => SetActiveSubTab(SubTabKind.Actions);

        _eventsTab = Add(new CanvasButton("EventsTab", ui));
        _eventsTab.Text.Text = "Events";
        _eventsTab.OnClicked += () => SetActiveSubTab(SubTabKind.Events);

        _variablesTab = Add(new CanvasButton("VariablesTab", ui));
        _variablesTab.Text.Text = "Variables";
        _variablesTab.OnClicked += () => SetActiveSubTab(SubTabKind.Variables);

        _sequencerTab = Add(new CanvasButton("SequencerTab", ui));
        _sequencerTab.Text.Text = "Sequencer";
        _sequencerTab.OnClicked += () => SetActiveSubTab(SubTabKind.Sequencer);

        _scrollView = Add(new CanvasScrollView("ScrollView"));
        _scrollView.SetContent(new CanvasPanel("Content"));

        _scrollbar = Add(new CanvasScrollbar("Scrollbar", ui) { ScrollView = _scrollView });

        // Added last so its whole subtree renders/hit-tests on top of the scroll view and scrollbar -
        // matching the "later-added sibling renders in front" convention used throughout this codebase.
        _dragGhost = Add(new CanvasPanel("DragGhost"));
        _dragGhost.ActiveSelf = false;
        CanvasImage dragGhostBackground = _dragGhost.Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.ButtonActive });
        dragGhostBackground.AddBorder(ui.AccentColor);
        _dragGhostText = _dragGhost.Add(new CanvasText("Label", ui));
        _dragGhostText.Alignment = TextAnchor.MiddleLeft;
        _dragGhostText.Overflow = HorizontalWrapMode.Overflow;

        UpdateSubTabToggles();

        // This panel is always active whenever the right panel is, so its own OnUpdate is the one
        // safe place to decide the scrollbar's ActiveSelf - see CanvasScrollbar.ShouldBeVisible for
        // why the scrollbar can't make that call about itself.
        OnUpdate += () => _scrollbar.ActiveSelf = _scrollbar.ShouldBeVisible;
    }

    public override void Build(Transform? rootParent = null)
    {
        Layout();
        base.Build(rootParent);
    }

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        Layout();
    }

    private void Layout()
    {
        _stateHeaderDot.LocalPosition = new Vector2(0f, (HeaderHeight - DotWidth) / 2f);
        _stateHeaderDot.Size = new Vector2(DotWidth, DotWidth);

        _header.LocalPosition = new Vector2(DotWidth + DotGap, 0f);
        _header.Size = new Vector2(Mathf.Max(0f, Size.x - DotWidth - DotGap), HeaderHeight);

        float tabY = HeaderHeight + 2f;
        float tabWidth = Size.x / 4f;
        _actionsTab.LocalPosition = new Vector2(0f, tabY);
        _actionsTab.Size = new Vector2(tabWidth, SubTabButtonHeight);
        _eventsTab.LocalPosition = new Vector2(tabWidth, tabY);
        _eventsTab.Size = new Vector2(tabWidth, SubTabButtonHeight);
        _variablesTab.LocalPosition = new Vector2(tabWidth * 2f, tabY);
        _variablesTab.Size = new Vector2(tabWidth, SubTabButtonHeight);
        _sequencerTab.LocalPosition = new Vector2(tabWidth * 3f, tabY);
        _sequencerTab.Size = new Vector2(Size.x - tabWidth * 3f, SubTabButtonHeight);

        float scrollY = tabY + SubTabButtonHeight + 4f;
        float scrollHeight = Mathf.Max(0f, Size.y - scrollY);

        // The Sequencer tab's blocks reach this panel's own right edge - lining up with the tab strip's
        // own right edge above them - rather than stopping short by the scrollbar's usual reserved
        // gutter. This is safe to do per-subtab (not just uniformly for every tab) because the scrollbar
        // is a sibling of _scrollView, not one of its clipped children (see its own construction below),
        // so it can still float on top of that wider content whenever it's actually visible instead of
        // needing a dedicated non-overlapping lane.
        float scrollWidth = _activeSubTab == SubTabKind.Sequencer
            ? Mathf.Max(0f, Size.x)
            : Mathf.Max(0f, Size.x - ScrollbarWidth - 2f);
        _scrollView.LocalPosition = new Vector2(0f, scrollY);
        _scrollView.Size = new Vector2(scrollWidth, scrollHeight);

        _scrollbar.LocalPosition = new Vector2(Size.x - ScrollbarWidth, scrollY);
        _scrollbar.Size = new Vector2(ScrollbarWidth, scrollHeight);

        _dragGhost.Size = new Vector2(DragGhostWidth, RowHeight);
        _dragGhostText.Size = _dragGhost.Size;
    }

    private void SetActiveSubTab(SubTabKind kind)
    {
        if (_activeSubTab == kind)
        {
            return;
        }

        _activeSubTab = kind;
        UpdateSubTabToggles();

        // Layout()'s own scroll-view width depends on _activeSubTab (see its own comment) - Layout is
        // otherwise only re-run on an actual panel resize (Build/OnUpdateSize), so switching tabs needs
        // its own explicit call to pick that up immediately rather than only on the next resize.
        Layout();
    }

    private void UpdateSubTabToggles()
    {
        _actionsTab.Toggled = _activeSubTab == SubTabKind.Actions;
        _eventsTab.Toggled = _activeSubTab == SubTabKind.Events;
        _variablesTab.Toggled = _activeSubTab == SubTabKind.Variables;
        _sequencerTab.Toggled = _activeSubTab == SubTabKind.Sequencer;
    }

    // Called every frame by the owner (see FsmMasterPlugin.Update) with whatever FSM/state is
    // currently active - a cheap no-op unless the (fsm, state, sub-tab) triple actually changed. This
    // also means an in-progress edit is never clobbered by a same-selection re-poll - RebuildContent
    // only ever runs on an actual tab/state/sub-tab change, which already tears down (and re-creates)
    // every row via CanvasPanel.ClearChildren, discarding any unsaved in-progress text deliberately.
    public void Refresh(FsmInfo? fsm, string? stateName)
    {
        if (ReferenceEquals(_cachedFsm, fsm) && _cachedStateName == stateName && _cachedSubTab == _activeSubTab)
        {
            return;
        }

        _cachedFsm = fsm;
        _cachedStateName = stateName;
        _cachedSubTab = _activeSubTab;

        RebuildContent(fsm, stateName);
    }

    // Called by FsmMasterPlugin.Update right after Refresh(), once per request, whenever a transition
    // click in the graph overlay resolves to a specific action (see FsmTabState.PendingScrollActionIndex).
    // Forces the Actions subtab if a different one was showing - the whole point of this call is to bring
    // that action's fields into view, so a stale Events/Variables tab would defeat it.
    public void ScrollToAction(int actionIndex)
    {
        if (_activeSubTab != SubTabKind.Actions)
        {
            SetActiveSubTab(SubTabKind.Actions);
            RebuildContent(_cachedFsm, _cachedStateName);
            _cachedSubTab = _activeSubTab;
        }

        if (_actionBlocks.TryGetValue(actionIndex, out CanvasSectionBlock? block))
        {
            _scrollView.ScrollToShow(block.LocalPosition.y, block.Size.y);
            SelectBlock(block);
        }
    }

    // Called every frame regardless of whether Refresh() just rebuilt anything - see _valueRefreshers.
    public void RefreshLiveValues()
    {
        foreach (Action refresh in _valueRefreshers)
        {
            refresh();
        }

        foreach (Action refresh in _headerRefreshers)
        {
            refresh();
        }
    }

    private void RebuildContent(FsmInfo? fsm, string? stateName)
    {
        var content = (CanvasPanel)_scrollView.Content!;
        content.ClearChildren();
        _valueRefreshers.Clear();
        _actionBlocks.Clear();
        _selectedBlock = null;
        _sequencerBlockWidgets.Clear();
        _sequenceColumnRects.Clear();
        _sequenceRowRanges.Clear();

        FsmStateInfo? state = null;
        if (fsm != null && stateName != null)
        {
            foreach (FsmStateInfo candidate in fsm.States)
            {
                if (candidate.Name == stateName)
                {
                    state = candidate;
                    break;
                }
            }
        }

        _header.Text = state != null ? $"State: {state.Name}" : "No state selected";
        _stateHeaderDot.ActiveSelf = fsm != null && state != null;

        // Every other subtab needs a specific selected state (Actions shows that state's own actions;
        // Events/Variables happen to be FSM-wide but have always required a selection too, for
        // consistency) - Sequencer is the one exception, since its blocks can span every state in the
        // FSM that has one open, independent of whichever state happens to be selected right now.
        if (fsm == null || (state == null && _activeSubTab != SubTabKind.Sequencer))
        {
            content.Size = new Vector2(_scrollView.Size.x, 0f);
            return;
        }

        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
        var cursor = new RowCursor();

        switch (_activeSubTab)
        {
            case SubTabKind.Actions:
                BuildActionsRows(content, cursor, fsmKey, state!);
                break;
            case SubTabKind.Events:
                BuildEventsRows(content, cursor, fsm);
                break;
            case SubTabKind.Variables:
                BuildVariableRows(content, cursor, fsmKey, fsm);
                break;
            case SubTabKind.Sequencer:
                BuildSequencerRows(content, cursor, fsmKey, fsm);
                break;
        }

        content.Size = new Vector2(_scrollView.Size.x, cursor.Y);
    }

    // Threaded through every row-builder below instead of a plain `ref float` - headers/dividers/rows
    // are no longer uniform height, and each row also needs a unique CanvasPanel child name.
    private sealed class RowCursor
    {
        public float Y;
        public int Count;
    }

    // ---------------- Actions tab ----------------

    private void BuildActionsRows(CanvasPanel content, RowCursor cursor, string fsmKey, FsmStateInfo state)
    {
        // Rank among this state's own Random-event-family actions specifically (not a raw action-array
        // index) - matches FsmActionSequencer.IndexRandomEventAction/FsmEditManager's own rank-based
        // sequencer addressing, so a state with more than one such action gets independently
        // addressable Sequencer buttons/blocks. -1 for any action that isn't itself a Random-event
        // action (AddActionHeaderRow only reads it when isRandom is true).
        int randomActionRank = -1;
        for (int actionIndex = 0; actionIndex < state.Actions.Count; actionIndex++)
        {
            FsmActionInfo action = state.Actions[actionIndex];
            if (action.ActionType.Name.Contains("Random"))
            {
                randomActionRank++;
            }

            CanvasSectionBlock block = BeginBlock(content, cursor);

            AddActionHeaderRow(content, cursor, fsmKey, state, actionIndex, randomActionRank, action);

            foreach (FsmActionFieldInfo field in action.Fields)
            {
                AddActionFieldRow(content, cursor, fsmKey, state.Name, actionIndex, action, field);
            }

            EndBlock(block, cursor);
            _actionBlocks[actionIndex] = block;
        }
    }

    private void AddActionFieldRow(CanvasPanel content, RowCursor cursor, string fsmKey, string stateName, int actionIndex, FsmActionInfo action, FsmActionFieldInfo field)
    {
        string expectedActionTypeName = action.ActionType.Name;
        string fieldName = field.FieldName;
        string label = field.IsHidden ? $"{fieldName} (hidden)" : fieldName;
        Color? labelColor = field.IsHidden ? _ui.HiddenFieldLabelColor : null;

        // PlayMaker action fields expose an array as Fsm<Type>[] (FsmFloat[], FsmEvent[], ...), never a
        // raw primitive array or an Array-typed FsmVariable (confirmed against
        // agent-context/Playmaker's decompiled actions) - each element is its own NamedVariable, so
        // this renders one row per element instead of the single joined-string read-only row
        // FormatActionField would otherwise produce for the whole array.
        if (field.FieldValue is Array array)
        {
            AddActionFieldArrayRows(content, cursor, fsmKey, stateName, actionIndex, action, field, array);
            return;
        }

        float dotIndent = AddToggleDot(content, cursor, FieldIndent,
            () => _tracker.IsActionFieldTracked(fsmKey, stateName, actionIndex, fieldName),
            () =>
            {
                if (_tracker.IsActionFieldTracked(fsmKey, stateName, actionIndex, fieldName))
                {
                    _tracker.UntrackActionField(fsmKey, stateName, actionIndex, fieldName);
                }
                else
                {
                    _tracker.TrackActionField(fsmKey, stateName, actionIndex, fieldName);
                }
            });

        if (!FsmEditManager.TryFormatValue(field.FieldValue, out string formatted))
        {
            AddReadOnlyRow(content, cursor, label, () => FormatActionField(action.Action, field.Field.GetValue(action.Action)), _ui.ReadOnlyColor, dotIndent, labelColor);
            return;
        }

        string ReadCurrentFieldText() =>
            FsmEditManager.TryFormatValue(field.Field.GetValue(action.Action), out string current) ? current : formatted;

        if (IsBoolLike(field.FieldValue))
        {
            AddBoolRow(content, cursor, label, () => ToBool(field.Field.GetValue(action.Action)), dotIndent, newValue =>
            {
                try
                {
                    _editManager.SetActionField(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, newValue.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"[FsmMaster] Failed to set action field '{stateName}'[{actionIndex}].{fieldName}: {e.Message}");
                }
            }, labelColor);
            return;
        }

        Color valueColor = ValueColorFor(field.FieldValue);
        AddTextFieldRow(content, cursor, label, ReadCurrentFieldText, valueColor, dotIndent, text =>
        {
            try
            {
                _editManager.SetActionField(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, text);
            }
            catch (Exception e)
            {
                _logger.LogWarning($"[FsmMaster] Failed to set action field '{stateName}'[{actionIndex}].{fieldName}: {e.Message}");
            }

            return ReadCurrentFieldText();
        }, labelColor);
    }

    private void AddActionFieldArrayRows(CanvasPanel content, RowCursor cursor, string fsmKey, string stateName, int actionIndex, FsmActionInfo action, FsmActionFieldInfo field, Array array)
    {
        string expectedActionTypeName = action.ActionType.Name;
        string fieldName = field.FieldName;
        string arrayLabel = field.IsHidden ? $"{fieldName} (hidden)" : fieldName;
        Color? labelColor = field.IsHidden ? _ui.HiddenFieldLabelColor : null;

        AddLabelOnlyRow(content, cursor, array.Length > 0 ? $"{arrayLabel} [{array.Length}]" : $"{arrayLabel} (empty)", labelColor ?? _ui.TextColor, FieldIndent);

        for (int elementIndex = 0; elementIndex < array.Length; elementIndex++)
        {
            object? element = array.GetValue(elementIndex);

            // Each element of an Fsm<Type>[] action field (unlike an FsmArray variable's boxed
            // elements) is its own NamedVariable, which is either bound to one of the FSM's own
            // named variables or left as an inline literal (empty Name) - showing the bound name
            // here is what actually tells BoolTestMulti-style actions (multiple FsmBool elements,
            // one per tested variable) apart, since "[i]" alone doesn't say which bool is being read.
            string elementLabel = element is NamedVariable namedElement && !string.IsNullOrEmpty(namedElement.Name)
                ? $"[{elementIndex}] \"{namedElement.Name}\""
                : $"[{elementIndex}]";
            int capturedIndex = elementIndex;

            float dotIndent = AddToggleDot(content, cursor, ArrayElementIndent,
                () => _tracker.IsActionFieldArrayElementTracked(fsmKey, stateName, actionIndex, fieldName, capturedIndex),
                () =>
                {
                    if (_tracker.IsActionFieldArrayElementTracked(fsmKey, stateName, actionIndex, fieldName, capturedIndex))
                    {
                        _tracker.UntrackActionFieldArrayElement(fsmKey, stateName, actionIndex, fieldName, capturedIndex);
                    }
                    else
                    {
                        _tracker.TrackActionFieldArrayElement(fsmKey, stateName, actionIndex, fieldName, capturedIndex);
                    }
                });

            if (!FsmEditManager.TryFormatValue(element, out string formatted))
            {
                AddReadOnlyRow(content, cursor, elementLabel, () => FormatActionField(action.Action, ReadCurrentElement(capturedIndex)), _ui.ReadOnlyColor, dotIndent, labelColor);
                continue;
            }

            if (IsBoolLike(element))
            {
                AddBoolRow(content, cursor, elementLabel, () => ToBool(ReadCurrentElement(capturedIndex)), dotIndent, newValue =>
                {
                    try
                    {
                        _editManager.SetActionFieldArrayElement(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, capturedIndex, newValue.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning($"[FsmMaster] Failed to set action field '{stateName}'[{actionIndex}].{fieldName}[{capturedIndex}]: {e.Message}");
                    }
                }, labelColor);
                continue;
            }

            string ReadCurrentElementText() =>
                FsmEditManager.TryFormatValue(ReadCurrentElement(capturedIndex), out string current) ? current : formatted;

            Color valueColor = ValueColorFor(element);
            AddTextFieldRow(content, cursor, elementLabel, ReadCurrentElementText, valueColor, dotIndent, text =>
            {
                try
                {
                    _editManager.SetActionFieldArrayElement(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, capturedIndex, text);
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"[FsmMaster] Failed to set action field '{stateName}'[{actionIndex}].{fieldName}[{capturedIndex}]: {e.Message}");
                }

                return ReadCurrentElementText();
            }, labelColor);
        }

        // Re-reads element i of this same array field directly off the live action each time - array
        // is only this frame's snapshot (from FsmDataCollector), so index i's own value must be
        // re-fetched through the live field reference, not through the captured array/element objects.
        object? ReadCurrentElement(int i) =>
            field.Field.GetValue(action.Action) is Array refreshed && i < refreshed.Length ? refreshed.GetValue(i) : null;
    }

    // ---------------- Events tab (read-only - no per-event "value" exists to attach an editor to) ----------------

    private void BuildEventsRows(CanvasPanel content, RowCursor cursor, FsmInfo fsm)
    {
        if (fsm.Fsm.Events.Length == 0)
        {
            return;
        }

        CanvasSectionBlock block = BeginBlock(content, cursor);

        AddSectionHeaderRow(content, cursor, "Events", RowIndent);

        foreach (FsmEvent fsmEvent in fsm.Fsm.Events)
        {
            AddLabelOnlyRow(content, cursor, fsmEvent.Name, _ui.TextColor, FieldIndent);
        }

        EndBlock(block, cursor);
    }

    // ---------------- Variables tab ----------------

    private void BuildVariableRows(CanvasPanel content, RowCursor cursor, string fsmKey, FsmInfo fsm)
    {
        FsmVariables variables = fsm.Fsm.Variables;

        AddNamedVariableGroup(content, cursor, fsmKey, "Float", variables.FloatVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Int", variables.IntVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Bool", variables.BoolVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "String", variables.StringVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Vector2", variables.Vector2Variables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Vector3", variables.Vector3Variables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Rect", variables.RectVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Quaternion", variables.QuaternionVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Color", variables.ColorVariables);
        AddNamedVariableGroup(content, cursor, fsmKey, "Enum", variables.EnumVariables);

        // Reference/collection-typed variables stay outside FsmEditManager.SupportedVariableTypes (see
        // FsmEditManager.ApplyVariableOverride) - always read-only, own bespoke display untouched.
        AddReadOnlyGroup(content, cursor, fsmKey, "GameObject", variables.GameObjectVariables, v => v.Value != null ? v.Value.name : "null");
        AddReadOnlyGroup(content, cursor, fsmKey, "Object", variables.ObjectVariables, v => v.Value != null ? v.Value.ToString() : "null");
        AddReadOnlyGroup(content, cursor, fsmKey, "Material", variables.MaterialVariables, v => v.Value != null ? v.Value.ToString() : "null");
        AddReadOnlyGroup(content, cursor, fsmKey, "Texture", variables.TextureVariables, v => v.Value != null ? v.Value.ToString() : "null");
        AddArrayVariableGroup(content, cursor, fsmKey, variables.ArrayVariables);
    }

    private void AddArrayVariableGroup(CanvasPanel content, RowCursor cursor, string fsmKey, FsmArray[] items)
    {
        if (items.Length == 0)
        {
            return;
        }

        CanvasSectionBlock block = BeginBlock(content, cursor);

        AddSectionHeaderRow(content, cursor, "Array", RowIndent);

        foreach (FsmArray array in items)
        {
            AddArrayVariableRow(content, cursor, fsmKey, array);
        }

        EndBlock(block, cursor);
    }

    // FsmArray stores every element as a boxed primitive keyed by its own ElementType, not as a
    // per-element NamedVariable - only the four simplest element types are editable here (see
    // FsmEditManager.SupportedArrayElementTypes for why vector/rect/quaternion/color-typed arrays are
    // excluded); anything else keeps the old single-row joined-string read-only display.
    private void AddArrayVariableRow(CanvasPanel content, RowCursor cursor, string fsmKey, FsmArray array)
    {
        string variableName = array.Name;

        if (!FsmEditManager.IsSupportedArrayElementType(array.ElementType))
        {
            AddReadOnlyRow(content, cursor, $"{variableName} ({array.ElementType})", () => string.Join(", ", array.Values), _ui.ReadOnlyColor, FieldIndent);
            return;
        }

        AddLabelOnlyRow(content, cursor, array.Length > 0 ? $"{variableName} ({array.ElementType}[{array.Length}])" : $"{variableName} (empty)", _ui.TextColor, FieldIndent);

        for (int elementIndex = 0; elementIndex < array.Length; elementIndex++)
        {
            object? element = array.Get(elementIndex);
            string elementLabel = $"[{elementIndex}]";
            int capturedIndex = elementIndex;

            float dotIndent = AddToggleDot(content, cursor, ArrayElementIndent,
                () => _tracker.IsVariableArrayElementTracked(fsmKey, variableName, capturedIndex),
                () =>
                {
                    if (_tracker.IsVariableArrayElementTracked(fsmKey, variableName, capturedIndex))
                    {
                        _tracker.UntrackVariableArrayElement(fsmKey, variableName, capturedIndex);
                    }
                    else
                    {
                        _tracker.TrackVariableArrayElement(fsmKey, variableName, capturedIndex);
                    }
                });

            if (array.ElementType == VariableType.Bool)
            {
                AddBoolRow(content, cursor, elementLabel, () => Convert.ToBoolean(array.Get(capturedIndex)), dotIndent, newValue =>
                {
                    try
                    {
                        _editManager.SetVariableArrayElement(fsmKey, variableName, capturedIndex, newValue.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning($"[FsmMaster] Failed to set '{variableName}[{capturedIndex}]': {e.Message}");
                    }
                });
                continue;
            }

            string formatted = FsmEditManager.FormatArrayElement(array.ElementType, element);
            string ReadCurrentElementText() =>
                capturedIndex < array.Length ? FsmEditManager.FormatArrayElement(array.ElementType, array.Get(capturedIndex)) : formatted;

            Color valueColor = ValueColorFor(element);
            AddTextFieldRow(content, cursor, elementLabel, ReadCurrentElementText, valueColor, dotIndent, text =>
            {
                try
                {
                    _editManager.SetVariableArrayElement(fsmKey, variableName, capturedIndex, text);
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"[FsmMaster] Failed to set '{variableName}[{capturedIndex}]': {e.Message}");
                }

                return ReadCurrentElementText();
            });
        }
    }

    private void AddNamedVariableGroup<T>(CanvasPanel content, RowCursor cursor, string fsmKey, string typeName, T[] items) where T : NamedVariable
    {
        if (items.Length == 0)
        {
            return;
        }

        CanvasSectionBlock block = BeginBlock(content, cursor);

        AddSectionHeaderRow(content, cursor, typeName, RowIndent);

        foreach (T variable in items)
        {
            AddNamedVariableRow(content, cursor, fsmKey, variable);
        }

        EndBlock(block, cursor);
    }

    private void AddNamedVariableRow(CanvasPanel content, RowCursor cursor, string fsmKey, NamedVariable variable)
    {
        string variableName = variable.Name;
        float dotIndent = AddToggleDot(content, cursor, FieldIndent,
            () => _tracker.IsVariableTracked(fsmKey, variableName),
            () =>
            {
                if (_tracker.IsVariableTracked(fsmKey, variableName))
                {
                    _tracker.UntrackVariable(fsmKey, variableName);
                }
                else
                {
                    _tracker.TrackVariable(fsmKey, variableName);
                }
            });

        // TryFormatValue is also the editability gate here - every type this method is ever called for
        // (see AddNamedVariableGroup's call sites above) is in FsmEditManager.SupportedVariableTypes,
        // so this always succeeds in practice; kept as a real check (not asserted) so this stays correct
        // if a caller is ever added for a type outside that set.
        if (!FsmEditManager.TryFormatValue(variable, out string formatted))
        {
            AddReadOnlyRow(content, cursor, variable.Name, () => FsmEditManager.TryFormatValue(variable, out string current) ? current : formatted, _ui.ReadOnlyColor, dotIndent);
            return;
        }

        string variableType = variable.VariableType.ToString();
        string ReadCurrentVariableText() => FsmEditManager.TryFormatValue(variable, out string current) ? current : formatted;

        if (IsBoolLike(variable))
        {
            AddBoolRow(content, cursor, variableName, () => ToBool(variable), dotIndent, newValue =>
            {
                try
                {
                    _editManager.SetVariable(fsmKey, variableName, variableType, newValue.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"[FsmMaster] Failed to set variable '{variableName}': {e.Message}");
                }
            });
            return;
        }

        Color valueColor = ValueColorFor(variable);
        AddTextFieldRow(content, cursor, variableName, ReadCurrentVariableText, valueColor, dotIndent, text =>
        {
            try
            {
                _editManager.SetVariable(fsmKey, variableName, variableType, text);
            }
            catch (Exception e)
            {
                _logger.LogWarning($"[FsmMaster] Failed to set variable '{variableName}': {e.Message}");
            }

            return ReadCurrentVariableText();
        });
    }

    private void AddReadOnlyGroup<T>(CanvasPanel content, RowCursor cursor, string fsmKey, string typeName, T[] items, Func<T, string> formatValue) where T : NamedVariable
    {
        if (items.Length == 0)
        {
            return;
        }

        CanvasSectionBlock block = BeginBlock(content, cursor);

        AddSectionHeaderRow(content, cursor, typeName, RowIndent);

        foreach (T item in items)
        {
            string variableName = item.Name;

            // GameObject/Object/Material/Texture variables are read-only for editing purposes, but
            // FsmVariableTracker.ResolveCurrentValue resolves any NamedVariable generically via
            // FindVariable + ToString() regardless of editability, so tracking these works fine too.
            float dotIndent = AddToggleDot(content, cursor, FieldIndent,
                () => _tracker.IsVariableTracked(fsmKey, variableName),
                () =>
                {
                    if (_tracker.IsVariableTracked(fsmKey, variableName))
                    {
                        _tracker.UntrackVariable(fsmKey, variableName);
                    }
                    else
                    {
                        _tracker.TrackVariable(fsmKey, variableName);
                    }
                });

            AddReadOnlyRow(content, cursor, item.Name, () => formatValue(item), _ui.ReadOnlyColor, dotIndent);
        }

        EndBlock(block, cursor);
    }

    // ---------------- Sequencer tab ----------------
    // One outlined block per open sequencer block key (StateName, ActionRank - ActionRank is the rank
    // among that state's own Random-event-family actions, see FsmActionSequencer.IndexRandomEventAction,
    // not a raw action array index), spanning every state in the current FSM tab that has one open - see
    // AddActionHeaderRow's own Sequencer button for how a block gets opened. Each block has no persistent
    // widget-level state of its own: every rebuild reads the live SequencerOverride (if any) fresh from
    // FsmEditManager, and every mutation (drag-drop, delete, loop change) writes straight back through
    // FsmEditManager and then forces an immediate RebuildContent - the same "apply immediately, no Save
    // button" convention every other editable row in this panel already follows.

    private void BuildSequencerRows(CanvasPanel content, RowCursor cursor, string fsmKey, FsmInfo fsm)
    {
        HashSet<(string StateName, int ActionRank)> keys = CollectOpenSequencerKeys(fsmKey);
        if (keys.Count == 0)
        {
            return;
        }

        foreach (FsmStateInfo state in fsm.States)
        {
            List<int> ranksForState = keys.Where(k => k.StateName == state.Name).Select(k => k.ActionRank).OrderBy(r => r).ToList();
            if (ranksForState.Count == 0)
            {
                continue;
            }

            int randomActionRank = -1;
            for (int actionIndex = 0; actionIndex < state.Actions.Count; actionIndex++)
            {
                FsmActionInfo action = state.Actions[actionIndex];
                if (!action.ActionType.Name.Contains("Random"))
                {
                    continue;
                }

                randomActionRank++;
                if (!ranksForState.Contains(randomActionRank))
                {
                    continue;
                }

                // Only disambiguated with a "(x)" ordinal when this state actually has more than one
                // open block - a state with just one reads as a plain state name.
                string label = ranksForState.Count > 1
                    ? $"{state.Name} ({ranksForState.IndexOf(randomActionRank) + 1})"
                    : state.Name;

                BuildSequencerBlock(content, cursor, fsmKey, state, randomActionRank, action, label);
            }
        }
    }

    // Union of every state+rank with an actually-installed SequencerOverride (persists independently of
    // this panel, e.g. after Save/Load or a scene revisit) and every state+rank the user has explicitly
    // opened this session but may not have populated yet (see _openSequencerBlocks's own comment).
    private HashSet<(string StateName, int ActionRank)> CollectOpenSequencerKeys(string fsmKey)
    {
        var keys = new HashSet<(string, int)>();

        FsmEditSet? editSet = _editManager.GetActiveEditSet(fsmKey);
        if (editSet != null)
        {
            foreach (SequencerOverride s in editSet.SequencerOverrides)
            {
                keys.Add((s.StateName, s.ActionIndex));
            }
        }

        if (_openSequencerBlocks.TryGetValue(fsmKey, out HashSet<(string, int)>? opened))
        {
            keys.UnionWith(opened);
        }

        return keys;
    }

    private void BuildSequencerBlock(CanvasPanel content, RowCursor cursor, string fsmKey, FsmStateInfo state, int actionRank, FsmActionInfo action, string label)
    {
        CanvasSectionBlock block = BeginBlock(content, cursor);

        float gap = UICommon.ScaleWidth(4f);
        float contentWidth = Mathf.Max(0f, _scrollView.Size.x - RowIndent);
        float headerWidth = Mathf.Max(0f, contentWidth - CloseButtonSize - gap);

        CanvasText header = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        header.Text = label;
        header.Font = _ui.HeaderFont;
        header.FontStyle = FontStyle.Bold;
        header.Color = _ui.TypeBadgeColor;
        header.Overflow = HorizontalWrapMode.Overflow;
        header.LocalPosition = new Vector2(RowIndent, cursor.Y);
        header.Size = new Vector2(headerWidth, HeaderRowHeight);
        header.Build();

        CanvasButton closeButton = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
        closeButton.Text.Text = "x";
        closeButton.Text.Color = _ui.ErrorColor;
        closeButton.LocalPosition = new Vector2(RowIndent + headerWidth + gap, cursor.Y);
        closeButton.Size = new Vector2(CloseButtonSize, HeaderRowHeight);
        closeButton.Build();
        string stateName = state.Name;
        closeButton.OnClicked += () => CloseSequencerBlock(fsmKey, stateName, actionRank);

        cursor.Y += HeaderRowHeight;

        float columnWidth = Mathf.Max(0f, (contentWidth - ColumnGap) / 2f);
        float leftX = RowIndent;
        float rightX = leftX + columnWidth + ColumnGap;
        float columnTopY = cursor.Y;

        List<string> pattern = GetSequencerPattern(fsmKey, stateName, actionRank);
        bool indefinite = GetSequencerIndefinite(fsmKey, stateName, actionRank);
        int fixedExtraLoops = GetSequencerFixedExtraLoops(fsmKey, stateName, actionRank);
        List<string> candidateEvents = FsmActionSequencer.ExtractEventCandidates(action.Action, state.State).Select(e => e.Name).ToList();

        float leftBottomY = BuildSequenceColumn(content, cursor, fsmKey, stateName, actionRank, pattern, leftX, columnTopY, columnWidth);
        float rightBottomY = BuildLoopAndEventsColumn(content, cursor, fsmKey, stateName, actionRank, candidateEvents, indefinite, fixedExtraLoops, rightX, columnTopY, columnWidth);

        cursor.Y = Mathf.Max(leftBottomY, rightBottomY);

        // Spans down to the taller of the two columns (not just the Sequence column's own, possibly
        // shorter, content) - covers a short Sequence column while Events is the taller one, and (via
        // BuildSequenceColumn's own always-present blank row) still leaves real, visible catch space
        // below the last sequence entry without needing an extra invisible buffer here on top of it.
        _sequenceColumnRects[(stateName, actionRank)] = new Rect(leftX, columnTopY, columnWidth, cursor.Y - columnTopY);

        EndBlock(block, cursor);
        _sequencerBlockWidgets[(stateName, actionRank)] = block;
    }

    // Left column: the ordered pattern - a draggable, reorderable, deletable row per entry. Records this
    // block's own per-row Y-ranges (in the shared _scrollView's content-space, the same coordinate
    // system cursor.Y already builds every row in) for EndDrag's own drop-index resolution - the
    // column's overall drop-acceptance rect is recorded separately by the caller (BuildSequencerBlock),
    // once it knows the taller of this column and the Loop+Events one, so a drop below the last event
    // still lands inside the block instead of being rejected right past the last row's own bottom edge.
    private float BuildSequenceColumn(CanvasPanel content, RowCursor cursor, string fsmKey, string stateName, int actionRank, List<string> pattern, float x, float y, float width)
    {
        (string, int) blockKey = (stateName, actionRank);

        CanvasText header = content.Add(new CanvasText($"Row{cursor.Count++}", _ui) { Text = "Sequence" });
        header.Color = _ui.ReadOnlyColor;
        header.Overflow = HorizontalWrapMode.Overflow;
        header.LocalPosition = new Vector2(x, y);
        header.Size = new Vector2(width, RowHeight);
        header.Build();
        y += RowHeight;

        var rowRanges = new List<(float Y, float Height)>();

        float dragWidth = Mathf.Max(0f, width - SeqDeleteButtonSize - SeqRowGap);
        for (int i = 0; i < pattern.Count; i++)
        {
            int capturedIndex = i;
            string eventName = pattern[i];

            CanvasButton dragSurface = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
            dragSurface.Text.Text = $"{i + 1}. {eventName}";
            dragSurface.Text.Alignment = TextAnchor.MiddleLeft;
            dragSurface.Text.Overflow = HorizontalWrapMode.Overflow;
            dragSurface.LocalPosition = new Vector2(x, y);
            dragSurface.Size = new Vector2(dragWidth, RowHeight);
            dragSurface.Build();
            dragSurface.AddEventTrigger(EventTriggerType.BeginDrag, e => BeginDragReorder(blockKey, capturedIndex, eventName, e));
            dragSurface.AddEventTrigger(EventTriggerType.Drag, MoveDragGhost);
            dragSurface.AddEventTrigger(EventTriggerType.EndDrag, e => EndDrag(fsmKey, e));

            // Added after the drag surface, so it renders/hit-tests on top - same layering
            // FsmTabStripPanel's own close-button-over-select-button uses.
            CanvasButton deleteButton = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
            deleteButton.Text.Text = "x";
            deleteButton.Text.Color = _ui.ErrorColor;
            deleteButton.LocalPosition = new Vector2(x + dragWidth + SeqRowGap, y);
            deleteButton.Size = new Vector2(SeqDeleteButtonSize, RowHeight);
            deleteButton.Build();
            deleteButton.OnClicked += () => RemovePatternEntry(fsmKey, stateName, actionRank, capturedIndex);

            rowRanges.Add((y, RowHeight));
            y += RowHeight + SeqRowGap;
        }

        // A minimum of one blank row always sits below the real entries (or alone, if the pattern is
        // still empty) - a bigger, easier-to-hit drop target than relying on empty space past the last
        // row's own bottom edge, and (when the pattern is empty) the home for the "drag events here"
        // hint. Not itself draggable/deletable, and deliberately NOT added to rowRanges: EndDrag's own
        // "past the last row's midpoint -> append at the end" fallback already resolves a drop anywhere
        // on or below it to the end of the list, so adding it there would only make that same fallback
        // trigger one row sooner with no behavioral difference.
        CanvasImage blankRow = content.Add(new CanvasImage($"Row{cursor.Count++}", _ui) { Tint = Color.clear });
        blankRow.LocalPosition = new Vector2(x, y);
        blankRow.Size = new Vector2(width, RowHeight);
        blankRow.AddBorder(_ui.PanelBorder);
        blankRow.Build();

        if (pattern.Count == 0)
        {
            CanvasText hint = content.Add(new CanvasText($"Row{cursor.Count++}", _ui) { Text = "(drag events here)" });
            hint.Color = _ui.ReadOnlyColor;
            hint.Alignment = TextAnchor.MiddleCenter;
            hint.Overflow = HorizontalWrapMode.Overflow;
            hint.LocalPosition = new Vector2(x, y);
            hint.Size = new Vector2(width, RowHeight);
            hint.Build();
        }

        y += RowHeight;

        _sequenceRowRanges[blockKey] = rowRanges;

        return y;
    }

    // Right column: Loop mode/count on top, the candidate-event palette ("Events") beneath it - each
    // Events block is draggable into this same block's own Sequence column (left).
    private float BuildLoopAndEventsColumn(CanvasPanel content, RowCursor cursor, string fsmKey, string stateName, int actionRank, List<string> candidateEvents, bool indefinite, int fixedExtraLoops, float x, float y, float width)
    {
        CanvasText loopHeader = content.Add(new CanvasText($"Row{cursor.Count++}", _ui) { Text = "Loop" });
        loopHeader.Color = _ui.ReadOnlyColor;
        loopHeader.Overflow = HorizontalWrapMode.Overflow;
        loopHeader.LocalPosition = new Vector2(x, y);
        loopHeader.Size = new Vector2(width, RowHeight);
        loopHeader.Build();
        y += RowHeight;

        float toggleWidth = Mathf.Max(0f, (width - SeqRowGap) / 2f);

        CanvasButton fixedButton = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
        fixedButton.Text.Text = "Fixed";
        fixedButton.Toggled = !indefinite;
        fixedButton.LocalPosition = new Vector2(x, y);
        fixedButton.Size = new Vector2(toggleWidth, RowHeight);
        fixedButton.Build();
        fixedButton.OnClicked += () => SetLoopMode(fsmKey, stateName, actionRank, indefinite: false);

        CanvasButton indefiniteButton = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
        indefiniteButton.Text.Text = "Indefinite";
        indefiniteButton.Toggled = indefinite;
        indefiniteButton.LocalPosition = new Vector2(x + toggleWidth + SeqRowGap, y);
        indefiniteButton.Size = new Vector2(Mathf.Max(0f, width - toggleWidth - SeqRowGap), RowHeight);
        indefiniteButton.Build();
        indefiniteButton.OnClicked += () => SetLoopMode(fsmKey, stateName, actionRank, indefinite: true);

        y += RowHeight + SeqRowGap;

        if (!indefinite)
        {
            CanvasImage countBackground = content.Add(new CanvasImage($"Row{cursor.Count++}", _ui) { Tint = _ui.ButtonNormal });
            countBackground.LocalPosition = new Vector2(x, y);
            countBackground.Size = new Vector2(width, RowHeight);
            countBackground.AddBorder(_ui.PanelBorder);
            countBackground.Build();

            CanvasTextField countField = content.Add(new CanvasTextField($"Row{cursor.Count++}", _ui));
            countField.Text = fixedExtraLoops.ToString(CultureInfo.InvariantCulture);
            countField.LocalPosition = new Vector2(x + 3f, y);
            countField.Size = new Vector2(Mathf.Max(0f, width - 6f), RowHeight);
            countField.Build();
            countField.OnSubmit += text => SubmitLoopCount(fsmKey, stateName, actionRank, text);

            y += RowHeight;
        }

        y += SeqRowGap;

        CanvasText eventsHeader = content.Add(new CanvasText($"Row{cursor.Count++}", _ui) { Text = "Events" });
        eventsHeader.Color = _ui.ReadOnlyColor;
        eventsHeader.Overflow = HorizontalWrapMode.Overflow;
        eventsHeader.LocalPosition = new Vector2(x, y);
        eventsHeader.Size = new Vector2(width, RowHeight);
        eventsHeader.Build();
        y += RowHeight;

        (string, int) blockKey = (stateName, actionRank);
        foreach (string eventName in candidateEvents)
        {
            CanvasButton eventBlock = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
            eventBlock.Text.Text = eventName;
            eventBlock.Text.Alignment = TextAnchor.MiddleLeft;
            eventBlock.Text.Overflow = HorizontalWrapMode.Overflow;
            eventBlock.LocalPosition = new Vector2(x, y);
            eventBlock.Size = new Vector2(width, RowHeight);
            eventBlock.Build();
            eventBlock.AddEventTrigger(EventTriggerType.BeginDrag, e => BeginDragFromEvents(blockKey, eventName, e));
            eventBlock.AddEventTrigger(EventTriggerType.Drag, MoveDragGhost);
            eventBlock.AddEventTrigger(EventTriggerType.EndDrag, e => EndDrag(fsmKey, e));

            y += RowHeight + SeqRowGap;
        }

        return y;
    }

    private SequencerOverride? FindSequencerOverride(string fsmKey, string stateName, int actionRank) =>
        _editManager.GetActiveEditSet(fsmKey)?.SequencerOverrides.FirstOrDefault(s => s.StateName == stateName && s.ActionIndex == actionRank);

    private List<string> GetSequencerPattern(string fsmKey, string stateName, int actionRank)
    {
        SequencerOverride? existing = FindSequencerOverride(fsmKey, stateName, actionRank);
        return existing != null ? new List<string>(existing.Pattern) : new List<string>();
    }

    private bool GetSequencerIndefinite(string fsmKey, string stateName, int actionRank)
    {
        SequencerOverride? existing = FindSequencerOverride(fsmKey, stateName, actionRank);
        return existing == null || existing.RepeatCount == 0;
    }

    private int GetSequencerFixedExtraLoops(string fsmKey, string stateName, int actionRank)
    {
        SequencerOverride? existing = FindSequencerOverride(fsmKey, stateName, actionRank);
        return existing != null ? Mathf.Max(0, existing.RepeatCount - 1) : 0;
    }

    // Every Sequencer-tab mutation (add/remove/reorder a pattern entry, change loop mode/count) funnels
    // through here, matching this panel's existing SetVariable/SetActionField "apply immediately"
    // convention - no separate Save button. Ends with a direct RebuildContent call (bypassing Refresh's
    // cache guard, which only gates on (fsm, stateName, subtab) and would otherwise leave a structural
    // change - a new/removed/reordered row - unreflected until something else happens to change that
    // cache key).
    private void ApplySequencer(string fsmKey, string stateName, int actionRank, List<string> pattern, bool indefinite, int fixedExtraLoops)
    {
        if (pattern.Count == 0)
        {
            _editManager.RemoveSequencer(fsmKey, stateName, actionRank);
        }
        else
        {
            _editManager.InstallSequencer(fsmKey, new SequencerOverride
            {
                StateName = stateName,
                ActionIndex = actionRank,
                Pattern = new List<string>(pattern),
                RepeatCount = indefinite ? 0 : fixedExtraLoops + 1,
            });
        }

        RebuildContent(_cachedFsm, _cachedStateName);
    }

    private void RemovePatternEntry(string fsmKey, string stateName, int actionRank, int index)
    {
        List<string> pattern = GetSequencerPattern(fsmKey, stateName, actionRank);
        if (index < 0 || index >= pattern.Count)
        {
            return;
        }

        pattern.RemoveAt(index);
        ApplySequencer(fsmKey, stateName, actionRank, pattern, GetSequencerIndefinite(fsmKey, stateName, actionRank), GetSequencerFixedExtraLoops(fsmKey, stateName, actionRank));
    }

    private void SetLoopMode(string fsmKey, string stateName, int actionRank, bool indefinite)
    {
        List<string> pattern = GetSequencerPattern(fsmKey, stateName, actionRank);
        int fixedExtraLoops = GetSequencerFixedExtraLoops(fsmKey, stateName, actionRank);
        ApplySequencer(fsmKey, stateName, actionRank, pattern, indefinite, fixedExtraLoops);
    }

    private void SubmitLoopCount(string fsmKey, string stateName, int actionRank, string text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
        {
            n = 0;
        }

        List<string> pattern = GetSequencerPattern(fsmKey, stateName, actionRank);
        ApplySequencer(fsmKey, stateName, actionRank, pattern, indefinite: false, fixedExtraLoops: n);
    }

    // Removes the block from view AND uninstalls its live sequencer (restoring the original random
    // action) - the block's own explicit close button, distinct from emptying its pattern (which
    // uninstalls the live sequencer too, via ApplySequencer, but leaves the now-empty block open for
    // further editing).
    private void CloseSequencerBlock(string fsmKey, string stateName, int actionRank)
    {
        _editManager.RemoveSequencer(fsmKey, stateName, actionRank);

        if (_openSequencerBlocks.TryGetValue(fsmKey, out HashSet<(string, int)>? opened))
        {
            opened.Remove((stateName, actionRank));
        }

        _showStatus("Sequencer Removed");

        RebuildContent(_cachedFsm, _cachedStateName);
    }

    // ---- Sequencer drag/drop ----
    // Drop-to-target-index rather than continuous live-reorder-while-dragging: there's no existing
    // precedent in this codebase for reflowing sibling row positions mid-drag (the only other uGUI drag
    // consumer, CanvasResizeHandle, only ever reads raw per-frame delta), and calling into FsmEditManager
    // on every Drag frame would spam dozens of live-install calls per second for no benefit. One
    // ApplySequencer call per completed gesture is simpler and still gives correct, predictable
    // reordering.

    private void BeginDragFromEvents((string StateName, int ActionRank) blockKey, string eventName, PointerEventData e)
    {
        _dragBlockKey = blockKey;
        _dragEventName = eventName;
        _dragSourceIndex = null;
        ShowDragGhost(eventName, e);
    }

    private void BeginDragReorder((string StateName, int ActionRank) blockKey, int sourceIndex, string eventName, PointerEventData e)
    {
        _dragBlockKey = blockKey;
        _dragEventName = eventName;
        _dragSourceIndex = sourceIndex;
        ShowDragGhost(eventName, e);
    }

    private void ShowDragGhost(string eventName, PointerEventData e)
    {
        _dragGhostText.Text = eventName;
        _dragGhost.ActiveSelf = true;
        MoveDragGhost(e);
    }

    private void MoveDragGhost(PointerEventData e)
    {
        Vector2 mouseTopLeft = new(e.position.x, Screen.height - e.position.y);
        _dragGhost.LocalPosition = mouseTopLeft - Position + new Vector2(10f, 10f);
    }

    // Cross-block drops are rejected outright - a drop is only accepted if it lands inside the SAME
    // block's own Sequence-column rect the drag originated from (_dragBlockKey), since a block's Events
    // column only ever offers candidates for, and only ever makes sense to feed into, that same block's
    // own Sequence.
    private void EndDrag(string fsmKey, PointerEventData e)
    {
        _dragGhost.ActiveSelf = false;

        (string StateName, int ActionRank)? blockKey = _dragBlockKey;
        string? draggedEventName = _dragEventName;
        int? sourceIndex = _dragSourceIndex;
        _dragBlockKey = null;
        _dragEventName = null;
        _dragSourceIndex = null;

        if (blockKey == null || draggedEventName == null)
        {
            return;
        }

        if (!_sequenceColumnRects.TryGetValue(blockKey.Value, out Rect columnRect))
        {
            return;
        }

        Vector2 contentPoint = ScreenToScrollContentPoint(e.position);
        if (!columnRect.Contains(contentPoint))
        {
            return; // dropped outside this block's own Sequence column - cancel with no mutation
        }

        List<string> pattern = GetSequencerPattern(fsmKey, blockKey.Value.StateName, blockKey.Value.ActionRank);
        int dropIndex = ComputeDropIndex(blockKey.Value, contentPoint.y);

        if (sourceIndex.HasValue)
        {
            int from = sourceIndex.Value;
            if (from < 0 || from >= pattern.Count)
            {
                return;
            }

            pattern.RemoveAt(from);
            if (dropIndex > from)
            {
                dropIndex--;
            }
        }

        dropIndex = Mathf.Clamp(dropIndex, 0, pattern.Count);
        pattern.Insert(dropIndex, draggedEventName);

        ApplySequencer(fsmKey, blockKey.Value.StateName, blockKey.Value.ActionRank, pattern,
            GetSequencerIndefinite(fsmKey, blockKey.Value.StateName, blockKey.Value.ActionRank),
            GetSequencerFixedExtraLoops(fsmKey, blockKey.Value.StateName, blockKey.Value.ActionRank));
    }

    // Finds which row (if any) contentY landed above the midpoint of, within the given block's own
    // recorded Sequence rows - returns ranges.Count (append) if it's past the last row's midpoint.
    private int ComputeDropIndex((string StateName, int ActionRank) blockKey, float contentY)
    {
        if (!_sequenceRowRanges.TryGetValue(blockKey, out List<(float Y, float Height)>? ranges))
        {
            return 0;
        }

        for (int i = 0; i < ranges.Count; i++)
        {
            (float y, float height) = ranges[i];
            if (contentY < y + height / 2f)
            {
                return i;
            }
        }

        return ranges.Count;
    }

    // Converts a raw screen point (bottom-left/y-up, e.g. PointerEventData.position) into the shared
    // _scrollView's content-space (top-left/y-down, unscrolled) - same conversion CanvasNode.IsMouseOver
    // uses for Y, plus subtracting the scroll view's own content offset (Content.LocalPosition.y, which
    // is <= 0 while scrolled down per CanvasScrollView.Poll's own clamp) to land in the same
    // coordinate system every row's own LocalPosition/cursor.Y already uses.
    private Vector2 ScreenToScrollContentPoint(Vector2 screenPos)
    {
        float viewportLocalY = (Screen.height - screenPos.y) - _scrollView.Position.y;
        float contentY = viewportLocalY - (_scrollView.Content?.LocalPosition.y ?? 0f);
        float contentX = screenPos.x - _scrollView.Position.x;
        return new Vector2(contentX, contentY);
    }

    // ---------------- Shared editable-value helpers ----------------

    private static bool IsBoolLike(object? value) =>
        value is bool || (value is NamedVariable { VariableType: VariableType.Bool });

    private static bool ToBool(object? value) => value switch
    {
        bool b => b,
        FsmBool fsmBool => fsmBool.Value,
        _ => false,
    };

    // Numeric vs. string vs. plain-text color-coding, concept from FSMExpress's type/value converter -
    // vector/rect/quaternion/color-typed variables fall through to the numeric color, since they're
    // ultimately just a comma-joined list of floats (see FsmEditManager.FormatFloats).
    private Color ValueColorFor(object? value)
    {
        object? unwrapped = value is NamedVariable nv ? nv.RawValue : value;
        return unwrapped is string ? _ui.StringValueColor : _ui.NumericValueColor;
    }

    // Delegates scalar formatting to FsmConsoleLogger's own formatter so both the console dump and
    // this panel describe FsmOwnerDefault/FsmEventTarget/NamedVariable fields identically.
    private static string FormatActionField(FsmStateAction action, object? fieldValue)
    {
        switch (fieldValue)
        {
            case FsmArray fsmArray:
                var arrayParts = new string[fsmArray.Length];
                for (int i = 0; i < fsmArray.Length; i++)
                {
                    arrayParts[i] = FsmConsoleLogger.FormatActionFieldValue(action, fsmArray.Values[i]);
                }
                return string.Join(", ", arrayParts);
            case Array array:
                var parts = new string[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    parts[i] = FsmConsoleLogger.FormatActionFieldValue(action, array.GetValue(i));
                }
                return string.Join(", ", parts);
            default:
                return FsmConsoleLogger.FormatActionFieldValue(action, fieldValue);
        }
    }

    // ---------------- Row-building primitives ----------------

    private (float LabelWidth, float ValueX, float ValueWidth) ComputeColumns(float indent)
    {
        float available = Mathf.Max(0f, _scrollView.Size.x - indent);
        float labelWidth = available * 0.52f;
        float valueX = indent + labelWidth + UICommon.ScaleWidth(6);
        float valueWidth = Mathf.Max(0f, _scrollView.Size.x - valueX);
        return (labelWidth, valueX, valueWidth);
    }

    // Draws a small watch-toggle dot at the row's current indent and returns a bumped indent for the
    // caller to pass into the row helper it's about to invoke (AddBoolRow/AddTextFieldRow/AddReadOnlyRow),
    // rather than growing those helpers' own signatures - they're also called from places that never
    // need a dot (Events tab, section headers), so a shared signature change would ripple there for no
    // reason. isOn/onClicked are re-read from the tracker rather than captured once, so the dot always
    // reflects the tracker's current state even if the same variable/field gets tracked from elsewhere.
    private float AddToggleDot(CanvasPanel content, RowCursor cursor, float indent, Func<bool> isOn, Action onClicked)
    {
        CanvasToggleDot dot = content.Add(new CanvasToggleDot($"Row{cursor.Count++}", _ui));
        dot.LocalPosition = new Vector2(indent, cursor.Y + (RowHeight - DotWidth) / 2f);
        dot.Size = new Vector2(DotWidth, DotWidth);
        dot.On = isOn();
        dot.Build();
        dot.OnClicked += onClicked;

        _valueRefreshers.Add(() => dot.On = isOn());

        return indent + DotWidth + DotGap;
    }

    private void AddSectionHeaderRow(CanvasPanel content, RowCursor cursor, string title, float indent)
    {
        CanvasText header = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        header.Text = title;
        header.Font = _ui.HeaderFont;
        header.FontStyle = FontStyle.Bold;
        header.Color = _ui.TypeBadgeColor;
        header.Overflow = HorizontalWrapMode.Overflow;
        header.LocalPosition = new Vector2(indent, cursor.Y);
        header.Size = new Vector2(Mathf.Max(0f, _scrollView.Size.x - indent), HeaderRowHeight);
        header.Build();

        cursor.Y += HeaderRowHeight;
    }

    // Same as AddSectionHeaderRow, except a Random-event-family action (SendRandomEvent and its
    // v2/v3/v3ActiveBool/v4 variants - reusing FsmActionSequencer's own generalized "Random" substring
    // check rather than hardcoding a list of type names) gets a narrower header plus a "Sequencer"
    // button that opens/reveals that action's block in the Sequencer tab. The button's Toggled state
    // (colored when on) reflects whether a block is currently open for this action - recomputed once
    // here and again every frame via _valueRefreshers, so it stays in sync with edits made in the
    // Sequencer tab (including closing the block there) without this row ever needing a rebuild
    // callback. randomActionRank is only meaningful (and only read) when the action is itself a
    // Random-event-family one - see BuildActionsRows.
    private void AddActionHeaderRow(CanvasPanel content, RowCursor cursor, string fsmKey, FsmStateInfo state, int actionIndex, int randomActionRank, FsmActionInfo action)
    {
        if (!action.ActionType.Name.Contains("Random"))
        {
            AddSectionHeaderRow(content, cursor, action.ActionType.Name, RowIndent);
            return;
        }

        float gap = UICommon.ScaleWidth(4f);
        float available = Mathf.Max(0f, _scrollView.Size.x - RowIndent);
        float headerWidth = Mathf.Max(0f, available - SequencerButtonWidth - gap);

        CanvasText header = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        header.Text = action.ActionType.Name;
        header.Font = _ui.HeaderFont;
        header.FontStyle = FontStyle.Bold;
        header.Color = _ui.TypeBadgeColor;
        header.Overflow = HorizontalWrapMode.Overflow;
        header.LocalPosition = new Vector2(RowIndent, cursor.Y);
        header.Size = new Vector2(headerWidth, HeaderRowHeight);
        header.Build();

        CanvasButton sequencerButton = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
        sequencerButton.Text.Text = "Sequencer";
        sequencerButton.LocalPosition = new Vector2(RowIndent + headerWidth + gap, cursor.Y);
        sequencerButton.Size = new Vector2(SequencerButtonWidth, HeaderRowHeight);
        sequencerButton.Toggled = IsSequencerOpen(fsmKey, state.Name, randomActionRank);
        sequencerButton.Build();

        string stateName = state.Name;
        sequencerButton.OnClicked += () => OpenSequencerBlock(fsmKey, stateName, randomActionRank);

        _valueRefreshers.Add(() => sequencerButton.Toggled = IsSequencerOpen(fsmKey, stateName, randomActionRank));

        cursor.Y += HeaderRowHeight;
    }

    // Adds (stateName, actionRank) to this fsmKey's open-block set (a no-op if already open), switches
    // to the Sequencer subtab, and scrolls to/selects the block - whether newly created or already
    // existing, so clicking Sequencer again on an action that already has a block just reveals it rather
    // than creating a duplicate.
    private void OpenSequencerBlock(string fsmKey, string stateName, int actionRank)
    {
        if (!_openSequencerBlocks.TryGetValue(fsmKey, out HashSet<(string, int)>? opened))
        {
            opened = new HashSet<(string, int)>();
            _openSequencerBlocks[fsmKey] = opened;
        }

        // HashSet.Add's own return value, not a separate Contains check - only a genuinely new block
        // (not just re-revealing one that was already open) counts as "inserting a sequencer."
        if (opened.Add((stateName, actionRank)))
        {
            _showStatus("Sequencer Added");
        }

        SetActiveSubTab(SubTabKind.Sequencer);
        RebuildContent(_cachedFsm, _cachedStateName);
        _cachedSubTab = _activeSubTab;

        if (_sequencerBlockWidgets.TryGetValue((stateName, actionRank), out CanvasSectionBlock? block))
        {
            _scrollView.ScrollToShow(block.LocalPosition.y, block.Size.y);
            SelectBlock(block);
        }
    }

    private bool IsSequencerOpen(string fsmKey, string stateName, int actionRank)
    {
        if (_openSequencerBlocks.TryGetValue(fsmKey, out HashSet<(string, int)>? opened) && opened.Contains((stateName, actionRank)))
        {
            return true;
        }

        return _editManager.GetActiveEditSet(fsmKey)?.SequencerOverrides.Any(s => s.StateName == stateName && s.ActionIndex == actionRank) == true;
    }

    // Opens a new outline block, spanning the section's full width - a leading gap is inserted first
    // unless this is the very first block in the subtab (cursor.Y == 0), matching the old divider's own
    // "only between entries" placement. The block's own Size is a placeholder until EndBlock below
    // measures how much content actually got added inside it.
    private CanvasSectionBlock BeginBlock(CanvasPanel content, RowCursor cursor)
    {
        if (cursor.Y > 0f)
        {
            cursor.Y += BlockGap;
        }

        CanvasSectionBlock block = content.Add(new CanvasSectionBlock($"Block{cursor.Count++}", _ui));
        block.LocalPosition = new Vector2(0f, cursor.Y);
        block.Size = new Vector2(_scrollView.Size.x, 0f);
        block.Build();
        block.OnClicked += () => SelectBlock(block);

        cursor.Y += BlockPadding;
        return block;
    }

    // Closes a block opened by BeginBlock - sizes it to exactly wrap everything the caller added since
    // (block.LocalPosition.y is where cursor.Y stood right when the block was opened, before its own
    // top padding), then adds the bottom padding.
    private void EndBlock(CanvasSectionBlock block, RowCursor cursor)
    {
        cursor.Y += BlockPadding;
        block.Size = new Vector2(_scrollView.Size.x, cursor.Y - block.LocalPosition.y);
    }

    // Single-select across whichever blocks the currently-built subtab has - selecting a new block
    // deselects whatever was selected before it. Also invoked (not just from a block's own OnClicked)
    // by ScrollToAction, so clicking a transition in the graph overlay selects the action block it
    // scrolled to, exactly as if the user had clicked that block directly.
    private void SelectBlock(CanvasSectionBlock block)
    {
        if (_selectedBlock == block)
        {
            return;
        }

        if (_selectedBlock != null)
        {
            _selectedBlock.Selected = false;
        }

        block.Selected = true;
        _selectedBlock = block;
    }

    private void AddLabelOnlyRow(CanvasPanel content, RowCursor cursor, string label, Color color, float indent)
    {
        CanvasText row = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        row.Text = label;
        row.Color = color;
        row.Overflow = HorizontalWrapMode.Overflow;
        row.LocalPosition = new Vector2(indent, cursor.Y);
        row.Size = new Vector2(Mathf.Max(0f, _scrollView.Size.x - indent), RowHeight);
        row.Build();

        cursor.Y += RowHeight;
    }

    // readValue is re-invoked every frame (see _valueRefreshers) rather than the row capturing a
    // one-time string, so an external mutation (Load, Reset, the FSM's own running code) is reflected
    // without the user needing to switch tabs/state to force a rebuild.
    private void AddReadOnlyRow(CanvasPanel content, RowCursor cursor, string label, Func<string> readValue, Color valueColor, float indent, Color? labelColor = null)
    {
        (float labelWidth, float valueX, float valueWidth) = ComputeColumns(indent);

        CanvasText labelText = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        labelText.Text = label;
        labelText.Color = labelColor ?? _ui.TextColor;
        labelText.Overflow = HorizontalWrapMode.Overflow;
        labelText.LocalPosition = new Vector2(indent, cursor.Y);
        labelText.Size = new Vector2(labelWidth, RowHeight);
        labelText.Build();

        CanvasText valueText = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        valueText.Text = readValue();
        valueText.Color = valueColor;
        valueText.Overflow = HorizontalWrapMode.Overflow;
        valueText.LocalPosition = new Vector2(valueX, cursor.Y);
        valueText.Size = new Vector2(valueWidth, RowHeight);
        valueText.Build();

        _valueRefreshers.Add(() => valueText.Text = readValue());

        cursor.Y += RowHeight;
    }

    // readCurrent is re-invoked every frame (see _valueRefreshers), same reasoning as AddReadOnlyRow.
    private void AddBoolRow(CanvasPanel content, RowCursor cursor, string label, Func<bool> readCurrent, float indent, Action<bool> onToggle, Color? labelColor = null)
    {
        (float labelWidth, float valueX, float valueWidth) = ComputeColumns(indent);

        CanvasText labelText = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        labelText.Text = label;
        labelText.Color = labelColor ?? _ui.TextColor;
        labelText.Overflow = HorizontalWrapMode.Overflow;
        labelText.LocalPosition = new Vector2(indent, cursor.Y);
        labelText.Size = new Vector2(labelWidth, RowHeight);
        labelText.Build();

        CanvasButton toggle = content.Add(new CanvasButton($"Row{cursor.Count++}", _ui));
        toggle.Toggled = readCurrent();
        toggle.Text.Text = toggle.Toggled.ToString();
        toggle.LocalPosition = new Vector2(valueX, cursor.Y);
        toggle.Size = new Vector2(valueWidth, RowHeight);
        toggle.Build();
        toggle.OnClicked += () =>
        {
            bool next = !toggle.Toggled;
            toggle.Toggled = next;
            toggle.Text.Text = next.ToString();
            onToggle(next);
        };

        _valueRefreshers.Add(() =>
        {
            bool current = readCurrent();
            toggle.Toggled = current;
            toggle.Text.Text = current.ToString();
        });

        cursor.Y += RowHeight;
    }

    // onSubmit returns the text to redisplay immediately afterward (the freshly re-read current value,
    // whether the edit succeeded or was rejected) - always re-reading rather than trusting the typed
    // input lets a silent rejection inside FsmEditManager (e.g. a type mismatch, which logs a warning
    // and returns without throwing) redisplay correctly too, not just a thrown parse exception.
    // readCurrent is the same re-read, invoked again every frame thereafter (see _valueRefreshers) via
    // UpdateDefaultText, which itself no-ops while the field is focused - so continuous refresh never
    // fights with the user actively typing into this exact field.
    private void AddTextFieldRow(CanvasPanel content, RowCursor cursor, string label, Func<string> readCurrent, Color valueColor, float indent, Func<string, string> onSubmit, Color? labelColor = null)
    {
        (float labelWidth, float valueX, float valueWidth) = ComputeColumns(indent);

        CanvasText labelText = content.Add(new CanvasText($"Row{cursor.Count++}", _ui));
        labelText.Text = label;
        labelText.Color = labelColor ?? _ui.TextColor;
        labelText.Overflow = HorizontalWrapMode.Overflow;
        labelText.LocalPosition = new Vector2(indent, cursor.Y);
        labelText.Size = new Vector2(labelWidth, RowHeight);
        labelText.Build();

        // A visible input box - without it, an editable text field renders as plain colored text,
        // indistinguishable from a read-only row (CanvasButton's own background already gives bool
        // toggle rows this same affordance for free). Added as a sibling BEFORE valueField (not a
        // child of it - CanvasImage isn't a CanvasPanel and can't parent arbitrary children), so it
        // renders behind the text field it's paired with.
        CanvasImage background = content.Add(new CanvasImage($"Row{cursor.Count++}", _ui) { Tint = _ui.ButtonNormal });
        background.LocalPosition = new Vector2(valueX, cursor.Y);
        background.Size = new Vector2(valueWidth, RowHeight);
        background.AddBorder(_ui.PanelBorder);
        background.Build();

        CanvasTextField valueField = content.Add(new CanvasTextField($"Row{cursor.Count++}", _ui));
        valueField.Text = readCurrent();
        valueField.Color = valueColor;
        valueField.Overflow = HorizontalWrapMode.Overflow;
        valueField.LocalPosition = new Vector2(valueX + 3f, cursor.Y);
        valueField.Size = new Vector2(Mathf.Max(0f, valueWidth - 6f), RowHeight);
        valueField.Build();
        valueField.OnSubmit += text =>
        {
            // Plain Text setter, not UpdateDefaultText - the field is still focused at this point
            // (InputField.onSubmit fires before onEndEdit), and UpdateDefaultText no-ops while focused.
            valueField.Text = onSubmit(text);
        };

        _valueRefreshers.Add(() => valueField.UpdateDefaultText(readCurrent()));

        cursor.Y += RowHeight;
    }
}
