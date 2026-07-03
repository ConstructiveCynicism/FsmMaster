using System;
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
// agent-context/Silksong.FsmUtil-main/src/FsmUtil.cs), and fsm.Fsm.Events for Events. FsmGraphOverlay
// keeps its own copy of this logic until the old IMGUI side panel is deleted once this panel fully
// replaces it.
internal sealed class FsmActiveStatePanel : CanvasPanel
{
    private const float HeaderHeight = 18f;
    private const float SubTabButtonHeight = 24f;
    private const float RowHeight = 18f;
    private const float ScrollbarWidth = 10f;

    private enum SubTabKind
    {
        Actions,
        Events,
        Variables,
    }

    private readonly UICommon _ui;
    private readonly CanvasText _header;
    private readonly CanvasButton _actionsTab;
    private readonly CanvasButton _eventsTab;
    private readonly CanvasButton _variablesTab;
    private readonly CanvasScrollView _scrollView;
    private readonly CanvasScrollbar _scrollbar;

    private SubTabKind _activeSubTab = SubTabKind.Actions;
    private FsmInfo? _cachedFsm;
    private string? _cachedStateName;
    private SubTabKind? _cachedSubTab;

    public FsmActiveStatePanel(UICommon ui) : base("ActiveStatePanel")
    {
        _ui = ui;

        _header = Add(new CanvasText("Header", ui) { Text = "No state selected" });

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
        _header.LocalPosition = Vector2.zero;
        _header.Size = new Vector2(Size.x, HeaderHeight);

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
    // currently active - a cheap no-op unless the (fsm, state, sub-tab) triple actually changed.
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

    private void RebuildContent(FsmInfo? fsm, string? stateName)
    {
        var content = (CanvasPanel)_scrollView.Content!;
        content.ClearChildren();

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

        if (fsm == null || state == null)
        {
            content.Size = new Vector2(_scrollView.Size.x, 0f);
            return;
        }

        int rowIndex = 0;
        switch (_activeSubTab)
        {
            case SubTabKind.Actions:
                BuildActionsRows(content, state, ref rowIndex);
                break;
            case SubTabKind.Events:
                BuildEventsRows(content, fsm, ref rowIndex);
                break;
            case SubTabKind.Variables:
                BuildVariableRows(content, fsm, ref rowIndex);
                break;
        }

        content.Size = new Vector2(_scrollView.Size.x, rowIndex * RowHeight);
    }

    private void BuildActionsRows(CanvasPanel content, FsmStateInfo state, ref int rowIndex)
    {
        foreach (FsmActionInfo action in state.Actions)
        {
            AddRow(content, ref rowIndex, action.ActionType.Name, "", _ui.AccentColor);
            foreach (FsmActionFieldInfo field in action.Fields)
            {
                string value = FormatActionField(action.Action, field.FieldValue);
                AddRow(content, ref rowIndex, field.FieldName, value, _ui.TextColor);
            }
        }
    }

    private void BuildEventsRows(CanvasPanel content, FsmInfo fsm, ref int rowIndex)
    {
        foreach (FsmEvent fsmEvent in fsm.Fsm.Events)
        {
            AddRow(content, ref rowIndex, fsmEvent.Name, "", _ui.TextColor);
        }
    }

    // Same typed-array set FsmConsoleLogger.LogFsmVariables and the old side panel already enumerate -
    // FsmUtil has no bulk-enumeration helper for FsmVariables, so this reads the raw arrays directly.
    private void BuildVariableRows(CanvasPanel content, FsmInfo fsm, ref int rowIndex)
    {
        FsmVariables variables = fsm.Fsm.Variables;

        foreach (FsmFloat v in variables.FloatVariables) AddRow(content, ref rowIndex, $"Float \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmInt v in variables.IntVariables) AddRow(content, ref rowIndex, $"Int \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmBool v in variables.BoolVariables) AddRow(content, ref rowIndex, $"Bool \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmString v in variables.StringVariables) AddRow(content, ref rowIndex, $"String \"{v.Name}\"", v.Value ?? "null", _ui.TextColor);
        foreach (FsmVector2 v in variables.Vector2Variables) AddRow(content, ref rowIndex, $"Vector2 \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmVector3 v in variables.Vector3Variables) AddRow(content, ref rowIndex, $"Vector3 \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmRect v in variables.RectVariables) AddRow(content, ref rowIndex, $"Rect \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmQuaternion v in variables.QuaternionVariables) AddRow(content, ref rowIndex, $"Quaternion \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmColor v in variables.ColorVariables) AddRow(content, ref rowIndex, $"Color \"{v.Name}\"", v.Value.ToString(), _ui.TextColor);
        foreach (FsmGameObject v in variables.GameObjectVariables) AddRow(content, ref rowIndex, $"GameObject \"{v.Name}\"", v.Value != null ? v.Value.name : "null", _ui.TextColor);
        foreach (FsmObject v in variables.ObjectVariables) AddRow(content, ref rowIndex, $"Object \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null", _ui.TextColor);
        foreach (FsmMaterial v in variables.MaterialVariables) AddRow(content, ref rowIndex, $"Material \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null", _ui.TextColor);
        foreach (FsmTexture v in variables.TextureVariables) AddRow(content, ref rowIndex, $"Texture \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null", _ui.TextColor);
        foreach (FsmEnum v in variables.EnumVariables) AddRow(content, ref rowIndex, $"Enum \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null", _ui.TextColor);
        foreach (FsmArray v in variables.ArrayVariables) AddRow(content, ref rowIndex, $"Array \"{v.Name}\"", string.Join(", ", v.Values), _ui.TextColor);
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

    private void AddRow(CanvasPanel content, ref int rowIndex, string label, string value, Color color)
    {
        float y = rowIndex * RowHeight;
        CanvasText row = content.Add(new CanvasText($"Row{rowIndex}", _ui));
        row.Text = string.IsNullOrEmpty(value) ? label : $"{label}: {value}";
        row.Color = color;
        row.Overflow = HorizontalWrapMode.Overflow;
        row.LocalPosition = new Vector2(4f, y);
        row.Size = new Vector2(Mathf.Max(0f, _scrollView.Size.x - 8f), RowHeight);
        row.Build();

        rowIndex++;
    }
}
