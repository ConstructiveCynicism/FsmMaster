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
// this), reusing FsmDrilldownHierarchy for the actual scene/object/fsm grouping. No windowing/
// virtualization of the row list (unlike the old IMGUI list panel) - scene/object/fsm counts are
// small enough in practice that this isn't needed for a first pass.
internal sealed class FsmOpenDropdown : CanvasPanel
{
    private const float Width = 260f;
    private const float RowHeight = 22f;
    private const float HeaderHeight = 22f;
    private const float BackButtonWidth = 70f;

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

    private readonly CanvasButton _backButton;
    private readonly CanvasText _headerText;
    private readonly CanvasPanel _rowsContainer;
    private readonly List<CanvasButton> _rowButtons = new();

    private Level _level = Level.Scenes;
    private List<SceneGroup>? _sceneGroups;
    private int _selectedSceneIndex = -1;
    private int _selectedObjectIndex = -1;

    public FsmOpenDropdown(UICommon ui, FsmTabManager tabManager, Func<FsmSnapshot?> getSnapshot, CanvasButton anchorButton)
        : base("OpenDropdown")
    {
        _ui = ui;
        _tabManager = tabManager;
        _getSnapshot = getSnapshot;
        _anchorButton = anchorButton;
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

        _rowsContainer = Add(new CanvasPanel("Rows"));
        _rowsContainer.LocalPosition = new Vector2(0f, HeaderHeight);

        OnUpdate += CloseOnOutsideClick;
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

        float y = 0f;
        foreach ((string label, Action onClick) in BuildRowList())
        {
            CanvasButton button = _rowsContainer.Add(new CanvasButton($"Row{_rowButtons.Count}", _ui));
            button.LocalPosition = new Vector2(0f, y);
            button.Size = new Vector2(Width, RowHeight);
            button.Text.Text = label;
            button.Text.Alignment = TextAnchor.MiddleLeft;
            button.Text.Overflow = HorizontalWrapMode.Overflow;
            button.OnClicked += () => onClick();
            button.Build();

            y += RowHeight;
            _rowButtons.Add(button);
        }

        Size = new Vector2(Width, HeaderHeight + Mathf.Max(RowHeight, y));
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
