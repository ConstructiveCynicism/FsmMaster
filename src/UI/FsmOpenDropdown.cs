using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Scene -> Object -> FSM drill-down popup, anchored below the Open button - rebuilt fresh every time
// it's shown rather than kept in sync incrementally, since it's only open briefly. FsmMaster's own
// class (not a generic reusable dialog base) since this mod only has one such popup; concept modeled
// on Silksong.DebugMod's CanvasDialog show/hide/outside-click pattern
// (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasDialog.cs is the closest existing pattern to
// this), reusing FsmDrilldownHierarchy for the actual scene/object/fsm grouping. Every row for the
// current level is still built up front rather than windowed/virtualized (unlike the old IMGUI list
// panel) - scene/object/fsm counts are small enough in practice that building them all is cheap - but
// the row list itself scrolls within a capped-height viewport (see RebuildRows) rather than growing
// this popup past whatever room is left inside the parent panel.
internal sealed class FsmOpenDropdown : CanvasPanel
{
    private const float Width = 260f;
    private const float RowHeight = 22f;
    private const float HeaderHeight = 22f;
    private const float BackButtonWidth = 70f;
    private const float ScrollbarWidth = 10f;

    private enum Level
    {
        Scenes,
        Objects,
        Fsms,
    }

    private readonly UICommon _ui;
    private readonly FsmTabManager _tabManager;
    private readonly Func<FsmSnapshot?> _getSnapshot;
    private readonly CanvasButton _anchorButton;
    private readonly Action<string> _showStatus;

    private readonly CanvasButton _backButton;
    private readonly CanvasText _headerText;
    private readonly CanvasScrollView _rowsScrollView;
    private readonly CanvasScrollbar _rowsScrollbar;
    private readonly CanvasPanel _rowsContainer;
    private readonly List<CanvasButton> _rowButtons = new();

    private Level _level = Level.Scenes;
    private List<SceneGroup>? _sceneGroups;
    private int _selectedSceneIndex = -1;
    private int _selectedObjectIndex = -1;

    public FsmOpenDropdown(UICommon ui, FsmTabManager tabManager, Func<FsmSnapshot?> getSnapshot, CanvasButton anchorButton, Action<string> showStatus)
        : base("OpenDropdown")
    {
        _ui = ui;
        _tabManager = tabManager;
        _getSnapshot = getSnapshot;
        _anchorButton = anchorButton;
        _showStatus = showStatus;
        ActiveSelf = false;
        Size = new Vector2(Width, HeaderHeight + RowHeight);

        CanvasImage background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        background.AddBorder(ui.PanelBorder);

        _backButton = Add(new CanvasButton("Back", ui));
        _backButton.LocalPosition = Vector2.zero;
        _backButton.Size = new Vector2(BackButtonWidth, HeaderHeight);
        _backButton.Text.Text = "< Back";
        _backButton.OnClicked += GoBack;

        _headerText = Add(new CanvasText("Header", ui));
        _headerText.LocalPosition = new Vector2(BackButtonWidth + 4f, 0f);
        _headerText.Size = new Vector2(Width - BackButtonWidth - 8f, HeaderHeight);
        _headerText.Alignment = TextAnchor.MiddleLeft;
        _headerText.Overflow = HorizontalWrapMode.Overflow;

        // Rows sit in a scrollable viewport rather than growing this panel's own Size without bound -
        // the popup is anchored inside FsmRightPanel, which the user can resize down to a small
        // MinPanelHeight, and a scene/object/FSM list can easily be taller than whatever room is left
        // below the anchor button (see the height clamp in RebuildRows). Width always reserves
        // ScrollbarWidth regardless of whether the scrollbar ends up visible, matching
        // FsmMonitorPanel/FsmActiveStatePanel's own scroll view + scrollbar pairing, rather than
        // reflowing row width whenever the row count crosses the overflow threshold.
        _rowsScrollView = Add(new CanvasScrollView("RowsScroll"));
        _rowsScrollView.LocalPosition = new Vector2(0f, HeaderHeight);
        _rowsContainer = _rowsScrollView.SetContent(new CanvasPanel("Rows"));

        _rowsScrollbar = Add(new CanvasScrollbar("RowsScrollbar", ui) { ScrollView = _rowsScrollView });

        OnUpdate += CloseOnOutsideClick;
        // Mirrors FsmActiveStatePanel/FsmMonitorPanel's own pattern: the scrollbar can't decide its own
        // visibility (its OnUpdate only runs once already active - see CanvasScrollbar.ShouldBeVisible),
        // so the owning panel's OnUpdate (which always runs while the dropdown itself is shown) polls it
        // every frame instead.
        OnUpdate += () => _rowsScrollbar.ActiveSelf = _rowsScrollbar.ShouldBeVisible;
    }

