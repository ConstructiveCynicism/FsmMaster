using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FsmMaster;

// Small clickable circular on/off indicator - unfilled ring when off, filled disc when on. Not a
// CanvasImage subclass: CanvasImage.Build() hardcodes image.sprite to UICommon's single shared
// SolidSprite, and this widget needs to swap between two different procedurally-generated sprites
// (UICommon.DotRingSprite/DotFilledSprite) instead.
internal sealed class CanvasToggleDot : CanvasNode
{
    private readonly UICommon _ui;
    private Image? _image;
    private bool _on;

    public event Action? OnClicked;

    protected override bool Interactable => true;

    public CanvasToggleDot(string name, UICommon ui) : base(name)
    {
        _ui = ui;
    }

    public bool On
    {
        get => _on;
        set
        {
            _on = value;
            if (_image != null)
            {
                _image.sprite = _on ? _ui.DotFilledSprite : _ui.DotRingSprite;
            }
        }
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);

        _image = GameObject!.AddComponent<Image>();
        _image.sprite = _on ? _ui.DotFilledSprite : _ui.DotRingSprite;
        _image.color = _ui.AccentColor;

        AddEventTrigger(EventTriggerType.PointerClick, (PointerEventData _) => OnClicked?.Invoke());
    }
}
