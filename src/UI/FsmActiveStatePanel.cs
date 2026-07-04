using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;
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
// colors themselves live in UICommon, FsmMaster's own palette.
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
    private const float DividerHeightDesign = 8f;
    private const float ScrollbarWidthDesign = 10f;
    private const float RowIndentDesign = 4f;
    private const float FieldIndentDesign = 14f;

    private static float HeaderHeight => UICommon.ScaleHeight(HeaderHeightDesign);
    private static float SubTabButtonHeight => UICommon.ScaleHeight(SubTabButtonHeightDesign);
    private static float RowHeight => UICommon.ScaleHeight(RowHeightDesign);
    private static float HeaderRowHeight => UICommon.ScaleHeight(HeaderRowHeightDesign);
    private static float DividerHeight => UICommon.ScaleHeight(DividerHeightDesign);
    private static float ScrollbarWidth => UICommon.ScaleWidth(ScrollbarWidthDesign);
    private static float RowIndent => UICommon.ScaleWidth(RowIndentDesign);
    private static float FieldIndent => UICommon.ScaleWidth(FieldIndentDesign);
    private static float ArrayElementIndent => FieldIndent + UICommon.ScaleWidth(12f);

    private enum SubTabKind
    {
        Actions,
        Events,
        Variables,
    }

    private readonly UICommon _ui;
    private readonly FsmEditManager _editManager;
    private readonly FsmVariableTracker _tracker;
    private readonly ManualLogSource _logger;
    private readonly CanvasText _header;
    private readonly CanvasToggleDot _stateHeaderDot;
    private readonly CanvasButton _actionsTab;
    private readonly CanvasButton _eventsTab;
    private readonly CanvasButton _variablesTab;
    private readonly CanvasScrollView _scrollView;
    private readonly CanvasScrollbar _scrollbar;

    private SubTabKind _activeSubTab = SubTabKind.Actions;
    private FsmInfo? _cachedFsm;
    private string? _cachedStateName;
    private SubTabKind? _cachedSubTab;

    private static float DotWidth => UICommon.ScaleWidth(16f);
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

    // Content-space Y range of each action's whole row block (header + fields), keyed by action index -
    // rebuilt every RebuildContent call alongside the rows themselves, so ScrollToAction always has an
    // up-to-date offset to scroll to even after a state/tab switch changes every row's position.
    private readonly Dictionary<int, (float Y, float Height)> _actionRowRanges = new();

    public FsmActiveStatePanel(UICommon ui, FsmEditManager editManager, FsmVariableTracker tracker, ManualLogSource logger) : base("ActiveStatePanel")
    {
        _ui = ui;
        _editManager = editManager;
        _tracker = tracker;
        _logger = logger;

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

        _scrollView = Add(new CanvasScrollView("ScrollView"));
        _scrollView.SetContent(new CanvasPanel("Content"));

        _scrollbar = Add(new CanvasScrollbar("Scrollbar", ui) { ScrollView = _scrollView });

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
        float tabWidth = Size.x / 3f;
        _actionsTab.LocalPosition = new Vector2(0f, tabY);
        _actionsTab.Size = new Vector2(tabWidth, SubTabButtonHeight);
        _eventsTab.LocalPosition = new Vector2(tabWidth, tabY);
        _eventsTab.Size = new Vector2(tabWidth, SubTabButtonHeight);
        _variablesTab.LocalPosition = new Vector2(tabWidth * 2f, tabY);
        _variablesTab.Size = new Vector2(Size.x - tabWidth * 2f, SubTabButtonHeight);

        float scrollY = tabY + SubTabButtonHeight + 4f;
        float scrollHeight = Mathf.Max(0f, Size.y - scrollY);
        _scrollView.LocalPosition = new Vector2(0f, scrollY);
        _scrollView.Size = new Vector2(Mathf.Max(0f, Size.x - ScrollbarWidth - 2f), scrollHeight);

        _scrollbar.LocalPosition = new Vector2(Size.x - ScrollbarWidth, scrollY);
        _scrollbar.Size = new Vector2(ScrollbarWidth, scrollHeight);
    }

    private void SetActiveSubTab(SubTabKind kind)
    {
        if (_activeSubTab == kind)
        {
            return;
        }

        _activeSubTab = kind;
        UpdateSubTabToggles();
    }

    private void UpdateSubTabToggles()
    {
        _actionsTab.Toggled = _activeSubTab == SubTabKind.Actions;
        _eventsTab.Toggled = _activeSubTab == SubTabKind.Events;
        _variablesTab.Toggled = _activeSubTab == SubTabKind.Variables;
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

        if (_actionRowRanges.TryGetValue(actionIndex, out (float Y, float Height) range))
        {
            _scrollView.ScrollToShow(range.Y, range.Height);
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
        _actionRowRanges.Clear();

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

        if (fsm == null || state == null)
        {
            content.Size = new Vector2(_scrollView.Size.x, 0f);
            return;
        }

        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
        var cursor = new RowCursor();

        switch (_activeSubTab)
        {
            case SubTabKind.Actions:
                BuildActionsRows(content, cursor, fsmKey, state);
                break;
            case SubTabKind.Events:
                BuildEventsRows(content, cursor, fsm);
                break;
            case SubTabKind.Variables:
                BuildVariableRows(content, cursor, fsmKey, fsm);
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
        for (int actionIndex = 0; actionIndex < state.Actions.Count; actionIndex++)
        {
            FsmActionInfo action = state.Actions[actionIndex];

            if (actionIndex > 0)
            {
                AddDividerRow(content, cursor);
            }

            float blockStartY = cursor.Y;

            AddSectionHeaderRow(content, cursor, action.ActionType.Name, RowIndent);

            foreach (FsmActionFieldInfo field in action.Fields)
            {
                AddActionFieldRow(content, cursor, fsmKey, state.Name, actionIndex, action, field);
            }

            _actionRowRanges[actionIndex] = (blockStartY, cursor.Y - blockStartY);
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

        AddSectionHeaderRow(content, cursor, "Events", RowIndent);

        foreach (FsmEvent fsmEvent in fsm.Fsm.Events)
        {
            AddLabelOnlyRow(content, cursor, fsmEvent.Name, _ui.TextColor, FieldIndent);
        }
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

        AddSectionHeaderRow(content, cursor, "Array", RowIndent);

        foreach (FsmArray array in items)
        {
            AddArrayVariableRow(content, cursor, fsmKey, array);
        }
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

        AddSectionHeaderRow(content, cursor, typeName, RowIndent);

        foreach (T variable in items)
        {
            AddNamedVariableRow(content, cursor, fsmKey, variable);
        }
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

    private void AddDividerRow(CanvasPanel content, RowCursor cursor)
    {
        cursor.Y += DividerHeight / 2f;

        CanvasImage divider = content.Add(new CanvasImage($"Row{cursor.Count++}", _ui) { Tint = _ui.DividerColor });
        divider.LocalPosition = new Vector2(0f, cursor.Y);
        divider.Size = new Vector2(_scrollView.Size.x, 1f);
        divider.Build();

        cursor.Y += DividerHeight / 2f + 1f;
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
