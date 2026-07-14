using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using Silksong.FsmUtil;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// In-game IMGUI overlay for browsing live FSMs and their state graphs, toggled by the configurable
// toggle-overlay hotkey (BepInEx config, default "0"). Collects its own FsmSnapshot (see
// RefreshSnapshot) rather than sharing FsmMasterPlugin's - FsmMasterPlugin calls RefreshSnapshot for
// it on initial Awake and on every scene load (it already owns the SceneManager.sceneLoaded
// subscription for its own persisted-edit reapplication, so this reuses that rather than adding a
// second one here). The hotkey itself only flips visibility - it must never rebuild the snapshot or
// reset the current selection, since scene load is the only point PlayMakerFSM references can
// actually go stale.
internal sealed class FsmGraphOverlay
{
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 3f;
    private const float ZoomSpeed = 0.05f;
    private const float MinNodeWidth = 60f;
    private const float TitleBarHeight = 16f;
    private const float TransitionRowHeight = 16f;
    private const float GlobalPseudoNodeHeight = 22f;
    private const float GlobalPseudoNodeGap = 10f;
    private const float GlobalPseudoNodeOffset = 40f;
    private const float FitMargin = 60f;

    private const float NodeCornerRadius = 10f;
    private const float NodeBorderThickness = 2f;
    private const float NodeActiveOutlineThickness = 3f;
    private const float NodeSelectedOutlineThickness = 3f;
    private const float BezierControlOffset = 40f;
    private const float BezierTargetSegmentLength = 14f;
    private const int MinBezierSegments = 6;
    private const int MaxBezierSegments = 40;
    private const float DynamicFontPointSize = 12f;
    private const float TransitionLineThickness = 3f;
    private const float ActiveTransitionLineThickness = 6f;
    private const float SelectedTransitionOutlineMargin = 3f;

    // "Segoe UI" is only guaranteed present on Windows - CreateDynamicFontFromOSFont's string[]
    // overload picks the first installed name it finds, falling through to common Linux/Mac
    // equivalents ("DejaVu Sans" ships with most Linux distros, "Liberation Sans" is Fedora/RHEL's
    // default, "Arial" covers Mac and Wine-mapped fonts).
    private static readonly string[] DynamicFontNames = ["Segoe UI", "DejaVu Sans", "Liberation Sans", "Arial"];

    // Every named color this overlay draws with is now a live BepInEx ConfigEntry<Color> (see
    // FsmGraphColorConfig and the _colors field below) rather than a hardcoded constant, so the whole
    // palette can be retuned via Configuration Manager's color picker without a recompile.

    // Action-zone/drag-to-rebind tuning - fixed screen pixels, not scaled by zoom (see
    // TryHitTestTransitionLine's own comment on why click-precision tolerances stay fixed).
    private const float TransitionLineHitTolerance = 6f;
    private const float DragStartThreshold = 4f;
    private const double DoubleClickWindowSeconds = 0.35;

    private readonly ManualLogSource _logger;
    private readonly FsmEditManager _editManager;
    private readonly FsmTabManager _tabManager;
    private readonly ConfigEntry<KeyboardShortcut> _toggleOverlayHotkey;
    private readonly ConfigEntry<KeyboardShortcut> _toggleMinimalViewHotkey;
    private readonly FsmGraphColorConfig _colors;
    private readonly FsmGraphPerformanceConfig _performance;

    // Unlit, vertex-colored built-in shader shared by every GL batch this overlay draws (transition
    // lines and node chrome alike) instead of one GUI.DrawTexture call per line segment/rounded-rect
    // piece - ships with every Unity player, so this is not a new asset or package dependency.
    // _glMaterialFailed latches so a missing shader (should never happen on a real Unity player) only
    // logs once, not every frame.
    private Material? _glMaterial;
    private bool _glMaterialFailed;

    private Font? _dynamicFont;
    private GUIStyle? _titleStyle;
    private GUIStyle? _eventStyle;
    private GUIStyle? _globalEventStyle;
    private float _textStyleBuiltForZoom = -1f;

    private bool _isVisible;
    private bool _selectionUiVisible = true;
    private bool _graphVisible = true;
    private FsmSnapshot? _snapshot;

    // Lazily-collected full FsmInfo (the reflection-heavy state/action/field walk - see
    // FsmDataCollector.CollectFsmInfo), keyed by FsmKey and cleared on every RefreshSnapshot (scene
    // load). RefreshSnapshot itself only ever builds the cheap identity-only FsmSnapshot.Fsms list;
    // this cache is what lets ResolveFsmInfo run that expensive walk at most once per FsmKey per scene
    // visit, for only whichever FSM(s) a graph tab actually resolves against, rather than the previous
    // eager approach of collecting it for every single FSM the scene scan discovers up front.
    private readonly Dictionary<string, FsmInfo> _fullInfoCache = new();

    // FsmKeys ResolveFsmInfo has already scanned _snapshot.Fsms for and found nothing this scene visit
    // - cleared alongside _fullInfoCache on every RefreshSnapshot, same lifetime. _snapshot.Fsms is
    // frozen for the whole scene visit (only RefreshSnapshot ever replaces it), so "not found in this
    // snapshot" can never change until the next RefreshSnapshot also clears this set - without this, a
    // tab whose FSM isn't in the current scene (e.g. its owning enemy isn't in this room) re-ran the
    // full O(live FSM count) scan, including a regex-based GetFsmKey per FSM, every single frame for as
    // long as that tab stayed open, instead of failing fast after the first miss.
    private readonly HashSet<string> _notFoundThisScene = new();

    // Working copy of whichever tab is currently active - synced in from FsmTabState at the top of
    // OnGUI and back out at the bottom (see OnGUI), rather than threading an FsmTabState parameter
    // through every one of the ~20 call sites below that read pan/zoom while drawing the graph (GL
    // line/chrome batching, bezier sampling, corner-radius math, text sizing). This keeps all of that
    // existing, already-tuned rendering code unchanged while still giving each tab its own persisted
    // pan/zoom/selected-state across switches - the observable behavior the tab system needs.
    private string? _selectedStateName;
    private Vector2 _panWorldCenter;
    private float _zoom = 1f;

    // Exposed so FsmMasterPlugin can hand the same snapshot to FsmTabManager.RebindAfterRefresh
    // instead of collecting a second, independent one via FsmDataCollector after every
    // RefreshSnapshot call - matching the single-owner snapshot approach already used elsewhere.
    internal FsmSnapshot? CurrentSnapshot => _snapshot;

    // Resolves an open tab's FsmKey back to this frame's FsmInfo - used both internally (OnGUI) and
    // externally by FsmMasterPlugin.Update to feed FsmActiveStatePanel whichever FSM/state the active
    // tab points at.
    //
    // _snapshot is only rebuilt on RefreshSnapshot (initial Awake, each scene load), but Update/OnGUI
    // run every frame in between - during a scene transition, the previous scene's PlayMakerFSM
    // components can be destroyed by Unity before SceneManager.sceneLoaded actually fires and
    // RefreshSnapshot replaces this stale snapshot. A destroyed component isn't a null *reference*
    // (Unity's fake-null), so `fsm.Component == null` correctly detects it via Object's overridden
    // equality without touching the destroyed native object - but FsmIdentity.GetFsmKey immediately
    // reads component.gameObject, which throws NullReferenceException on a destroyed component. Skip
    // those entries rather than let one destroyed FSM anywhere in the snapshot crash every frame's
    // Update() and, with it, everything after this call in FsmMasterPlugin.Update (the whole uGUI
    // right panel's per-frame CollectSubtree() walk never runs while this keeps throwing).
    internal FsmInfo? ResolveFsmInfo(string fsmKey)
    {
        if (_snapshot == null)
        {
            return null;
        }

        // Re-checked on every call (not just on the cache-miss path below) so a component destroyed
        // mid-scene, after already being collected and cached once this scene visit, still goes back
        // to returning null the same way the cache-miss path's own identity.Component == null check
        // does - matching this method's original per-call safety before the lazy cache existed.
        if (_fullInfoCache.TryGetValue(fsmKey, out FsmInfo? cached))
        {
            return cached.Component == null ? null : cached;
        }

        if (_notFoundThisScene.Contains(fsmKey))
        {
            return null;
        }

        foreach (FsmIdentityInfo identity in _snapshot.Fsms)
        {
            if (identity.Component == null)
            {
                continue;
            }

            if (FsmIdentity.GetFsmKey(identity.Component) == fsmKey)
            {
                FsmInfo full = FsmDataCollector.CollectFsmInfo(identity.Component);
                _fullInfoCache[fsmKey] = full;
                return full;
            }
        }

        _notFoundThisScene.Add(fsmKey);
        return null;
    }

    // Hook-based (not polled) tracking of which states each visible FSM has actually entered - see its
    // own header comment for why a per-frame Fsm.ActiveStateName poll alone isn't enough. Owned here
    // (not shared with the rest of the plugin) since only this overlay's own rendering ever reads it.
    private readonly FsmActiveStateTracker _activeStateTracker = new();

    private bool _isPanning;

    // Every piece of per-FSM layout/render state that used to live in singleton fields here, one
    // instance per FsmKey (see _layoutCachesByFsmKey below) - this is what lets a pinned tab's graph
    // stay drawn (frozen, from its own cache) while a different tab is active, instead of the single
    // shared cache the overlay used back when it only ever drew one FSM at a time.
    private sealed class GraphLayoutCache
    {
        // Node/edge layout cache - rebuilt only when the FSM selection, pan, zoom, or canvas size
        // changes. Unity dispatches OnGUI once per queued input event (several times per rendered
        // frame); recomputing this from scratch on every single call was the confirmed source of a
        // GC stutter, so it's now cache-gated and only ever rebuilt when something actually moved.
        public Dictionary<string, NodeLayout>? NodeLayoutCache;
        public List<GlobalPseudoNodeLayout>? GlobalPseudoNodeCache;

        // Bumped every time the layout cache above is actually rebuilt - lets DrawCachedGraph's own
        // chrome buffer cache (see ChromeCacheLayoutVersion) tell "layout is still what it built its
        // geometry against" apart from "layout object reference happens to be the same," without
        // re-diffing every field DrawGraph's own cacheStale check already compares.
        public int NodeLayoutVersion;

        // Keyed on the live PlayMakerFSM component reference, not the owning tab's FsmKey - FsmKey is
        // a stable string derived from object/FSM name, so it stays identical across a tab reconnecting
        // to a different live instance in a newly-loaded scene (see FsmTabManager.RebindAfterRefresh).
        // Gating on the string alone meant a reconnect never rebuilt this cache, leaving the graph
        // showing stale layout built from the previous (now-destroyed) instance's FsmState objects.
        public PlayMakerFSM? LayoutCacheComponent;
        public Vector2 LayoutCachePanCenter;
        public float LayoutCacheZoom;
        public Rect LayoutCacheCanvasRect;

        // Compared against FsmEditManager.EditGeneration - catches a live edit made from outside this
        // overlay (the right panel's Save/Load/Undo/Reset buttons, or a saved edit set reapplied on
        // scene load) that none of the four fields above would otherwise notice, since none of
        // pan/zoom/component/canvasRect necessarily changed.
        public int LayoutCacheEditGeneration = -1;

        // Reused per-Repaint scratch buffer of (already-tessellated polyline, color, base thickness,
        // arrowhead length, outline margin) entries - filled while walking pseudo-nodes/edges (which
        // still draw their labels via GUI as before) and then drawn as a single GL batch (see
        // DrawLineBufferGL), instead of allocating a new list every frame. Thickness is stored per-line
        // (not a single shared value) so the active state's outgoing transitions can draw thicker than
        // the rest of the graph. ArrowLength is likewise stored explicitly per-line rather than derived
        // from Thickness inside the Emit* methods - the selected-state outline pass draws a wider line
        // than its underlying transition but still wants an arrowhead sized off that transition's own
        // thickness, not one that's rescaled by the outline's inflated stroke width. OutlineMargin is
        // zero for every ordinary line; the selected-state outline entry sets it to a constant distance
        // and every vertex of its arrowhead is pushed outward from the triangle's own centroid by that
        // distance (see EmitThickArrowhead) - a uniform "grow outward in every direction" margin, so it
        // reads as a consistent border around the whole arrow (shaft and head alike) instead of the
        // shaft's own halo it would otherwise get from Thickness alone, which fades to nothing right at
        // the point where the head is thinnest (the tip).
        public readonly List<(Vector2[] Points, Color Color, float Thickness, float ArrowLength, float OutlineMargin)> LineDrawBuffer = new();

        // Reused per-Repaint scratch buffer of (vertex position, color) pairs, 3 entries per triangle -
        // holds every node/pseudo-node chrome shape (backgrounds, outline rings, rounded corners) for
        // the frame, gathered while walking nodes/rows (which still draw their labels via GUI as
        // before) and then drawn as a single GL batch (see FlushChromeBufferGL), replacing what used to
        // be up to ~7 GUI.DrawTexture calls per rounded rect (flat fills plus a baked corner-mask
        // texture per corner).
        public readonly List<(Vector2 Position, Color Color)> ChromeVertexBuffer = new();

        // Everything DrawCachedGraph's node/pseudo-node chrome gathering pass actually reads that isn't
        // already covered by NodeLayoutVersion (pan/zoom/canvas/selection/edit-generation) - tracked
        // alongside it so that pass can be skipped (leaving ChromeVertexBuffer exactly as the previous
        // Repaint left it) on every frame where none of it has actually changed, rather than
        // re-tessellating every rounded rect on the graph from scratch on every single Repaint
        // regardless. This is the cost Detailed box style pays that Standard mostly doesn't (see
        // GraphBoxStyle) - a state box with several transition rows emits multiple
        // AddRoundedRectToChromeBuffer calls, each an 8-segment trig-driven fan per rounded corner, and
        // a large FSM's worth of that redone every frame whether or not anything actually moved was the
        // dominant remaining Detailed-mode cost once per-frame work was already gated to Repaint-only
        // and off-screen-culled (see the comments earlier in DrawCachedGraph). activeStateName is read
        // live (see DrawCachedGraph) since it drives the active-halo ring and title color and changes
        // independently of everything NodeLayoutVersion tracks; the selected state and BoxStyle are
        // likewise read live rather than folded into edit generation. ConfigGeneration (see
        // FsmGraphPerformanceConfig) is what keeps a color retuned live via Configuration Manager from
        // going stale here for longer than one frame. Dimming (see ApplyDim) is deliberately NOT part
        // of this cache key - it's applied as a pure per-flush color multiplier at draw time, so the
        // same cached buffer contents are correct whether or not this pass happens to be dimmed.
        public int ChromeCacheLayoutVersion = -1;
        public Rect ChromeCacheVisibleRect;
        public string? ChromeCacheActiveStateName;
        public string? ChromeCacheSelectedStateName;
        public GraphBoxStyle ChromeCacheBoxStyle;
        public int ChromeCacheConfigGeneration = -1;
    }

    // One GraphLayoutCache per FsmKey that's ever been drawn this scene visit (the active tab, plus
    // every pinned tab, whether or not it's currently the active one) - cleared wholesale on
    // InvalidateGraphCaches (scene load), same lifetime as _fullInfoCache. _currentCache always points
    // at whichever entry the method currently running is operating on - set once at the top of
    // DrawGraph for the FSM being drawn this pass, then left untouched by every helper method below it
    // (RebuildNodeLayoutCache, DrawCachedGraph, the hit-testing/drag methods, etc.), which all still
    // read/write through it exactly as they used to read/write the old singleton fields directly.
    private readonly Dictionary<string, GraphLayoutCache> _layoutCachesByFsmKey = new();
    private GraphLayoutCache _currentCache = new();

    private GraphLayoutCache GetOrCreateLayoutCache(string fsmKey)
    {
        if (!_layoutCachesByFsmKey.TryGetValue(fsmKey, out GraphLayoutCache? cache))
        {
            cache = new GraphLayoutCache();
            _layoutCachesByFsmKey[fsmKey] = cache;
        }

        return cache;
    }

    // Timestamp/name of the last node MouseDown, for double-click-to-force-state detection - not part of
    // the tab-synced working-field set above, since it's purely a transient input-timing detail, never
    // something a tab needs to remember across a switch.
    private double _lastNodeClickTime = double.NegativeInfinity;
    private string? _lastNodeClickName;

    // One-shot "please scroll the Actions panel to this action index" request, synced out to the active
    // tab's own PendingScrollActionIndex at the tail of OnGUI (mirrors _selectedStateName's sync-out) and
    // cleared immediately after, since (unlike selection) this is consumed once by FsmMasterPlugin.Update
    // rather than persisted. Left null for a cross-tab (global transition) match, which instead writes
    // directly onto the OTHER tab's own PendingScrollActionIndex/SelectedStateName - see the global pseudo
    // line click handling.
    private int? _pendingScrollActionIndex;

