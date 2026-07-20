using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FsmMaster;

// Read-only, freely-draggable HUD listing whatever FsmVariableTracker entries have been toggled on
// via the dots in FsmActiveStatePanel (variable/action-field rows, state header - the state header dot
// tracks whichever state is the FSM's live active state at the moment it's clicked, not the selected
// state the panel happens to be showing) - docked bottom-right by default, structural template
// borrowed from FsmRightPanel (background/border, scrollable content list, Reposition/
// ReflowOnResolutionChange). Unlike FsmRightPanel, this panel survives the toggle-minimal-view hotkey
// rather than disappearing with it - see FsmMasterPlugin.Update, which drives Locked from
// FsmGraphOverlay.SelectionUiVisible instead of gating ActiveSelf on it.
internal sealed class FsmMonitorPanel : CanvasPanel
{
    private const int PanelWidth = 355;
    private const int PanelHeight = 231;
    private const int ScreenMargin = 0;
    private const float TitleBarHeightDesign = 20f;
    private const float RowHeightDesign = 18f;
    private const float ScrollbarWidthDesign = 10f;
    private const float ResizeHandleSizeDesign = 14f;
    private const int MinPanelWidth = 150;
    private const int MinPanelHeight = 100;

    private static float TitleBarHeight => UICommon.ScaleHeight(TitleBarHeightDesign);
    private static float RowHeight => UICommon.ScaleHeight(RowHeightDesign);
    private static float ScrollbarWidth => UICommon.ScaleWidth(ScrollbarWidthDesign);
    private static float ResizeHandleSize => UICommon.ScaleWidth(ResizeHandleSizeDesign);
    private static float MinWidth => UICommon.ScaleWidth(MinPanelWidth);
    private static float MinHeight => UICommon.ScaleHeight(MinPanelHeight);

    private readonly UICommon _ui;
    private readonly CanvasImage _background;
    private readonly CanvasButton _dragSurface;
    private readonly CanvasText _titleText;
    private readonly CanvasScrollView _scrollView;
    private readonly CanvasScrollbar _scrollbar;
    private readonly CanvasResizeHandle _resizeHandle;
    private readonly FsmPanelLayoutConfig _layout;

    private readonly List<CanvasText> _valueTexts = new();

    // Parallel to _valueTexts - the CurrentValue string last written into each row, so RefreshRows can
    // skip the format-string allocation and CanvasText.Text assignment for a row whose live value
    // hasn't actually changed since the previous frame, instead of redoing both unconditionally every
    // single frame regardless of whether anything changed.
    private readonly List<string> _lastValues = new();
    private int _lastTrackerVersion = -1;
    private bool _locked;

    private int _lastScreenWidth = -1;
    private int _lastScreenHeight = -1;

    public FsmMonitorPanel(UICommon ui, FsmVariableTracker tracker, FsmPanelLayoutConfig layout) : base("FsmMonitorPanel")
    {
        _ui = ui;
        _layout = layout;

        _background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        _background.AddBorder(ui.PanelBorder);

        _dragSurface = Add(new CanvasButton("DragSurface", ui) { Tint = Color.clear });
        _dragSurface.RemoveBorder();

        _titleText = Add(new CanvasText("Title", ui) { Text = "Monitor" });
        _titleText.Font = ui.HeaderFont;
        _titleText.FontStyle = FontStyle.Bold;
        _titleText.Alignment = TextAnchor.MiddleLeft;

        _scrollView = Add(new CanvasScrollView("ScrollView"));
        _scrollView.SetContent(new CanvasPanel("Content"));

        _scrollbar = Add(new CanvasScrollbar("Scrollbar", ui) { ScrollView = _scrollView });

        _resizeHandle = Add(new CanvasResizeHandle("ResizeHandle", ui));
        _resizeHandle.OnDragDelta += OnResizeDragged;
        _resizeHandle.OnDragEnd += OnResizeDragEnded;

        Reposition();
        Layout();

        OnUpdate += () =>
        {
            _scrollbar.ActiveSelf = _scrollbar.ShouldBeVisible;
            ReflowOnResolutionChange();
        };
    }

