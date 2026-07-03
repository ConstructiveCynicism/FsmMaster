using System.Collections.Generic;
using UnityEngine;

namespace FsmMaster;

// Row of FSM tabs below the button row - one composite widget per FsmTabManager.Tabs entry, with a
// close "x" button pinned to its top-right corner. A horizontal scrollbar sits above the tab row
// (per the original design) and only appears once there are more tabs than fit in the available
// width; the tabs themselves live inside a CanvasHorizontalScrollStrip's clipped viewport rather than
// being added straight to this panel, so overflow tabs are hidden/scrollable instead of trailing off
// the edge of the right panel.
internal sealed class FsmTabStripPanel : CanvasPanel
{
    private const float TabWidth = 140f;
    private const float TabGap = 4f;
    private const float CloseButtonSize = 14f;
    private const float ScrollbarHeight = 10f;
    private const float ScrollbarGap = 2f;

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

        _scrollbar.ScrollStrip = _scrollStrip;

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
    // refreshes each widget's Toggled/highlight state and the scrollbar's visibility, which are cheap
    // and need to track the active tab/content width every frame regardless.
    private void Refresh()
    {
        IReadOnlyList<FsmTabState> tabs = _tabManager.Tabs;
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
        }

        _scrollbar.ActiveSelf = _scrollbar.ShouldBeVisible;
    }

    private void RebuildWidgets(IReadOnlyList<FsmTabState> tabs)
    {
        foreach (TabWidget widget in _tabWidgets)
        {
            widget.Root.Destroy();
            _content.Remove(widget.Root);
        }

        _tabWidgets.Clear();

        float x = 0f;
        int index = 0;
        foreach (FsmTabState tab in tabs)
        {
            TabWidget widget = CreateTabWidget(tab, index);
            widget.Root.LocalPosition = new Vector2(x, 0f);
            widget.Root.Size = new Vector2(TabWidth, TabRowHeight);
            widget.Root.Build();

            x += TabWidth + TabGap;
            index++;
            _tabWidgets.Add(widget);
        }

        // Floors at the viewport's own width so a handful of tabs (narrower than the strip) never
        // register as "overflowing" - GetScrollableWidth/ShouldBeVisible both key off this.
        _content.Size = new Vector2(Mathf.Max(_scrollStrip.Size.x, x - TabGap), TabRowHeight);
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
        CanvasButton closeButton = root.Add(new CanvasButton("Close", _ui));
        closeButton.LocalPosition = new Vector2(TabWidth - CloseButtonSize - 2f, 2f);
        closeButton.Size = new Vector2(CloseButtonSize, CloseButtonSize);
        closeButton.Text.Text = "x";
        closeButton.OnClicked += () => _tabManager.Close(tab);

        return new TabWidget(tab, root, selectButton, closeButton);
    }

    private sealed class TabWidget
    {
        public FsmTabState Tab { get; }
        public CanvasPanel Root { get; }
        public CanvasButton SelectButton { get; }
        public CanvasButton CloseButton { get; }

        public TabWidget(FsmTabState tab, CanvasPanel root, CanvasButton selectButton, CanvasButton closeButton)
        {
            Tab = tab;
            Root = root;
            SelectButton = selectButton;
            CloseButton = closeButton;
        }
    }
}
