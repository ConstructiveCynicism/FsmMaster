// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FsmMaster;

// Pick-a-saved-configuration popup shown by the Load button - lists every named save
// FsmSaveDataStore.ListSaveNames finds for the active tab's FsmKey, anchored below whichever button
// opened it. Rebuilt fresh every time it's shown rather than kept in sync incrementally, matching
// FsmOpenDropdown's own "only open briefly" reasoning - the list is small and this isn't a
// performance-sensitive path.
internal sealed class FsmLoadDialog : CanvasPanel
{
    private const float Width = 240f;
    private const float RowHeight = 24f;
    private const float HeaderHeight = 22f;

    private readonly UICommon _ui;
    private readonly CanvasButton _anchorButton;
    private readonly CanvasText _headerText;
    private readonly CanvasPanel _rowsContainer;
    private readonly List<CanvasNode> _rowNodes = new();

    private string _sceneName = "";
    private string _fsmKey = "";

    // sceneName, fsmKey, saveName - raised when the user clicks one of the listed saves.
    public event Action<string, string, string>? OnSelected;

    public FsmLoadDialog(UICommon ui, CanvasButton anchorButton) : base("LoadDialog")
    {
        _ui = ui;
        _anchorButton = anchorButton;
        ActiveSelf = false;
        Size = new Vector2(Width, HeaderHeight + RowHeight);

        CanvasImage background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        background.AddBorder(ui.PanelBorder);

        _headerText = Add(new CanvasText("Header", ui) { Text = "Load configuration:" });
        _headerText.LocalPosition = new Vector2(6f, 0f);
        _headerText.Size = new Vector2(Width - 12f, HeaderHeight);
        _headerText.Alignment = TextAnchor.MiddleLeft;

        _rowsContainer = Add(new CanvasPanel("Rows"));
        _rowsContainer.LocalPosition = new Vector2(0f, HeaderHeight);

        OnUpdate += CloseOnOutsideClick;
    }

    public void Show(string sceneName, string fsmKey)
    {
        _sceneName = sceneName;
        _fsmKey = fsmKey;

        RebuildRows(FsmSaveDataStore.ListSaveNames(sceneName, fsmKey));

        LocalPosition = new Vector2(_anchorButton.LocalPosition.x, _anchorButton.LocalPosition.y + _anchorButton.Size.y + 4f);
        ActiveSelf = true;
        transform!.SetAsLastSibling();
    }

    private void Hide() => ActiveSelf = false;

    private void RebuildRows(List<string> saveNames)
    {
        foreach (CanvasNode node in _rowNodes)
        {
            node.Destroy();
            _rowsContainer.Remove(node);
        }

        _rowNodes.Clear();

        if (saveNames.Count == 0)
        {
            CanvasText empty = _rowsContainer.Add(new CanvasText("Empty", _ui) { Text = "(no saved configurations)" });
            empty.LocalPosition = new Vector2(6f, 0f);
            empty.Size = new Vector2(Width - 12f, RowHeight);
            empty.Alignment = TextAnchor.MiddleLeft;
            empty.Color = _ui.ReadOnlyColor;
            empty.Build();
            _rowNodes.Add(empty);
        }
        else
        {
            float y = 0f;
            foreach (string saveName in saveNames)
            {
                CanvasButton button = _rowsContainer.Add(new CanvasButton($"Row{_rowNodes.Count}", _ui));
                button.LocalPosition = new Vector2(0f, y);
                button.Size = new Vector2(Width, RowHeight);
                button.Text.Text = saveName;
                button.Text.Alignment = TextAnchor.MiddleLeft;
                button.Text.Overflow = HorizontalWrapMode.Overflow;
                button.OnClicked += () => Select(saveName);
                button.Build();

                y += RowHeight;
                _rowNodes.Add(button);
            }
        }

        Size = new Vector2(Width, HeaderHeight + Mathf.Max(RowHeight, _rowNodes.Count * RowHeight));
    }

    private void Select(string saveName)
    {
        OnSelected?.Invoke(_sceneName, _fsmKey, saveName);
        Hide();
    }

    // Polls raw Input rather than routing through EventSystem, matching FsmOpenDropdown's own outside-
    // click handling - excludes clicks on the anchor Load button itself, since that click's own
    // OnClicked handler (opening this dialog) already decides the resulting state.
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
}
