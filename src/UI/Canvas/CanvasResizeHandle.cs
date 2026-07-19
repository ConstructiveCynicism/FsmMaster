using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Small square grip meant to sit in a panel's bottom-right corner - dragging it fires OnDragDelta
// with each frame's raw pointer movement so the owning panel can grow/shrink its own Size. Built on
// CanvasButton purely for its border/hover-highlight affordance (same reuse-a-button-for-its-drag-
// surface trick FsmMonitorPanel's own title bar already uses), not for its click event.
internal sealed class CanvasResizeHandle : CanvasButton
{
    public event Action<Vector2>? OnDragDelta;

    // Fired once the drag gesture actually releases - lets an owning panel persist its final Size/
    // LocalPosition (e.g. to GlobalSettings) a single time per resize rather than on every per-frame
    // OnDragDelta call.
    public event Action? OnDragEnd;

    public CanvasResizeHandle(string name, UICommon ui) : base(name, ui)
    {
        Tint = ui.AccentColor;
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        AddEventTrigger(EventTriggerType.Drag, (PointerEventData e) => OnDragDelta?.Invoke(e.delta));
        AddEventTrigger(EventTriggerType.EndDrag, (PointerEventData _) => OnDragEnd?.Invoke());
    }
}