    public bool Locked
    {
        set
        {
            if (_locked == value)
            {
                return;
            }

            _locked = value;
            _dragSurface.ActiveSelf = !value;
            _resizeHandle.ActiveSelf = !value;
            _background.Tint = value ? Color.clear : _ui.PanelBackground;
        }
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        _dragSurface.AddEventTrigger(EventTriggerType.Drag, OnDragged);
        _dragSurface.AddEventTrigger(EventTriggerType.EndDrag, OnDragEnded);
    }

    // Rebuilds row structure only when the tracker's tracked set actually changed since last check
    // (FsmVariableTracker.Version), then re-reads every row's current value every frame regardless -
    // GetTracked()'s returned values are re-resolved fresh each call (against a reused buffer, not
    // freshly allocated), so refreshing by index against that freshly-resolved list (not a captured
    // closure per row) is what stays correct. _lastValues then gates the actual Text write so an
    // unchanged value doesn't cost a format-string allocation every single frame.
    public void RefreshRows(FsmVariableTracker tracker)
    {
        IReadOnlyList<TrackedVariableValue> values = tracker.GetTracked();

        if (tracker.Version != _lastTrackerVersion)
        {
            _lastTrackerVersion = tracker.Version;
            RebuildRows(values);
            return;
        }

        for (int i = 0; i < values.Count && i < _valueTexts.Count; i++)
        {
            string currentValue = values[i].CurrentValue;
            if (_lastValues[i] == currentValue)
            {
                continue;
            }

            _lastValues[i] = currentValue;
            _valueTexts[i].Text = $"{values[i].DisplayLabel}: {currentValue}";
        }
    }

    private void RebuildRows(IReadOnlyList<TrackedVariableValue> values)
    {
        var content = (CanvasPanel)_scrollView.Content!;
        content.ClearChildren();
        _valueTexts.Clear();
        _lastValues.Clear();

        float y = 0f;
        int count = 0;
        foreach (TrackedVariableValue value in values)
        {
            CanvasText row = content.Add(new CanvasText($"Row{count++}", _ui));
            row.Overflow = HorizontalWrapMode.Overflow;
            row.LocalPosition = new Vector2(4f, y);
            row.Size = new Vector2(Mathf.Max(0f, _scrollView.Size.x - 8f), RowHeight);
            row.Text = $"{value.DisplayLabel}: {value.CurrentValue}";
            row.Build();

            _valueTexts.Add(row);
            _lastValues.Add(value.CurrentValue);
            y += RowHeight;
        }

        content.Size = new Vector2(_scrollView.Size.x, y);
    }

    private void OnDragged(PointerEventData eventData)
    {
        if (_locked)
        {
            return;
        }

        Vector2 next = new(LocalPosition.x + eventData.delta.x, LocalPosition.y - eventData.delta.y);
        next.x = Mathf.Clamp(next.x, 0f, Mathf.Max(0f, Screen.width - Size.x));
        next.y = Mathf.Clamp(next.y, 0f, Mathf.Max(0f, Screen.height - Size.y));
        LocalPosition = next;
    }

    // Persists once per drag gesture (not per-frame) - see CanvasResizeHandle.OnDragEnd's own comment
    // for why, and FsmRightPanel.OnDragEnded for the matching pattern there.
    private void OnDragEnded(PointerEventData _)
    {
        if (_locked)
        {
            return;
        }

        _layout.Position.Value = LocalPosition;
    }

    // Bottom-LEFT corner drag. This panel is docked flush against BOTH the right and bottom edges of
    // the screen (see Reposition), so a naive "grow by extending right/down" has essentially no room
    // on either axis before hitting the screen edge - width instead grows/shrinks by keeping the RIGHT
    // edge fixed and moving the left edge (dragging left grows, right shrinks), and height by keeping
    // the BOTTOM edge fixed and moving the top edge (dragging up grows, down shrinks) - mirroring
    // FsmRightPanel's own bottom-left resize fix. The one wrinkle versus FsmRightPanel: that panel only
    // needed this treatment on its width (it's merely top-pinned, not bottom-pinned, so its height
    // already had room to grow downward - matching where its handle sits). Here BOTH axes are pinned
    // on the same side the handle visually occupies for height (bottom), so growing taller keeps the
    // handle's on-screen position fixed instead of following the cursor - purely cosmetic, since Unity
    // keeps delivering Drag events against whichever object was originally pressed regardless of
    // whether it visually tracks the pointer, so the resize itself still responds correctly throughout.
    private void OnResizeDragged(Vector2 delta)
    {
        if (_locked)
        {
            return;
        }

        float rightEdge = LocalPosition.x + Size.x;
        float bottomEdge = LocalPosition.y + Size.y;

        float newWidth = Mathf.Clamp(Size.x - delta.x, MinWidth, Mathf.Max(MinWidth, rightEdge));
        float newHeight = Mathf.Clamp(Size.y + delta.y, MinHeight, Mathf.Max(MinHeight, bottomEdge));

        LocalPosition = new Vector2(rightEdge - newWidth, bottomEdge - newHeight);
        Size = new Vector2(newWidth, newHeight);
        Layout();
    }

