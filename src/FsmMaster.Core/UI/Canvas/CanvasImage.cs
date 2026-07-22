// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Displays UICommon's single shared solid-color sprite, tinted per instance via Tint. Never
// generates or swaps in a per-instance Texture2D/Sprite, avoiding any per-widget texture lifecycle
// to track through a ScriptEngine reload.
internal class CanvasImage : CanvasNode
{
    private readonly UICommon _ui;
    private Color _tint = Color.white;
    private CanvasBorder? _border;

    // Rebuilt only in AddBorder/RemoveBorder (rare, setup-time calls) rather than on every ChildList()
    // call - see CanvasNode.ChildList's own comment on why a yield-return version of this was a
    // continuous per-frame GC source.
    private CanvasNode[] _childList = ArrayPolyfill.Empty<CanvasNode>();

    public CanvasBorder? Border => _border;
    public bool IsBackground { get; set; }

    protected override bool Interactable => false;

    public CanvasImage(string name, UICommon ui) : base(name)
    {
        _ui = ui;
    }

    public Color Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            if (GameObject != null)
            {
                ApplyTint();
            }
        }
    }

    public virtual CanvasBorder AddBorder(Color color)
    {
        _border ??= new CanvasBorder("Border", _ui, color);
        _border.Parent = this;
        _border.Size = Size;
        RebuildChildList();
        return _border;
    }

    public virtual void RemoveBorder()
    {
        _border = null;
        RebuildChildList();
    }

    private void RebuildChildList()
    {
        _childList = _border != null ? new CanvasNode[] { _border } : ArrayPolyfill.Empty<CanvasNode>();
    }

    protected override IEnumerable<CanvasNode> ChildList() => _childList;

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        if (_border != null)
        {
            _border.Size = Size;
        }
    }

    public override void Build(Transform? rootParent = null)
    {
        if (IsBackground && Parent != null)
        {
            Size = Parent.Size;
        }

        base.Build(rootParent);

        Image image = GameObject!.AddComponent<Image>();
        image.sprite = _ui.SolidSprite;
        image.color = _tint;

        if (IsBackground)
        {
            // A background is otherwise non-Interactable (see above), which by default also lets
            // clicks pass through to whatever renders behind it (the graph canvas) - re-enable
            // raycast blocking just for backgrounds so a click on empty panel space doesn't also
            // pan/select a node underneath.
            CanvasGroup? group = GameObject.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.blocksRaycasts = true;
            }
        }
    }

    private void ApplyTint()
    {
        Image? image = GameObject!.GetComponent<Image>();
        if (image != null)
        {
            image.color = _tint;
        }
    }
}
