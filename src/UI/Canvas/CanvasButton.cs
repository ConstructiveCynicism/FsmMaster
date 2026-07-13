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

    // Rebuilt only when the own border is added/removed (RebuildOwnChildList) rather than on every
    // ChildList() call - see CanvasNode.ChildList's own comment on why a yield-return version of this
    // was a continuous per-frame GC source.
    private CanvasNode[] _ownChildList = Array.Empty<CanvasNode>();

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

        RebuildOwnChildList();
    }

    public override CanvasBorder AddBorder(Color color)
    {
        CanvasBorder result = base.AddBorder(color);
        RebuildOwnChildList();
        return result;
    }

    public override void RemoveBorder()
    {
        base.RemoveBorder();
        RebuildOwnChildList();
    }

    // _text/_hoverBorder are still null the first time this runs - AddBorder(ui.PanelBorder) in the
    // constructor above triggers this via the override before either field is assigned. Skipped there;
    // the constructor's own trailing call (after both are constructed) builds the real combined list.
    private void RebuildOwnChildList()
    {
        if (_text == null || _hoverBorder == null)
        {
            return;
        }

        _ownChildList = Border != null
            ? new CanvasNode[] { Border, _hoverBorder, _text }
            : new CanvasNode[] { _hoverBorder, _text };
    }

    protected override IEnumerable<CanvasNode> ChildList() => _ownChildList;

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