    // Persists once per resize gesture, matching OnDragEnded above - both Size and LocalPosition, since
    // this resize's own right/bottom-edge-fixed math (see OnResizeDragged) moves LocalPosition too.
    private void OnResizeDragEnded()
    {
        if (_locked)
        {
            return;
        }

        _layout.Position.Value = LocalPosition;
        _layout.Size.Value = Size;
    }

    private void Layout()
    {
        _background.Size = Size;

        _dragSurface.LocalPosition = Vector2.zero;
        _dragSurface.Size = new Vector2(Size.x, TitleBarHeight);

        _titleText.LocalPosition = new Vector2(6f, 0f);
        _titleText.Size = new Vector2(Mathf.Max(0f, Size.x - 12f), TitleBarHeight);

        // Bottom bound is pulled up by ResizeHandleSize so the scroll view/scrollbar never share
        // screen space with the resize handle in the corner - without this, the handle (rendered last,
        // so it hit-tests on top) would sit directly over the bottom of the scrollbar's own track,
        // making its grip hard or impossible to grab whenever it scrolls near the bottom.
        float scrollY = TitleBarHeight + 2f;
        float scrollHeight = Mathf.Max(0f, Size.y - scrollY - 4f - ResizeHandleSize);
        _scrollView.LocalPosition = new Vector2(4f, scrollY);
        _scrollView.Size = new Vector2(Mathf.Max(0f, Size.x - ScrollbarWidth - 8f), scrollHeight);

        _scrollbar.LocalPosition = new Vector2(Size.x - ScrollbarWidth - 2f, scrollY);
        _scrollbar.Size = new Vector2(ScrollbarWidth, scrollHeight);

        _resizeHandle.LocalPosition = new Vector2(0f, Size.y - ResizeHandleSize);
        _resizeHandle.Size = new Vector2(ResizeHandleSize, ResizeHandleSize);
    }

    // Only repositions/relays out on an actual resolution change (matching FsmRightPanel's own
    // ReflowOnResolutionChange) - a manual drag is never clobbered by this on an unchanged resolution.
    private void ReflowOnResolutionChange()
    {
        if (Screen.width == _lastScreenWidth && Screen.height == _lastScreenHeight)
        {
            return;
        }

        Reposition();
        Layout();
    }

    // Falls back to the screen-relative default corner only when nothing's been saved yet
    // (FsmPanelLayoutConfig's own (-1, -1) sentinel) - otherwise restores the user's last dragged/
    // resized layout, clamped back into the current screen bounds (same clamp OnDragged already
    // applies) so a saved position/size from a larger resolution never leaves the panel off-screen.
    private void Reposition()
    {
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;

        Size = _layout.HasSavedSize
            ? new Vector2(Mathf.Max(MinWidth, _layout.Size.Value.x), Mathf.Max(MinHeight, _layout.Size.Value.y))
            : new Vector2(UICommon.ScaleWidth(PanelWidth), UICommon.ScaleHeight(PanelHeight));

        Vector2 defaultPosition = new(
            Screen.width - UICommon.ScaleWidth(ScreenMargin) - Size.x,
            Screen.height - UICommon.ScaleHeight(ScreenMargin) - Size.y);
        Vector2 desiredPosition = _layout.HasSavedPosition ? _layout.Position.Value : defaultPosition;
        LocalPosition = new Vector2(
            Mathf.Clamp(desiredPosition.x, 0f, Mathf.Max(0f, Screen.width - Size.x)),
            Mathf.Clamp(desiredPosition.y, 0f, Mathf.Max(0f, Screen.height - Size.y)));
    }
}
