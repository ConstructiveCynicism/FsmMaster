using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Clickable button - background image + centered label + a border, plus a hover-highlight border
// shown only while the pointer is over it. Concept-ported from Silksong.DebugMod's CanvasButton
// (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasButton.cs), but each button owns its own
// hover-border child instance rather than sharing one static, reparented-per-hover CanvasBorder -
// DebugMod's shared instance is a persistent-singleton optimization that doesn't fit FsmMaster's
// per-reload-rebuilt tree, and this widget count is small enough that per-button ownership costs
// nothing meaningful.
internal class CanvasButton : CanvasImage
{
    private readonly UICommon _ui;
    private readonly CanvasText _text;
    private readonly CanvasBorder _hoverBorder;
    private bool _toggled;

    public CanvasText Text => _text;

    public event Action? OnClicked;

    public bool Toggled
    {
        get => _toggled;
        set
        {
            if (_toggled != value)
            {
                _toggled = value;
                Tint = _toggled ? _ui.ButtonActive : _ui.ButtonNormal;
            }
        }
    }

    protected override bool Interactable => true;

    public CanvasButton(string name, UICommon ui) : base(name, ui)
    {
        _ui = ui;
        Tint = ui.ButtonNormal;
        AddBorder(ui.PanelBorder);

        _text = new CanvasText("Label", ui) { Alignment = TextAnchor.MiddleCenter };
        _text.Parent = this;

        _hoverBorder = new CanvasBorder("HoverBorder", ui, ui.AccentColor) { ActiveSelf = false };
        _hoverBorder.Parent = this;
    }

    protected override IEnumerable<CanvasNode> ChildList()
    {
        foreach (CanvasNode child in base.ChildList())
        {
            yield return child;
        }

        yield return _hoverBorder;
        yield return _text;
    }

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        _text.Size = Size;
        _hoverBorder.Size = Size;
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);

        AddEventTrigger(EventTriggerType.PointerClick, (PointerEventData _) => OnClicked?.Invoke());
        AddEventTrigger(EventTriggerType.PointerEnter, (PointerEventData _) => _hoverBorder.ActiveSelf = true);
        AddEventTrigger(EventTriggerType.PointerExit, (PointerEventData _) => _hoverBorder.ActiveSelf = false);
    }
}