    // Endpoint drag-to-rebind state - null OwningStateName means the anchor being dragged is a global
    // transition's pseudo-node (no real originating state to relocate away from a plain retarget, only a
    // rebind onto another event node - see HandleTransitionDrag).
    private DraggingTransition? _draggingTransition;

    public FsmGraphOverlay(
        ManualLogSource logger,
        FsmEditManager editManager,
        FsmTabManager tabManager,
        ConfigEntry<KeyboardShortcut> toggleOverlayHotkey,
        ConfigEntry<KeyboardShortcut> toggleMinimalViewHotkey,
        FsmGraphColorConfig colors,
        FsmGraphPerformanceConfig performance,
        bool startVisible = false)
    {
        _logger = logger;
        _editManager = editManager;
        _tabManager = tabManager;
        _toggleOverlayHotkey = toggleOverlayHotkey;
        _toggleMinimalViewHotkey = toggleMinimalViewHotkey;
        _colors = colors;
        _performance = performance;
        _isVisible = startVisible;
    }

    public void Shutdown()
    {
        // Must run on every reload (see FsmActiveStateTracker.UnsubscribeAll's own comment) - a
        // ScriptEngine reload otherwise leaves this instance's StateChanged handlers subscribed to
        // still-live Fsm instances forever, stacked underneath whatever the next Awake's own tracker
        // subscribes afresh.
        _activeStateTracker.UnsubscribeAll();

        if (_glMaterial != null)
        {
            UnityEngine.Object.Destroy(_glMaterial);
            _glMaterial = null;
        }
        _glMaterialFailed = false;

        if (_dynamicFont != null)
        {
            UnityEngine.Object.Destroy(_dynamicFont);
            _dynamicFont = null;
        }

        // GUIStyle is a plain managed object, not a UnityEngine.Object - nothing to Destroy, just
        // drop the references so a ScriptEngine reload rebuilds them cleanly on next use.
        _titleStyle = null;
        _eventStyle = null;
        _globalEventStyle = null;
        _textStyleBuiltForZoom = -1f;
    }

    // Read by FsmMasterPlugin.Update each frame to mirror this overlay's own toggle-overlay/toggle-
    // minimal-view hotkey state onto the new uGUI right panel's ActiveSelf, since that panel has no
    // IMGUI presence of its own for these hotkeys to toggle directly.
    internal bool IsVisible => _isVisible;
    internal bool SelectionUiVisible => _selectionUiVisible;

    // Independent of IsVisible/SelectionUiVisible above - those are driven by the toggle-overlay/
    // toggle-minimal-view hotkeys and hide the whole tool (graph + uGUI right panel) together. This
    // only suppresses the graph's own node/line drawing (see its gate in OnGUI), so the right panel's
    // Hide/Show button (FsmRightPanel) stays visible and clickable to bring the graph back, rather
    // than hiding itself along with the graph the way the toggle-overlay hotkey does.
    internal bool GraphVisible
    {
        get => _graphVisible;
        set => _graphVisible = value;
    }

    public void Update()
    {
        // Purely a visibility flip - RefreshSnapshot runs only off scene load (see FsmMasterPlugin),
        // never here, so toggling the overlay off and back on never discards the current FSM
        // selection or graph pan/zoom.
        // Both hotkeys are gated on !CanvasTextField.AnyFieldFocused - values typed into the Actions/
        // Events/Variables panel's edit fields (FsmActiveStatePanel) routinely contain digits matching
        // these hotkeys' default bindings, which would otherwise toggle this overlay mid-edit.
        if (!CanvasTextField.AnyFieldFocused && _toggleOverlayHotkey.Value.IsDown())
        {
            _isVisible = !_isVisible;
        }

        // Hides the uGUI right panel entirely (via IsVisible/SelectionUiVisible above) for a more
        // minimal graph-only look - the toggle-overlay hotkey's job is hiding the whole tool;
        // toggle-minimal-view is a secondary toggle nested within that. Pressing it again restores it.
        // Only active while the overlay itself is on. Line/box rendering detail is a separate, always-
        // applied setting now (see FsmGraphPerformanceConfig), not tied to this hotkey.
        if (_isVisible && !CanvasTextField.AnyFieldFocused && _toggleMinimalViewHotkey.Value.IsDown())
        {
            _selectionUiVisible = !_selectionUiVisible;
            _logger.LogInfo($"[FsmMaster] Graph overlay: minimal view {(!_selectionUiVisible ? "on" : "off")}.");
        }
    }

    // Called by FsmMasterPlugin on initial Awake and on every scene load (which already owns the
    // PlayMakerFSM discovery for its own persisted-edit reapplication, so this reuses that array
    // rather than re-walking the scene a second time) - never from the toggle-overlay hotkey itself
    // (see Update).
    // Tab selection (FsmTabManager) is intentionally untouched here - a tab whose FSM isn't in the
    // new snapshot is rebound to not-live by FsmMasterPlugin's own FsmTabManager.RebindAfterRefresh
    // call, not reset by this method. The graph/side-panel caches are invalidated as the very last
    // step, after the new snapshot has been fully captured and logged - not interleaved with that
    // work - so nothing here can observe a state where the new snapshot exists but the caches built
    // from the previous scene's snapshot haven't been torn down yet.
    internal void RefreshSnapshot(string sceneName, PlayMakerFSM[] components)
    {
        _snapshot = FsmDataCollector.CollectSnapshot(sceneName, components);
        _fullInfoCache.Clear();
        _notFoundThisScene.Clear();

        if (_snapshot.Fsms.Count == 0)
        {
            _logger.LogInfo("[FsmMaster] Graph overlay: no live PlayMakerFSM instances found in this scene.");
        }

        InvalidateGraphCaches();
    }

    // rightPanelScreenRect/monitorPanelScreenRect/openDropdownScreenRect are the uGUI panels' current
    // screen rects (top-left origin, matching both this Rect's own convention and IMGUI's
    // Event.current.mousePosition - no axis flip needed, unlike CanvasNode.IsMouseOver's conversion to
    // Input.mousePosition), or null whenever a given panel is hidden - see FsmMasterPlugin.OnGUI, which
    // computes them from CanvasNode.Position/Size each frame. openDropdownScreenRect is separate from
    // rightPanelScreenRect because the dropdown isn't clipped to the right panel's own rect and can
    // extend past it (see FsmRightPanel.OpenDropdownScreenRect).
    public void OnGUI(FsmTabState? activeTab, Rect? rightPanelScreenRect, Rect? monitorPanelScreenRect, Rect? openDropdownScreenRect)
    {
        if (!_isVisible || _snapshot == null)
        {
            return;
        }

        var canvasRect = new Rect(0f, 0f, Screen.width, Screen.height);
        Rect interactiveRect = ComputeInteractiveRect(rightPanelScreenRect);

        // Semi-transparent dark fill, shown whenever the uGUI right panel itself is up (mirrors
        // _rightPanel.ActiveSelf in FsmMasterPlugin.Update, which is driven by these same two fields) -
        // not just while the Open dropdown specifically is expanded. Both panels are now freely
        // draggable/resizable (see FsmRightPanel/FsmMonitorPanel's own drag/resize handles), so this
        // can no longer reuse interactiveRect's own "everything left of the panel" approximation - that
        // was only ever correct because the panel used to always be docked flush against the screen's
        // right edge, occupying the entire strip the approximation assumed. DrawVignette instead skips
        // over each panel's own actual current rect, wherever it's been moved/resized to.
        if (_selectionUiVisible)
        {
            DrawVignette(rightPanelScreenRect, monitorPanelScreenRect, openDropdownScreenRect);
        }

        if (!_graphVisible)
        {
            return;
        }

        // Pinned tabs other than the active one draw first (so the active tab's own graph, drawn
        // last/on top below, is never hidden behind one) - each as a frozen, non-interactive "ghost"
        // using that tab's own last pan/zoom/selection, dimmed while the right panel/tab strip is up
        // (_selectionUiVisible) so it reads as background reference rather than something you could
        // currently be editing. In minimal view (_selectionUiVisible false, no tab strip to show which
        // tab is even "active"), every drawn graph - pinned or active - renders at full color instead.
        foreach (FsmTabState tab in _tabManager.Tabs)
        {
            if (!tab.IsPinned || !tab.IsLive || ReferenceEquals(tab, activeTab))
            {
                continue;
            }

            FsmInfo? pinnedFsm = ResolveFsmInfo(tab.FsmKey);
            if (pinnedFsm != null)
            {
                DrawGraph(pinnedFsm, tab, canvasRect, interactiveRect, interactive: false, dim: _selectionUiVisible);
            }
        }

        // A tab stays open even once its backing FSM disappears from the scene (see
        // FsmTabManager.RebindAfterRefresh) - IsLive false / an unresolvable FsmKey both mean "draw
        // nothing for the graph this frame," not "drop the tab."
        FsmInfo? activeFsm = activeTab is { IsLive: true } ? ResolveFsmInfo(activeTab.FsmKey) : null;

        if (activeFsm != null)
        {
            DrawGraph(activeFsm, activeTab!, canvasRect, interactiveRect, interactive: true, dim: false);
        }

        // Reconciles everything EnsureTracked/the StateChanged hook observed this frame - see
        // FsmActiveStateTracker.CommitFrame's own comment for why this is gated on Repaint specifically
        // rather than running on every OnGUI event.
        if (Event.current.type == EventType.Repaint)
        {
            _activeStateTracker.CommitFrame();
        }
    }

    // Excludes the uGUI right panel's own screen rect (a fixed, right-docked, full-height strip is a
    // conservative approximation - the panel isn't always literally full-height, but this matches how
    // the old IMGUI list panel's rect exclusion worked too) from the graph's pan/zoom/click-to-select
    // input region, so a click on it never also pans the graph or selects a node underneath it. Also
    // reused as a draw-time exclusion by DrawCachedGraph's own node/line culling, so the graph's own
    // geometry never paints into the panel's screen region either - see its own comment for why. NOT
    // reused by the vignette any more (see DrawVignette) - that approximation only held while the panel
    // was always docked flush against the screen's own right edge.
    private static Rect ComputeInteractiveRect(Rect? rightPanelScreenRect)
    {
        float excludedFromX = rightPanelScreenRect?.x ?? Screen.width;
        return new Rect(0f, 0f, Mathf.Max(0f, excludedFromX), Screen.height);
    }

    // Darkens the whole screen except wherever a docked HUD panel currently sits. Both panels are
    // freely draggable/resizable now (FsmRightPanel/FsmMonitorPanel), so a single "everything left of
    // the panel" rect can no longer stand in for "everywhere the vignette should skip" - that only
    // worked while both panels were permanently flush against the screen's right edge. IMGUI has no
    // way to punch a literal hole out of an already-drawn box, so instead this rasterizes the screen
    // into a grid using every panel rect's own edges as cut lines, then fills every cell whose center
    // isn't inside either panel's rect - correct regardless of where the panels have been dragged to,
    // whether they overlap, or whether only one of them is currently visible.
    // Reused across calls instead of freshly allocated each time - OnGUI (and everything it calls,
    // including this) runs once per IMGUI event (Layout, Repaint, every mouse/key event), not once per
    // frame, so three fresh List<> allocations here happened several times more often than the actual
    // frame rate. Only Repaint below produces any visible output, so that's also the only event worth
    // paying for the list-building/sorting work at all.
    private readonly List<Rect> _vignetteHolesBuffer = new(3);
    private readonly List<float> _vignetteXCutsBuffer = new();
    private readonly List<float> _vignetteYCutsBuffer = new();

    private void DrawVignette(Rect? rightPanelScreenRect, Rect? monitorPanelScreenRect, Rect? openDropdownScreenRect)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        List<Rect> holes = _vignetteHolesBuffer;
        holes.Clear();
        if (rightPanelScreenRect.HasValue)
        {
            holes.Add(rightPanelScreenRect.Value);
        }

        if (monitorPanelScreenRect.HasValue)
        {
            holes.Add(monitorPanelScreenRect.Value);
        }

        // The Open dropdown isn't clipped to the right panel's own rect and routinely extends past it
        // (a resized-down panel, or a scene/object/FSM list tall enough to overflow) - without its own
        // hole here, this vignette (legacy OnGUI, which always composites on top of the uGUI canvas
        // regardless of z-order) paints over whatever part of the list sticks out past the panel.
        if (openDropdownScreenRect.HasValue)
        {
            holes.Add(openDropdownScreenRect.Value);
        }

