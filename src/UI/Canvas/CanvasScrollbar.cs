using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Vertical scrollbar paired with a CanvasScrollView - track + draggable grip, sized proportional to
// the visible/content height ratio. Concept-ported from Silksong.DebugMod's CanvasScrollbar
// (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasScrollbar.cs), but the track is a plain
// tinted strip reusing UICommon's shared solid sprite (see CanvasImage) rather than a baked
// per-instance Texture2D - the reference generates one small texture per scrollbar in Build() and
// never destroys it, which would leak across this mod's ScriptEngine reloads.
internal sealed class CanvasScrollbar : CanvasNode
{
    private const float MinGripHeight = 24f;

    private readonly CanvasButton _dragSurface;
    private readonly CanvasImage _grip;

    public CanvasScrollView? ScrollView { get; set; }

    protected override bool Interactable => true;

    public CanvasScrollbar(string name, UICommon ui) : base(name)
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
        if (ScrollView == null)
        {
            return;
        }

        float mouseY = Screen.height - Input.mousePosition.y;
        if (mouseY >= _grip.Position.y && mouseY <= _grip.Position.y + _grip.Size.y)
        {
            // Clicked on the grip itself - that's a drag, not a page-jump; let OnDragged handle it.
            return;
        }

        float travel = Mathf.Max(1f, Size.y - _grip.Size.y);
        float y = Mathf.Clamp(mouseY - Position.y - _grip.Size.y / 2f, 0f, travel);
        ScrollView.SetScrollPercentage(y / travel);
    }

    private void OnDragged(PointerEventData eventData)
    {
        if (ScrollView == null)
        {
            return;
        }

        float travel = Mathf.Max(1f, Size.y - _grip.Size.y);
        float y = Mathf.Clamp(_grip.LocalPosition.y - eventData.delta.y, 0f, travel);
        ScrollView.SetScrollPercentage(y / travel);
    }

    // Whether ActiveSelf *should* be true right now, given the paired scroll view's current content
    // height - a plain query, not a mutation. The scrollbar can never decide this for itself: this
    // class's own OnUpdate (Refresh, below) only runs while ActiveSelf is already true (see
    // FsmMasterPlugin.Update's subtree walk, which skips Update() on inactive nodes so a hidden
    // panel's per-frame work doesn't run) - if Refresh set ActiveSelf = false itself, nothing would
    // ever run again to set it back to true. The owning panel (FsmActiveStatePanel, whose own
    // OnUpdate always runs) polls this every frame and assigns ActiveSelf accordingly instead.
    public bool ShouldBeVisible => ScrollView?.Content != null && ScrollView.Content.Size.y > Size.y;

    // Recomputes grip size/position every frame from the paired scroll view's current content height
    // and scroll offset - covers mouse-wheel-driven scrolling (which doesn't go through this class at
    // all) and a content rebuild changing the scrollable height, not just drags on the grip itself.
    // Only ever runs while this node is already active (see ShouldBeVisible above), which is fine -
    // nothing reads the grip's position/size while the scrollbar itself is hidden.
    private void Refresh()
    {
        if (ScrollView?.Content == null)
        {
            return;
        }

        float contentHeight = ScrollView.Content.Size.y;
        float gripHeight = Mathf.Clamp(Size.y * Size.y / Mathf.Max(1f, contentHeight), MinGripHeight, Size.y);
        _grip.Size = new Vector2(Size.x, gripHeight);

        float scrollable = ScrollView.GetScrollableHeight();
        float percentage = scrollable > 0f ? Mathf.Clamp01(-ScrollView.Content.LocalPosition.y / scrollable) : 0f;
        _grip.LocalPosition = new Vector2(0f, percentage * (Size.y - gripHeight));
    }
}
