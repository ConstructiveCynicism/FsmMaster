using System.Collections.Generic;
using UnityEngine;

namespace FsmMaster;

// Vertical scroll viewport - concept and clip-rect math ported from Silksong.DebugMod's
// CanvasScrollView (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasScrollView.cs). This node
// itself is the clipped viewport; SetContent supplies the single child panel whose rows a caller
// adds to, and scrolling moves that child's LocalPosition.y within the viewport - clamped so content
// never scrolls past its own bounds. Mouse-wheel scrolling polls Input.mouseScrollDelta directly
// (matching the reference), independent of EventSystem/GraphicRaycaster - see FsmGraphOverlay's
// interactiveRect/IsPointerOverGameObject gating, which exists precisely so the graph canvas's own
// scroll-wheel zoom doesn't also fire while the pointer is over a scroll view like this one.
internal sealed class CanvasScrollView : CanvasNode
{
    private const float ScrollSpeed = 30f;

    private CanvasNode? _content;
    private float _contentHeightAtLastCheck;

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
        return content;
    }

    protected override IEnumerable<CanvasNode> ChildList()
    {
        if (_content != null)
        {
            yield return _content;
        }
    }

    protected override bool GetClipRect(out Rect clipRect)
    {
        clipRect = new Rect(
            Position.x - Screen.width / 2f,
            Screen.height / 2f - Position.y - Size.y,
            Size.x,
            Size.y);
        return true;
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

        if (!Mathf.Approximately(Input.mouseScrollDelta.y, 0f) && IsMouseOver())
        {
            float y = _content.LocalPosition.y + Input.mouseScrollDelta.y * ScrollSpeed;
            y = Mathf.Clamp(y, Mathf.Min(-GetScrollableHeight(), 0f), 0f);
            _content.LocalPosition = new Vector2(_content.LocalPosition.x, y);
        }
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

    public float GetScrollableHeight() => Mathf.Max(0f, (_content?.Size.y ?? 0f) - Size.y);
}