        Color previousColor = GUI.color;
        GUI.color = _colors.VignetteColor.Value;
        try
        {
            if (holes.Count == 0)
            {
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                return;
            }

            List<float> xCuts = _vignetteXCutsBuffer;
            List<float> yCuts = _vignetteYCutsBuffer;
            xCuts.Clear();
            xCuts.Add(0f);
            xCuts.Add(Screen.width);
            yCuts.Clear();
            yCuts.Add(0f);
            yCuts.Add(Screen.height);
            foreach (Rect hole in holes)
            {
                xCuts.Add(Mathf.Clamp(hole.xMin, 0f, Screen.width));
                xCuts.Add(Mathf.Clamp(hole.xMax, 0f, Screen.width));
                yCuts.Add(Mathf.Clamp(hole.yMin, 0f, Screen.height));
                yCuts.Add(Mathf.Clamp(hole.yMax, 0f, Screen.height));
            }

            xCuts.Sort();
            yCuts.Sort();

            for (int i = 0; i < xCuts.Count - 1; i++)
            {
                float x0 = xCuts[i];
                float x1 = xCuts[i + 1];
                if (x1 - x0 <= 0f)
                {
                    continue;
                }

                for (int j = 0; j < yCuts.Count - 1; j++)
                {
                    float y0 = yCuts[j];
                    float y1 = yCuts[j + 1];
                    if (y1 - y0 <= 0f)
                    {
                        continue;
                    }

                    var cellCenter = new Vector2((x0 + x1) / 2f, (y0 + y1) / 2f);
                    if (!IsInsideAny(cellCenter, holes))
                    {
                        GUI.DrawTexture(new Rect(x0, y0, x1 - x0, y1 - y0), Texture2D.whiteTexture);
                    }
                }
            }
        }
        finally
        {
            GUI.color = previousColor;
        }
    }

    private static bool IsInsideAny(Vector2 point, List<Rect> rects)
    {
        foreach (Rect rect in rects)
        {
            if (rect.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    // Second guard alongside ComputeInteractiveRect's rect-subtraction: the Open dropdown can render
    // outside the right panel's own base rect (e.g. if its row list grows tall enough to extend past
    // the panel's bottom edge), so rect-subtraction alone can't account for it. Note this polls a
    // separate channel from CanvasScrollView/CanvasHorizontalScrollStrip's own direct
    // Input.mouseScrollDelta polling - a scroll-wheel gesture over one of those could still also
    // reach this method's ScrollWheel branch in HandlePanAndZoom if IsPointerOverGameObject somehow
    // returned false while the pointer is actually over uGUI content; this hasn't been observed, but
    // is worth watching for if a report of the graph zooming while scrolling a panel ever comes in.
    private static bool IsPointerOverUi() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    // Builds the title/event GUIStyles from a dynamic OS font. Gated on its own _zoom-only check
    // rather than the full layout cacheStale flag, since text styling depends only on zoom (font
    // size must scale with it) and not on pan/selection/canvas-size - rebuilding a GUIStyle is cheap
    // (a managed object, no texture work) but there's no reason to redo it on every pan-only frame.
    private void EnsureTextStyles()
    {
        // CreateDynamicFontFromOSFont's string[] overload picks the first installed name from
        // DynamicFontNames it finds; the requested rendered size is controlled entirely by each
        // GUIStyle's own fontSize below, so this base size is arbitrary and never itself re-created
        // per zoom level.
        _dynamicFont ??= Font.CreateDynamicFontFromOSFont(DynamicFontNames, (int)DynamicFontPointSize);

        if (_titleStyle != null && Mathf.Approximately(_textStyleBuiltForZoom, _zoom))
        {
            return;
        }

        _titleStyle = new GUIStyle
        {
            font = _dynamicFont,
            fontSize = Mathf.Max(1, Mathf.RoundToInt(DynamicFontPointSize * _zoom)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
            wordWrap = false,
        };
        // Actual text color is set per-node right before each label draw (see the label pass in
        // DrawCachedGraph) - state nodes use their own StateColors[ColorIndex] entry, so this default
        // is never what actually renders for them; it only matters for _globalEventStyle below,
        // which never gets overridden per-node.
        _titleStyle.normal.textColor = Color.white;

        _eventStyle = new GUIStyle(_titleStyle) { fontStyle = FontStyle.Normal };

        _globalEventStyle = new GUIStyle(_eventStyle);
        _globalEventStyle.normal.textColor = _colors.GlobalPseudoNodeTextColor.Value;

        _textStyleBuiltForZoom = _zoom;
    }

    // Clears every FsmKey's layout cache, not just whichever one _currentCache happens to point at -
    // necessary for RefreshSnapshot's scene-load call (which runs outside any DrawGraph pass, so
    // _currentCache is stale/arbitrary at that point) and harmless for the edit-handler call sites
    // (HandleNodeRightClick etc.), which only ever fire from a rare user action, not a hot per-frame
    // path - a pinned tab's cache rebuilding on the next frame it's drawn costs nothing meaningful.
    private void InvalidateGraphCaches()
    {
        _layoutCachesByFsmKey.Clear();
    }

    // FsmState.Position values live in PlayMaker's own unbounded editor-canvas space, authored
    // per-FSM with an arbitrary origin - fit the view to whatever this FSM's states actually
    // occupy rather than assuming a fixed pan/zoom starting point.
    // Pure function (no instance-field mutation) so callers can seed a specific tab's pan/zoom state
    // rather than always overwriting this overlay's own singleton fields - see the call site in
    // SelectFsm, which is the only remaining caller today.
    internal static (Vector2 PanCenter, float Zoom) FitViewToFsm(FsmInfo fsm)
    {
        if (fsm.States.Count == 0)
        {
            return (Vector2.zero, 1f);
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (FsmStateInfo state in fsm.States)
        {
            Rect position = state.State.Position;
            minX = Mathf.Min(minX, position.x);
            minY = Mathf.Min(minY, position.y);
            maxX = Mathf.Max(maxX, position.x + Mathf.Max(position.width, MinNodeWidth));
            maxY = Mathf.Max(maxY, position.y + ComputeNodeWorldHeight(state));
        }

        float worldWidth = Mathf.Max(maxX - minX, 1f) + FitMargin * 2f;
        float worldHeight = Mathf.Max(maxY - minY, 1f) + FitMargin * 2f;

        // Fits against the graph canvas's actual, always-full-screen size (see OnGUI) rather than a
        // size reduced by the list/side panel - the panels are drawn as an overlay on top of the
        // graph, not a reduction of its coordinate space, so fitting against a smaller area here
        // would make the initial zoom inconsistent with how the graph actually renders.
        float canvasWidth = Mathf.Max(Screen.width, 100f);
        float canvasHeight = Mathf.Max(Screen.height, 100f);

        float zoom = Mathf.Clamp(Mathf.Min(canvasWidth / worldWidth, canvasHeight / worldHeight), MinZoom, MaxZoom);
        var panCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
        return (panCenter, zoom);
    }

    // Node height is content-driven (title band + one row per transition) rather than the
    // FSM-authored Position.height, so a node's box always fits its transition list instead of
    // clipping or leaving dead space based on whatever height was authored in the FSM editor.
    private static float ComputeNodeWorldHeight(FsmStateInfo state)
    {
        return TitleBarHeight + state.Transitions.Count * TransitionRowHeight;
    }

    private Vector2 WorldToScreen(Vector2 worldPoint, Rect canvasRect)
    {
        Vector2 canvasCenter = canvasRect.position + canvasRect.size / 2f;
        return (worldPoint - _panWorldCenter) * _zoom + canvasCenter;
    }

    private Rect WorldToScreen(Rect worldRect, Rect canvasRect)
    {
        Vector2 topLeft = WorldToScreen(worldRect.position, canvasRect);
        return new Rect(topLeft.x, topLeft.y, worldRect.width * _zoom, worldRect.height * _zoom);
    }

    // tab owns the pan/zoom/selection this pass draws with - read from it up front and, if interactive,
    // written back at the end (mirrors the sync in/out OnGUI used to do directly against activeTab
    // before pinned-but-inactive tabs needed their own, separate, read-only pass through this same
    // method). _currentCache is repointed at this FsmKey's own GraphLayoutCache for the duration of the
    // call, so every helper below (RebuildNodeLayoutCache, DrawCachedGraph, hit-testing, dragging)
    // keeps reading/writing through it exactly as it used to read/write the old singleton fields.
    private void DrawGraph(FsmInfo fsm, FsmTabState tab, Rect canvasRect, Rect interactiveRect, bool interactive, bool dim)
    {
        _currentCache = GetOrCreateLayoutCache(tab.FsmKey);
        _panWorldCenter = tab.PanWorldCenter;
        _zoom = tab.Zoom;
        _selectedStateName = tab.SelectedStateName;

        _activeStateTracker.EnsureTracked(tab.FsmKey, fsm.Fsm);

        // No background of its own here - GUIStyle.none - the full-screen dark vignette (when it's
        // shown at all) is drawn once in OnGUI, before this method runs, keyed off the FSM picker
        // popup's own visibility rather than the graph's. This group's only job is establishing the
        // local GUIClip coordinate space canvasRect (see WorldToScreen and the GL-drawing calls below,
        // which take canvasScreenOffset separately since raw GL bypasses GUIClip).
        GUI.BeginGroup(canvasRect, GUIStyle.none);
        var localCanvasRect = new Rect(0f, 0f, canvasRect.width, canvasRect.height);

        // Split from the pan check on purpose: panning (dragging the graph) changes every node/curve's
        // absolute screen position but nothing about their shape or count, while any of these other
        // four changing means the underlying data itself might be different and genuinely needs a
        // full re-walk. A large FSM's full rebuild - one NodeLayout plus one Vector2[] curve array per
        // transition, for every state - was the confirmed source of a GC stutter that kept recurring
        // specifically while continuously dragging a large open graph, since the old single cacheStale
        // check rebuilt everything from scratch on every single frame of that drag.
        bool structuralCacheStale = _currentCache.NodeLayoutCache == null
            || !ReferenceEquals(_currentCache.LayoutCacheComponent, fsm.Component)
            || !Mathf.Approximately(_currentCache.LayoutCacheZoom, _zoom)
            || _currentCache.LayoutCacheCanvasRect != localCanvasRect
            || _currentCache.LayoutCacheEditGeneration != _editManager.EditGeneration;

        bool panOnlyStale = !structuralCacheStale && _currentCache.LayoutCachePanCenter != _panWorldCenter;

        if (structuralCacheStale)
        {
            RebuildNodeLayoutCache(fsm, localCanvasRect);
            _currentCache.NodeLayoutVersion++;
            _currentCache.LayoutCacheComponent = fsm.Component;
            _currentCache.LayoutCachePanCenter = _panWorldCenter;
            _currentCache.LayoutCacheZoom = _zoom;
            _currentCache.LayoutCacheCanvasRect = localCanvasRect;
            _currentCache.LayoutCacheEditGeneration = _editManager.EditGeneration;
        }
        else if (panOnlyStale)
        {
            // Shifts every already-cached screen-space rect/curve-point array by the pan delta in
            // place (mutating existing arrays' contents, not reallocating them) instead of re-walking
            // every state/transition from scratch for a change that never altered their shape.
            Vector2 screenDelta = (_currentCache.LayoutCachePanCenter - _panWorldCenter) * _zoom;
            TranslateNodeLayoutCache(screenDelta);
            _currentCache.NodeLayoutVersion++;
            _currentCache.LayoutCachePanCenter = _panWorldCenter;
        }

        // canvasRect.position is passed through separately from localCanvasRect: node/label rects are
        // drawn via GUI calls local to this GUI.BeginGroup, so Unity's GUIClip stack silently adds
        // canvasRect's screen offset for them, but raw GL calls bypass GUIClip entirely - DrawLineBufferGL
        // needs the real screen-space offset to line its geometry up with those GUI-drawn nodes.
        DrawCachedGraph(fsm, tab.FsmKey, canvasRect.position, interactiveRect, interactive, dim);

        // A pinned-but-inactive tab's graph is a frozen ghost - it never pans/zooms/drags/selects on
        // its own, and only the active tab's own gestures should ever mutate _draggingTransition/
        // _isPanning (both single, shared instance fields - see their own declarations - since only one
        // graph can ever be interactive at a time).
        if (interactive)
        {
            HandlePanAndZoom(interactiveRect);
            HandleTransitionDrag(fsm, interactiveRect);
        }

        GUI.EndGroup();

        if (interactive)
        {
            tab.PanWorldCenter = _panWorldCenter;
            tab.Zoom = _zoom;
            tab.SelectedStateName = _selectedStateName;

            // One-shot "scroll the Actions panel to this action" request from a transition click on
            // THIS tab's own fsm (see HandleTransitionLeftClick) - cleared immediately after handoff,
            // unlike SelectedStateName, since FsmMasterPlugin.Update consumes it once per request rather
            // than treating it as persisted selection. A cross-tab (global transition) match instead
            // writes directly onto the OTHER tab's own PendingScrollActionIndex, bypassing this field
            // entirely - see HandleTransitionLeftClick's global-search branch.
            if (_pendingScrollActionIndex.HasValue)
            {
                tab.PendingScrollActionIndex = _pendingScrollActionIndex;
                _pendingScrollActionIndex = null;
            }
        }
    }

    // Walks every state/transition on this FSM and builds the screen-space node/curve geometry the
    // rest of this class draws and hit-tests against, applying live edit-set overrides (disabled
    // states/transitions, retargets) along the way. Result is written into _currentCache.
    private void RebuildNodeLayoutCache(FsmInfo fsm, Rect canvasRect)
    {
        // Active edit set drives disabled-state/transition styling below - null for an FSM with no edits
        // this session, in which case every IsDisabled/disabledByState/disabledDirectly check below is
        // simply false.
        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
        FsmEditSet? editSet = _editManager.GetActiveEditSet(fsmKey);

        var nodeScreenRects = new Dictionary<string, Rect>();
        foreach (FsmStateInfo state in fsm.States)
        {
            Rect worldRect = state.State.Position;
            worldRect.width = Mathf.Max(worldRect.width, MinNodeWidth);
            worldRect.height = ComputeNodeWorldHeight(state);
            nodeScreenRects[state.Name] = WorldToScreen(worldRect, canvasRect);
        }

        var layoutCache = new Dictionary<string, NodeLayout>();
        foreach (FsmStateInfo state in fsm.States)
        {
            Rect screenRect = nodeScreenRects[state.Name];
            int colorIndex = Mathf.Clamp(state.State.ColorIndex, 0, _colors.StateColors.Length - 1);
            var titleRect = new Rect(screenRect.x, screenRect.y, screenRect.width, TitleBarHeight * _zoom);

            bool isDisabled = editSet != null && editSet.DisabledStates.Contains(state.Name);

            // The one transition FsmEditManager.DisableState leaves reachable (the exit event its own
            // injected SendExitEventAction fires) - every other row for a disabled state greys out below,
            // this one alone keeps its normal color since it's the one still actually reachable. Only
            // meaningful (and only computed) for a disabled state; FindExitEvent is safe/idempotent to
            // re-run against an already-neutered state (see its own comment).
            FsmEvent? survivingExitEvent = isDisabled ? FsmEditManager.FindExitEvent(state.State) : null;

            var node = new NodeLayout
            {
                Name = state.Name,
                ScreenRect = screenRect,
                TitleRect = titleRect,
                // ColorIndex 0 is PlayMaker's "no color set" default - baked to black here (rather
                // than its literal grey StateColors entry). This is the title band's DEFAULT
                // background - see ActiveTitleBackgroundColor for what it switches to on activation.
                FillColor = colorIndex == 0 ? Color.black : _colors.StateColors[colorIndex].Value,
                ColorIndex = colorIndex,
                IsDisabled = isDisabled,
            };

            float rowHeight = TransitionRowHeight * _zoom;

            // Node height is now sized to exactly fit the title band plus every transition row
            // (see ComputeNodeWorldHeight), so every row always has room - no overflow/collapse
            // fallback is needed here anymore.
            for (int i = 0; i < state.Transitions.Count; i++)
            {
                FsmTransitionInfo transition = state.Transitions[i];
                var rowRect = new Rect(screenRect.x, titleRect.yMax + i * rowHeight, screenRect.width, rowHeight);

                // This row's DTO (EventName/ToState) is frozen from scene-load time - FsmDataCollector
                // is never re-run mid-scene, so a live retarget/rename (drag-to-rebind, or a saved edit
                // reapplied on scene load before this snapshot was taken) would otherwise never show up
                // here until the next scene reload. Reconcile against the active edit set instead of
                // trusting the DTO blindly, so the arrow reflects where the transition CURRENTLY points.
                // A relocation to a DIFFERENT owning state isn't reconciled here (only a same-state
                // retarget/rename is) - that rarer cross-state case still renders at its old location
                // until the next scene reload; a directly-disabled transition (DisabledMarker) is
                // deliberately left alone here too, since it must keep showing its original destination
                // as a grey row (see disabledDirectly below), not whatever a partial edit might imply.
                TransitionRetarget? liveEdit = editSet?.TransitionRetargets.Find(t =>
                    t.StateName == state.Name && t.EventName == transition.EventName);
                bool disabledDirectly = liveEdit != null && liveEdit.NewToState == TransitionRetarget.DisabledMarker;

                string effectiveToState = transition.ToState;
                string effectiveEventName = transition.EventName;
                if (liveEdit != null && !disabledDirectly && liveEdit.NewStateName == state.Name)
                {
                    effectiveToState = liveEdit.NewToState;
                    effectiveEventName = FsmEditManager.EffectiveNewEventName(liveEdit);
                }

                Vector2 sourceAnchor = rowRect.center;
                Vector2 targetAnchor = rowRect.center;
                Vector2 exitDirection = new(1f, 0f);
                Vector2 entryDirection = new(-1f, 0f);

                if (nodeScreenRects.TryGetValue(effectiveToState, out Rect targetRect))
                {
                    // Exit side is still whichever of the source row's left/right edges faces the
                    // target - see PickExitDirection.
                    exitDirection = PickExitDirection(screenRect, targetRect);
                    sourceAnchor = new Vector2(exitDirection.x > 0f ? rowRect.xMax : rowRect.x, rowRect.center.y);

                    // Every incoming transition enters through one of the two sides of the target's
                    // title band ("the state name"), never the top - whichever side faces the source,
                    // via the same left/right heuristic as the exit side (PickExitDirection), just
                    // evaluated against the title band instead of the whole node. Multiple incoming
                    // lines landing on the same side overlap at that side's single fixed point rather
                    // than being spread out to avoid it.
                    var targetTitleRect = new Rect(targetRect.x, targetRect.y, targetRect.width, TitleBarHeight * _zoom);
                    entryDirection = PickExitDirection(targetTitleRect, screenRect);
                    targetAnchor = entryDirection.x > 0f
                        ? new Vector2(targetTitleRect.xMax, targetTitleRect.center.y)
                        : new Vector2(targetTitleRect.x, targetTitleRect.center.y);
                }

                Vector2[] curvePoints = SampleBezierCurve(sourceAnchor, targetAnchor, exitDirection, entryDirection, _zoom);

                // Every row of a disabled state greys out except the one transition FindExitEvent picked
                // as still reachable (see survivingExitEvent above); independently, a row can also be
                // directly disabled via right-click regardless of whole-state disabling (disabledDirectly
                // computed above, alongside the effective-destination reconciliation).
                bool disabledByState = isDisabled && (survivingExitEvent == null || effectiveEventName != survivingExitEvent.Name);

                node.Rows.Add(new TransitionRow(effectiveToState, effectiveEventName, rowRect, curvePoints, ComputeCurveBounds(curvePoints), disabledByState || disabledDirectly));
            }

            layoutCache[state.Name] = node;
        }

        // Global transitions - synthetic pseudo-nodes stacked above their target, not plain edges,
        // grouped with a plain loop rather than LINQ GroupBy.
        var globalByTarget = new Dictionary<string, List<FsmTransitionInfo>>();
        foreach (FsmTransitionInfo transition in fsm.GlobalTransitions)
        {
            if (!globalByTarget.TryGetValue(transition.ToState, out List<FsmTransitionInfo>? list))
            {
                list = new List<FsmTransitionInfo>();
                globalByTarget[transition.ToState] = list;
            }

            list.Add(transition);
        }

        // Height/gap/offset are world-space-ish constants that must be scaled by _zoom just like
        // TitleBarHeight/TransitionRowHeight are at their point of use - targetRect is already a
        // zoom-scaled screen position, so leaving these as raw fixed pixel values made the pseudo
        // node grow relatively larger than the (zoom-scaled) state boxes when zoomed out, and
        // relatively smaller than them when zoomed in.
        float pseudoHeight = GlobalPseudoNodeHeight * _zoom;
        float pseudoGap = GlobalPseudoNodeGap * _zoom;
        float pseudoOffset = GlobalPseudoNodeOffset * _zoom;

        var globalPseudoNodes = new List<GlobalPseudoNodeLayout>();
        foreach (KeyValuePair<string, List<FsmTransitionInfo>> group in globalByTarget)
        {
            if (!nodeScreenRects.TryGetValue(group.Key, out Rect targetRect))
            {
                continue;
            }

            for (int i = 0; i < group.Value.Count; i++)
            {
                string eventName = group.Value[i].EventName;
                float y = targetRect.y - pseudoOffset - i * (pseudoHeight + pseudoGap);
                var pseudoRect = new Rect(targetRect.x, y, targetRect.width, pseudoHeight);

                // Global transitions aren't owned by any state, so whole-state disabling never greys
                // one out - only a direct right-click disable on this specific global transition does.
                bool isDisabled = editSet != null && editSet.TransitionRetargets.Exists(t =>
                    t.StateName == "" && t.EventName == eventName && t.NewToState == TransitionRetarget.DisabledMarker);

                globalPseudoNodes.Add(new GlobalPseudoNodeLayout(
                    eventName,
                    pseudoRect,
                    new Vector2(pseudoRect.center.x, pseudoRect.yMax),
                    new Vector2(targetRect.center.x, targetRect.y),
                    isDisabled));
            }
        }

        _currentCache.NodeLayoutCache = layoutCache;
        _currentCache.GlobalPseudoNodeCache = globalPseudoNodes;
    }

    // Cheap alternative to a full RebuildNodeLayoutCache for a pure pan (see DrawGraph's
    // structuralCacheStale/panOnlyStale split) - shifts every already-cached node rect and curve point
    // by `screenDelta` in place, reusing every existing Vector2[]/NodeLayout/list instead of
    // reallocating any of them, since a plain pan changes nothing about a node/curve's shape or count.
    private void TranslateNodeLayoutCache(Vector2 screenDelta)
    {
        if (_currentCache.NodeLayoutCache != null)
        {
            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                node.ScreenRect = Translate(node.ScreenRect, screenDelta);
                node.TitleRect = Translate(node.TitleRect, screenDelta);

                for (int i = 0; i < node.Rows.Count; i++)
                {
                    node.Rows[i] = node.Rows[i].Translated(screenDelta);
                }
            }
        }

        if (_currentCache.GlobalPseudoNodeCache != null)
        {
            for (int i = 0; i < _currentCache.GlobalPseudoNodeCache.Count; i++)
            {
                _currentCache.GlobalPseudoNodeCache[i] = _currentCache.GlobalPseudoNodeCache[i].Translated(screenDelta);
            }
        }
    }

    private static Rect Translate(Rect rect, Vector2 delta) => new(rect.x + delta.x, rect.y + delta.y, rect.width, rect.height);

    // Picks which side (left or right - never top/bottom) of `fromRect` a transition exits through
    // on its way to `toRect`. Deliberately NOT a raw comparison of the two centers' x: for nodes
    // stacked in a rough vertical column, the target's center can fall a few pixels to either side
    // of the source's center almost incidentally (an FSM author dragging boxes into a column by eye,
    // never intending any horizontal offset), and a raw sign comparison flips inconsistently between
    // otherwise-similar transitions, producing a messy, crossing/braided look. Instead: only prefer
    // right/left when the target's center genuinely sits outside the source's own horizontal extent
    // (a real side-by-side layout); when the target's center falls within the source's own width
    // (the common vertically-stacked case), fall back to a single stable default side so every such
    // transition routes the same way instead of flip-flopping.
    private static Vector2 PickExitDirection(Rect fromRect, Rect toRect)
    {
        if (toRect.center.x > fromRect.xMax)
        {
            return new Vector2(1f, 0f);
        }

        if (toRect.center.x < fromRect.x)
        {
            return new Vector2(-1f, 0f);
        }

        return new Vector2(1f, 0f);
    }

    // Samples a cubic Bezier once at layout-cache-build time (not per-frame) so DrawBezierArrow can
    // just walk a cached polyline every OnGUI call. The curve exits a node from whichever side faces
    // the target via a control point offset from the anchor, and enters the target's title band
    // through whichever of its two sides faces the source (see the call site in
    // RebuildNodeLayoutCache) - control2 extends further in that same direction (away from the
    // target) so the curve swoops in from whichever side it's actually attached to.
    //
    // The control-point offsets are scaled by zoom, not fixed screen pixels: source/target anchors
    // are already zoom-scaled screen positions, so a fixed-pixel offset would shrink relative to the
    // anchor spacing as you zoom in (curve flattens out) and grow relative to it as you zoom out
    // (curve over-bends) - scaling keeps the curve's apparent shape constant across zoom levels.
    //
    // Segment count is likewise derived from the curve's actual on-screen size (via its control
    // polygon length, a standard cheap over-estimate of a Bezier's arc length) rather than a fixed
    // count - a fixed count means each segment's screen length (and the thick rotated-rectangle
    // joints between them) grows right along with zoom, so a curve that looked smooth zoomed out
    // turns visibly faceted zoomed in. Keeping segment length roughly constant on screen keeps the
    // curve looking equally smooth at any zoom, and uses fewer segments (cheaper) for small/distant
    // curves instead of spending a flat, often-excessive count on every edge.
    private static Vector2[] SampleBezierCurve(Vector2 source, Vector2 target, Vector2 exitDirection, Vector2 entryDirection, float zoom)
    {
        Vector2 control1 = source + exitDirection * (BezierControlOffset * zoom);
        Vector2 control2 = target + entryDirection * (BezierControlOffset * zoom);

        float controlPolygonLength = Vector2.Distance(source, control1) + Vector2.Distance(control1, control2) + Vector2.Distance(control2, target);
        int segmentCount = Mathf.Clamp(Mathf.RoundToInt(controlPolygonLength / BezierTargetSegmentLength), MinBezierSegments, MaxBezierSegments);

        var points = new Vector2[segmentCount + 1];
        for (int i = 0; i <= segmentCount; i++)
        {
            float t = (float)i / segmentCount;
            points[i] = CubicBezierPoint(source, control1, control2, target, t);
        }

        return points;
    }

    private static Vector2 CubicBezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        return (u * u * u) * p0 + (3f * u * u * t) * p1 + (3f * u * t * t) * p2 + (t * t * t) * p3;
    }

    // Axis-aligned bounding box of a sampled curve, computed once at layout-cache-build time so the
    // per-Repaint viewport cull below (see DrawCachedGraph) is a single cheap Rect.Overlaps call
    // instead of re-walking every curve's points every frame.
    private static Rect ComputeCurveBounds(Vector2[] points)
    {
        float minX = points[0].x, maxX = points[0].x, minY = points[0].y, maxY = points[0].y;
        for (int i = 1; i < points.Length; i++)
        {
            minX = Mathf.Min(minX, points[i].x);
            maxX = Mathf.Max(maxX, points[i].x);
            minY = Mathf.Min(minY, points[i].y);
            maxY = Mathf.Max(maxY, points[i].y);
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    // Renders one FSM's graph from its already-built layout cache: gathers line/chrome geometry into
    // GL batches, draws state/event labels, then (when interactive) hit-tests this frame's mouse-down
    // against transition action zones and node rects to drive click/drag handling.
    private void DrawCachedGraph(FsmInfo fsm, string fsmKey, Vector2 canvasScreenOffset, Rect interactiveRect, bool interactive, bool dim)
    {
        if (_currentCache.NodeLayoutCache == null)
        {
            return;
        }

        // Read live, not cached - the active state changes every frame as the FSM runs, independent of
        // the layout cache (which only tracks pan/zoom/selection/canvas-size).
        string? activeStateName = fsm.Fsm.ActiveStateName;

        // Whether ANY state on this FSM currently has a "was active" fade running - forces the chrome
        // cache below to rebuild every Repaint while true, since a fade's progress changes continuously
        // and none of chromeCacheValid's other inputs (layout version, active/selected state name, box
        // style, config generation) would otherwise ever catch that on their own.
        bool anyFading = _activeStateTracker.HasAnyFading(fsmKey);

        // Unity dispatches OnGUI once per Layout pass plus once per queued input event (MouseMove,
        // ScrollWheel, etc.), in addition to Repaint - only Repaint actually puts pixels on screen,
        // so issuing the full set of DrawTexture/Label calls on every other event type was pure
        // waste and the dominant cost behind a reported 500->30fps drop while the overlay was open.
        // Click-through detection further below still needs to run on MouseDown regardless.
        if (Event.current.type == EventType.Repaint)
        {
            EnsureTextStyles();

            // Off-screen nodes/edges are skipped entirely rather than drawn-then-clipped by GUIClip -
            // a boss or late-game enemy FSM can easily have 100+ states, and once zoomed/panned in on
            // a small region most of them sit outside the canvas. GUIClip still eats the CPU cost of
            // every GUI.Label/DrawTexture call issued even if nothing ends up visible, so skipping the
            // call outright (rather than relying on clipping) is the actual saving. Bounds checks are
            // all against precomputed rects (ScreenRect/CurveBounds/Rect), so this adds no per-frame
            // math beyond a Rect.Overlaps call per node/edge.
            //
            // interactiveRect, not the full _currentCache.LayoutCacheCanvasRect, is what's actually used here: it's
            // already screen minus the uGUI right panel's docked strip (see ComputeInteractiveRect), and
            // reusing it also keeps the panel visually unobstructed. GUIClip can't help with that on its
            // own - legacy OnGUI always composites on top of the uGUI Canvas regardless of nesting, and
            // this method's node/line geometry is drawn via raw GL (DrawLineBufferGL/FlushChromeBufferGL),
            // which bypasses GUIClip entirely - so the only way to keep it out of the panel's screen
            // region is to never add it to those GL buffers in the first place.
            Rect visibleRect = interactiveRect;
            _currentCache.LineDrawBuffer.Clear();

            // See the _chromeCache* fields' declaration comment - true when every input the chrome
            // gathering pass below (both loops) reads is identical to the Repaint that last filled
            // _currentCache.ChromeVertexBuffer, letting that whole pass be skipped and the previous frame's buffer
            // reused untouched instead of re-tessellating every rounded rect on the graph again for an
            // output that would come out pixel-identical anyway.
            bool chromeCacheValid = _currentCache.ChromeCacheLayoutVersion == _currentCache.NodeLayoutVersion
                && _currentCache.ChromeCacheVisibleRect == visibleRect
                && _currentCache.ChromeCacheActiveStateName == activeStateName
                && _currentCache.ChromeCacheSelectedStateName == _selectedStateName
                && _currentCache.ChromeCacheBoxStyle == _performance.BoxStyle.Value
                && _currentCache.ChromeCacheConfigGeneration == _performance.Generation
                && !anyFading;

            if (!chromeCacheValid)
            {
                _currentCache.ChromeVertexBuffer.Clear();
            }

            if (_currentCache.GlobalPseudoNodeCache != null)
            {
                foreach (GlobalPseudoNodeLayout pseudo in _currentCache.GlobalPseudoNodeCache)
                {
                    if (!pseudo.Rect.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    // Same rounded-outline chrome as a regular state box, but always plain grey with
                    // a black outline - global transitions aren't owned by any one colorIndex, so
                    // there's no state color to draw the outline in. Single band, so rounding all 4
                    // corners is safe here. Geometry is only collected here - actually drawn in one
                    // GL batch below, alongside every other node's chrome gathered for this frame.
                    // The label itself is deliberately NOT drawn here (see the label pass after both
                    // GL flushes below). Skipped when the chrome cache is still valid - a pseudo-node's
                    // chrome never itself depends on activeStateName/_selectedStateName, but it's gated
                    // on the same flag as the per-state loop below so draw order in the shared vertex
                    // buffer (pseudo chrome always before node chrome) stays identical to a full rebuild.
                    if (!chromeCacheValid)
                    {
                        AddRoundedRectOutlineToChromeBuffer(pseudo.Rect, _colors.GlobalPseudoNodeColor.Value, _colors.GlobalPseudoNodeOutlineColor.Value, NodeBorderThickness * _zoom);
                    }

                    // Arrow geometry is only collected here - actually drawn in one GL batch below,
                    // once every pseudo/edge line for this frame has been gathered.
                    Color pseudoLineColor = pseudo.IsDisabled ? _colors.DisabledTransitionLineColor.Value : _colors.GlobalTransitionColor.Value;
                    _currentCache.LineDrawBuffer.Add((pseudo.ArrowPoints, pseudoLineColor, TransitionLineThickness, ArrowheadLength, 0f));
                }
            }

            // Regular per-state transition lines - geometry collected here (same cull check as
            // before), drawn in the same GL batch as the pseudo-node arrows above. Default color is
            // each state's own TransitionColors entry (see NodeLayout.ColorIndex); lines leaving the
            // active state switch to the fixed ActiveStateColor and draw thicker instead, matching its
            // transition-name text color below.
            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                // Disabled styling takes precedence over active-state styling - a disabled state can
                // technically still be ActiveStateName for one frame before its injected exit fires.
                bool nodeIsActiveForLines = !node.IsDisabled && activeStateName != null && node.Name == activeStateName;
                Color lineColor = nodeIsActiveForLines ? _colors.ActiveStateColor.Value : _colors.TransitionColors[node.ColorIndex].Value;
                float lineThickness = nodeIsActiveForLines ? ActiveTransitionLineThickness : TransitionLineThickness;

                // A selected state's outgoing transitions get a yellow outline - cheaper than an
                // actual stroked-outline shape, this just re-emits the same curve underneath, thicker
                // and in SelectedStateColor, so it peeks out from behind the normal line and arrowhead
                // drawn on top of it right after (see the append order into _currentCache.LineDrawBuffer below).
                bool nodeIsSelectedForLines = _selectedStateName != null && node.Name == _selectedStateName;

                foreach (TransitionRow row in node.Rows)
                {
                    if (!row.CurveBounds.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    // A disabled row (either the whole state's cascading effect, minus the one
                    // FindExitEvent-picked survivor, or a directly right-click-disabled single
                    // transition) always greys out regardless of active/theme coloring.
                    Color rowColor = row.IsDisabled ? _colors.DisabledTransitionLineColor.Value : lineColor;
                    float rowThickness = row.IsDisabled ? TransitionLineThickness : lineThickness;
                    float rowArrowLength = ArrowheadLength * (rowThickness / TransitionLineThickness);

                    if (nodeIsSelectedForLines)
                    {
                        // Same arrowLength as the real line below - only Thickness and OutlineMargin
                        // grow, both by the same margin on each side/direction, so the outline reads
                        // as a uniform border the entire length of the arrow rather than a wedge that
                        // only shows up near the arrowhead's wide back edge (see the OutlineMargin
                        // field comment on _currentCache.LineDrawBuffer).
                        _currentCache.LineDrawBuffer.Add((row.CurvePoints, _colors.SelectedStateColor.Value, rowThickness + SelectedTransitionOutlineMargin * 2f, rowArrowLength, SelectedTransitionOutlineMargin));
                    }

                    _currentCache.LineDrawBuffer.Add((row.CurvePoints, rowColor, rowThickness, rowArrowLength, 0f));
                }
            }

            // Live rubber-band preview for an in-progress endpoint drag (see HandleTransitionDrag) -
            // drawn from whichever anchor is being dragged to the current mouse position, as a bezier
            // curve (SampleBezierCurve, the same curve every committed transition uses) rather than a
            // straight line, so the preview reads as "a transition being formed" rather than a raw
            // debug line. Fixed green (DragTransitionColor) rather than ActiveStateColor's cyan, since
            // this is a not-yet-committed drag, not an already-active transition. The anchor's exit
            // side is re-picked every frame against the live mouse position (see ResolveDragAnchor),
            // the same way PickExitDirection picks a side for a committed transition - so the curve
            // swaps to the row/title band's other edge as soon as that edge is the closer one, instead
            // of staying stuck on whichever side happened to be closer at the moment the drag started.
            // _draggingTransition is a single shared instance field (only one graph is ever interactive
            // at a time - see DrawGraph), so a pinned-but-inactive pass must not draw this preview: its
            // _currentCache belongs to a different FsmKey than whatever drag is actually in progress on
            // the active tab.
            if (interactive && _draggingTransition is { HasCrossedThreshold: true })
            {
                Vector2 mousePos = Event.current.mousePosition;
                (Vector2 Anchor, Vector2 ExitDirection)? drag = ResolveDragAnchor(mousePos);
                if (drag.HasValue)
                {
                    Vector2[] dragCurvePoints = SampleBezierCurve(drag.Value.Anchor, mousePos, drag.Value.ExitDirection, -drag.Value.ExitDirection, _zoom);
                    _currentCache.LineDrawBuffer.Add((dragCurvePoints, _colors.DragTransitionColor.Value, ActiveTransitionLineThickness, ArrowheadLength * (ActiveTransitionLineThickness / TransitionLineThickness), 0f));
                }
            }

            // Nodes (title band + transition rows) - chrome geometry collected here, actually drawn
            // in the chrome GL batch below (once every node's geometry has been gathered), so it
            // renders as one batch on top of the lines above. The title band and the last row each
            // round only the corners they actually touch, in their own color - a node's title is
            // only ever the top of the box and its last row (or the title itself, if there are no
            // rows) is only ever the bottom, so this is the only way to get correctly-colored
            // rounded corners without a separate body-fill layer showing the wrong color through at
            // the title's end (the previous bug). Middle rows touch no corner and stay plain rects.
            //
            // This whole loop - the actual rounded-rect tessellation Detailed box style pays for - is
            // skipped entirely when chromeCacheValid, reusing _currentCache.ChromeVertexBuffer exactly as the
            // previous Repaint left it (see that flag's declaration comment).
            if (!chromeCacheValid)
            {
                foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
                {
                    if (!node.ScreenRect.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    // One shared radius for every rounded rect belonging to this node (active ring,
                    // outer ring, title, last row). Previously each of those called GetScreenCornerRadius
                    // on its own rect, and since the title/row bands are much shorter than the node as a
                    // whole (TitleBarHeight/TransitionRowHeight vs. the node's full height), their radius
                    // clamped down independently to a smaller value than the outer ring's - the two
                    // curves no longer matched, leaving a thin "hook" of the outer ring's color poking out
                    // past the tighter inner curve at every corner. Concentric rings (outer ring, active
                    // ring) still need a correspondingly larger radius so their curve stays parallel to
                    // this one rather than reusing it as-is, which is what the "+ offset" below does.
                    bool standardBoxStyle = _performance.BoxStyle.Value == GraphBoxStyle.Standard;
                    float cornerRadius = standardBoxStyle ? 0f : GetNodeCornerRadius(node.ScreenRect);

                    // The active-indicator ring is the outer "chrome" halo - drawn behind and larger than
                    // the inner ring below, so only its outer edge shows past it. Always the fixed
                    // ActiveStateColor (cyan), regardless of the state's own theme - it's a generic
                    // "this is active" signal, not a color-identity one. Disabled styling takes precedence
                    // over active styling (see the line-color pass above for the same rule) - a disabled
                    // state never shows the active halo even if it's still ActiveStateName for one frame.
                    bool isActive = !node.IsDisabled && activeStateName != null && node.Name == activeStateName;
                    bool isSelected = _selectedStateName != null && node.Name == _selectedStateName;

                    // "Was active this frame (per the StateChanged hook - see FsmActiveStateTracker),
                    // but isn't the FSM's final resolved active state" - 0 right after it stopped being
                    // active, 1 once its 1s fade has fully settled back to this node's own default look.
                    // Never set for an already-active or disabled node - disabled matches the isActive
                    // rule above, and a node can't be both active and fading for the same reconciliation.
                    float? fadeT = node.IsDisabled ? null : _activeStateTracker.GetFadeProgress(fsmKey, node.Name);

                    // Selected-state ring(s) are added first (outermost), so the active halo and inner
                    // ring below still draw on top of them. A state that's both active and selected gets
                    // a third ring further out than the normal active halo; a state that's only selected
                    // gets a single ring in the same slot the active halo would otherwise occupy.
                    if (isActive && isSelected)
                    {
                        float selectedOffset = (NodeBorderThickness + NodeActiveOutlineThickness + NodeSelectedOutlineThickness) * _zoom;
                        var selectedRect = new Rect(
                            node.ScreenRect.x - selectedOffset,
                            node.ScreenRect.y - selectedOffset,
                            node.ScreenRect.width + selectedOffset * 2f,
                            node.ScreenRect.height + selectedOffset * 2f);
                        AddRoundedRectToChromeBuffer(selectedRect, cornerRadius + selectedOffset, _colors.SelectedStateColor.Value);
                    }
                    else if (isSelected)
                    {
                        float selectedOffset = (NodeBorderThickness + NodeActiveOutlineThickness) * _zoom;
                        var selectedRect = new Rect(
                            node.ScreenRect.x - selectedOffset,
                            node.ScreenRect.y - selectedOffset,
                            node.ScreenRect.width + selectedOffset * 2f,
                            node.ScreenRect.height + selectedOffset * 2f);
                        AddRoundedRectToChromeBuffer(selectedRect, cornerRadius + selectedOffset, _colors.SelectedStateColor.Value);
                    }

                    if (isActive || fadeT.HasValue)
                    {
                        // Same ring, same position, for both cases - only its color differs: solid
                        // ActiveStateColor while genuinely active, or that same color fading toward
                        // fully transparent (never toward a literal "default ring color," since a
                        // non-active node has no ring of its own to fade into - see GetFadingActiveColor)
                        // while merely fading. isSelected's own "not active" offset above already
                        // reserves this exact radial slot regardless of which of the two this is, so a
                        // fading + selected node's rings still stack correctly.
                        Color ringColor = isActive ? _colors.ActiveStateColor.Value : GetFadingActiveColor(fadeT!.Value);
                        float activeOffset = (NodeBorderThickness + NodeActiveOutlineThickness) * _zoom;
                        var activeRect = new Rect(
                            node.ScreenRect.x - activeOffset,
                            node.ScreenRect.y - activeOffset,
                            node.ScreenRect.width + activeOffset * 2f,
                            node.ScreenRect.height + activeOffset * 2f);
                        AddRoundedRectToChromeBuffer(activeRect, cornerRadius + activeOffset, ringColor);
                    }

                    // The inner ring - drawn on top of the active halo above - is NodeOutlineColor's white
                    // by default, switching to the state's own theme color (or the ActiveStateColor
                    // fallback for an unthemed (colorIndex 0) state - see GetActiveOutlineColor) while
                    // active, or DisabledOutlineColor's grey while disabled (right-click-disabled state).
                    // Title/rows below are drawn full node width and round their own top/bottom corners
                    // themselves, fully covering the inner area, so no separate inner fill pass is needed
                    // here. Always present in the Detailed box style; in Standard it's skipped for a plain,
                    // non-active/non-selected state, so only active/selected states show a border at all.
                    Color innerOutlineColor = node.IsDisabled
                        ? _colors.DisabledOutlineColor.Value
                        : isActive
                            ? GetActiveOutlineColor(node.ColorIndex)
                            : fadeT.HasValue
                                ? Color.Lerp(GetActiveOutlineColor(node.ColorIndex), _colors.NodeOutlineColor.Value, fadeT.Value)
                                : _colors.NodeOutlineColor.Value;
                    if (!standardBoxStyle || isActive || isSelected || fadeT.HasValue)
                    {
                        float outlineThickness = NodeBorderThickness * _zoom;
                        var outerRect = new Rect(
                            node.ScreenRect.x - outlineThickness,
                            node.ScreenRect.y - outlineThickness,
                            node.ScreenRect.width + outlineThickness * 2f,
                            node.ScreenRect.height + outlineThickness * 2f);
                        AddRoundedRectToChromeBuffer(outerRect, cornerRadius + outlineThickness, innerOutlineColor);
                    }

                    // Title band is this state's own color by default (colorIndex 0 baked to black - see
                    // NodeLayout.FillColor), switching to the fixed ActiveTitleBackgroundColor (light blue)
                    // while it's the active state - see the label pass below for its matching black text.
                    bool titleIsOnlyBand = node.Rows.Count == 0;
                    Color titleFillColor = isActive
                        ? _colors.ActiveTitleBackgroundColor.Value
                        : fadeT.HasValue
                            ? Color.Lerp(_colors.ActiveTitleBackgroundColor.Value, node.FillColor, fadeT.Value)
                            : node.FillColor;
                    AddRoundedRectToChromeBuffer(node.TitleRect, cornerRadius, titleFillColor, roundTop: true, roundBottom: titleIsOnlyBand);
                    // Title/row labels are deliberately NOT drawn here (see the label pass after both GL
                    // flushes below).

                    // Title/row divider - same color as the inner ring (see above), so every separator on
                    // the node reads as one consistent "outline" whether or not the state is active. Skipped
                    // entirely in the Standard box style ("no separating lines"), regardless of active/
                    // selected state.
                    float dividerThickness = NodeBorderThickness * _zoom;
                    if (!standardBoxStyle && node.Rows.Count > 0)
                    {
                        AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, node.TitleRect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), innerOutlineColor);
                    }

                    for (int i = 0; i < node.Rows.Count; i++)
                    {
                        TransitionRow row = node.Rows[i];
                        bool isLastRow = i == node.Rows.Count - 1;

                        if (isLastRow)
                        {
                            AddRoundedRectToChromeBuffer(row.Rect, cornerRadius, _colors.TransitionRowBackgroundColor.Value, roundTop: false, roundBottom: true);
                        }
                        else
                        {
                            AddFilledRectToChromeBuffer(row.Rect, _colors.TransitionRowBackgroundColor.Value);
                        }

                        if (!standardBoxStyle && !isLastRow)
                        {
                            AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, row.Rect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), innerOutlineColor);
                        }
                    }
                }
            }

            if (!chromeCacheValid)
            {
                _currentCache.ChromeCacheLayoutVersion = _currentCache.NodeLayoutVersion;
                _currentCache.ChromeCacheVisibleRect = visibleRect;
                _currentCache.ChromeCacheActiveStateName = activeStateName;
                _currentCache.ChromeCacheSelectedStateName = _selectedStateName;
                _currentCache.ChromeCacheBoxStyle = _performance.BoxStyle.Value;
                _currentCache.ChromeCacheConfigGeneration = _performance.Generation;
            }

            // One GL batch for every line gathered above - drawn before the chrome batch below so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            DrawLineBufferGL(canvasScreenOffset, interactiveRect.width, dim);

            // One GL batch for every rounded-rect/fill gathered above - drawn after the line batch so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            FlushChromeBufferGL(canvasScreenOffset, interactiveRect.width, dim);

            // Labels are drawn last, only once every line/chrome GL batch above has actually been
            // rendered. Drawing them earlier (interleaved with chrome collection, as a first attempt
            // at this batching did) meant the deferred chrome GL flush - which draws opaque
            // backgrounds - painted over labels that had already rendered several calls earlier in
            // the same Repaint pass, since GUI.Label and GL both draw immediately at the point they're
            // called, in call order. Re-walks the same cull checks rather than caching a visible-list,
            // since Rect.Overlaps is cheap and this keeps the two passes independent.
            //
            // Nested in its own GUI.BeginGroup(interactiveRect): unlike the GL-drawn lines/chrome above
            // (which needed the Viewport-based clip in ApplyClippedPixelMatrix, since raw GL bypasses
            // GUIClip entirely), GUI.Label calls are ordinary IMGUI content and respect GUIClip like any
            // other nested group - so a plain clip group is all that's needed here to keep a node's
            // title/event text from bleeding onto the uGUI right panel the same way its chrome would.
            GUI.BeginGroup(interactiveRect);

            // GUI.color multiplies whatever color a GUIStyle/GUI.Label call already specifies - cheaper
            // than threading dim through _titleStyle/_eventStyle/_globalEventStyle themselves, and reset
            // to white right after this group so it never leaks into some other IMGUI content drawn
            // later this same event.
            Color previousGuiColor = GUI.color;
            GUI.color = dim ? new Color(1f, 1f, 1f, DimAlphaMultiplier) : Color.white;

            if (_currentCache.GlobalPseudoNodeCache != null)
            {
                foreach (GlobalPseudoNodeLayout pseudo in _currentCache.GlobalPseudoNodeCache)
                {
                    if (!pseudo.Rect.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    GUI.Label(pseudo.Rect, pseudo.EventName, _globalEventStyle);
                }
            }

            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                if (!node.ScreenRect.Overlaps(visibleRect))
                {
                    continue;
                }

                // Default title text is white, switching to the fixed ActiveTitleTextColor (black,
                // matching ActiveTitleBackgroundColor's light blue - see the chrome pass above) only
                // for the active state's own name. Event/transition text stays this state's own
                // (desaturated) TransitionColors entry regardless of activity; only the state name
                // itself, its title/chrome background, its outline ring, and its outgoing lines (see
                // the chrome and line-color passes above) pick up the active styling. Both styles are
                // shared/mutable, so this only needs setting once per node right before its labels
                // draw, not rebuilt from scratch.
                bool nodeIsActive = !node.IsDisabled && activeStateName != null && node.Name == activeStateName;
                _titleStyle!.normal.textColor = node.IsDisabled ? _colors.DisabledTitleTextColor.Value : nodeIsActive ? _colors.ActiveTitleTextColor.Value : Color.white;
                _eventStyle!.normal.textColor = node.IsDisabled ? _colors.DisabledEventTextColor.Value : _colors.TransitionColors[node.ColorIndex].Value;

                GUI.Label(node.TitleRect, node.Name, _titleStyle);

                foreach (TransitionRow row in node.Rows)
                {
                    GUI.Label(row.Rect, row.EventName, _eventStyle);
                }
            }

            GUI.color = previousGuiColor;
            GUI.EndGroup();
        }

        // Gated on interactiveRect (not just node.ScreenRect) so a click on the uGUI right panel never
        // also selects whatever node happens to render underneath it - legacy OnGUI always composites
        // on top of the uGUI Canvas (regardless of draw order or Canvas sort order), so without this
        // exclusion the graph would both visually cover the panel and steal its clicks. IsPointerOverUi
        // is a second guard for uGUI content that can render outside that panel's own base rect (the
        // Open dropdown) - see ComputeInteractiveRect's own comment for why rect-subtraction alone isn't
        // enough for that case.
        //
        // Both mouse buttons are handled here (not just button 0, like the old select-only version) -
        // priority order is action zone (a transition's row box or its connecting curve - arms a
        // tentative click-or-hold gesture for button 0, see HandleTransitionDrag; resolves immediately
        // for button 1) > node, since an action zone sits on/near a node's title band and each
        // successive check is progressively less specific.
        if (interactive && Event.current.type == EventType.MouseDown
            && interactiveRect.Contains(Event.current.mousePosition) && !IsPointerOverUi())
        {
            Vector2 mousePos = Event.current.mousePosition;

            if (Event.current.button == 0 && _draggingTransition == null
                && TryHitTestActionZone(mousePos, out string actionOwnerStateName, out string actionEventName, out bool isSourceEnd))
            {
                // Not yet known to be a click or a drag - HandleTransitionDrag resolves that once the
                // mouse either crosses DragStartThreshold (drag) or comes back up first (click, fired
                // there via HandleTransitionLeftClick).
                _draggingTransition = new DraggingTransition
                {
                    OwningStateName = actionOwnerStateName,
                    EventName = actionEventName,
                    DraggingSourceEnd = isSourceEnd,
                    MouseDownScreenPos = mousePos,
                };
                Event.current.Use();
                return;
            }

            if (Event.current.button == 1
                && TryHitTestActionZone(mousePos, out string rightClickOwnerStateName, out string rightClickEventName, out _))
            {
                HandleTransitionRightClick(fsm, rightClickOwnerStateName, rightClickEventName);
                Event.current.Use();
                return;
            }

            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                if (!node.ScreenRect.Contains(mousePos))
                {
                    continue;
                }

                if (Event.current.button == 0)
                {
                    HandleNodeLeftClick(fsm, node.Name);
                }
                else if (Event.current.button == 1)
                {
                    HandleNodeRightClick(fsm, node.Name);
                }

                Event.current.Use();
                break;
            }
        }
    }

    // ---- Click/double-click/right-click handlers ----

    // A second MouseDown on the same node within DoubleClickWindowSeconds forces the FSM into that
    // state via PlayMaker's own Fsm.SetState, which exits/enters properly via SwitchState rather than
    // a raw ActiveState assignment - and only affects the one live instance this tab is showing, not
    // every instance that happens to share this FsmKey. A single click still just selects, as before.
    private void HandleNodeLeftClick(FsmInfo fsm, string stateName)
    {
        double now = Time.realtimeSinceStartup;
        if (_lastNodeClickName == stateName && now - _lastNodeClickTime <= DoubleClickWindowSeconds)
        {
            fsm.Fsm.SetState(stateName);
            _logger.LogInfo($"[FsmMaster] Forced fsm '{fsm.FsmName}' on '{fsm.GameObjectName}' into state '{stateName}'.");
            _lastNodeClickTime = double.NegativeInfinity;
            _lastNodeClickName = null;
        }
        else
        {
            _lastNodeClickTime = now;
            _lastNodeClickName = stateName;
        }

        _selectedStateName = stateName;
    }

    private void HandleNodeRightClick(FsmInfo fsm, string stateName)
    {
        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
        FsmEditSet? editSet = _editManager.GetActiveEditSet(fsmKey);
        bool isDisabled = editSet != null && editSet.DisabledStates.Contains(stateName);

        if (isDisabled)
        {
            _editManager.EnableState(fsmKey, stateName);
        }
        else
        {
            _editManager.DisableState(fsmKey, stateName);
        }

        InvalidateGraphCaches();
    }

    // owningStateName == "" means a global transition's pseudo-node line. For a per-state transition,
    // selects that state and requests a scroll to whichever action sends this event (correlated against
    // the already-collected FsmActionInfo.Fields - no new reflection walk). For a global transition,
    // scans every currently open tab (not the whole scene) for the first state with a matching action
    // and focuses that tab.
    private void HandleTransitionLeftClick(FsmInfo fsm, string owningStateName, string eventName)
    {
        if (owningStateName != "")
        {
            _selectedStateName = owningStateName;

            FsmStateInfo? state = FindStateInfo(fsm, owningStateName);
            int? actionIndex = state != null ? FindActionIndexForEvent(state, eventName) : null;
            if (actionIndex.HasValue)
            {
                _pendingScrollActionIndex = actionIndex;
            }

            return;
        }

        foreach (FsmTabState tab in _tabManager.Tabs)
        {
            FsmInfo? candidateFsm = ResolveFsmInfo(tab.FsmKey);
            if (candidateFsm == null)
            {
                continue;
            }

            foreach (FsmStateInfo state in candidateFsm.States)
            {
                int? actionIndex = FindActionIndexForEvent(state, eventName);
                if (!actionIndex.HasValue)
                {
                    continue;
                }

                tab.SelectedStateName = state.Name;
                tab.PendingScrollActionIndex = actionIndex;
                _tabManager.Focus(tab);
                return;
            }
        }
    }

    private void HandleTransitionRightClick(FsmInfo fsm, string owningStateName, string eventName)
    {
        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
        FsmEditSet? editSet = _editManager.GetActiveEditSet(fsmKey);
        bool isDisabled = editSet != null && editSet.TransitionRetargets.Exists(t =>
            t.StateName == owningStateName && t.EventName == eventName && t.NewToState == TransitionRetarget.DisabledMarker);

        if (isDisabled)
        {
            _editManager.EnableTransition(fsmKey, owningStateName, eventName);
        }
        else
        {
            _editManager.DisableTransition(fsmKey, owningStateName, eventName);
        }

        InvalidateGraphCaches();
    }

    private static FsmStateInfo? FindStateInfo(FsmInfo fsm, string stateName)
    {
        foreach (FsmStateInfo state in fsm.States)
        {
            if (state.Name == stateName)
            {
                return state;
            }
        }

        return null;
    }

    // Scans a state's already-collected FsmActionInfo.Fields (from FsmDataCollector - the same generic
    // reflection walk it already does for read-only display, not a new one) for a field whose value is
    // an FsmEvent with a matching .Name, or an FsmEvent[] containing one - compared by name, not
    // reference, since FsmEvent.GetFsmEvent only interns while PlayMakerGlobals.IsPlaying and
    // FsmUtil.AddGlobalTransition builds events via `new FsmEvent(...)` instead of that interning path.
    // First match wins, consistent with FsmEditManager.FindExitEvent's own first/last-match precedent.
    private static int? FindActionIndexForEvent(FsmStateInfo state, string eventName)
    {
        for (int actionIndex = 0; actionIndex < state.Actions.Count; actionIndex++)
        {
            foreach (FsmActionFieldInfo field in state.Actions[actionIndex].Fields)
            {
                if (field.FieldValue is FsmEvent fsmEvent && fsmEvent.Name == eventName)
                {
                    return actionIndex;
                }

                if (field.FieldValue is FsmEvent[] fsmEvents)
                {
                    foreach (FsmEvent? candidate in fsmEvents)
                    {
                        if (candidate != null && candidate.Name == eventName)
                        {
                            return actionIndex;
                        }
                    }
                }
            }
        }

        return null;
    }

    // ---- Transition hit-testing ----

    // The combined "this is action X" zone used to arm a click-or-hold gesture on MouseDown (see the
    // MouseDown block above) - the row's own labeled box (or a global pseudo-node's box; delegates to
    // TryHitTestEventNodeBox), widened by the connecting curve/arrow itself (TryHitTestTransitionLine),
    // so grabbing anywhere on a transition - its event label inside the owning state, or the line/arrow
    // connecting it to its target - counts, not just a precise few-pixel radius around one of its two
    // curve endpoints. Whether the gesture then resolves as a click or a drag is decided afterwards by
    // HandleTransitionDrag's own DragStartThreshold check, not here.
    //
    // isSourceEnd is only meaningful for a per-state row (a global pseudo-node has no source anchor of
    // its own - see DraggingTransition's own comment) and is picked by whichever of the row's two curve
    // endpoints sits closer to mousePos - purely to choose which anchor the drag rubber-band preview
    // treats as fixed (see ResolveDragAnchor). It has no effect on what a completed drag actually does
    // (see CommitTransitionDrag, which only looks at where the drag is dropped).
    private bool TryHitTestActionZone(Vector2 mousePos, out string owningStateName, out string eventName, out bool isSourceEnd)
    {
        if (!TryHitTestEventNodeBox(mousePos, out owningStateName, out eventName)
            && !TryHitTestTransitionLine(mousePos, out owningStateName, out eventName))
        {
            isSourceEnd = false;
            return false;
        }

        isSourceEnd = false;
        if (owningStateName != "" && _currentCache.NodeLayoutCache != null && _currentCache.NodeLayoutCache.TryGetValue(owningStateName, out NodeLayout? node))
        {
            foreach (TransitionRow row in node.Rows)
            {
                if (row.EventName == eventName)
                {
                    isSourceEnd = Vector2.Distance(mousePos, row.CurvePoints[0]) <= Vector2.Distance(mousePos, row.CurvePoints[^1]);
                    break;
                }
            }
        }

        return true;
    }

    // Used both as the drop-target test for a committed drag (see CommitTransitionDrag) and, via
    // TryHitTestActionZone above, as half of the MouseDown arming zone - deliberately the row/pseudo-
    // node's own small labeled Rect, NOT the connecting curve (TryHitTestTransitionLine): an incoming
    // transition's curve necessarily terminates right at/inside its target state's title band, so
    // testing the whole curve here would make dropping ON a state ambiguous with dropping onto
    // whatever transition happens to already arrive there. The label box has no such overlap - it
    // always sits entirely within its own owning state (or its own free-standing pseudo-node), never
    // inside a neighbor.
    private bool TryHitTestEventNodeBox(Vector2 dropScreenPos, out string owningStateName, out string eventName)
    {
        if (_currentCache.NodeLayoutCache != null)
        {
            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                foreach (TransitionRow row in node.Rows)
                {
                    if (row.Rect.Contains(dropScreenPos))
                    {
                        owningStateName = node.Name;
                        eventName = row.EventName;
                        return true;
                    }
                }
            }
        }

        if (_currentCache.GlobalPseudoNodeCache != null)
        {
            foreach (GlobalPseudoNodeLayout pseudo in _currentCache.GlobalPseudoNodeCache)
            {
                if (pseudo.Rect.Contains(dropScreenPos))
                {
                    owningStateName = "";
                    eventName = pseudo.EventName;
                    return true;
                }
            }
        }

        owningStateName = "";
        eventName = "";
        return false;
    }

    // Wider tolerance along the whole sampled polyline, not just its endpoints - used both for the
    // MouseDown arming zone above (TryHitTestActionZone) and, on its own, for plain right-clicks
    // (disable) on the line itself outside any row box.
    private bool TryHitTestTransitionLine(Vector2 mousePos, out string owningStateName, out string eventName)
    {
        // Fixed screen-pixel tolerance, deliberately NOT scaled by _zoom like most other on-screen
        // lengths in this file (BezierControlOffset, NodeBorderThickness, etc.) - those scale because
        // the shapes they size grow/shrink with zoom, but click precision is a physical mouse/hand-
        // precision matter that doesn't change with camera zoom; scaling this down at low zoom would
        // make a zoomed-out graph's small rendered targets even harder to hit exactly when they need
        // more forgiveness, not less.
        float tolerance = TransitionLineHitTolerance;
        var testBounds = new Rect(mousePos.x - tolerance, mousePos.y - tolerance, tolerance * 2f, tolerance * 2f);

        if (_currentCache.NodeLayoutCache != null)
        {
            foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
            {
                foreach (TransitionRow row in node.Rows)
                {
                    if (!row.CurveBounds.Overlaps(testBounds))
                    {
                        continue;
                    }

                    if (IsPointNearPolyline(mousePos, row.CurvePoints, tolerance))
                    {
                        owningStateName = node.Name;
                        eventName = row.EventName;
                        return true;
                    }
                }
            }
        }

        if (_currentCache.GlobalPseudoNodeCache != null)
        {
            foreach (GlobalPseudoNodeLayout pseudo in _currentCache.GlobalPseudoNodeCache)
            {
                if (IsPointNearPolyline(mousePos, pseudo.ArrowPoints, tolerance))
                {
                    owningStateName = "";
                    eventName = pseudo.EventName;
                    return true;
                }
            }
        }

        owningStateName = "";
        eventName = "";
        return false;
    }

    private static bool IsPointNearPolyline(Vector2 point, Vector2[] polyline, float tolerance)
    {
        for (int i = 0; i < polyline.Length - 1; i++)
        {
            if (DistancePointToSegment(point, polyline[i], polyline[i + 1]) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.sqrMagnitude;
        if (lengthSquared < 0.0001f)
        {
            return Vector2.Distance(point, a);
        }

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
        Vector2 projection = a + ab * t;
        return Vector2.Distance(point, projection);
    }

    // ---- Endpoint drag-to-rebind ----

    // Called every OnGUI pass alongside HandlePanAndZoom - a no-op unless a drag was armed by the
    // MouseDown handling above. MouseDown itself is handled there (not here), since it needs to run
    // inside the same hit-testing pass as the plain click handlers it takes priority over.
    private void HandleTransitionDrag(FsmInfo fsm, Rect interactiveRect)
    {
        if (_draggingTransition == null)
        {
            return;
        }

        Event current = Event.current;

        if (current.type == EventType.MouseDrag)
        {
            if (!_draggingTransition.HasCrossedThreshold
                && Vector2.Distance(current.mousePosition, _draggingTransition.MouseDownScreenPos) >= DragStartThreshold)
            {
                _draggingTransition.HasCrossedThreshold = true;
            }

            current.Use();
            return;
        }

        if (current.type != EventType.MouseUp)
        {
            return;
        }

        DraggingTransition dragging = _draggingTransition;
        _draggingTransition = null;
        current.Use();

        if (!dragging.HasCrossedThreshold)
        {
            // Released without ever crossing the drag threshold - the endpoint was just clicked, not
            // dragged, so it's a plain "left click a transition line" (endpoints are part of the line).
            HandleTransitionLeftClick(fsm, dragging.OwningStateName, dragging.EventName);
            return;
        }

        CommitTransitionDrag(fsm, dragging, current.mousePosition);
    }

    // Resolves the drag preview's anchor point AND its exit direction together, re-picked against the
    // live mouse position every call (not cached from layout time) - see the Repaint call site above.
    // For a per-state row this re-runs the same left/right-edge choice PickExitDirection makes for a
    // committed transition, just against mousePos instead of a target rect, so the anchor slides to
    // whichever edge of the row (source end) or target's title band (target end) is currently closer
    // to the cursor rather than staying pinned to whichever edge was closer when the drag started.
    // A global pseudo-node has no such left/right choice (it always enters its target from directly
    // above - see GlobalPseudoNodeLayout's own construction), so its anchor/direction stay fixed.
    private (Vector2 Anchor, Vector2 ExitDirection)? ResolveDragAnchor(Vector2 mousePos)
    {
        if (_draggingTransition == null)
        {
            return null;
        }

        if (_draggingTransition.OwningStateName == "")
        {
            if (_currentCache.GlobalPseudoNodeCache != null)
            {
                foreach (GlobalPseudoNodeLayout pseudo in _currentCache.GlobalPseudoNodeCache)
                {
                    if (pseudo.EventName == _draggingTransition.EventName)
                    {
                        return (pseudo.ArrowPoints[1], new Vector2(0f, -1f));
                    }
                }
            }

            return null;
        }

        if (_currentCache.NodeLayoutCache != null && _currentCache.NodeLayoutCache.TryGetValue(_draggingTransition.OwningStateName, out NodeLayout? node))
        {
            foreach (TransitionRow row in node.Rows)
            {
                if (row.EventName != _draggingTransition.EventName)
                {
                    continue;
                }

                Rect anchorRect = row.Rect;
                if (!_draggingTransition.DraggingSourceEnd
                    && _currentCache.NodeLayoutCache.TryGetValue(row.ToState, out NodeLayout? targetNode))
                {
                    anchorRect = targetNode.TitleRect;
                }

                Vector2 exitDirection = mousePos.x >= anchorRect.center.x ? new Vector2(1f, 0f) : new Vector2(-1f, 0f);
                Vector2 anchor = new(exitDirection.x > 0f ? anchorRect.xMax : anchorRect.x, anchorRect.center.y);
                return (anchor, exitDirection);
            }
        }

        return null;
    }

    // Drop priority: (1) another event node (transition row or global pseudo-node) anywhere in the
    // graph - relocates this transition to originate there and adopt that event's name, deleting
    // whatever transition previously occupied that slot (RetargetTransitionEvent); (2) a state's
    // title/body - retargets just the ToState, keeping the same origin/event (the existing, already-
    // tested ApplyTransitionRetarget path). Dropping back onto the same slot/state it started from is a
    // no-op either way.
    //
    // Priority (1) deliberately tests the row/pseudo-node's own small labeled Rect (TryHitTestEventNodeBox),
    // NOT the connecting curve (TryHitTestTransitionLine, used for plain clicks elsewhere): an incoming
    // transition's curve necessarily terminates right at/inside its target state's title band, so testing
    // the whole curve here made dropping ON a state ambiguous with dropping onto whatever transition
    // happened to already arrive there - the most common "drag onto a different state" gesture was
    // getting misread as "rebind onto that state's own incoming transition" instead, deleting the wrong
    // transition and creating an unrelated one. The label box has no such overlap - it always sits
    // entirely within its own owning state (or its own free-standing pseudo-node), never inside a neighbor.
    private void CommitTransitionDrag(FsmInfo fsm, DraggingTransition dragging, Vector2 dropScreenPos)
    {
        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);

        if (TryHitTestEventNodeBox(dropScreenPos, out string targetOwnerStateName, out string targetEventName))
        {
            bool isSameSlot = targetOwnerStateName == dragging.OwningStateName && targetEventName == dragging.EventName;
            if (isSameSlot)
            {
                return;
            }

            string? currentToState = ReadCurrentToState(fsm, dragging.OwningStateName, dragging.EventName);
            if (currentToState == null)
            {
                return;
            }

            _editManager.RetargetTransitionEvent(fsmKey, dragging.OwningStateName, dragging.EventName, targetOwnerStateName, targetEventName, currentToState);
            InvalidateGraphCaches();
            return;
        }

        if (_currentCache.NodeLayoutCache == null)
        {
            return;
        }

        foreach (NodeLayout node in _currentCache.NodeLayoutCache.Values)
        {
            if (!node.ScreenRect.Contains(dropScreenPos))
            {
                continue;
            }

            if (node.Name == dragging.OwningStateName)
            {
                return;
            }

            _editManager.RetargetTransitionToState(fsmKey, dragging.OwningStateName, dragging.EventName, node.Name);
            InvalidateGraphCaches();
            return;
        }
    }

    private static string? ReadCurrentToState(FsmInfo fsm, string owningStateName, string eventName)
    {
        FsmTransition? transition = owningStateName == ""
            ? fsm.Fsm.GetGlobalTransition(eventName)
            : fsm.Fsm.GetTransition(owningStateName, eventName);
        return transition?.ToState;
    }

    // Drives left-drag panning and scroll-wheel zoom for the active graph, updating _panWorldCenter/
    // _zoom directly from mouse input.
    private void HandlePanAndZoom(Rect canvasRect)
    {
        Event current = Event.current;

        // IsPointerOverUi only gates *starting* a new pan/zoom gesture - an already-in-progress drag
        // (the MouseDrag branch below) keeps going even if the cursor drifts over uGUI content
        // mid-drag, matching how a drag normally isn't interrupted by crossing over an overlapping
        // widget.
        if (current.type == EventType.MouseDown && current.button == 0 && canvasRect.Contains(current.mousePosition) && !IsPointerOverUi())
        {
            _isPanning = true;
        }
        else if (current.type == EventType.MouseUp)
        {
            _isPanning = false;
        }
        else if (current.type == EventType.MouseDrag && _isPanning)
        {
            _panWorldCenter -= current.delta / _zoom;
            current.Use();
        }
        else if (current.type == EventType.ScrollWheel && canvasRect.Contains(current.mousePosition) && !IsPointerOverUi())
        {
            _zoom = Mathf.Clamp(_zoom - current.delta.y * ZoomSpeed, MinZoom, MaxZoom);
            current.Use();
        }
    }

    // Corner radius in screen pixels for a whole node - clamped so it never exceeds half the node's
    // width, nor half the height of its shortest band (title bar / transition row). Deliberately
    // NOT based on whichever individual rect is being drawn: a node's title/rows are much shorter
    // than the node as a whole, so clamping per-band independently used to shrink their radius below
    // the outer ring's, leaving the two curves mismatched (see the call site in DrawCachedGraph).
    private float GetNodeCornerRadius(Rect nodeScreenRect)
    {
        float shortestBandHeight = Mathf.Min(TitleBarHeight, TransitionRowHeight) * _zoom;
        return Mathf.Min(NodeCornerRadius * _zoom, Mathf.Min(nodeScreenRect.width, shortestBandHeight) / 2f);
    }

    // Corner radius for a single free-standing rect with no concentric sibling rects to stay aligned
    // with (currently only the global-transition pseudo node) - safe to clamp against its own size.
    private float GetScreenCornerRadius(Rect rect)
    {
        return Mathf.Min(NodeCornerRadius * _zoom, Mathf.Min(rect.width, rect.height) / 2f);
    }

    // The active-indicator ring's color: a genuinely themed state (colorIndex 1-7) uses its own
    // StateColors entry, so its outline reads as "its own color, just thicker" while active. ColorIndex
    // 0 (PlayMaker's "no color set" default) has no theme color to borrow, so it falls back to the
    // fixed ActiveStateColor instead.
    private Color GetActiveOutlineColor(int colorIndex)
    {
        return colorIndex == 0 ? _colors.ActiveStateColor.Value : _colors.StateColors[colorIndex].Value;
    }

    // The active halo ring has no "default" (non-active) counterpart to tween its color toward - a
    // node with no ring at all isn't a color, it's an absence - so a fading ring instead keeps
    // ActiveStateColor's own RGB and fades its alpha toward 0, reading as the same halo gradually
    // dissolving rather than shifting to some other hue. FlushChromeBufferGL's material is already set
    // up for SrcAlpha/OneMinusSrcAlpha blending (see EnsureGlMaterial), so this alpha is respected.
    private Color GetFadingActiveColor(float fadeT)
    {
        Color active = _colors.ActiveStateColor.Value;
        return new Color(active.r, active.g, active.b, active.a * (1f - fadeT));
    }

    private const int CornerFanSegments = 8;

    private void AddTriangleToChromeBuffer(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        _currentCache.ChromeVertexBuffer.Add((a, color));
        _currentCache.ChromeVertexBuffer.Add((b, color));
        _currentCache.ChromeVertexBuffer.Add((c, color));
    }

    private void AddFilledRectToChromeBuffer(Rect rect, Color color)
    {
        var topLeft = new Vector2(rect.x, rect.y);
        var topRight = new Vector2(rect.xMax, rect.y);
        var bottomRight = new Vector2(rect.xMax, rect.yMax);
        var bottomLeft = new Vector2(rect.x, rect.yMax);
        AddTriangleToChromeBuffer(topLeft, topRight, bottomRight, color);
        AddTriangleToChromeBuffer(topLeft, bottomRight, bottomLeft, color);
    }

    // Appends a solid rounded rect to _currentCache.ChromeVertexBuffer: straight edges/center are plain flat-fill
    // triangles, and each corner is a small triangle fan - the GL/geometry equivalent of the old
    // DrawRoundedRect, which stretched a pre-baked corner-mask texture per corner instead.
    //
    // radius is always passed in by the caller rather than derived from `rect` here - callers that
    // draw several concentric or adjoining rounded rects for the same node (outer ring, title, rows)
    // must share one consistent radius (see GetNodeCornerRadius) rather than each recomputing its
    // own from its own (possibly much shorter) rect, or their curves visibly stop matching.
    //
    // roundTop/roundBottom let a caller round only the corners it actually touches - a node's title
    // band and its last transition row are each their own differently-colored rect, and only the
    // ends that meet the node's own rounded silhouette should be rounded (in that band's own color);
    // rounding all 4 corners of every band unconditionally previously left a sliver of whatever the
    // shared underlying body-fill color was showing through at the title's corners, since only the
    // last row's corners are ever supposed to be that color.
    private void AddRoundedRectToChromeBuffer(Rect rect, float radius, Color color, bool roundTop = true, bool roundBottom = true)
    {
        radius = Mathf.Min(radius, Mathf.Min(rect.width, rect.height) / 2f);
        radius = (roundTop || roundBottom) ? radius : 0f;
        float topInset = roundTop ? radius : 0f;
        float bottomInset = roundBottom ? radius : 0f;

        AddFilledRectToChromeBuffer(new Rect(rect.x + radius, rect.y, Mathf.Max(0f, rect.width - radius * 2f), rect.height), color);
        AddFilledRectToChromeBuffer(new Rect(rect.x, rect.y + topInset, radius, Mathf.Max(0f, rect.height - topInset - bottomInset)), color);
        AddFilledRectToChromeBuffer(new Rect(rect.xMax - radius, rect.y + topInset, radius, Mathf.Max(0f, rect.height - topInset - bottomInset)), color);

        if (roundTop)
        {
            AddCornerFanToChromeBuffer(new Vector2(rect.x + radius, rect.y + radius), radius, fromDegrees: 180f, color);
            AddCornerFanToChromeBuffer(new Vector2(rect.xMax - radius, rect.y + radius), radius, fromDegrees: 270f, color);
        }

        if (roundBottom)
        {
            AddCornerFanToChromeBuffer(new Vector2(rect.x + radius, rect.yMax - radius), radius, fromDegrees: 90f, color);
            AddCornerFanToChromeBuffer(new Vector2(rect.xMax - radius, rect.yMax - radius), radius, fromDegrees: 0f, color);
        }
    }

    // Fills a 90-degree arc (fromDegrees..fromDegrees+90, standard math convention: 0 = +x axis,
    // increasing counter-clockwise) around center with the given radius, as a triangle fan - the
    // corner-rounding equivalent of the old baked quarter-circle corner-mask texture draw. Screen-
    // space corner assignment: top-left is the 180-270 arc, top-right 270-360, bottom-left 90-180,
    // bottom-right 0-90 (matches the old DrawCornerMask's towardRight/towardBottom convention, just
    // parameterized by angle instead of a baked texture orientation).
    private void AddCornerFanToChromeBuffer(Vector2 center, float radius, float fromDegrees, Color color)
    {
        if (radius < 0.5f)
        {
            return;
        }

        for (int i = 0; i < CornerFanSegments; i++)
        {
            float angle0 = (fromDegrees + 90f * i / CornerFanSegments) * Mathf.Deg2Rad;
            float angle1 = (fromDegrees + 90f * (i + 1) / CornerFanSegments) * Mathf.Deg2Rad;
            Vector2 p0 = center + new Vector2(Mathf.Cos(angle0), Mathf.Sin(angle0)) * radius;
            Vector2 p1 = center + new Vector2(Mathf.Cos(angle1), Mathf.Sin(angle1)) * radius;
            AddTriangleToChromeBuffer(center, p0, p1, color);
        }
    }

    // Appends `rect` as the rounded, filled content area, with a rounded outline ring grown outward
    // around it - rather than shrinking the fill inward, so title/row bands (which are still drawn
    // flush to this same `rect` in DrawCachedGraph) never end up overlapping the border itself.
    // Global-transition pseudo-nodes have no active/selected concept of their own, so unlike a state
    // box's inner ring (see the Standard-box conditional in DrawCachedGraph) this outline always draws
    // regardless of box style - only the corner rounding itself is affected.
    private void AddRoundedRectOutlineToChromeBuffer(Rect rect, Color fillColor, Color outlineColor, float borderThickness)
    {
        var outerRect = new Rect(
            rect.x - borderThickness,
            rect.y - borderThickness,
            rect.width + borderThickness * 2f,
            rect.height + borderThickness * 2f);

        float radius = _performance.BoxStyle.Value == GraphBoxStyle.Standard ? 0f : GetScreenCornerRadius(rect);
        AddRoundedRectToChromeBuffer(outerRect, radius + borderThickness, outlineColor);
        AddRoundedRectToChromeBuffer(rect, radius, fillColor);
    }

    // Draws every triangle gathered into _currentCache.ChromeVertexBuffer this frame as a single GL batch,
    // replacing what used to be up to ~7 GUI.DrawTexture calls per rounded rect (flat fills plus a
    // baked corner-mask texture draw per corner) - this was the confirmed second-largest cost in the
    // graph overlay's Repaint path after the transition lines (see the PERF diagnostics above). Shares
    // the same material/pixel-matrix-offset approach as DrawLineBufferGL, drawn after it so chrome
    // sits on top of the lines.
    // clipRightAbsolute is interactiveRect.width (screen minus the uGUI right panel's docked strip -
    // see ApplyClippedPixelMatrix for why this needs a real Viewport clip, not just bounds culling).
    // Pinned-but-inactive tabs (see FsmGraphOverlay.OnGUI) blend every color toward a neutral grey and
    // pull back alpha, applied here at flush time rather than baked into the gathered buffers - a pure
    // per-draw multiplier means the same cached ChromeVertexBuffer/LineDrawBuffer contents are correct
    // whether or not this particular Repaint happens to be a dimmed pass (see ChromeCacheLayoutVersion's
    // own comment on why dim isn't part of that cache's staleness key).
    private const float DimGreyBlend = 0.6f;
    private const float DimAlphaMultiplier = 0.55f;

    private static Color ApplyDim(Color color, bool dim)
    {
        if (!dim)
        {
            return color;
        }

        var grey = new Color(0.5f, 0.5f, 0.5f, color.a);
        Color blended = Color.Lerp(color, grey, DimGreyBlend);
        blended.a = color.a * DimAlphaMultiplier;
        return blended;
    }

    // Draws every triangle gathered into _currentCache.ChromeVertexBuffer this frame as a single GL batch.
    private void FlushChromeBufferGL(Vector2 canvasScreenOffset, float clipRightAbsolute, bool dim)
    {
        if (_currentCache.ChromeVertexBuffer.Count == 0)
        {
            return;
        }

        EnsureGlMaterial();
        if (_glMaterial == null)
        {
            return;
        }

        GL.PushMatrix();
        ApplyClippedPixelMatrix(canvasScreenOffset, clipRightAbsolute);
        _glMaterial.SetPass(0);

        GL.Begin(GL.TRIANGLES);
        foreach ((Vector2 position, Color color) in _currentCache.ChromeVertexBuffer)
        {
            GL.Color(ApplyDim(color, dim));
            GL.Vertex3(position.x, position.y, 0f);
        }
        GL.End();

        GL.PopMatrix();
        ResetGlViewport();
    }

    // Restricts subsequent GL drawing to absolute screen-space x in [0, clipRightAbsolute) (always the
    // full screen height - the uGUI panel this exists to avoid drawing under is always docked with a
    // full-height exclusion strip, see ComputeInteractiveRect). Bounds-culling individual shapes (see
    // the visibleRect checks in DrawCachedGraph) only skips shapes ENTIRELY outside that region - a
    // transition curve or node straddling the boundary would still have the portion past it rendered,
    // since raw GL bypasses Unity's GUIClip stack entirely (unlike GUI.Label/GUI.DrawTexture, which
    // GUI.BeginGroup already clips). A real Viewport is the closest thing to a scissor rect available
    // here: narrowing GL.Viewport alone would just squash everything already inside it into the smaller
    // area (per Unity's own docs on GL.Viewport), so LoadPixelMatrix's right bound is narrowed to
    // exactly match the new Viewport width - the visible portion still maps pixel-for-pixel as before,
    // while anything past clipRightAbsolute now falls outside the projection's NDC range and is
    // discarded by ordinary GPU clipping instead of being stretched into view.
    //
    // Viewport is global GL state, not part of the PushMatrix/PopMatrix stack this sits inside of - the
    // caller must pair this with ResetGlViewport once its GL.Begin/End block is done, or every other GL
    // consumer this frame (and into the next) would inherit this narrowed viewport.
    private static void ApplyClippedPixelMatrix(Vector2 canvasScreenOffset, float clipRightAbsolute)
    {
        GL.Viewport(new Rect(0f, 0f, clipRightAbsolute, Screen.height));
        GL.LoadPixelMatrix(
            -canvasScreenOffset.x,
            clipRightAbsolute - canvasScreenOffset.x,
            Screen.height - canvasScreenOffset.y,
            -canvasScreenOffset.y);
    }

    private static void ResetGlViewport()
    {
        GL.Viewport(new Rect(0f, 0f, Screen.width, Screen.height));
    }

    // Lazily creates the unlit, vertex-colored material every GL batch (lines and chrome alike) is
    // drawn through - built from a shader that ships with every Unity player (no new asset/package
    // dependency), same lazy-init + Shutdown-teardown pattern as the rest of this class's cached
    // resources.
    private void EnsureGlMaterial()
    {
        if (_glMaterial != null || _glMaterialFailed)
        {
            return;
        }

        Shader? shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            _glMaterialFailed = true;
            _logger.LogError("[FsmMaster] Graph overlay: built-in shader \"Hidden/Internal-Colored\" was not found - transition lines will not render.");
            return;
        }

        _glMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _glMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _glMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _glMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        _glMaterial.SetInt("_ZWrite", 0);
    }

    // Draws every polyline gathered into _currentCache.LineDrawBuffer this frame as a single GL batch, replacing
    // what used to be one GUI.DrawTexture call per bezier segment (up to MaxBezierSegments per edge,
    // plus 2 more per arrowhead) - this was the confirmed dominant cost in the graph overlay's
    // Repaint path (see the PERF diagnostics above). GL.LoadPixelMatrix maps GL vertex coordinates
    // onto the same top-left-origin, y-down pixel convention GUI uses, but unlike GUI.Label/
    // GUI.DrawTexture calls made inside the caller's GUI.BeginGroup, raw GL isn't subject to Unity's
    // GUIClip stack, so it never picks up that group's screen-space offset on its own - canvasScreenOffset
    // (the group's own Rect.position) has to be folded into the matrix here instead, or every line
    // renders shifted from the nodes by that offset (this was the reported "lines never line up with
    // the nodes" bug: constant in screen pixels, so more noticeable relative to small zoomed-out nodes
    // and easy to mistake for "fixed by zooming in" when large zoomed-in nodes happen to still overlap it).
    // clipRightAbsolute is interactiveRect.width (screen minus the uGUI right panel's docked strip -
    // see ApplyClippedPixelMatrix for why this needs a real Viewport clip, not just bounds culling).
    private void DrawLineBufferGL(Vector2 canvasScreenOffset, float clipRightAbsolute, bool dim)
    {
        if (_currentCache.LineDrawBuffer.Count == 0)
        {
            return;
        }

        EnsureGlMaterial();
        if (_glMaterial == null)
        {
            return;
        }

        GL.PushMatrix();
        ApplyClippedPixelMatrix(canvasScreenOffset, clipRightAbsolute);
        _glMaterial.SetPass(0);

        // Line detail level is a persistent, always-applied config choice (see FsmGraphPerformanceConfig)
        // rather than tied to the toggle-minimal-view hotkey. Thin/Straight ignore each line's own
        // Thickness (GL.LINES has no per-vertex width control); Straight also skips the arrowhead
        // entirely and the curve itself, drawing only the transition's real source/target anchors -
        // points[0]/points[^1] of the cached bezier polyline are always those true endpoints regardless
        // of how the curve bends between them (see SampleBezierCurve), so no separate cache is needed
        // for this style.
        switch (_performance.LineStyle.Value)
        {
            case GraphLineStyle.Straight:
                GL.Begin(GL.LINES);
                foreach ((Vector2[] points, Color color, _, _, _) in _currentCache.LineDrawBuffer)
                {
                    if (points.Length >= 2)
                    {
                        EmitThinSegment(points[0], points[^1], ApplyDim(color, dim));
                    }
                }
                GL.End();
                break;

            case GraphLineStyle.Thin:
                GL.Begin(GL.LINES);
                foreach ((Vector2[] points, Color color, _, float baseArrowLength, float baseOutlineMargin) in _currentCache.LineDrawBuffer)
                {
                    EmitThinPolyline(points, ApplyDim(color, dim), baseArrowLength * _zoom, baseOutlineMargin * _zoom);
                }
                GL.End();
                break;

            default:
                GL.Begin(GL.TRIANGLES);
                foreach ((Vector2[] points, Color color, float baseThickness, float baseArrowLength, float baseOutlineMargin) in _currentCache.LineDrawBuffer)
                {
                    EmitThickPolyline(points, ApplyDim(color, dim), baseThickness * _zoom, baseArrowLength * _zoom, baseOutlineMargin * _zoom);
                }
                GL.End();
                break;
        }

        GL.PopMatrix();
        ResetGlViewport();
    }

    // Base screen-space arrowhead size at the default TransitionLineThickness (scaled by zoom like
    // every other on-screen length constant here - see BezierControlOffset) rather than derived from
    // the distance between the curve's last two sampled points: that distance is only
    // ~BezierTargetSegmentLength (14 world units) to begin with, and the old "0.15 of that" arrowhead
    // worked out to just a couple of screen pixels - visually indistinguishable from the line itself.
    // Direction still comes from those two points (the curve's approach tangent). Actual arrow length
    // scales with each line's own thickness (see EmitThinArrowhead/EmitThickArrowhead) so the active
    // state's thicker transitions get a proportionally bigger arrowhead to match.
    private const float ArrowheadLength = 14f;

    // How far back along the curve (in screen pixels, at the arrowhead's own zoom/thickness-scaled
    // size - see ArrowheadLength) to read the angle from, expressed as a multiple of that size rather
    // than a fixed number of sampled points. A fixed segment count was wrong: SampleBezierCurve only
    // targets ~BezierTargetSegmentLength per segment on average, and actually clamps the segment
    // count to [MinBezierSegments, MaxBezierSegments] - outside that window a curve's real per-segment
    // screen length drifts well away from the target (shorter when a small, zoomed-out curve still
    // gets the minimum segment count; longer when a large, zoomed-in curve is capped at the maximum).
    // A fixed segment count back therefore covered a wildly different on-screen distance depending on
    // zoom. Walking back by a fixed *distance* instead (see WalkBackAlongPolyline) keeps the angle's
    // sample window - and so the arrowhead's apparent shape - consistent at any zoom level.
    private const float ArrowheadAngleLookbackFactor = 0.6f;

    // Half-angle of each arrowhead wing off the approach direction - shared by EmitThinArrowhead,
    // EmitThickArrowhead, and the line-trim distance below, so all three agree on the same triangle
    // shape rather than the wing rotation being a magic number duplicated in two places.
    private const float ArrowheadWingAngleDegrees = 25f;

    // How far back from the tip the arrowhead triangle's own base (the line between its two wing
    // points) sits, as a fraction of ArrowheadLength - cos(wing angle) because the wings are
    // ArrowheadLength away from the tip but rotated off the centerline. Used to trim the filled
    // polyline's endpoint back to that base (see EmitThickPolyline) so its flat, full-thickness cap
    // doesn't poke out past the arrowhead's pointed tip, which is narrower than the line at that point.
    private static readonly float ArrowheadBackTrimFactor = Mathf.Cos(ArrowheadWingAngleDegrees * Mathf.Deg2Rad);

    // Walks backward from the curve's endpoint along its sampled points, accumulating on-screen
    // distance, and returns the point `distance` back (interpolated between the two samples straddling
    // it) - or the curve's start if the whole curve is shorter than `distance`.
    private static Vector2 WalkBackAlongPolyline(Vector2[] points, float distance)
    {
        float remaining = distance;
        for (int i = points.Length - 2; i >= 0; i--)
        {
            float segmentLength = Vector2.Distance(points[i], points[i + 1]);
            if (segmentLength >= remaining)
            {
                return Vector2.Lerp(points[i + 1], points[i], remaining / segmentLength);
            }

            remaining -= segmentLength;
        }

        return points[0];
    }

    // Same backward walk as WalkBackAlongPolyline, but returns the whole shortened polyline rather
    // than a single point - used to hold the filled line (EmitThickPolyline) back from the curve's
    // true endpoint so it doesn't draw underneath/past the arrowhead's pointed tip (see
    // ArrowheadBackTrimFactor). Only the trailing points beyond the cut are dropped; everything before
    // it is untouched.
    private static Vector2[] TrimPolylineEnd(Vector2[] points, float distance)
    {
        float remaining = distance;
        for (int i = points.Length - 1; i > 0; i--)
        {
            float segmentLength = Vector2.Distance(points[i - 1], points[i]);
            if (segmentLength >= remaining)
            {
                var trimmed = new Vector2[i + 1];
                System.Array.Copy(points, trimmed, i);
                trimmed[i] = Vector2.Lerp(points[i], points[i - 1], remaining / segmentLength);
                return trimmed;
            }

            remaining -= segmentLength;
        }

        return new[] { points[0] };
    }

    // Plain 1px hard-edged GL.LINES, used when the selection UI is hidden - no thickness or
    // antialiasing control on the line itself, matching the more minimal look of that mode. Only
    // arrowLength is used (for the arrowhead) - the line itself has no per-vertex width control.
    private static void EmitThinPolyline(Vector2[] points, Color color, float arrowLength, float outlineMargin)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            EmitThinSegment(points[i], points[i + 1], color);
        }

        if (points.Length >= 2)
        {
            EmitThinArrowhead(points, color, arrowLength, outlineMargin);
        }
    }

    private static void EmitThinSegment(Vector2 from, Vector2 to, Color color)
    {
        GL.Color(color);
        GL.Vertex3(from.x, from.y, 0f);
        GL.Vertex3(to.x, to.y, 0f);
    }

    // GL.LINES can't fill a shape, so this draws the outline of the same triangle EmitThickArrowhead
    // fills solid (see below) - closing the third edge between the two wing tips reads as a proper
    // arrowhead rather than the old open two-stroke chevron.
    private static void EmitThinArrowhead(Vector2[] points, Color color, float arrowLength, float outlineMargin)
    {
        Vector2 to = points[points.Length - 1];
        Vector2 from = WalkBackAlongPolyline(points, arrowLength * ArrowheadAngleLookbackFactor);

        Vector2 approachDirection = to - from;
        if (approachDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 backDirection = (Vector3)(-approachDirection.normalized) * arrowLength;
        Vector2 left = to + (Vector2)(Quaternion.Euler(0f, 0f, ArrowheadWingAngleDegrees) * backDirection);
        Vector2 right = to + (Vector2)(Quaternion.Euler(0f, 0f, -ArrowheadWingAngleDegrees) * backDirection);

        if (outlineMargin > 0f)
        {
            PushTriangleOutward(ref to, ref left, ref right, outlineMargin);
        }

        EmitThinSegment(to, left, color);
        EmitThinSegment(to, right, color);
        EmitThinSegment(left, right, color);
    }

    // Pushes each of a triangle's 3 vertices outward from the triangle's own centroid by a constant
    // distance - a cheap approximation of a uniform polygon outline/dilate. Exact per-edge offsetting
    // (a true Minkowski-sum outline) would need per-edge normals and re-intersecting adjacent edges;
    // this is simpler and, for the arrowhead's fairly regular triangle shape, reads as a consistent
    // border rather than the lopsided wedge that grows only at the tip or only at the back.
    private static void PushTriangleOutward(ref Vector2 a, ref Vector2 b, ref Vector2 c, float margin)
    {
        Vector2 centroid = (a + b + c) / 3f;
        a += DirectionFrom(centroid, a) * margin;
        b += DirectionFrom(centroid, b) * margin;
        c += DirectionFrom(centroid, c) * margin;

        static Vector2 DirectionFrom(Vector2 from, Vector2 to)
        {
            Vector2 delta = to - from;
            return delta.sqrMagnitude < 0.0001f ? Vector2.zero : delta.normalized;
        }
    }

    private const float LineAntiAliasWidth = 1.5f;

    // Samples a cached cubic-Bezier polyline as a series of straight segments (IMGUI/GL have no
    // native curve primitive), then caps it with an arrowhead oriented to the final segment's
    // direction so the head reads correctly on a curve rather than pointing along the start-to-end
    // straight line - same approach the old DrawBezierArrow used, now emitting GL triangles instead
    // of issuing a GUI.DrawTexture call per segment.
    private static void EmitThickPolyline(Vector2[] points, Color color, float thickness, float arrowLength, float outlineMargin)
    {
        if (points.Length < 2)
        {
            return;
        }

        // Held back from the curve's true endpoint by the arrowhead's own base distance (see
        // ArrowheadBackTrimFactor), plus outlineMargin so the shaft still recedes far enough to stay
        // hidden behind the outline pass's own arrowhead, which is pushed out (see PushTriangleOutward)
        // rather than lengthened, but still grows slightly past the original base plane in the process.
        Vector2[] trimmedPoints = TrimPolylineEnd(points, arrowLength * ArrowheadBackTrimFactor + outlineMargin);

        for (int i = 0; i < trimmedPoints.Length - 1; i++)
        {
            EmitThickSegment(trimmedPoints[i], trimmedPoints[i + 1], color, thickness);
        }

        EmitThickArrowhead(points, color, arrowLength, outlineMargin);
    }

    // A single solid filled triangle (apex at the curve's endpoint, base spanning the two wing
    // points) rather than the old two-stroke open chevron - much more readable as a direction
    // indicator at a glance. Emitted as three plain vertices directly into the caller's already-open
    // GL.Begin(GL.TRIANGLES) block (see DrawLineBufferGL), the same way EmitThickSegment's quads are.
    // arrowLength is passed in explicitly (already zoom-scaled by the caller) rather than derived from
    // thickness here, so a wider line can still be given an arrowhead sized off its underlying
    // transition's own thickness - see the ArrowLength field comment on _currentCache.LineDrawBuffer. outlineMargin
    // (zero for every ordinary arrowhead) pushes all 3 vertices outward from the triangle's own
    // centroid by a constant distance for the selected-state outline pass - see PushTriangleOutward.
    private static void EmitThickArrowhead(Vector2[] points, Color color, float arrowLength, float outlineMargin)
    {
        Vector2 to = points[points.Length - 1];
        Vector2 from = WalkBackAlongPolyline(points, arrowLength * ArrowheadAngleLookbackFactor);

        Vector2 approachDirection = to - from;
        if (approachDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 backDirection = (Vector3)(-approachDirection.normalized) * arrowLength;
        Vector2 left = to + (Vector2)(Quaternion.Euler(0f, 0f, ArrowheadWingAngleDegrees) * backDirection);
        Vector2 right = to + (Vector2)(Quaternion.Euler(0f, 0f, -ArrowheadWingAngleDegrees) * backDirection);

        if (outlineMargin > 0f)
        {
            PushTriangleOutward(ref to, ref left, ref right, outlineMargin);
        }

        GL.Color(color);
        GL.Vertex3(to.x, to.y, 0f);
        GL.Vertex3(left.x, left.y, 0f);
        GL.Vertex3(right.x, right.y, 0f);
    }

    // One thickness-wide solid quad down the segment's length, flanked by two slightly wider quads
    // whose outer edge fades to zero alpha - the GL/vertex-color equivalent of the soft-edged
    // _lineMaskTexture the old GUI.DrawTexture-based DrawLine stretched across a line's thickness.
    // Hidden/Internal-Colored interpolates per-vertex color (including alpha) linearly across each
    // triangle, so the fade needs no texture, just the right vertex colors.
    private static void EmitThickSegment(Vector2 from, Vector2 to, Color color, float thickness)
    {
        Vector2 direction = to - from;
        float length = direction.magnitude;
        if (length < 0.01f)
        {
            return;
        }

        Vector2 normal = new Vector2(-direction.y, direction.x) / length;
        Vector2 halfInner = normal * (thickness / 2f);
        Vector2 halfOuter = normal * (thickness / 2f + LineAntiAliasWidth);
        Color transparent = new Color(color.r, color.g, color.b, 0f);

        EmitQuad(from - halfInner, from + halfInner, to + halfInner, to - halfInner, color, color, color, color);
        EmitQuad(from + halfInner, from + halfOuter, to + halfOuter, to + halfInner, color, transparent, transparent, color);
        EmitQuad(from - halfOuter, from - halfInner, to - halfInner, to - halfOuter, transparent, color, color, transparent);
    }

    // Emits a quad (a-b-c-d in order around the perimeter) as two GL triangles, one vertex color per
    // corner - used both for the solid inner band and the soft-edge bands of EmitThickSegment.
    private static void EmitQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color colorA, Color colorB, Color colorC, Color colorD)
    {
        GL.Color(colorA);
        GL.Vertex3(a.x, a.y, 0f);
        GL.Color(colorB);
        GL.Vertex3(b.x, b.y, 0f);
        GL.Color(colorC);
        GL.Vertex3(c.x, c.y, 0f);

        GL.Color(colorA);
        GL.Vertex3(a.x, a.y, 0f);
        GL.Color(colorC);
        GL.Vertex3(c.x, c.y, 0f);
        GL.Color(colorD);
        GL.Vertex3(d.x, d.y, 0f);
    }

    private sealed class NodeLayout
    {
        public string Name = "";
        public Rect ScreenRect;
        public Rect TitleRect;
        public Color FillColor;
        public int ColorIndex;
        public bool IsDisabled;
        public List<TransitionRow> Rows = new();
    }

    private readonly struct TransitionRow
    {
        public readonly string ToState;
        public readonly string EventName;
        public readonly Rect Rect;
        public readonly Vector2[] CurvePoints;
        public readonly Rect CurveBounds;
        public readonly bool IsDisabled;

        public TransitionRow(string toState, string eventName, Rect rect, Vector2[] curvePoints, Rect curveBounds, bool isDisabled)
        {
            ToState = toState;
            EventName = eventName;
            Rect = rect;
            CurvePoints = curvePoints;
            CurveBounds = curveBounds;
            IsDisabled = isDisabled;
        }

        // Shifts CurvePoints in place (mutating the existing array's elements, not allocating a new
        // one) and returns a new struct value reusing that same array reference - see
        // FsmGraphOverlay.TranslateNodeLayoutCache, the only caller.
        public TransitionRow Translated(Vector2 delta)
        {
            for (int i = 0; i < CurvePoints.Length; i++)
            {
                CurvePoints[i] += delta;
            }

            return new TransitionRow(ToState, EventName, Translate(Rect, delta), CurvePoints, Translate(CurveBounds, delta), IsDisabled);
        }
    }

    private readonly struct GlobalPseudoNodeLayout
    {
        public readonly string EventName;
        public readonly Rect Rect;

        // Precomputed 2-point polyline for the GL line batch (see DrawLineBufferGL) - built once here
        // at layout-cache-build time rather than allocated fresh every Repaint frame.
        public readonly Vector2[] ArrowPoints;
        public readonly bool IsDisabled;

        public GlobalPseudoNodeLayout(string eventName, Rect rect, Vector2 arrowFrom, Vector2 arrowTo, bool isDisabled)
        {
            EventName = eventName;
            Rect = rect;
            ArrowPoints = new[] { arrowFrom, arrowTo };
            IsDisabled = isDisabled;
        }

        private GlobalPseudoNodeLayout(string eventName, Rect rect, Vector2[] arrowPoints, bool isDisabled)
        {
            EventName = eventName;
            Rect = rect;
            ArrowPoints = arrowPoints;
            IsDisabled = isDisabled;
        }

        // Shifts ArrowPoints in place (mutating the existing array's elements, not allocating a new
        // one) and returns a new struct value reusing that same array reference - see
        // FsmGraphOverlay.TranslateNodeLayoutCache, the only caller.
        public GlobalPseudoNodeLayout Translated(Vector2 delta)
        {
            for (int i = 0; i < ArrowPoints.Length; i++)
            {
                ArrowPoints[i] += delta;
            }

            return new GlobalPseudoNodeLayout(EventName, Translate(Rect, delta), ArrowPoints, IsDisabled);
        }
    }

    // Endpoint drag-to-rebind state, held only while a drag is actually in progress (null otherwise) -
    // see HandleTransitionDrag. OwningStateName == "" means the anchor being dragged belongs to a global
    // transition's pseudo-node rather than a per-state row.
    private sealed class DraggingTransition
    {
        public string OwningStateName = "";
        public string EventName = "";

        // true = dragging the row's own source-side anchor (CurvePoints[0]) - dropping this end onto
        // another event node rebinds which event fires this transition (RetargetTransitionEvent).
        // false = dragging the target/arrowhead anchor (CurvePoints[^1] or a pseudo-node's ArrowPoints[1])
        // - dropping this end onto a state retargets ToState (the existing ApplyTransitionRetarget path).
        public bool DraggingSourceEnd;
        public Vector2 MouseDownScreenPos;
        public bool HasCrossedThreshold;
    }
}
