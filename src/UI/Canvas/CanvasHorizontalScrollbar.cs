using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Shared by anything a CanvasHorizontalScrollbar can drive - CanvasHorizontalScrollStrip (the FSM tab
// strip) and CanvasScrollView (FsmActiveStatePanel's own vertical scroll view, which also needs
// horizontal panning whenever a row's text measures wider than its column - see that panel's
// RowCursor.MaxWidth). CanvasScrollView already has an unrelated public vertical SetScrollPercentage,
// so it implements this member explicitly rather than exposing a second, ambiguous overload.
internal interface IHorizontalScrollSource
{
    CanvasNode? Content { get; }
    float GetScrollableWidth();
    void SetScrollPercentage(float percentage);
}

// Horizontal scrollbar paired with an IHorizontalScrollSource - the same track+grip design as
// CanvasScrollbar (see that file's own header comment), rotated to the X axis.
internal sealed class CanvasHorizontalScrollbar : CanvasNode
{
    private const float MinGripWidth = 24f;

    private readonly CanvasButton _dragSurface;
    private readonly CanvasImage _grip;
    private readonly CanvasNode[] _childList;

    public IHorizontalScrollSource? Source { get; set; }

    protected override bool Interactable => true;

    public CanvasHorizontalScrollbar(string name, UICommon ui) : base(name)
    {
        _dragSurface = new CanvasButton("DragSurface", ui) { Tint = ui.ScrollTrackColor };
        _dragSurface.RemoveBorder();
        _dragSurface.Parent = this;

        _grip = new CanvasImage("Grip", ui) { Tint = ui.PanelBorder };
        _grip.AddBorder(ui.AccentColor);
        _grip.Parent = this;

        // Fixed for this node's whole lifetime - built once here instead of a yield-return ChildList()
        // override, which allocated a fresh compiler-generated enumerator on every call (see
        // CanvasNode.ChildList's own comment; this is walked every frame by CollectSubtree).
        _childList = new CanvasNode[] { _dragSurface, _grip };

        OnUpdate += Refresh;
    }

    protected override IEnumerable<CanvasNode> ChildList() => _childList;

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        _dragSurface.Size = Size;
    }

    public override void Build(Transform? rootParent = null)
    {
        _dragSurface.Size = Size;
        base.Build(rootParent);

        _dragSurface.OnClicked += OnTrackClicked;
        _dragSurface.AddEventTrigger(EventTriggerType.Drag, OnDragged);
    }

    private void OnTrackClicked()
    {
        if (Source == null)
        {
            return;
        }

        float mouseX = Input.mousePosition.x;
        if (mouseX >= _grip.Position.x && mouseX <= _grip.Position.x + _grip.Size.x)
        {
            // Clicked on the grip itself - that's a drag, not a page-jump; let OnDragged handle it.
            return;
        }

        float travel = Mathf.Max(1f, Size.x - _grip.Size.x);
        float x = Mathf.Clamp(mouseX - Position.x - _grip.Size.x / 2f, 0f, travel);
        Source.SetScrollPercentage(x / travel);
    }

    private void OnDragged(PointerEventData eventData)
    {
        if (Source == null)
        {
            return;
        }

        float travel = Mathf.Max(1f, Size.x - _grip.Size.x);
        float x = Mathf.Clamp(_grip.LocalPosition.x + eventData.delta.x, 0f, travel);
        Source.SetScrollPercentage(x / travel);
    }

    // Whether ActiveSelf *should* be true right now - a plain query, not a mutation, for the same
    // reason CanvasScrollbar.ShouldBeVisible is: this class's own OnUpdate only runs while it's
    // already active, so it can never turn itself back on if it decided to turn itself off. The
    // owning panel (whose own OnUpdate always runs) polls this every frame instead.
    public bool ShouldBeVisible => Source?.Content != null && Source.Content.Size.x > Size.x;

    private void Refresh()
    {
        if (Source?.Content == null)
        {
            return;
        }

        float contentWidth = Source.Content.Size.x;
        float gripWidth = Mathf.Clamp(Size.x * Size.x / Mathf.Max(1f, contentWidth), MinGripWidth, Size.x);
        _grip.Size = new Vector2(gripWidth, Size.y);

        float scrollable = Source.GetScrollableWidth();
        float percentage = scrollable > 0f ? Mathf.Clamp01(-Source.Content.LocalPosition.x / scrollable) : 0f;
        _grip.LocalPosition = new Vector2(percentage * (Size.x - gripWidth), 0f);
    }
}
