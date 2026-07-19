using System.Collections.Generic;
using UnityEngine;

namespace FsmMaster;

// Row of FSM tabs below the button row - one composite widget per FsmTabManager.Tabs entry, with a
// close "x" button and a minimize "-" button pinned to its top-right corner (minimize just left of
// close) and a pin toggle-dot (the same widget/size as the "add to monitor" dots in
// FsmActiveStatePanel - see UICommon.DotSize) pinned to its top-left. A horizontal scrollbar sits
// above the tab row and only appears once there are more tabs than fit in the available width; the
// tabs themselves live inside a CanvasHorizontalScrollStrip's clipped viewport rather than being
// added straight to this panel, so overflow tabs are hidden/scrollable instead of trailing off the
// edge of the right panel.
internal sealed class FsmTabStripPanel : CanvasPanel
{
    private const float TabWidth = 170f;
    private const float TabGap = 4f;
    private const float CloseButtonSize = 14f;
    private const float MinimizeButtonSize = 14f;
    private const float TabButtonGap = 2f;
    private const float ScrollbarHeight = 10f;
    private const float ScrollbarGap = 2f;

    // Extra room past a tab's own raw measured text width (CanvasText.TextComponent.preferredWidth)
    // when it's the active tab - see LayoutWidgets. Without this, the widest line's glyphs would sit
    // flush against the tab's own edges/border.
    private const float SelectedTabTextPadding = 16f;

    public const float TabRowHeight = 40f;
    public const float TotalHeight = ScrollbarHeight + ScrollbarGap + TabRowHeight;

    private readonly UICommon _ui;
    private readonly FsmTabManager _tabManager;
    private readonly CanvasHorizontalScrollbar _scrollbar;
    private readonly CanvasHorizontalScrollStrip _scrollStrip;
    private readonly CanvasPanel _content;
    private readonly List<TabWidget> _tabWidgets = new();
    private readonly List<FsmTabState> _lastTabsSnapshot = new();

