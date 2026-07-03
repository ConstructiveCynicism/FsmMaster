using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Horizontal scrollbar paired with a CanvasHorizontalScrollStrip - the same track+grip design as
// CanvasScrollbar (see that file's own header comment), rotated to the X axis.
internal sealed class CanvasHorizontalScrollbar : CanvasNode
{
    private const float MinGripWidth = 24f;

    private readonly CanvasButton _dragSurface;
    private readonly CanvasImage _grip;

    public CanvasHorizontalScrollStrip? ScrollStrip { get; set; }

    protected override bool Interactable => true;

    public CanvasHorizontalScrollbar(string name, UICommon ui) : base(name)
    {
        _dragSurface = new CanvasButton("DragSurface", ui) { Tint = ui.ScrollTrackColor };
        _dragSurface.RemoveBorder();
        _dragSurface.Parent = this;

        _grip = new CanvasImage("Grip", ui) { Tint = ui.PanelBorder };
        _grip.AddBorder(ui.AccentColor);
        _grip.Parent = this;

        OnUpdate += Refresh;
    }

    protected override IEnumerable<CanvasNode> ChildList()
    {
        yield return _dragSurface;
        yield return _grip;
    }

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
        if (ScrollStrip == null)
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
        ScrollStrip.SetScrollPercentage(x / travel);
    }

    private void OnDragged(PointerEventData eventData)
    {
        if (ScrollStrip == null)
        {
            return;
        }

        float travel = Mathf.Max(1f, Size.x - _grip.Size.x);
        float x = Mathf.Clamp(_grip.LocalPosition.x + eventData.delta.x, 0f, travel);
        ScrollStrip.SetScrollPercentage(x / travel);
    }

    // Whether ActiveSelf *should* be true right now - a plain query, not a mutation, for the same
    // reason CanvasScrollbar.ShouldBeVisible is: this class's own OnUpdate only runs while it's
    // already active, so it can never turn itself back on if it decided to turn itself off. The
    // owning panel (whose own OnUpdate always runs) polls this every frame instead.
    public bool ShouldBeVisible => ScrollStrip?.Content != null && ScrollStrip.Content.Size.x > Size.x;

    private void Refresh()
    {
        if (ScrollStrip?.Content == null)
        {
            return;
        }

        float contentWidth = ScrollStrip.Content.Size.x;
        float gripWidth = Mathf.Clamp(Size.x * Size.x / Mathf.Max(1f, contentWidth), MinGripWidth, Size.x);
        _grip.Size = new Vector2(gripWidth, Size.y);

        float scrollable = ScrollStrip.GetScrollableWidth();
        float percentage = scrollable > 0f ? Mathf.Clamp01(-ScrollStrip.Content.LocalPosition.x / scrollable) : 0f;
        _grip.LocalPosition = new Vector2(percentage * (Size.x - gripWidth), 0f);
    }
}
