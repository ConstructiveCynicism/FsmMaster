// SPDX-License-Identifier: EUPL-1.2
using System;
using UnityEngine;

namespace FsmMaster;

// Name-this-save popup shown by the Save button - a single text field plus a confirm button, anchored
// below whichever button opened it (the Save button, see FsmRightPanel). Concept modeled on
// FsmOpenDropdown's own show/hide/outside-click pattern rather than a shared generic dialog base, since
// FsmMaster only has a couple of these small popups and they don't otherwise share behavior worth
// abstracting.
internal sealed class FsmSaveDialog : CanvasPanel
{
    private const float Width = 240f;
    private const float HeaderHeight = 22f;
    private const float FieldHeight = 26f;
    private const float ButtonHeight = 26f;
    private const float Padding = 8f;

    private readonly CanvasButton _anchorButton;
    private readonly CanvasText _headerText;
    private readonly CanvasTextField _nameField;
    private readonly CanvasButton _confirmButton;

    private string _sceneName = "";
    private string _fsmKey = "";

    // sceneName, fsmKey, saveName - raised once the user confirms a non-blank name, either by clicking
    // Save or pressing Enter in the field.
    public event Action<string, string, string>? OnConfirm;

    public FsmSaveDialog(UICommon ui, CanvasButton anchorButton) : base("SaveDialog")
    {
        _anchorButton = anchorButton;
        ActiveSelf = false;
        Size = new Vector2(Width, HeaderHeight + FieldHeight + ButtonHeight + Padding * 4f);

        CanvasImage background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        background.AddBorder(ui.PanelBorder);

        _headerText = Add(new CanvasText("Header", ui) { Text = "Save configuration as:" });
        _headerText.LocalPosition = new Vector2(Padding, Padding);
        _headerText.Size = new Vector2(Width - Padding * 2f, HeaderHeight);
        _headerText.Alignment = TextAnchor.MiddleLeft;

        _nameField = Add(new CanvasTextField("NameField", ui));
        _nameField.LocalPosition = new Vector2(Padding, Padding * 2f + HeaderHeight);
        _nameField.Size = new Vector2(Width - Padding * 2f, FieldHeight);
        _nameField.OnSubmit += _ => Confirm();

        _confirmButton = Add(new CanvasButton("ConfirmButton", ui));
        _confirmButton.Text.Text = "Save";
        _confirmButton.LocalPosition = new Vector2(Padding, Padding * 3f + HeaderHeight + FieldHeight);
        _confirmButton.Size = new Vector2(Width - Padding * 2f, ButtonHeight);
        _confirmButton.OnClicked += Confirm;

        OnUpdate += CloseOnOutsideClick;
    }

    public void Show(string sceneName, string fsmKey, string defaultName)
    {
        _sceneName = sceneName;
        _fsmKey = fsmKey;
        _nameField.Text = defaultName;

        LocalPosition = new Vector2(_anchorButton.LocalPosition.x, _anchorButton.LocalPosition.y + _anchorButton.Size.y + 4f);
        ActiveSelf = true;
        transform!.SetAsLastSibling();
        _nameField.Activate();
    }

    private void Hide()
    {
        // Mirrors FsmRightPanel.OnUpdateActive's own defensive release - hiding this dialog via
        // ActiveSelf = false doesn't reliably fire the focused InputField's own onEndEdit, so without
        // this a save/cancel while still typing could leave FsmMaster's input lock stuck on.
        CanvasTextField.ForceReleaseFocus();
        ActiveSelf = false;
    }

    private void Confirm()
    {
        string name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        OnConfirm?.Invoke(_sceneName, _fsmKey, name);
        Hide();
    }

    // Polls raw Input rather than routing through EventSystem, matching FsmOpenDropdown's own outside-
    // click handling - excludes clicks on the anchor Save button itself, since that click's own
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