    public FsmTabStripPanel(UICommon ui, FsmTabManager tabManager) : base("TabStrip")
    {
        _ui = ui;
        _tabManager = tabManager;

        _scrollbar = Add(new CanvasHorizontalScrollbar("Scrollbar", ui));
        _scrollbar.LocalPosition = Vector2.zero;

        _scrollStrip = Add(new CanvasHorizontalScrollStrip("ScrollStrip"));
        _scrollStrip.LocalPosition = new Vector2(0f, ScrollbarHeight + ScrollbarGap);
        _content = _scrollStrip.SetContent(new CanvasPanel("Content"));

        _scrollbar.Source = _scrollStrip;

        // This panel is always active whenever the right panel is, so its own OnUpdate is the one
        // safe place to decide the scrollbar's ActiveSelf and rebuild the tab widgets - see
        // CanvasHorizontalScrollbar.ShouldBeVisible for why the scrollbar can't make that call itself.
        OnUpdate += Refresh;
    }

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        Layout();
    }

    public override void Build(Transform? rootParent = null)
    {
        Layout();
        base.Build(rootParent);
    }

    private void Layout()
    {
        _scrollbar.Size = new Vector2(Size.x, ScrollbarHeight);
        _scrollStrip.Size = new Vector2(Size.x, TabRowHeight);
    }

    // Rebuilds the widget list only when the open-tab set actually changed (not every frame) - always
    // refreshes each widget's Toggled/pin state, its width/position (a tab's width depends on whether
    // it's currently the active one - see LayoutWidgets), and the scrollbar's visibility, all of which
    // are cheap and need to track the active tab/content width every frame regardless.
    private void Refresh()
    {
        List<FsmTabState> tabs = _tabManager.Tabs;
        bool changed = tabs.Count != _lastTabsSnapshot.Count;
        if (!changed)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (!ReferenceEquals(tabs[i], _lastTabsSnapshot[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            RebuildWidgets(tabs);
            _lastTabsSnapshot.Clear();
            _lastTabsSnapshot.AddRange(tabs);
        }

        FsmTabState? active = _tabManager.GetActive();
        foreach (TabWidget widget in _tabWidgets)
        {
            widget.SelectButton.Toggled = ReferenceEquals(widget.Tab, active);
            widget.PinDot.On = widget.Tab.IsPinned;
            widget.MinimizeButton.Toggled = widget.Tab.IsMinimized;
        }

        LayoutWidgets(active);

        _scrollbar.ActiveSelf = _scrollbar.ShouldBeVisible;
    }

    private void RebuildWidgets(List<FsmTabState> tabs)
    {
        foreach (TabWidget widget in _tabWidgets)
        {
            widget.Root.Destroy();
            _content.Remove(widget.Root);
        }

        _tabWidgets.Clear();

        int index = 0;
        foreach (FsmTabState tab in tabs)
        {
            TabWidget widget = CreateTabWidget(tab, index);
            widget.Root.Size = new Vector2(TabWidth, TabRowHeight);
            widget.Root.Build();

            // Measured once here (text never changes after a tab is opened - see
            // FsmTabManager.OpenOrFocus) rather than every frame in LayoutWidgets, which would mean a
            // TextGenerator call per tab per frame for no benefit.
            widget.PreferredTextWidth = widget.SelectButton.Text.TextComponent?.preferredWidth ?? TabWidth;

            index++;
            _tabWidgets.Add(widget);
        }
    }

    // Every tab defaults to TabWidth; the active tab alone grows past it (never shrinks below it) just
    // enough to fit its own two-line label without clipping, without permanently widening every
    // closed/inactive tab too. Runs every frame (not just after RebuildWidgets) since switching the
    // active tab alone - no open/close - still needs this to re-run, and it's cheap: no GameObject
    // churn, just position/size math over however many tabs are currently open.
    private void LayoutWidgets(FsmTabState? active)
    {
        float x = 0f;
        foreach (TabWidget widget in _tabWidgets)
        {
            bool isActive = ReferenceEquals(widget.Tab, active);
            float width = isActive ? Mathf.Max(TabWidth, widget.PreferredTextWidth + SelectedTabTextPadding) : TabWidth;

            widget.Root.LocalPosition = new Vector2(x, 0f);
            widget.Root.Size = new Vector2(width, TabRowHeight);
            widget.SelectButton.Size = new Vector2(width, TabRowHeight);
            widget.CloseButton.LocalPosition = new Vector2(width - CloseButtonSize - 2f, 2f);
            widget.MinimizeButton.LocalPosition = new Vector2(width - CloseButtonSize - TabButtonGap - MinimizeButtonSize - 2f, 2f);

            x += width + TabGap;
        }

        // Floors at the viewport's own width so a handful of tabs (narrower than the strip) never
        // register as "overflowing" - GetScrollableWidth/ShouldBeVisible both key off this.
        _content.Size = new Vector2(Mathf.Max(_scrollStrip.Size.x, Mathf.Max(0f, x - TabGap)), TabRowHeight);
    }

    private TabWidget CreateTabWidget(FsmTabState tab, int index)
    {
        var root = _content.Add(new CanvasPanel($"Tab{index}"));

        CanvasButton selectButton = root.Add(new CanvasButton("Select", _ui));
        selectButton.LocalPosition = Vector2.zero;
        selectButton.Size = new Vector2(TabWidth, TabRowHeight);
        selectButton.Text.Text = $"{tab.GameObjectNameForLabel}\n{tab.FsmNameForLabel}";
        selectButton.Text.Alignment = TextAnchor.MiddleCenter;
        selectButton.OnClicked += () => _tabManager.Focus(tab);

        // Added after (and so rendered/hit-tested on top of) selectButton - a click within its small
        // bounds is what a Unity GraphicRaycaster resolves to, not the larger select button underneath.
        // Its own LocalPosition.x is re-derived from the tab's actual current width every frame (see
        // LayoutWidgets), since that width isn't fixed once the active tab can grow past TabWidth.
        CanvasButton closeButton = root.Add(new CanvasButton("Close", _ui));
        closeButton.LocalPosition = new Vector2(TabWidth - CloseButtonSize - 2f, 2f);
        closeButton.Size = new Vector2(CloseButtonSize, CloseButtonSize);
        closeButton.NormalTint = _ui.ErrorColor;
        closeButton.ToggledTint = _ui.ErrorColor;
        closeButton.Tint = _ui.ErrorColor;
        closeButton.Text.Text = "x";
        closeButton.Text.Color = Color.white;
        closeButton.OnClicked += () => _tabManager.Close(tab);

        // Sits just left of Close, same reasoning for draw/hit-test order and per-frame repositioning.
        // Toggles this tab's own FsmTabState.IsMinimized rather than the old single global graph
        // Hide/Show button that used to live in the button row above the tab strip - suppresses only
        // this tab's graph drawing (see FsmGraphOverlay.OnGUI), leaving every other open tab's graph
        // (and this tab's own selection/pin state) untouched.
        CanvasButton minimizeButton = root.Add(new CanvasButton("Minimize", _ui));
        minimizeButton.LocalPosition = new Vector2(TabWidth - CloseButtonSize - TabButtonGap - MinimizeButtonSize - 2f, 2f);
        minimizeButton.Size = new Vector2(MinimizeButtonSize, MinimizeButtonSize);
        // Yellow while the tab's graph is showing (not minimized), grey once toggled off (minimized) -
        // the background itself is the on/off indicator, same as CanvasButton.Toggled's usual
        // ButtonActive/ButtonNormal swap, just with this button's own warning/read-only palette instead.
        minimizeButton.NormalTint = _ui.WarningColor;
        minimizeButton.ToggledTint = _ui.ReadOnlyColor;
        minimizeButton.Tint = tab.IsMinimized ? minimizeButton.ToggledTint : minimizeButton.NormalTint;
        minimizeButton.Text.Text = "-";
        minimizeButton.Text.Color = Color.white;
        minimizeButton.Toggled = tab.IsMinimized;
        minimizeButton.OnClicked += () => tab.IsMinimized = !tab.IsMinimized;

        // Opposite corner from Close, same reasoning for draw/hit-test order - a circular toggle-dot
        // (matching the "add to monitor" dots elsewhere, see UICommon.DotSize) rather than a labeled
        // button, so pinning reads as the same kind of on/off control throughout the panel. Toggling
        // this doesn't change tab selection or close anything - see FsmTabState.IsPinned and
        // FsmGraphOverlay.OnGUI for what pinning actually does to the graph. Its own position never
        // depends on the tab's width (top-left corner, fixed offset), so unlike closeButton it's set
        // once here and never revisited by LayoutWidgets.
        CanvasToggleDot pinDot = root.Add(new CanvasToggleDot("Pin", _ui));
        pinDot.LocalPosition = new Vector2(2f, 2f);
        pinDot.Size = new Vector2(_ui.DotSize, _ui.DotSize);
        pinDot.On = tab.IsPinned;
        pinDot.OnClicked += () => tab.IsPinned = !tab.IsPinned;

        return new TabWidget(tab, root, selectButton, closeButton, minimizeButton, pinDot);
    }

    private sealed class TabWidget
    {
        public FsmTabState Tab { get; }
        public CanvasPanel Root { get; }
        public CanvasButton SelectButton { get; }
        public CanvasButton CloseButton { get; }
        public CanvasButton MinimizeButton { get; }
        public CanvasToggleDot PinDot { get; }
        public float PreferredTextWidth { get; set; }

        public TabWidget(FsmTabState tab, CanvasPanel root, CanvasButton selectButton, CanvasButton closeButton, CanvasButton minimizeButton, CanvasToggleDot pinDot)
        {
            Tab = tab;
            Root = root;
            SelectButton = selectButton;
            CloseButton = closeButton;
            MinimizeButton = minimizeButton;
            PinDot = pinDot;
        }
    }
}
