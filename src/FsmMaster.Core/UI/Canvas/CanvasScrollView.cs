// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Vertical scroll viewport. Clipping uses a real Unity RectMask2D rather than CanvasNode's own
// hand-rolled CanvasRenderer.EnableRectClipping. That hand-rolled clip is rendering-only - it has no
// effect on GraphicRaycaster hit-testing - so content scrolled out of view stayed fully clickable at
// its real, moved RectTransform position, silently swallowing clicks meant for whatever it happened
// to overlap (e.g. rows scrolled above the viewport overlapping the button row above it). RectMask2D
// clips both rendering and raycasts for its whole subtree, so content correctly stops being
// interactive once it's actually scrolled out of view. SetContent supplies the single child panel
// whose rows a caller adds to, and scrolling moves that child's LocalPosition.y within the viewport,
// clamped so content never scrolls past its own bounds. Mouse-wheel scrolling polls
// Input.mouseScrollDelta directly, independent of EventSystem/GraphicRaycaster - see
// FsmGraphOverlay's panel-rect/IsPointerOverGameObject gating, which exists precisely so the
// graph canvas's own scroll-wheel zoom doesn't also fire while the pointer is over a scroll view like
// this one.
internal sealed class CanvasScrollView : CanvasNode, IHorizontalScrollSource
{
    private const float ScrollSpeed = 30f;

    private CanvasNode? _content;
    private float _contentHeightAtLastCheck;
    private float _contentWidthAtLastCheck;
    private CanvasNode[] _childList = ArrayPolyfill.Empty<CanvasNode>();

    public CanvasNode? Content => _content;

    // NOT overridden to false: this container has no Graphic of its own to raycast against, so it
    // needs no CanvasGroup either way - but CanvasNode.Build() adds a blocksRaycasts=false CanvasGroup
    // whenever Interactable is false, and Unity's CanvasGroup.blocksRaycasts cascades to the entire
    // subtree unless a descendant adds its own CanvasGroup to override it, silently disabling any
    // interactive content nested inside (see the identical bug this caused in
    // CanvasHorizontalScrollStrip once its content held buttons instead of plain text).

    public CanvasScrollView(string name) : base(name)
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

    // A small negative padding on every side (RectMask2D.padding shrinks the clip rect on positive
    // values - Vector4 is Left/Bottom/Right/Top) expands it by the same amount instead. Content flush
    // against the mask's own edge (the first block's top border at content.LocalPosition.y == 0, or any
    // block's right border, since block width always equals _scrollView.Size.x exactly) otherwise has
    // its 1px border clipped away outright rather than merely dimmed, since the border sits exactly on
    // the clip boundary rather than inside it.
    private static readonly Vector4 ClipPadding = new(-2f, -2f, -2f, -2f);

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
#if NET35
        // RectMask2D.padding doesn't exist on net35's older UnityEngine.UI.dll (confirmed against the
        // real hk1221 build) - masking itself still works there, just without ClipPadding's fix for
        // content flush against the mask's own edge (see the field's own comment).
        GameObject!.AddComponent<RectMask2D>();
#else
        GameObject!.AddComponent<RectMask2D>().padding = ClipPadding;
#endif
    }

    private void Poll()
    {
        if (_content == null)
        {
            return;
        }

        // Content grew/shrank (rows were rebuilt) since last frame - reclamp its scroll position so a
        // previously-valid offset doesn't leave a gap or an out-of-bounds scroll after a rebuild.
        if (!Mathf.Approximately(_content.Size.y, _contentHeightAtLastCheck))
        {
            float clampedY = Mathf.Clamp(_content.LocalPosition.y, Mathf.Min(-GetScrollableHeight(), 0f), 0f);
            _content.LocalPosition = new Vector2(_content.LocalPosition.x, clampedY);
            _contentHeightAtLastCheck = _content.Size.y;
        }

        // Same reclamp on the X axis - content here is normally only as wide as the viewport, but
        // FsmActiveStatePanel widens it past that whenever a row's own text measures wider than its
        // assigned column (see that panel's RowCursor.MaxWidth), which is what a paired
        // CanvasHorizontalScrollbar is for.
        if (!Mathf.Approximately(_content.Size.x, _contentWidthAtLastCheck))
        {
            float clampedX = Mathf.Clamp(_content.LocalPosition.x, Mathf.Min(-GetScrollableWidth(), 0f), 0f);
            _content.LocalPosition = new Vector2(clampedX, _content.LocalPosition.y);
            _contentWidthAtLastCheck = _content.Size.x;
        }

        if (!Mathf.Approximately(Input.mouseScrollDelta.y, 0f) && IsMouseOver())
        {
            float y = _content.LocalPosition.y + Input.mouseScrollDelta.y * ScrollSpeed;
            y = Mathf.Clamp(y, Mathf.Min(-GetScrollableHeight(), 0f), 0f);
            _content.LocalPosition = new Vector2(_content.LocalPosition.x, y);
        }
    }

    // Jumps straight to the top, unconditionally - unlike Poll's own reclamp (which only kicks in once
    // content size actually changes, and even then just clamps a stale offset into the new bounds
    // rather than zeroing it), so a caller switching to a shorter list mid-scroll doesn't land partway
    // down it with the first row's top edge cut off by the viewport mask.
    public void ScrollToTop()
    {
        if (_content == null)
        {
            return;
        }

        _content.LocalPosition = new Vector2(_content.LocalPosition.x, 0f);
    }

    public void SetScrollPercentage(float percentage)
    {
        if (_content == null)
        {
            return;
        }

        percentage = Mathf.Clamp01(percentage);
        _content.LocalPosition = new Vector2(_content.LocalPosition.x, -percentage * GetScrollableHeight());
    }

    // Scrolls just enough to bring the content-space range [y, y + height) fully into the viewport -
    // a no-op if it's already visible, unlike SetScrollPercentage which always jumps to an absolute
    // position. Used to bring a specific row (e.g. an action picked via the graph overlay's transition
    // click-through) into view without fighting a scroll position the user may have set deliberately.
    public void ScrollToShow(float y, float height)
    {
        if (_content == null)
        {
            return;
        }

        float scrollableHeight = GetScrollableHeight();
        float currentScroll = -_content.LocalPosition.y;
        float viewportHeight = Size.y;

        float targetScroll = currentScroll;
        if (y < currentScroll)
        {
            targetScroll = y;
        }
        else if (y + height > currentScroll + viewportHeight)
        {
            targetScroll = y + height - viewportHeight;
        }

        targetScroll = Mathf.Clamp(targetScroll, 0f, scrollableHeight);
        _content.LocalPosition = new Vector2(_content.LocalPosition.x, -targetScroll);
    }

    public float GetScrollableHeight() => Mathf.Max(0f, (_content?.Size.y ?? 0f) - Size.y);

    public float GetScrollableWidth() => Mathf.Max(0f, (_content?.Size.x ?? 0f) - Size.x);

    // Explicit implementation, not a public overload - a public `SetScrollPercentage(float)` here would
    // be ambiguous with the vertical one above (same name, same signature, different axis). Only
    // CanvasHorizontalScrollbar (via IHorizontalScrollSource) ever needs to drive the X axis; every
    // other caller keeps using the public vertical member unchanged.
    void IHorizontalScrollSource.SetScrollPercentage(float percentage)
    {
        if (_content == null)
        {
            return;
        }

        percentage = Mathf.Clamp01(percentage);
        _content.LocalPosition = new Vector2(-percentage * GetScrollableWidth(), _content.LocalPosition.y);
    }
}
