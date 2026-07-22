// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Horizontal scroll viewport for the FSM tab strip. This node is the clipped viewport; SetContent
// supplies the single child panel whose tabs a caller adds to, and scrolling moves that child's
// LocalPosition.x within the viewport, clamped so content never scrolls past its own bounds.
// Clipping uses a real Unity RectMask2D rather than CanvasNode's own hand-rolled
// CanvasRenderer.EnableRectClipping - that hand-rolled clip is rendering-only and has no effect on
// GraphicRaycaster hit-testing, so a tab scrolled out of view stayed fully clickable at its real,
// moved position (see CanvasScrollView's own header comment for the full explanation of this same
// bug and fix).
internal sealed class CanvasHorizontalScrollStrip : CanvasNode, IHorizontalScrollSource
{
    private const float ScrollSpeed = 30f;

    private CanvasNode? _content;
    private float _contentWidthAtLastCheck;
    private CanvasNode[] _childList = ArrayPolyfill.Empty<CanvasNode>();

    public CanvasNode? Content => _content;

    // NOT overridden to false: this container has no Graphic of its own to raycast against, so it
    // needs no CanvasGroup either way - but CanvasNode.Build() adds a blocksRaycasts=false CanvasGroup
    // whenever Interactable is false, and Unity's CanvasGroup.blocksRaycasts cascades to the entire
    // subtree unless a descendant adds its own CanvasGroup to override it. That silently disabled
    // every button nested inside this strip's content (the tab select/close buttons) until this was
    // left at the default (true).

    public CanvasHorizontalScrollStrip(string name) : base(name)
    {
        OnUpdate += Poll;
    }

    public T SetContent<T>(T content) where T : CanvasNode
    {
        _content = content;
        content.Parent = this;
        _childList = new CanvasNode[] { content };
        return content;
    }

    // _childList (built once in SetContent, rather than a yield-return here) - see
    // CanvasNode.ChildList's own comment on why the iterator form was a continuous per-frame GC
    // source once CollectSubtree started walking it every frame.
    protected override IEnumerable<CanvasNode> ChildList() => _childList;

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        GameObject!.AddComponent<RectMask2D>();
    }

    private void Poll()
    {
        if (_content == null)
        {
            return;
        }

        // Content grew/shrank (a tab opened/closed) since last frame - reclamp so a previously-valid
        // offset doesn't leave a gap or an out-of-bounds scroll after the tab list changes.
        if (!Mathf.Approximately(_content.Size.x, _contentWidthAtLastCheck))
        {
            float clampedX = Mathf.Clamp(_content.LocalPosition.x, Mathf.Min(-GetScrollableWidth(), 0f), 0f);
            _content.LocalPosition = new Vector2(clampedX, _content.LocalPosition.y);
            _contentWidthAtLastCheck = _content.Size.x;
        }

        // No physical horizontal wheel on most mice - the vertical wheel scrolls this strip instead
        // while it's hovered, matching how most desktop UI toolkits handle a horizontal list.
        if (!Mathf.Approximately(Input.mouseScrollDelta.y, 0f) && IsMouseOver())
        {
            float x = _content.LocalPosition.x + Input.mouseScrollDelta.y * ScrollSpeed;
            x = Mathf.Clamp(x, Mathf.Min(-GetScrollableWidth(), 0f), 0f);
            _content.LocalPosition = new Vector2(x, _content.LocalPosition.y);
        }
    }

    public void SetScrollPercentage(float percentage)
    {
        if (_content == null)
        {
            return;
        }

        percentage = Mathf.Clamp01(percentage);
        _content.LocalPosition = new Vector2(-percentage * GetScrollableWidth(), _content.LocalPosition.y);
    }

    public float GetScrollableWidth() => Mathf.Max(0f, (_content?.Size.x ?? 0f) - Size.x);
}