    public void Toggle()
    {
        if (ActiveSelf)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void Show()
    {
        _level = Level.Scenes;
        _selectedSceneIndex = -1;
        _selectedObjectIndex = -1;

        FsmSnapshot? snapshot = _getSnapshot();
        _sceneGroups = snapshot != null ? FsmDrilldownHierarchy.Build(snapshot) : new List<SceneGroup>();

        RebuildRows();
        ActiveSelf = true;

        // uGUI/Canvas render (and raycast-hit) order follows sibling index within a shared parent -
        // this popup is already the last child added to FsmRightPanel (see its constructor), but
        // re-asserting SetAsLastSibling here on every open is a cheap, explicit guarantee that it
        // stays the topmost-rendered item even if a future change adds more siblings afterward.
        transform!.SetAsLastSibling();
    }

    private void Hide()
    {
        ActiveSelf = false;
    }

    // Polls raw Input rather than routing through EventSystem (matching CanvasScrollView/
    // CanvasHorizontalScrollStrip's own wheel polling) - excludes clicks on the anchor Open button
    // itself, since that click's own OnClicked handler (Toggle) already decides the resulting state;
    // without this exclusion the two could race within the same frame and flicker.
    private void CloseOnOutsideClick()
    {
        if (!ActiveSelf)
        {
            return;
        }

        if (Input.GetMouseButtonDown(0) && !IsMouseOver() && !_anchorButton.IsMouseOver())
        {
            Hide();
        }
    }

    private void GoBack()
    {
        switch (_level)
        {
            case Level.Objects:
                _level = Level.Scenes;
                _selectedSceneIndex = -1;
                break;
            case Level.Fsms:
                _level = Level.Objects;
                _selectedObjectIndex = -1;
                break;
        }

        RebuildRows();
    }

    private void RebuildRows()
    {
        foreach (CanvasButton button in _rowButtons)
        {
            button.Destroy();
            _rowsContainer.Remove(button);
        }

        _rowButtons.Clear();
        _backButton.ActiveSelf = _level != Level.Scenes;
        _headerText.Text = BuildHeaderText();

        float rowsWidth = Width - ScrollbarWidth;

        float y = 0f;
        foreach ((string label, Action onClick) in BuildRowList())
        {
            CanvasButton button = _rowsContainer.Add(new CanvasButton($"Row{_rowButtons.Count}", _ui));
            button.LocalPosition = new Vector2(0f, y);
            button.Size = new Vector2(rowsWidth, RowHeight);
            button.Text.Text = label;
            button.Text.Alignment = TextAnchor.MiddleLeft;
            button.Text.Overflow = HorizontalWrapMode.Overflow;
            button.OnClicked += () => onClick();
            button.Build();

            y += RowHeight;
            _rowButtons.Add(button);
        }

        _rowsContainer.Size = new Vector2(rowsWidth, Mathf.Max(RowHeight, y));

        // Caps the popup's own total height at whatever room is actually left below its anchor
        // position inside the parent panel (FsmRightPanel, which the user can freely resize down to a
        // small MinPanelHeight) - without this, a scene/object/FSM list long enough to need more room
        // than that just kept growing the popup straight past the panel's own bottom edge instead of
        // scrolling within it. Falls back to the unclamped natural height if this ever runs with no
        // parent yet (shouldn't happen in practice - FsmRightPanel always Add()s this before Show() can
        // run).
        float desiredHeight = HeaderHeight + _rowsContainer.Size.y;
        float maxHeight = Parent != null ? Mathf.Max(HeaderHeight + RowHeight, Parent.Size.y - LocalPosition.y) : desiredHeight;
        float totalHeight = Mathf.Min(desiredHeight, maxHeight);

        Size = new Vector2(Width, totalHeight);

        float rowsViewportHeight = Mathf.Max(0f, totalHeight - HeaderHeight);
        _rowsScrollView.Size = new Vector2(rowsWidth, rowsViewportHeight);
        _rowsScrollbar.LocalPosition = new Vector2(rowsWidth, HeaderHeight);
        _rowsScrollbar.Size = new Vector2(ScrollbarWidth, rowsViewportHeight);
    }

    private List<(string Label, Action OnClick)> BuildRowList()
    {
        var rows = new List<(string, Action)>();
        if (_sceneGroups == null)
        {
            return rows;
        }

        switch (_level)
        {
            case Level.Scenes:
                for (int i = 0; i < _sceneGroups.Count; i++)
                {
                    int index = i;
                    rows.Add((_sceneGroups[i].SceneName, () =>
                    {
                        _selectedSceneIndex = index;
                        _level = Level.Objects;
                        RebuildRows();
                    }));
                }

                break;

            case Level.Objects:
                SceneGroup scene = _sceneGroups[_selectedSceneIndex];
                for (int i = 0; i < scene.Objects.Count; i++)
                {
                    int index = i;
                    rows.Add((scene.Objects[i].Label, () =>
                    {
                        _selectedObjectIndex = index;
                        _level = Level.Fsms;
                        RebuildRows();
                    }));
                }

                break;

            case Level.Fsms:
                ObjectGroup obj = _sceneGroups[_selectedSceneIndex].Objects[_selectedObjectIndex];
                for (int i = 0; i < obj.FsmLabels.Count; i++)
                {
                    int snapshotIndex = obj.FsmIndices[i];
                    rows.Add((obj.FsmLabels[i], () => OpenFsm(snapshotIndex)));
                }

                break;
        }

        return rows;
    }

    // Opens (or focuses, if already open) a tab for this FSM - the same FsmTabManager entry point the
    // old IMGUI list panel's leaf click uses (see FsmGraphOverlay.DrawFsmLeafList), so both drive one
    // shared notion of "what's currently open" rather than two parallel mechanisms.
    private void OpenFsm(int snapshotIndex)
    {
        FsmSnapshot? snapshot = _getSnapshot();
        if (snapshot == null || snapshotIndex < 0 || snapshotIndex >= snapshot.Fsms.Count)
        {
            return;
        }

        _tabManager.OpenOrFocus(snapshot.Fsms[snapshotIndex]);
        _showStatus("FSM Opened");
        Hide();
    }

    private string BuildHeaderText() => _level switch
    {
        Level.Scenes => "Select a scene",
        Level.Objects => $"Scene: {_sceneGroups![_selectedSceneIndex].SceneName}",
        Level.Fsms => $"Object: {_sceneGroups![_selectedSceneIndex].Objects[_selectedObjectIndex].Label}",
        _ => "",
    };
}
