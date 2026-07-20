using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Outlined, clickable background block FsmActiveStatePanel draws behind one action's fields (or one
// variable-type group's rows) to visually separate it from its neighbors - see
// FsmActiveStatePanel.BeginBlock/EndBlock. Not a CanvasButton: a button forces a single centered
// label, but this widget's "content" is whichever rows the caller positions as later-added (and so
// frontmost, per CanvasButton's own convention) siblings on top of it. Clicking anywhere on the block
// that isn't already covered by one of those rows (a plain label/value CanvasText is non-interactable
// and lets the click fall through - see CanvasText.Interactable) hits this block instead and selects it.
internal sealed class CanvasSectionBlock : CanvasImage
{
    private readonly UICommon _ui;
    private bool _selected;

    public event Action? OnClicked;

    protected override bool Interactable => true;

    public CanvasSectionBlock(string name, UICommon ui) : base(name, ui)
    {
        _ui = ui;
        Tint = ui.SectionBlockColor;
        AddBorder(ui.PanelBorder);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Tint = _selected ? _ui.SectionBlockSelectedColor : _ui.SectionBlockColor;
        }
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        AddEventTrigger(EventTriggerType.PointerClick, (PointerEventData _) => OnClicked?.Invoke());
    }
}
