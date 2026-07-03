using System.Collections.Generic;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// In-game IMGUI overlay for browsing live FSMs and their state graphs, toggled by the "0" key.
// Collects its own FsmSnapshot (see RefreshSnapshot) rather than sharing FsmMasterPlugin's -
// FsmMasterPlugin calls RefreshSnapshot for it on initial Awake and on every scene load (it
// already owns the SceneManager.sceneLoaded subscription for its own persisted-edit reapplication,
// so this reuses that rather than adding a second one here). The "0" key itself only flips
// visibility - it must never rebuild the snapshot or reset the current selection, since scene load
// is the only point PlayMakerFSM references can actually go stale.
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
    private const float BezierControlOffset = 40f;
    private const float BezierTargetSegmentLength = 14f;
    private const int MinBezierSegments = 6;
    private const int MaxBezierSegments = 40;
    private const float DynamicFontPointSize = 12f;
    private const float TransitionLineThickness = 3f;
    private const float ActiveTransitionLineThickness = 6f;

    // Translated from FSMExpress's own state-color palette, indexed by FsmState.ColorIndex - EXCEPT
    // index 1: FSMExpress's original blue (116,143,201) collided visually with the active state's own
    // blue/cyan highlighting (see ActiveStateColor/ActiveTitleBackgroundColor below), so it's rotated
    // to an otherwise-unused magenta/pink hue instead, keeping the same saturation and value
    // (brightness) as the original blue - same "weight" in the palette, just a hue none of the other
    // 7 entries (or the active-state colors) already use. The active-state colors themselves are
    // unchanged.
    private static readonly Color[] StateColors =
    {
        new(128f / 255f, 128f / 255f, 128f / 255f),
        new(201f / 255f, 116f / 255f, 173f / 255f),
        new(58f / 255f, 182f / 255f, 166f / 255f),
        new(93f / 255f, 164f / 255f, 53f / 255f),
        new(225f / 255f, 254f / 255f, 50f / 255f),
        new(235f / 255f, 131f / 255f, 46f / 255f),
        new(187f / 255f, 75f / 255f, 75f / 255f),
        new(117f / 255f, 53f / 255f, 164f / 255f),
    };

    // FSMExpress's paired lighter palette for the same colorIndex - used for a state's own
    // transition names/lines by default, so they read as visually associated with that state's node
    // color while staying less saturated than the node itself. Index 0 (PlayMaker's "no color set"
    // default, plain grey in StateColors) pairs with plain white rather than a tinted entry.
    private static readonly Color[] TransitionColors =
    {
        Color.white,
        new(248f / 255f, 197f / 255f, 231f / 255f),
        new(159f / 255f, 225f / 255f, 216f / 255f),
        new(183f / 255f, 225f / 255f, 159f / 255f),
        new(225f / 255f, 254f / 255f, 102f / 255f),
        new(255f / 255f, 198f / 255f, 152f / 255f),
        new(225f / 255f, 159f / 255f, 160f / 255f),
        new(197f / 255f, 159f / 255f, 225f / 255f),
    };

    private static readonly Color GlobalTransitionColor = new(0.6f, 0.6f, 0.6f);

    // Global-transition pseudo nodes are always a light grey with a black outline and black text,
    // regardless of which state they target - visually distinct from state nodes since a global
    // transition isn't "owned" by any one colorIndex.
    private static readonly Color GlobalPseudoNodeColor = new(0.82f, 0.82f, 0.82f);
    private static readonly Color GlobalPseudoNodeOutlineColor = Color.black;
    private static readonly Color GlobalPseudoNodeTextColor = Color.black;

    // A node's INNER ring - the one always present, drawn immediately around the node - and its
    // internal separator lines (title/row divider, dividers between transition rows) are this same
    // fixed white by default. While the state is active they switch to its own theme color instead
    // (or the ActiveStateColor fallback if it has none) - see GetActiveOutlineColor and the chrome
    // pass in DrawCachedGraph. The global-transition pseudo nodes always use a different (black)
    // outline color regardless of activity - see GlobalPseudoNodeOutlineColor above.
    private static readonly Color NodeOutlineColor = Color.white;

    // Title band background is driven by each state's own StateColors entry (see
    // NodeLayout.FillColor/ColorIndex below) - EXCEPT colorIndex 0, PlayMaker's "no color set"
    // default, which is baked to plain black there instead of its literal (grey) StateColors entry.
    // This is the DEFAULT (non-active) background only - see ActiveTitleBackgroundColor below for
    // what the active state's own title band uses instead. Transition name/line colors are driven by
    // TransitionColors instead - see their own use sites below. Only the transition-row body stays a
    // neutral fixed color, since it's just a background surface for the (already color-driven) event
    // text.
    private static readonly Color TransitionRowBackgroundColor = new(0.2f, 0.2f, 0.2f);

    // The OUTER "chrome" ring - the extra halo drawn further out than the inner ring, only for the
    // active state - is always this fixed cyan, regardless of the state's own theme; it's a generic
    // "this is active" signal, not a color-identity one (compare the inner ring/separators above,
    // which DO pick up the state's own theme color). Also the fallback inner-ring/transition-line
    // color for a state with no colored theme of its own (colorIndex 0) - see GetActiveOutlineColor,
    // which uses the state's own StateColors entry instead for a genuinely themed state (colorIndex
    // 1-7).
    private static readonly Color ActiveStateColor = new(0f, 1f, 1f);

    // The active state's own title band (state name) always uses this background - the same cyan hue
    // as ActiveStateColor above, just less saturated (lerped toward white, which is mathematically
    // equivalent to desaturating a fully-saturated color that's already at full brightness) so it
    // reads as "the same active color, softened for a background fill" rather than an unrelated blue.
    // Paired with black text (ActiveTitleTextColor) - regardless of the state's own colorIndex - so
    // the active state's name is unambiguous and easy to read at a glance. This does NOT apply to
    // event/transition text, which stays TransitionColors regardless of activity.
    private static readonly Color ActiveTitleBackgroundColor = Color.Lerp(ActiveStateColor, Color.white, 0.5f);
    private static readonly Color ActiveTitleTextColor = Color.black;

    private readonly ManualLogSource _logger;

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
    private FsmSnapshot? _snapshot;

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
    internal FsmInfo? ResolveFsmInfo(string fsmKey)
    {
        if (_snapshot == null)
        {
            return null;
        }

        foreach (FsmInfo fsm in _snapshot.Fsms)
        {
            if (FsmIdentity.GetFsmKey(fsm.Component) == fsmKey)
            {
                return fsm;
            }
        }

        return null;
    }

    private bool _isPanning;

    // Node/edge layout cache - rebuilt only when the FSM selection, pan, zoom, or canvas size
    // changes. Unity dispatches OnGUI once per queued input event (several times per rendered
    // frame); recomputing this from scratch on every single call was the confirmed source of a
    // GC stutter, so it's now cache-gated and only ever rebuilt when something actually moved.
    private Dictionary<string, NodeLayout>? _nodeLayoutCache;
    private List<GlobalPseudoNodeLayout>? _globalPseudoNodeCache;
    private string? _layoutCacheFsmKey;
    private Vector2 _layoutCachePanCenter;
    private float _layoutCacheZoom;
    private Rect _layoutCacheCanvasRect;

    // Reused per-Repaint scratch buffer of (already-tessellated polyline, color, base thickness)
    // triples - filled while walking pseudo-nodes/edges (which still draw their labels via GUI as
    // before) and then drawn as a single GL batch (see DrawLineBufferGL), instead of allocating a new
    // list every frame. Thickness is stored per-line (not a single shared value) so the active
    // state's outgoing transitions can draw thicker than the rest of the graph.
    private readonly List<(Vector2[] Points, Color Color, float Thickness)> _lineDrawBuffer = new();

    // Reused per-Repaint scratch buffer of (vertex position, color) pairs, 3 entries per triangle -
    // holds every node/pseudo-node chrome shape (backgrounds, outline rings, rounded corners) for the
    // frame, gathered while walking nodes/rows (which still draw their labels via GUI as before) and
    // then drawn as a single GL batch (see FlushChromeBufferGL), replacing what used to be up to ~7
    // GUI.DrawTexture calls per rounded rect (flat fills plus a baked corner-mask texture per corner).
    private readonly List<(Vector2 Position, Color Color)> _chromeVertexBuffer = new();

    // Thin GL.LINES render when the selection UI is hidden (see the "1" key in Update) - thick
    // quads with a soft antialiased edge are the default, full-selection-UI style.
    private bool _useThinGlLines;

    public FsmGraphOverlay(ManualLogSource logger)
    {
        _logger = logger;
    }

    public void Shutdown()
    {
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

    // Read by FsmMasterPlugin.Update each frame to mirror this overlay's own "0"/"1" hotkey state
    // onto the new uGUI right panel's ActiveSelf, since that panel has no IMGUI presence of its own
    // for these keys to toggle directly.
    internal bool IsVisible => _isVisible;
    internal bool SelectionUiVisible => _selectionUiVisible;

    public void Update()
    {
        // Purely a visibility flip - RefreshSnapshot runs only off scene load (see FsmMasterPlugin),
        // never here, so toggling the overlay off and back on never discards the current FSM
        // selection or graph pan/zoom.
        if (Input.GetKeyDown(KeyCode.Alpha0))
        {
            _isVisible = !_isVisible;
        }

        // Switches transition lines to the thinner style for a more minimal graph-only look, and
        // (via IsVisible/SelectionUiVisible above) hides the uGUI right panel entirely - the "0" key's
        // job is hiding the whole tool; "1" is a secondary "minimal view" toggle nested within that.
        // Pressing "1" again restores both. Only active while the overlay itself is on.
        if (_isVisible && Input.GetKeyDown(KeyCode.Alpha1))
        {
            _selectionUiVisible = !_selectionUiVisible;
            _useThinGlLines = !_selectionUiVisible;
            _logger.LogInfo($"[FsmMaster] Graph overlay: minimal view {(!_selectionUiVisible ? "on" : "off")}.");
        }
    }

    // Called by FsmMasterPlugin on initial Awake and on every scene load (which already owns the
    // PlayMakerFSM discovery for its own persisted-edit reapplication, so this reuses that array
    // rather than re-walking the scene a second time) - never from the "0" key itself (see Update).
    // Tab selection (FsmTabManager) is intentionally untouched here - a tab whose FSM isn't in the
    // new snapshot is rebound to not-live by FsmMasterPlugin's own FsmTabManager.RebindAfterRefresh
    // call, not reset by this method. The graph/side-panel caches are invalidated as the very last
    // step, after the new snapshot has been fully captured and logged - not interleaved with that
    // work - so nothing here can observe a state where the new snapshot exists but the caches built
    // from the previous scene's snapshot haven't been torn down yet.
    internal void RefreshSnapshot(string sceneName, PlayMakerFSM[] components)
    {
        _snapshot = FsmDataCollector.CollectSnapshot(sceneName, components);

        if (_snapshot.Fsms.Count == 0)
        {
            _logger.LogInfo("[FsmMaster] Graph overlay: no live PlayMakerFSM instances found in this scene.");
        }

        InvalidateGraphCaches();
    }

    // rightPanelScreenRect is the new uGUI right panel's current screen rect (top-left origin,
    // matching both this Rect's own convention and IMGUI's Event.current.mousePosition - no axis
    // flip needed, unlike CanvasNode.IsMouseOver's conversion to Input.mousePosition), or null
    // whenever that panel is hidden - see FsmMasterPlugin.OnGUI, which computes it from
    // CanvasNode.Position/Size each frame.
    public void OnGUI(FsmTabState? activeTab, Rect? rightPanelScreenRect)
    {
        if (!_isVisible || _snapshot == null)
        {
            return;
        }

        // Sync in from whichever tab is active this frame - a different tab may have been active
        // last frame with different pan/zoom/selection cached in these working fields (see their
        // declaration comment near the top of this class).
        if (activeTab != null)
        {
            _panWorldCenter = activeTab.PanWorldCenter;
            _zoom = activeTab.Zoom;
            _selectedStateName = activeTab.SelectedStateName;
        }

        var canvasRect = new Rect(0f, 0f, Screen.width, Screen.height);
        Rect interactiveRect = ComputeInteractiveRect(rightPanelScreenRect);

        // A tab stays open even once its backing FSM disappears from the scene (see
        // FsmTabManager.RebindAfterRefresh) - IsLive false / an unresolvable FsmKey both mean "draw
        // nothing for the graph this frame," not "drop the tab."
        FsmInfo? activeFsm = activeTab is { IsLive: true } ? ResolveFsmInfo(activeTab.FsmKey) : null;

        if (activeFsm != null)
        {
            DrawGraph(activeFsm, activeTab!, canvasRect, interactiveRect);
        }

        // Sync back out so a tab switch (or the next scene reload) doesn't lose whatever pan/zoom/
        // selection changed this frame (drag, scroll-zoom, click-to-select - see HandlePanAndZoom and
        // the click-select block in DrawCachedGraph).
        if (activeTab != null)
        {
            activeTab.PanWorldCenter = _panWorldCenter;
            activeTab.Zoom = _zoom;
            activeTab.SelectedStateName = _selectedStateName;
        }
    }

    // Excludes the uGUI right panel's own screen rect (a fixed, right-docked, full-height strip is a
    // conservative approximation - the panel isn't always literally full-height, but this matches how
    // the old IMGUI list panel's rect exclusion worked too) from the graph's pan/zoom/click-to-select
    // input region, so a click on it never also pans the graph or selects a node underneath it.
    private static Rect ComputeInteractiveRect(Rect? rightPanelScreenRect)
    {
        float excludedFromX = rightPanelScreenRect?.x ?? Screen.width;
        return new Rect(0f, 0f, Mathf.Max(0f, excludedFromX), Screen.height);
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
        // "Segoe UI" is a standard, always-installed Windows font - CreateDynamicFontFromOSFont just
        // supplies glyphs from it; the requested rendered size is controlled entirely by each
        // GUIStyle's own fontSize below, so this base size is arbitrary and never itself re-created
        // per zoom level.
        _dynamicFont ??= Font.CreateDynamicFontFromOSFont("Segoe UI", (int)DynamicFontPointSize);

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
        _globalEventStyle.normal.textColor = GlobalPseudoNodeTextColor;

        _textStyleBuiltForZoom = _zoom;
    }

    private void InvalidateGraphCaches()
    {
        _nodeLayoutCache = null;
        _globalPseudoNodeCache = null;
        _layoutCacheFsmKey = null;
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
    // FSM-authored Position.height, matching FSMExpress's own node sizing (its outer Border only
    // sets Width from the authored bounds - Height is left unset so it auto-sizes to content).
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

    private void DrawGraph(FsmInfo fsm, FsmTabState activeTab, Rect canvasRect, Rect interactiveRect)
    {
        // GUI.skin.box's background is a semi-transparent dark fill - fine as a backdrop while the
        // list/side panel are shown, but with the selection UI hidden (the "1" key in Update) that
        // same fill would darken the game view behind the now-unobscured graph. GUIStyle.none draws
        // no background at all in that mode. canvasRect itself always spans the full screen (see
        // OnGUI) regardless of which of those two applies.
        GUI.BeginGroup(canvasRect, _selectionUiVisible ? GUI.skin.box : GUIStyle.none);
        var localCanvasRect = new Rect(0f, 0f, canvasRect.width, canvasRect.height);

        bool cacheStale = _nodeLayoutCache == null
            || _layoutCacheFsmKey != activeTab.FsmKey
            || _layoutCachePanCenter != _panWorldCenter
            || !Mathf.Approximately(_layoutCacheZoom, _zoom)
            || _layoutCacheCanvasRect != localCanvasRect;

        if (cacheStale)
        {
            RebuildNodeLayoutCache(fsm, localCanvasRect);
            _layoutCacheFsmKey = activeTab.FsmKey;
            _layoutCachePanCenter = _panWorldCenter;
            _layoutCacheZoom = _zoom;
            _layoutCacheCanvasRect = localCanvasRect;
        }

        // Read live, not cached - the active state changes every frame as the FSM runs, independent
        // of the layout cache (which only tracks pan/zoom/selection/canvas-size).
        // canvasRect.position is passed through separately from localCanvasRect: node/label rects are
        // drawn via GUI calls local to this GUI.BeginGroup, so Unity's GUIClip stack silently adds
        // canvasRect's screen offset for them, but raw GL calls bypass GUIClip entirely - DrawLineBufferGL
        // needs the real screen-space offset to line its geometry up with those GUI-drawn nodes.
        DrawCachedGraph(fsm.Fsm.ActiveStateName, canvasRect.position, interactiveRect);
        HandlePanAndZoom(interactiveRect);

        GUI.EndGroup();
    }

    private void RebuildNodeLayoutCache(FsmInfo fsm, Rect canvasRect)
    {
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
            int colorIndex = Mathf.Clamp(state.State.ColorIndex, 0, StateColors.Length - 1);
            var titleRect = new Rect(screenRect.x, screenRect.y, screenRect.width, TitleBarHeight * _zoom);

            var node = new NodeLayout
            {
                Name = state.Name,
                ScreenRect = screenRect,
                TitleRect = titleRect,
                // ColorIndex 0 is PlayMaker's "no color set" default - baked to black here (rather
                // than its literal grey StateColors entry). This is the title band's DEFAULT
                // background - see ActiveTitleBackgroundColor for what it switches to on activation.
                FillColor = colorIndex == 0 ? Color.black : StateColors[colorIndex],
                ColorIndex = colorIndex,
            };

            float rowHeight = TransitionRowHeight * _zoom;

            // Node height is now sized to exactly fit the title band plus every transition row
            // (see ComputeNodeWorldHeight), so every row always has room - no overflow/collapse
            // fallback is needed here anymore.
            for (int i = 0; i < state.Transitions.Count; i++)
            {
                FsmTransitionInfo transition = state.Transitions[i];
                var rowRect = new Rect(screenRect.x, titleRect.yMax + i * rowHeight, screenRect.width, rowHeight);

                Vector2 sourceAnchor = rowRect.center;
                Vector2 targetAnchor = rowRect.center;
                Vector2 exitDirection = new(1f, 0f);
                Vector2 entryDirection = new(-1f, 0f);

                if (nodeScreenRects.TryGetValue(transition.ToState, out Rect targetRect))
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
                node.Rows.Add(new TransitionRow(transition.ToState, transition.EventName, rowRect, curvePoints, ComputeCurveBounds(curvePoints)));
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
                float y = targetRect.y - pseudoOffset - i * (pseudoHeight + pseudoGap);
                var pseudoRect = new Rect(targetRect.x, y, targetRect.width, pseudoHeight);
                globalPseudoNodes.Add(new GlobalPseudoNodeLayout(
                    group.Value[i].EventName,
                    pseudoRect,
                    new Vector2(pseudoRect.center.x, pseudoRect.yMax),
                    new Vector2(targetRect.center.x, targetRect.y)));
            }
        }

        _nodeLayoutCache = layoutCache;
        _globalPseudoNodeCache = globalPseudoNodes;
    }

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
    // just walk a cached polyline every OnGUI call. Adapted from FSMExpress's FsmCanvasArrow, which
    // exits a node from whichever side faces the target via a control point offset from the anchor,
    // and enters the target's title band through whichever of its two sides faces the source (see the
    // call site in RebuildNodeLayoutCache) - control2 extends further in that same direction (away
    // from the target) so the curve swoops in from whichever side it's actually attached to.
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

    private void DrawCachedGraph(string? activeStateName, Vector2 canvasScreenOffset, Rect interactiveRect)
    {
        if (_nodeLayoutCache == null)
        {
            return;
        }

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
            Rect visibleRect = _layoutCacheCanvasRect;
            _lineDrawBuffer.Clear();
            _chromeVertexBuffer.Clear();

            if (_globalPseudoNodeCache != null)
            {
                foreach (GlobalPseudoNodeLayout pseudo in _globalPseudoNodeCache)
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
                    // GL flushes below).
                    AddRoundedRectOutlineToChromeBuffer(pseudo.Rect, GlobalPseudoNodeColor, GlobalPseudoNodeOutlineColor, NodeBorderThickness * _zoom);

                    // Arrow geometry is only collected here - actually drawn in one GL batch below,
                    // once every pseudo/edge line for this frame has been gathered.
                    _lineDrawBuffer.Add((pseudo.ArrowPoints, GlobalTransitionColor, TransitionLineThickness));
                }
            }

            // Regular per-state transition lines - geometry collected here (same cull check as
            // before), drawn in the same GL batch as the pseudo-node arrows above. Default color is
            // each state's own TransitionColors entry (see NodeLayout.ColorIndex); lines leaving the
            // active state switch to the fixed ActiveStateColor and draw thicker instead, matching its
            // transition-name text color below.
            foreach (NodeLayout node in _nodeLayoutCache.Values)
            {
                bool nodeIsActiveForLines = activeStateName != null && node.Name == activeStateName;
                Color lineColor = nodeIsActiveForLines ? ActiveStateColor : TransitionColors[node.ColorIndex];
                float lineThickness = nodeIsActiveForLines ? ActiveTransitionLineThickness : TransitionLineThickness;

                foreach (TransitionRow row in node.Rows)
                {
                    if (!row.CurveBounds.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    _lineDrawBuffer.Add((row.CurvePoints, lineColor, lineThickness));
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
            foreach (NodeLayout node in _nodeLayoutCache.Values)
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
                float cornerRadius = GetNodeCornerRadius(node.ScreenRect);

                // The active-indicator ring is the outer "chrome" halo - drawn behind and larger than
                // the inner ring below, so only its outer edge shows past it. Always the fixed
                // ActiveStateColor (cyan), regardless of the state's own theme - it's a generic
                // "this is active" signal, not a color-identity one.
                bool isActive = activeStateName != null && node.Name == activeStateName;
                if (isActive)
                {
                    float activeOffset = (NodeBorderThickness + NodeActiveOutlineThickness) * _zoom;
                    var activeRect = new Rect(
                        node.ScreenRect.x - activeOffset,
                        node.ScreenRect.y - activeOffset,
                        node.ScreenRect.width + activeOffset * 2f,
                        node.ScreenRect.height + activeOffset * 2f);
                    AddRoundedRectToChromeBuffer(activeRect, cornerRadius + activeOffset, ActiveStateColor);
                }

                // The inner ring - always present, drawn on top of the active halo above - is
                // NodeOutlineColor's white by default, switching to the state's own theme color (or
                // the ActiveStateColor fallback for an unthemed (colorIndex 0) state - see
                // GetActiveOutlineColor) while active. Title/rows below are drawn full node width and
                // round their own top/bottom corners themselves, fully covering the inner area, so no
                // separate inner fill pass is needed here.
                Color innerOutlineColor = isActive ? GetActiveOutlineColor(node.ColorIndex) : NodeOutlineColor;
                float outlineThickness = NodeBorderThickness * _zoom;
                var outerRect = new Rect(
                    node.ScreenRect.x - outlineThickness,
                    node.ScreenRect.y - outlineThickness,
                    node.ScreenRect.width + outlineThickness * 2f,
                    node.ScreenRect.height + outlineThickness * 2f);
                AddRoundedRectToChromeBuffer(outerRect, cornerRadius + outlineThickness, innerOutlineColor);

                // Title band is this state's own color by default (colorIndex 0 baked to black - see
                // NodeLayout.FillColor), switching to the fixed ActiveTitleBackgroundColor (light blue)
                // while it's the active state - see the label pass below for its matching black text.
                bool titleIsOnlyBand = node.Rows.Count == 0;
                Color titleFillColor = isActive ? ActiveTitleBackgroundColor : node.FillColor;
                AddRoundedRectToChromeBuffer(node.TitleRect, cornerRadius, titleFillColor, roundTop: true, roundBottom: titleIsOnlyBand);
                // Title/row labels are deliberately NOT drawn here (see the label pass after both GL
                // flushes below).

                // Title/row divider - same color as the inner ring (see above), so every separator on
                // the node reads as one consistent "outline" whether or not the state is active.
                float dividerThickness = NodeBorderThickness * _zoom;
                if (node.Rows.Count > 0)
                {
                    AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, node.TitleRect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), innerOutlineColor);
                }

                for (int i = 0; i < node.Rows.Count; i++)
                {
                    TransitionRow row = node.Rows[i];
                    bool isLastRow = i == node.Rows.Count - 1;

                    if (isLastRow)
                    {
                        AddRoundedRectToChromeBuffer(row.Rect, cornerRadius, TransitionRowBackgroundColor, roundTop: false, roundBottom: true);
                    }
                    else
                    {
                        AddFilledRectToChromeBuffer(row.Rect, TransitionRowBackgroundColor);
                    }

                    if (!isLastRow)
                    {
                        AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, row.Rect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), innerOutlineColor);
                    }
                }
            }

            // One GL batch for every line gathered above - drawn before the chrome batch below so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            DrawLineBufferGL(canvasScreenOffset);

            // One GL batch for every rounded-rect/fill gathered above - drawn after the line batch so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            FlushChromeBufferGL(canvasScreenOffset);

            // Labels are drawn last, only once every line/chrome GL batch above has actually been
            // rendered. Drawing them earlier (interleaved with chrome collection, as a first attempt
            // at this batching did) meant the deferred chrome GL flush - which draws opaque
            // backgrounds - painted over labels that had already rendered several calls earlier in
            // the same Repaint pass, since GUI.Label and GL both draw immediately at the point they're
            // called, in call order. Re-walks the same cull checks rather than caching a visible-list,
            // since Rect.Overlaps is cheap and this keeps the two passes independent.
            if (_globalPseudoNodeCache != null)
            {
                foreach (GlobalPseudoNodeLayout pseudo in _globalPseudoNodeCache)
                {
                    if (!pseudo.Rect.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    GUI.Label(pseudo.Rect, pseudo.EventName, _globalEventStyle);
                }
            }

            foreach (NodeLayout node in _nodeLayoutCache.Values)
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
                bool nodeIsActive = activeStateName != null && node.Name == activeStateName;
                _titleStyle!.normal.textColor = nodeIsActive ? ActiveTitleTextColor : Color.white;
                _eventStyle!.normal.textColor = TransitionColors[node.ColorIndex];

                GUI.Label(node.TitleRect, node.Name, _titleStyle);

                foreach (TransitionRow row in node.Rows)
                {
                    GUI.Label(row.Rect, row.EventName, _eventStyle);
                }
            }
        }

        // Gated on interactiveRect (not just node.ScreenRect) so a click on the uGUI right panel -
        // which visually sits on top of the graph now that the canvas spans the full screen - never
        // also selects whatever node happens to render underneath it. IsPointerOverUi is a second
        // guard for uGUI content that can render outside that panel's own base rect (the Open
        // dropdown) - see ComputeInteractiveRect's own comment for why rect-subtraction alone isn't
        // enough for that case.
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0
            && interactiveRect.Contains(Event.current.mousePosition) && !IsPointerOverUi())
        {
            foreach (NodeLayout node in _nodeLayoutCache.Values)
            {
                if (node.ScreenRect.Contains(Event.current.mousePosition))
                {
                    _selectedStateName = node.Name;
                    Event.current.Use();
                    break;
                }
            }
        }
    }

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
    private static Color GetActiveOutlineColor(int colorIndex)
    {
        return colorIndex == 0 ? ActiveStateColor : StateColors[colorIndex];
    }

    private const int CornerFanSegments = 8;

    private void AddTriangleToChromeBuffer(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        _chromeVertexBuffer.Add((a, color));
        _chromeVertexBuffer.Add((b, color));
        _chromeVertexBuffer.Add((c, color));
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

    // Appends a solid rounded rect to _chromeVertexBuffer: straight edges/center are plain flat-fill
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
    private void AddRoundedRectOutlineToChromeBuffer(Rect rect, Color fillColor, Color outlineColor, float borderThickness)
    {
        var outerRect = new Rect(
            rect.x - borderThickness,
            rect.y - borderThickness,
            rect.width + borderThickness * 2f,
            rect.height + borderThickness * 2f);

        float radius = GetScreenCornerRadius(rect);
        AddRoundedRectToChromeBuffer(outerRect, radius + borderThickness, outlineColor);
        AddRoundedRectToChromeBuffer(rect, radius, fillColor);
    }

    // Draws every triangle gathered into _chromeVertexBuffer this frame as a single GL batch,
    // replacing what used to be up to ~7 GUI.DrawTexture calls per rounded rect (flat fills plus a
    // baked corner-mask texture draw per corner) - this was the confirmed second-largest cost in the
    // graph overlay's Repaint path after the transition lines (see the PERF diagnostics above). Shares
    // the same material/pixel-matrix-offset approach as DrawLineBufferGL, drawn after it so chrome
    // sits on top of the lines.
    private void FlushChromeBufferGL(Vector2 canvasScreenOffset)
    {
        if (_chromeVertexBuffer.Count == 0)
        {
            return;
        }

        EnsureGlMaterial();
        if (_glMaterial == null)
        {
            return;
        }

        GL.PushMatrix();
        GL.LoadPixelMatrix(
            -canvasScreenOffset.x,
            Screen.width - canvasScreenOffset.x,
            Screen.height - canvasScreenOffset.y,
            -canvasScreenOffset.y);
        _glMaterial.SetPass(0);

        GL.Begin(GL.TRIANGLES);
        foreach ((Vector2 position, Color color) in _chromeVertexBuffer)
        {
            GL.Color(color);
            GL.Vertex3(position.x, position.y, 0f);
        }
        GL.End();

        GL.PopMatrix();
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

    // Draws every polyline gathered into _lineDrawBuffer this frame as a single GL batch, replacing
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
    private void DrawLineBufferGL(Vector2 canvasScreenOffset)
    {
        if (_lineDrawBuffer.Count == 0)
        {
            return;
        }

        EnsureGlMaterial();
        if (_glMaterial == null)
        {
            return;
        }

        GL.PushMatrix();
        GL.LoadPixelMatrix(
            -canvasScreenOffset.x,
            Screen.width - canvasScreenOffset.x,
            Screen.height - canvasScreenOffset.y,
            -canvasScreenOffset.y);
        _glMaterial.SetPass(0);

        // Thin lines while the selection UI is hidden, thick otherwise - see _useThinGlLines. Thin
        // mode ignores each line's own Thickness for the line itself (GL.LINES has no per-vertex
        // width control), but the arrowhead size is still driven by it either way - see
        // EmitThinArrowhead/EmitThickArrowhead.
        if (_useThinGlLines)
        {
            GL.Begin(GL.LINES);
            foreach ((Vector2[] points, Color color, float baseThickness) in _lineDrawBuffer)
            {
                EmitThinPolyline(points, color, baseThickness * _zoom);
            }
            GL.End();
        }
        else
        {
            GL.Begin(GL.TRIANGLES);
            foreach ((Vector2[] points, Color color, float baseThickness) in _lineDrawBuffer)
            {
                EmitThickPolyline(points, color, baseThickness * _zoom);
            }
            GL.End();
        }

        GL.PopMatrix();
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

    // Plain 1px hard-edged GL.LINES, used when the selection UI is hidden - no thickness or
    // antialiasing control on the line itself, matching the more minimal look of that mode.
    private static void EmitThinPolyline(Vector2[] points, Color color, float thickness)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            EmitThinSegment(points[i], points[i + 1], color);
        }

        if (points.Length >= 2)
        {
            EmitThinArrowhead(points, color, thickness);
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
    private static void EmitThinArrowhead(Vector2[] points, Color color, float thickness)
    {
        float arrowLength = ArrowheadLength * (thickness / TransitionLineThickness);
        Vector2 to = points[points.Length - 1];
        Vector2 from = WalkBackAlongPolyline(points, arrowLength * ArrowheadAngleLookbackFactor);

        Vector2 approachDirection = to - from;
        if (approachDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 backDirection = (Vector3)(-approachDirection.normalized) * arrowLength;
        Vector2 left = Quaternion.Euler(0f, 0f, 25f) * backDirection;
        Vector2 right = Quaternion.Euler(0f, 0f, -25f) * backDirection;
        EmitThinSegment(to, to + left, color);
        EmitThinSegment(to, to + right, color);
        EmitThinSegment(to + left, to + right, color);
    }

    private const float LineAntiAliasWidth = 1.5f;

    // Samples a cached cubic-Bezier polyline as a series of straight segments (IMGUI/GL have no
    // native curve primitive), then caps it with an arrowhead oriented to the final segment's
    // direction so the head reads correctly on a curve rather than pointing along the start-to-end
    // straight line - same approach the old DrawBezierArrow used, now emitting GL triangles instead
    // of issuing a GUI.DrawTexture call per segment.
    private static void EmitThickPolyline(Vector2[] points, Color color, float thickness)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            EmitThickSegment(points[i], points[i + 1], color, thickness);
        }

        if (points.Length >= 2)
        {
            EmitThickArrowhead(points, color, thickness);
        }
    }

    // A single solid filled triangle (apex at the curve's endpoint, base spanning the two wing
    // points) rather than the old two-stroke open chevron - much more readable as a direction
    // indicator at a glance. Emitted as three plain vertices directly into the caller's already-open
    // GL.Begin(GL.TRIANGLES) block (see DrawLineBufferGL), the same way EmitThickSegment's quads are.
    // Sized relative to the line's own thickness (both already zoom-scaled by the caller), so the
    // active state's thicker transitions get a proportionally bigger arrowhead to match.
    private static void EmitThickArrowhead(Vector2[] points, Color color, float thickness)
    {
        float arrowLength = ArrowheadLength * (thickness / TransitionLineThickness);
        Vector2 to = points[points.Length - 1];
        Vector2 from = WalkBackAlongPolyline(points, arrowLength * ArrowheadAngleLookbackFactor);

        Vector2 approachDirection = to - from;
        if (approachDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 backDirection = (Vector3)(-approachDirection.normalized) * arrowLength;
        Vector2 left = Quaternion.Euler(0f, 0f, 25f) * backDirection;
        Vector2 right = Quaternion.Euler(0f, 0f, -25f) * backDirection;

        GL.Color(color);
        GL.Vertex3(to.x, to.y, 0f);
        GL.Vertex3(to.x + left.x, to.y + left.y, 0f);
        GL.Vertex3(to.x + right.x, to.y + right.y, 0f);
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
        public List<TransitionRow> Rows = new();
    }

    private readonly struct TransitionRow
    {
        public readonly string ToState;
        public readonly string EventName;
        public readonly Rect Rect;
        public readonly Vector2[] CurvePoints;
        public readonly Rect CurveBounds;

        public TransitionRow(string toState, string eventName, Rect rect, Vector2[] curvePoints, Rect curveBounds)
        {
            ToState = toState;
            EventName = eventName;
            Rect = rect;
            CurvePoints = curvePoints;
            CurveBounds = curveBounds;
        }
    }

    private readonly struct GlobalPseudoNodeLayout
    {
        public readonly string EventName;
        public readonly Rect Rect;

        // Precomputed 2-point polyline for the GL line batch (see DrawLineBufferGL) - built once here
        // at layout-cache-build time rather than allocated fresh every Repaint frame.
        public readonly Vector2[] ArrowPoints;

        public GlobalPseudoNodeLayout(string eventName, Rect rect, Vector2 arrowFrom, Vector2 arrowTo)
        {
            EventName = eventName;
            Rect = rect;
            ArrowPoints = new[] { arrowFrom, arrowTo };
        }
    }
}
