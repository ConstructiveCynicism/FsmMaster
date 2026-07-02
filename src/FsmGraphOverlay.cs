using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FsmMaster;

// In-game IMGUI overlay for browsing live FSMs and their state graphs, toggled by the "0" key.
// Fully self-contained: collects its own FsmSnapshot on demand (toggle-on) rather than sharing
// FsmMasterPlugin's, so it owns no scene-load subscription of its own - the only teardown it
// needs is releasing the small texture it draws colored rects/lines with.
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
    private const float ListWidth = 260f;
    private const float SidePanelWidth = 340f;
    private const float FsmListRowHeight = 22f;

    private const float NodeCornerRadius = 10f;
    private const float NodeBorderThickness = 2f;
    private const float NodeActiveOutlineThickness = 3f;
    private const float BezierControlOffset = 40f;
    private const float BezierTargetSegmentLength = 14f;
    private const int MinBezierSegments = 6;
    private const int MaxBezierSegments = 40;
    private const float DynamicFontPointSize = 12f;
    private const float TransitionLineThickness = 3f;

    // Translated from FSMExpress's own state-color palette, indexed by FsmState.ColorIndex.
    private static readonly Color[] StateColors =
    {
        new(128f / 255f, 128f / 255f, 128f / 255f),
        new(116f / 255f, 143f / 255f, 201f / 255f),
        new(58f / 255f, 182f / 255f, 166f / 255f),
        new(93f / 255f, 164f / 255f, 53f / 255f),
        new(225f / 255f, 254f / 255f, 50f / 255f),
        new(235f / 255f, 131f / 255f, 46f / 255f),
        new(187f / 255f, 75f / 255f, 75f / 255f),
        new(117f / 255f, 53f / 255f, 164f / 255f),
    };

    // FSMExpress's paired lighter palette for the same colorIndex - used for a state's own
    // transition rows/lines so they read as visually associated with that state's node color.
    private static readonly Color[] TransitionColors =
    {
        new(222f / 255f, 222f / 255f, 222f / 255f),
        new(197f / 255f, 213f / 255f, 248f / 255f),
        new(159f / 255f, 225f / 255f, 216f / 255f),
        new(183f / 255f, 225f / 255f, 159f / 255f),
        new(225f / 255f, 254f / 255f, 102f / 255f),
        new(255f / 255f, 198f / 255f, 152f / 255f),
        new(225f / 255f, 159f / 255f, 160f / 255f),
        new(197f / 255f, 159f / 255f, 225f / 255f),
    };

    private static readonly Color GlobalTransitionColor = new(0.6f, 0.6f, 0.6f);

    // Fixed literal colors for the node chrome - these take priority over the per-colorIndex
    // StateColors/TransitionColors palette above, which is kept computed (NodeLayout.FillColor/
    // ColorIndex) but is otherwise dormant at draw time now that title/row/outline/line colors
    // are all fixed rather than state-identity-driven.
    private static readonly Color NodeOutlineColor = Color.white;
    private static readonly Color TitleBackgroundColor = Color.black;
    private static readonly Color TransitionRowBackgroundColor = new(0.2f, 0.2f, 0.2f);
    private static readonly Color TransitionLineColor = new(0.85f, 0.85f, 0.85f);
    private static readonly Color ActiveStateOutlineColor = new(0f, 0.75f, 1f);

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
    private float _textStyleBuiltForZoom = -1f;

    private bool _isVisible;
    private bool _selectionUiVisible = true;
    private FsmSnapshot? _snapshot;
    private int _selectedFsmIndex = -1;
    private string? _selectedStateName;

    // Scene -> object -> FSM drill-down for the list panel. Built once per RefreshSnapshot rather
    // than every OnGUI call or every navigation click - Unity dispatches OnGUI several times per
    // rendered frame (Layout, Repaint, once per queued input event), and re-grouping/re-formatting
    // labels on every single one of those was a steady GC-allocation source even with no graph open.
    private enum FsmListLevel { Scenes, Objects, Fsms }
    private FsmListLevel _fsmListLevel = FsmListLevel.Scenes;
    private int _selectedSceneIndex = -1;
    private int _selectedObjectIndex = -1;
    private List<SceneGroup>? _sceneGroups;

    // Navigation clicks (scene/object rows, back buttons) stage their change here instead of
    // mutating _fsmListLevel/_selectedSceneIndex/_selectedObjectIndex directly. Unity dispatches
    // OnGUI multiple times per rendered frame (Layout first, then queued input events, then
    // Repaint last), all sharing one GUILayoutGroup control count established during that frame's
    // Layout pass - if a click (detected on a later event, e.g. MouseUp) switched the level
    // immediately, the Repaint pass later in the same frame would render a different level (and
    // therefore a different control count) than Layout recorded, which Unity reports as
    // "Getting control N's position in a group with only N controls". Applying the change in
    // Update() instead means it always takes effect at the very start of the NEXT frame, before
    // that frame's own Layout pass runs, so every event within a given frame always sees the same
    // level/selection throughout.
    private FsmListLevel? _pendingFsmListLevel;
    private int _pendingSelectedSceneIndex;
    private int _pendingSelectedObjectIndex;

    // Cached per-frame virtualization window for whichever list level is active - recomputed only
    // on the Layout event (see ComputeFsmListWindow) and reused for every other event dispatched
    // that same frame. _fsmListScrollPosition can change mid-frame (e.g. a ScrollWheel event
    // processed after Layout but before Repaint), and recomputing the window from its live value on
    // every event hits the same Layout/Repaint control-count mismatch described above.
    private int _fsmListWindowFirstVisible;
    private int _fsmListWindowLastVisible;

    // World-space point currently centered in the canvas, plus zoom - resolution-independent by
    // design. The canvas rect itself is recomputed from Screen.width/height every OnGUI call, so
    // resizing the window or running at a different resolution never distorts node layout.
    private Vector2 _panWorldCenter;
    private float _zoom = 1f;
    private bool _isPanning;

    private Vector2 _fsmListScrollPosition;
    private Vector2 _sidePanelScrollPosition;

    // Node/edge layout cache - rebuilt only when the FSM selection, pan, zoom, or canvas size
    // changes. Unity dispatches OnGUI once per queued input event (several times per rendered
    // frame); recomputing this from scratch on every single call was the confirmed source of a
    // GC stutter, so it's now cache-gated and only ever rebuilt when something actually moved.
    private Dictionary<string, NodeLayout>? _nodeLayoutCache;
    private List<GlobalPseudoNodeLayout>? _globalPseudoNodeCache;
    private int _layoutCacheFsmIndex = -1;
    private Vector2 _layoutCachePanCenter;
    private float _layoutCacheZoom;
    private Rect _layoutCacheCanvasRect;

    // Pure topology (which states point at a given target, sorted by source world-X) - independent
    // of screen space, so this only needs recomputing when the selected FSM changes, not the camera.
    private Dictionary<string, List<IncomingTransition>>? _incomingTransitionsByTarget;

    // Reused per-Repaint scratch buffer of (already-tessellated polyline, color) pairs - filled while
    // walking pseudo-nodes/edges (which still draw their labels via GUI as before) and then drawn as
    // a single GL batch (see DrawLineBufferGL), instead of allocating a new list every frame.
    private readonly List<(Vector2[] Points, Color Color)> _lineDrawBuffer = new();

    // Reused per-Repaint scratch buffer of (vertex position, color) pairs, 3 entries per triangle -
    // holds every node/pseudo-node chrome shape (backgrounds, outline rings, rounded corners) for the
    // frame, gathered while walking nodes/rows (which still draw their labels via GUI as before) and
    // then drawn as a single GL batch (see FlushChromeBufferGL), replacing what used to be up to ~7
    // GUI.DrawTexture calls per rounded rect (flat fills plus a baked corner-mask texture per corner).
    private readonly List<(Vector2 Position, Color Color)> _chromeVertexBuffer = new();

    // Side panel content cache - rebuilt only when the selected state changes.
    private string? _sidePanelCachedStateName;
    private List<(string Header, List<(string Label, string Value)> Fields)>? _sidePanelActionCache;
    private List<(string Label, string Value)>? _sidePanelVariableCache;
    private List<string>? _sidePanelEventCache;

    // ---- TEMPORARY PERF DIAGNOSTICS: delete this block after profiling ----
    // Buckets the Repaint draw path in DrawCachedGraph into line drawing, label drawing, and node
    // "chrome" (rounded rects/outlines/dividers), plus off-screen cull counts, so a slow-graph
    // report can be traced to an actual cause instead of guessed at. Logs an averaged summary every
    // DiagnosticLogIntervalFrames Repaint frames rather than every frame, so the logging call itself
    // doesn't skew the measurement.
    private const int DiagnosticLogIntervalFrames = 60;
    private readonly Stopwatch _diagnosticLineTimer = new();
    private readonly Stopwatch _diagnosticLabelTimer = new();
    private readonly Stopwatch _diagnosticChromeTimer = new();
    private int _diagnosticFrameCount;
    private int _diagnosticNodesTotal;
    private int _diagnosticNodesDrawn;
    private int _diagnosticEdgesTotal;
    private int _diagnosticEdgesDrawn;
    // ---- END TEMPORARY PERF DIAGNOSTICS ----

    // ---- TEMPORARY PERF DIAGNOSTICS: FSM list UI - delete this block after profiling ----
    // Times the whole DrawFsmList call (scene/object/FSM drill-down list panel) on every OnGUI
    // event - deliberately not Repaint-gated like the graph diagnostics above, since GUILayout
    // controls run their full layout/hit-test logic on every event type (Layout, Repaint, once per
    // queued input event), not just Repaint. Logs an averaged summary every
    // DiagnosticLogIntervalFrames calls rather than every one, so the logging call itself doesn't
    // skew the measurement.
    private readonly Stopwatch _diagnosticFsmListTimer = new();
    private int _diagnosticFsmListCallCount;
    // ---- END TEMPORARY PERF DIAGNOSTICS: FSM list UI ----

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
        _textStyleBuiltForZoom = -1f;
    }

    public void Update()
    {
        // Applied here rather than at click time - see _pendingFsmListLevel's declaration comment.
        // Update() always runs before this frame's own OnGUI (Layout/input/Repaint) dispatches, so
        // the level/selection is guaranteed stable for every one of them.
        if (_pendingFsmListLevel.HasValue)
        {
            _fsmListLevel = _pendingFsmListLevel.Value;
            _selectedSceneIndex = _pendingSelectedSceneIndex;
            _selectedObjectIndex = _pendingSelectedObjectIndex;
            _fsmListScrollPosition = Vector2.zero;
            _pendingFsmListLevel = null;
        }

        if (Input.GetKeyDown(KeyCode.Alpha0))
        {
            _isVisible = !_isVisible;
            if (_isVisible)
            {
                RefreshSnapshot();
            }
        }

        // Toggles the FSM list, side panel, and their box backgrounds off, leaving just the graph
        // canvas - and switches transition lines to the thinner style to match the more minimal
        // look. Pressing "1" again restores both. Only active while the overlay itself is on.
        if (_isVisible && Input.GetKeyDown(KeyCode.Alpha1))
        {
            _selectionUiVisible = !_selectionUiVisible;
            _useThinGlLines = !_selectionUiVisible;
            _logger.LogInfo($"[FsmMaster] Graph overlay: selection UI {(_selectionUiVisible ? "shown" : "hidden")}.");
        }
    }

    // Only runs once per toggle-on (see Update).
    private void RefreshSnapshot()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        PlayMakerFSM[] allComponents = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);
        _snapshot = FsmDataCollector.CollectSnapshot(sceneName, allComponents);
        BuildFsmListHierarchy();

        _selectedFsmIndex = -1;
        _selectedStateName = null;
        _fsmListLevel = FsmListLevel.Scenes;
        _selectedSceneIndex = -1;
        _selectedObjectIndex = -1;
        _fsmListScrollPosition = Vector2.zero;
        _pendingFsmListLevel = null;
        InvalidateGraphCaches();
        InvalidateSidePanelCache();

        if (_snapshot.Fsms.Count == 0)
        {
            _logger.LogInfo("[FsmMaster] Graph overlay: no live PlayMakerFSM instances found in this scene.");
        }
    }

    // Groups the flat snapshot into scene -> object -> FSM for the drill-down list panel, sorted
    // alphabetically at each level so the list panel never has to format or sort anything itself.
    private void BuildFsmListHierarchy()
    {
        var scenesByName = new Dictionary<string, SceneGroup>();
        var objectsByScene = new Dictionary<string, Dictionary<int, ObjectGroup>>();

        for (int i = 0; i < _snapshot!.Fsms.Count; i++)
        {
            FsmInfo fsm = _snapshot.Fsms[i];
            GameObject gameObject = fsm.Component.gameObject;
            string sceneName = gameObject.scene.name;

            if (!scenesByName.TryGetValue(sceneName, out SceneGroup? sceneGroup))
            {
                sceneGroup = new SceneGroup { SceneName = sceneName };
                scenesByName[sceneName] = sceneGroup;
                objectsByScene[sceneName] = new Dictionary<int, ObjectGroup>();
            }

            Dictionary<int, ObjectGroup> objectsById = objectsByScene[sceneName];
            int instanceId = gameObject.GetInstanceID();
            if (!objectsById.TryGetValue(instanceId, out ObjectGroup? objectGroup))
            {
                objectGroup = new ObjectGroup { InstanceId = instanceId, Label = fsm.GameObjectName };
                objectsById[instanceId] = objectGroup;
                sceneGroup.Objects.Add(objectGroup);
            }

            objectGroup.FsmIndices.Add(i);
            objectGroup.FsmLabels.Add(fsm.FsmName);
        }

        var sceneGroups = new List<SceneGroup>(scenesByName.Values);
        sceneGroups.Sort((a, b) => string.CompareOrdinal(a.SceneName, b.SceneName));

        foreach (SceneGroup sceneGroup in sceneGroups)
        {
            sceneGroup.Objects.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));

            foreach (ObjectGroup objectGroup in sceneGroup.Objects)
            {
                // FsmIndices/FsmLabels are parallel lists - sort a permutation of indices by label,
                // then rebuild both lists in that order so they stay aligned.
                var order = new List<int>(objectGroup.FsmLabels.Count);
                for (int i = 0; i < objectGroup.FsmLabels.Count; i++)
                {
                    order.Add(i);
                }

                order.Sort((a, b) => string.CompareOrdinal(objectGroup.FsmLabels[a], objectGroup.FsmLabels[b]));

                var sortedIndices = new List<int>(order.Count);
                var sortedLabels = new List<string>(order.Count);
                foreach (int i in order)
                {
                    sortedIndices.Add(objectGroup.FsmIndices[i]);
                    sortedLabels.Add(objectGroup.FsmLabels[i]);
                }

                objectGroup.FsmIndices = sortedIndices;
                objectGroup.FsmLabels = sortedLabels;
            }
        }

        _sceneGroups = sceneGroups;
    }

    // ---- TEMPORARY PERF DIAGNOSTICS: FSM list UI - delete this method after profiling ----
    // Logs an averaged ms/call summary every DiagnosticLogIntervalFrames OnGUI calls, then resets
    // the accumulator for the next window - see _diagnosticFsmListTimer's declaration comment.
    private void LogFsmListDiagnosticSummaryIfDue()
    {
        _diagnosticFsmListCallCount++;
        if (_diagnosticFsmListCallCount < DiagnosticLogIntervalFrames)
        {
            return;
        }

        double avgMs = _diagnosticFsmListTimer.Elapsed.TotalMilliseconds / _diagnosticFsmListCallCount;
        _logger.LogInfo(FormattableString.Invariant(
            $"[FsmMaster] PERF FsmList UI avg/call over {_diagnosticFsmListCallCount} OnGUI calls: {avgMs:F3}ms (level={_fsmListLevel})"));

        _diagnosticFsmListTimer.Reset();
        _diagnosticFsmListCallCount = 0;
    }
    // ---- END TEMPORARY PERF DIAGNOSTICS: FSM list UI ----

    public void OnGUI()
    {
        if (!_isVisible || _snapshot == null)
        {
            return;
        }

        // With the selection UI hidden (see the "1" key in Update), the list/side-panel rects - and
        // their GUI.skin.box backgrounds - collapse to zero width entirely, rather than just having
        // their contents skipped, so the graph canvas expands to fill the freed screen space.
        float listWidth = _selectionUiVisible ? ListWidth : 0f;
        var listRect = new Rect(0f, 0f, listWidth, Screen.height);
        bool showSidePanel = _selectionUiVisible && _selectedStateName != null;
        float sidePanelWidth = showSidePanel ? SidePanelWidth : 0f;
        var canvasRect = new Rect(listRect.width, 0f, Screen.width - listRect.width - sidePanelWidth, Screen.height);
        var sidePanelRect = new Rect(canvasRect.xMax, 0f, sidePanelWidth, Screen.height);

        if (_selectionUiVisible)
        {
            GUILayout.BeginArea(listRect, GUI.skin.box);
            _diagnosticFsmListTimer.Start();
            DrawFsmList(listRect.height);
            _diagnosticFsmListTimer.Stop();
            LogFsmListDiagnosticSummaryIfDue();
            GUILayout.EndArea();
        }

        if (_selectedFsmIndex >= 0 && _selectedFsmIndex < _snapshot.Fsms.Count)
        {
            FsmInfo selectedFsm = _snapshot.Fsms[_selectedFsmIndex];
            if (selectedFsm.Component == null)
            {
                // Backing GameObject/component was destroyed (e.g. scene change) while the overlay
                // was open - drop the stale selection rather than drawing a dead FSM's graph.
                _selectedFsmIndex = -1;
                _selectedStateName = null;
                InvalidateGraphCaches();
                InvalidateSidePanelCache();
            }
            else
            {
                DrawGraph(selectedFsm, canvasRect);

                if (showSidePanel)
                {
                    GUILayout.BeginArea(sidePanelRect, GUI.skin.box);
                    DrawSidePanel(selectedFsm);
                    GUILayout.EndArea();
                }
            }
        }
    }

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
        _titleStyle.normal.textColor = Color.white;

        _eventStyle = new GUIStyle(_titleStyle) { fontStyle = FontStyle.Normal };

        _textStyleBuiltForZoom = _zoom;
    }

    // Scene -> object -> FSM drill-down, dispatching to one of three level-specific draw methods
    // below rather than a single generic helper - a generic Func<int,string>/Action<int> helper
    // would allocate a fresh closure on every OnGUI call (Layout, Repaint, each queued input
    // event), which is exactly the per-frame GC-allocation pattern this file otherwise avoids.
    private void DrawFsmList(float viewportHeight)
    {
        switch (_fsmListLevel)
        {
            case FsmListLevel.Scenes:
                DrawSceneList(viewportHeight);
                break;
            case FsmListLevel.Objects:
                DrawObjectList(viewportHeight);
                break;
            case FsmListLevel.Fsms:
                DrawFsmLeafList(viewportHeight);
                break;
        }
    }

    // Recomputes the shared virtualization window only on the Layout event and caches it into
    // _fsmListWindowFirstVisible/_fsmListWindowLastVisible - see those fields' declaration comment
    // for why every other event this frame must reuse the cached values instead of recomputing.
    private void ComputeFsmListWindow(int count, float viewportHeight)
    {
        if (Event.current.type != EventType.Layout)
        {
            return;
        }

        _fsmListWindowFirstVisible = Mathf.Clamp(Mathf.FloorToInt(_fsmListScrollPosition.y / FsmListRowHeight), 0, count);
        int visibleRowCount = Mathf.CeilToInt(viewportHeight / FsmListRowHeight) + 1;
        _fsmListWindowLastVisible = Mathf.Clamp(_fsmListWindowFirstVisible + visibleRowCount, _fsmListWindowFirstVisible, count);
    }

    private void DrawSceneList(float viewportHeight)
    {
        GUILayout.Label("Loaded scenes");
        _fsmListScrollPosition = GUILayout.BeginScrollView(_fsmListScrollPosition);

        // Only the rows actually inside the scroll viewport get a real GUILayout.Button - with a
        // long list, GUILayout controls run their full layout/style/hit-test logic on *every*
        // OnGUI event (Layout, Repaint, each queued input event), not just Repaint, so one button
        // per row regardless of scroll position was the actual remaining cost. Space() placeholders
        // before/after the visible slice preserve the scrollbar's total content height without
        // instantiating off-screen controls. Same pattern repeated in DrawObjectList/DrawFsmLeafList
        // below for the other two levels.
        List<SceneGroup> scenes = _sceneGroups!;
        int count = scenes.Count;
        ComputeFsmListWindow(count, viewportHeight);
        int firstVisible = _fsmListWindowFirstVisible;
        int lastVisible = _fsmListWindowLastVisible;

        if (firstVisible > 0)
        {
            GUILayout.Space(firstVisible * FsmListRowHeight);
        }

        for (int i = firstVisible; i < lastVisible; i++)
        {
            if (GUILayout.Button(scenes[i].SceneName, GUILayout.Height(FsmListRowHeight)))
            {
                _pendingFsmListLevel = FsmListLevel.Objects;
                _pendingSelectedSceneIndex = i;
                _pendingSelectedObjectIndex = -1;
            }
        }

        if (lastVisible < count)
        {
            GUILayout.Space((count - lastVisible) * FsmListRowHeight);
        }

        GUILayout.EndScrollView();
    }

    private void DrawObjectList(float viewportHeight)
    {
        SceneGroup sceneGroup = _sceneGroups![_selectedSceneIndex];

        if (GUILayout.Button("< Back to scenes"))
        {
            _pendingFsmListLevel = FsmListLevel.Scenes;
            _pendingSelectedSceneIndex = -1;
            _pendingSelectedObjectIndex = -1;
            return;
        }

        GUILayout.Label($"Scene: {sceneGroup.SceneName}");
        _fsmListScrollPosition = GUILayout.BeginScrollView(_fsmListScrollPosition);

        List<ObjectGroup> objects = sceneGroup.Objects;
        int count = objects.Count;
        ComputeFsmListWindow(count, viewportHeight);
        int firstVisible = _fsmListWindowFirstVisible;
        int lastVisible = _fsmListWindowLastVisible;

        if (firstVisible > 0)
        {
            GUILayout.Space(firstVisible * FsmListRowHeight);
        }

        for (int i = firstVisible; i < lastVisible; i++)
        {
            if (GUILayout.Button(objects[i].Label, GUILayout.Height(FsmListRowHeight)))
            {
                _pendingFsmListLevel = FsmListLevel.Fsms;
                _pendingSelectedSceneIndex = _selectedSceneIndex;
                _pendingSelectedObjectIndex = i;
            }
        }

        if (lastVisible < count)
        {
            GUILayout.Space((count - lastVisible) * FsmListRowHeight);
        }

        GUILayout.EndScrollView();
    }

    private void DrawFsmLeafList(float viewportHeight)
    {
        ObjectGroup objectGroup = _sceneGroups![_selectedSceneIndex].Objects[_selectedObjectIndex];

        if (GUILayout.Button("< Back to objects"))
        {
            _pendingFsmListLevel = FsmListLevel.Objects;
            _pendingSelectedSceneIndex = _selectedSceneIndex;
            _pendingSelectedObjectIndex = -1;
            return;
        }

        GUILayout.Label($"Object: {objectGroup.Label}");
        _fsmListScrollPosition = GUILayout.BeginScrollView(_fsmListScrollPosition);

        List<string> fsmLabels = objectGroup.FsmLabels;
        int count = fsmLabels.Count;
        ComputeFsmListWindow(count, viewportHeight);
        int firstVisible = _fsmListWindowFirstVisible;
        int lastVisible = _fsmListWindowLastVisible;

        if (firstVisible > 0)
        {
            GUILayout.Space(firstVisible * FsmListRowHeight);
        }

        // Selecting a leaf FSM does not change _fsmListLevel - the list stays on this object's FSMs
        // so the graph panel and list panel remain independently browsable, matching how they
        // already behave today (selecting a state doesn't change the list either). This assignment
        // is immediate (not deferred through _pendingFsmListLevel) because it never changes which
        // level-draw method runs or how many controls it lays out, so it carries none of the
        // Layout/Repaint mismatch risk the level transitions above do.
        for (int i = firstVisible; i < lastVisible; i++)
        {
            if (GUILayout.Button(fsmLabels[i], GUILayout.Height(FsmListRowHeight)))
            {
                SelectFsm(objectGroup.FsmIndices[i]);
            }
        }

        if (lastVisible < count)
        {
            GUILayout.Space((count - lastVisible) * FsmListRowHeight);
        }

        GUILayout.EndScrollView();
    }

    private void SelectFsm(int index)
    {
        _selectedFsmIndex = index;
        _selectedStateName = null;
        InvalidateGraphCaches();
        InvalidateSidePanelCache();

        FsmInfo fsm = _snapshot!.Fsms[index];
        FitViewToFsm(fsm);
        BuildIncomingTransitionsByTarget(fsm);
        LogNodePositionDiagnostics(fsm);
    }

    private void InvalidateGraphCaches()
    {
        _nodeLayoutCache = null;
        _globalPseudoNodeCache = null;
        _incomingTransitionsByTarget = null;
        _layoutCacheFsmIndex = -1;
    }

    private void InvalidateSidePanelCache()
    {
        _sidePanelCachedStateName = null;
        _sidePanelActionCache = null;
        _sidePanelVariableCache = null;
        _sidePanelEventCache = null;
    }

    // Which states point at a given target, sorted by source world-X. Pure topology - independent
    // of screen space, so computed once per FSM selection rather than in the per-frame draw path.
    // WorldToScreen is a uniform scale+translate, so sorting by world-X preserves the same relative
    // order screen-X would give, meaning this doesn't need recomputing as pan/zoom change either.
    private void BuildIncomingTransitionsByTarget(FsmInfo fsm)
    {
        var byTarget = new Dictionary<string, List<IncomingTransition>>();
        foreach (FsmStateInfo state in fsm.States)
        {
            foreach (FsmTransitionInfo transition in state.Transitions)
            {
                if (!byTarget.TryGetValue(transition.ToState, out List<IncomingTransition>? list))
                {
                    list = new List<IncomingTransition>();
                    byTarget[transition.ToState] = list;
                }

                list.Add(new IncomingTransition(state.Name, transition.EventName));
            }
        }

        var worldX = new Dictionary<string, float>();
        foreach (FsmStateInfo state in fsm.States)
        {
            worldX[state.Name] = state.State.Position.x;
        }

        foreach (List<IncomingTransition> list in byTarget.Values)
        {
            list.Sort((a, b) =>
            {
                float ax = worldX.TryGetValue(a.FromState, out float x1) ? x1 : 0f;
                float bx = worldX.TryGetValue(b.FromState, out float x2) ? x2 : 0f;
                return ax.CompareTo(bx);
            });
        }

        _incomingTransitionsByTarget = byTarget;
    }

    // Diagnostic for the "stacked nodes" report - logs each state's raw Position/ColorIndex next to
    // its computed screen Rect once per FSM selection (not continuously), so a cluster of states
    // with near-identical Position values (an FSM author never dragging them apart in the PlayMaker
    // editor) is distinguishable at a glance from a genuine WorldToScreen collision.
    private void LogNodePositionDiagnostics(FsmInfo fsm)
    {
        var localCanvasRect = new Rect(0f, 0f, Mathf.Max(Screen.width - ListWidth - SidePanelWidth, 100f), Screen.height);

        foreach (FsmStateInfo state in fsm.States)
        {
            Rect position = state.State.Position;
            Rect screenRect = WorldToScreen(position, localCanvasRect);
            _logger.LogInfo(FormattableString.Invariant(
                $"[FsmMaster] Graph overlay: state \"{state.Name}\" Position=({position.x:F1}, {position.y:F1}, {position.width:F1}, {position.height:F1}) ColorIndex={state.State.ColorIndex} ScreenRect=({screenRect.x:F1}, {screenRect.y:F1}, {screenRect.width:F1}, {screenRect.height:F1})"));
        }
    }

    // FsmState.Position values live in PlayMaker's own unbounded editor-canvas space, authored
    // per-FSM with an arbitrary origin - fit the view to whatever this FSM's states actually
    // occupy rather than assuming a fixed pan/zoom starting point.
    private void FitViewToFsm(FsmInfo fsm)
    {
        if (fsm.States.Count == 0)
        {
            _panWorldCenter = Vector2.zero;
            _zoom = 1f;
            return;
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

        float canvasWidth = Mathf.Max(Screen.width - ListWidth - SidePanelWidth, 100f);
        float canvasHeight = Mathf.Max(Screen.height, 100f);

        _zoom = Mathf.Clamp(Mathf.Min(canvasWidth / worldWidth, canvasHeight / worldHeight), MinZoom, MaxZoom);
        _panWorldCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
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

    private void DrawGraph(FsmInfo fsm, Rect canvasRect)
    {
        GUI.BeginGroup(canvasRect, GUI.skin.box);
        var localCanvasRect = new Rect(0f, 0f, canvasRect.width, canvasRect.height);

        bool cacheStale = _nodeLayoutCache == null
            || _layoutCacheFsmIndex != _selectedFsmIndex
            || _layoutCachePanCenter != _panWorldCenter
            || !Mathf.Approximately(_layoutCacheZoom, _zoom)
            || _layoutCacheCanvasRect != localCanvasRect;

        if (cacheStale)
        {
            RebuildNodeLayoutCache(fsm, localCanvasRect);
            _layoutCacheFsmIndex = _selectedFsmIndex;
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
        DrawCachedGraph(fsm.Fsm.ActiveStateName, canvasRect.position);
        HandlePanAndZoom(localCanvasRect);

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

        // Entry points for incoming transitions - this is the actual convergence fix. Always spread
        // along whichever of the target's left/right edges each transition attaches to (matching
        // FSMExpress's own convention - never the top/bottom), picked per-transition by
        // EntersLeftSide (built on top of PickExitDirection's own side choice - see both for the
        // reasoning), so nodes stacked in a rough vertical column - where different transitions
        // previously picked inconsistent left/right sides based on tiny, often-incidental x
        // differences between states an FSM author never intended to offset - now consistently
        // favor whichever side is actually closest instead of producing the crossing/braided look
        // that instability caused.
        var targetEntryPoints = new Dictionary<string, Vector2[]>();
        if (_incomingTransitionsByTarget != null)
        {
            foreach (KeyValuePair<string, List<IncomingTransition>> entry in _incomingTransitionsByTarget)
            {
                if (!nodeScreenRects.TryGetValue(entry.Key, out Rect targetRect))
                {
                    continue;
                }

                List<IncomingTransition> incoming = entry.Value;
                int count = incoming.Count;

                var entersLeftSide = new bool[count];
                int leftCount = 0;
                for (int i = 0; i < count; i++)
                {
                    bool entersLeft;
                    if (nodeScreenRects.TryGetValue(incoming[i].FromState, out Rect fromRect))
                    {
                        Vector2 exitDirection = PickExitDirection(fromRect, targetRect);
                        entersLeft = EntersLeftSide(fromRect, targetRect, exitDirection);
                    }
                    else
                    {
                        entersLeft = true;
                    }

                    entersLeftSide[i] = entersLeft;
                    if (entersLeft)
                    {
                        leftCount++;
                    }
                }

                int rightCount = count - leftCount;
                var points = new Vector2[count];
                int leftIndex = 0, rightIndex = 0;
                for (int i = 0; i < count; i++)
                {
                    if (entersLeftSide[i])
                    {
                        points[i] = new Vector2(targetRect.x, targetRect.y + targetRect.height * (leftIndex + 0.5f) / leftCount);
                        leftIndex++;
                    }
                    else
                    {
                        points[i] = new Vector2(targetRect.xMax, targetRect.y + targetRect.height * (rightIndex + 0.5f) / rightCount);
                        rightIndex++;
                    }
                }

                targetEntryPoints[entry.Key] = points;
            }
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
                FillColor = StateColors[colorIndex],
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
                    // Picked once per transition - always left or right (exitDirection.y is always
                    // 0, never top/bottom). See PickExitDirection for how the exit side is chosen.
                    exitDirection = PickExitDirection(screenRect, targetRect);
                    sourceAnchor = new Vector2(exitDirection.x > 0f ? rowRect.xMax : rowRect.x, rowRect.center.y);

                    // Entry side favors whichever side is actually closest to the source rather than
                    // always being the exit side's opposite - see EntersLeftSide. entryDirection feeds
                    // SampleBezierCurve's target-side control point, which must point away from the
                    // target on whichever side is actually being entered (not just the exit side
                    // negated), or the curve's approach tangent points the wrong way when entry and
                    // exit land on the same side.
                    bool entersLeftSide = EntersLeftSide(screenRect, targetRect, exitDirection);
                    entryDirection = new Vector2(entersLeftSide ? -1f : 1f, 0f);
                    targetAnchor = entersLeftSide
                        ? new Vector2(targetRect.x, targetRect.center.y)
                        : new Vector2(targetRect.xMax, targetRect.center.y);

                    // Look up this specific transition's assigned spread point among everything
                    // pointing at the same target, so multiple incoming lines land at distinct
                    // positions along whichever of the target's left/right edges they attach to,
                    // instead of converging on one point. Fallback (above) only applies if this
                    // transition is somehow missing from the topology map built on selection.
                    if (targetEntryPoints.TryGetValue(transition.ToState, out Vector2[]? entryPoints)
                        && _incomingTransitionsByTarget != null
                        && _incomingTransitionsByTarget.TryGetValue(transition.ToState, out List<IncomingTransition>? incoming))
                    {
                        for (int j = 0; j < incoming.Count; j++)
                        {
                            if (incoming[j].FromState == state.Name && incoming[j].EventName == transition.EventName)
                            {
                                targetAnchor = entryPoints[j];
                                break;
                            }
                        }
                    }
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

    // Which side of `toRect` a transition should enter through, given the side it already exits
    // `fromRect` from (see PickExitDirection). When PickExitDirection picked a side because the
    // target genuinely sits to one side of the source, the curve is actually traveling horizontally
    // across to it, so the natural approach is from the opposite side. But when PickExitDirection
    // only fell back to its stable default (the common vertically-stacked case, where the target's
    // center sits within the source's own horizontal extent), there's no real horizontal travel to
    // justify swinging across the target's full width to the far side - entering on the SAME side as
    // the exit instead keeps the curve as a tight bow hugging whichever side is actually closest,
    // matching FSMExpress's own rendering of stacked states instead of an unnecessary diagonal
    // crossing through the node.
    private static bool EntersLeftSide(Rect fromRect, Rect toRect, Vector2 exitDirection)
    {
        bool genuineHorizontalOffset = toRect.center.x > fromRect.xMax || toRect.center.x < fromRect.x;
        bool exitsRight = exitDirection.x > 0f;
        return genuineHorizontalOffset ? exitsRight : !exitsRight;
    }

    // Samples a cubic Bezier once at layout-cache-build time (not per-frame) so DrawBezierArrow can
    // just walk a cached polyline every OnGUI call. Adapted from FSMExpress's FsmCanvasArrow, which
    // exits a node from whichever side faces the target via a control point offset from the anchor,
    // and enters the target through its own independently-chosen side (see EntersLeftSide) - control2
    // extends further in the entry direction (away from the target, on whichever side is actually
    // being entered) so the curve swoops in from whichever side it's actually attached to, rather
    // than always dropping straight down into the target.
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

    private void DrawCachedGraph(string? activeStateName, Vector2 canvasScreenOffset)
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

                    // Same rounded-outline chrome as a regular state box, per the request to keep
                    // globals visually consistent with state color-coding rather than a distinct
                    // flat grey block. Single band, so rounding all 4 corners is safe here. Geometry
                    // is only collected here - actually drawn in one GL batch below, alongside every
                    // other node's chrome gathered for this frame. The label itself is deliberately
                    // NOT drawn here (see the label pass after both GL flushes below).
                    AddRoundedRectOutlineToChromeBuffer(pseudo.Rect, TransitionRowBackgroundColor, NodeOutlineColor, NodeBorderThickness * _zoom);

                    // Arrow geometry is only collected here - actually drawn in one GL batch below,
                    // once every pseudo/edge line for this frame has been gathered.
                    _lineDrawBuffer.Add((pseudo.ArrowPoints, GlobalTransitionColor));
                }
            }

            // Regular per-state transition lines - geometry collected here (same cull check as
            // before), drawn in the same GL batch as the pseudo-node arrows above.
            foreach (NodeLayout node in _nodeLayoutCache.Values)
            {
                foreach (TransitionRow row in node.Rows)
                {
                    _diagnosticEdgesTotal++;
                    if (!row.CurveBounds.Overlaps(visibleRect))
                    {
                        continue;
                    }

                    _diagnosticEdgesDrawn++;
                    _lineDrawBuffer.Add((row.CurvePoints, TransitionLineColor));
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
                _diagnosticNodesTotal++;
                if (!node.ScreenRect.Overlaps(visibleRect))
                {
                    continue;
                }

                _diagnosticNodesDrawn++;

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

                bool isActive = activeStateName != null && node.Name == activeStateName;
                if (isActive)
                {
                    float activeOffset = (NodeBorderThickness + NodeActiveOutlineThickness) * _zoom;
                    var activeRect = new Rect(
                        node.ScreenRect.x - activeOffset,
                        node.ScreenRect.y - activeOffset,
                        node.ScreenRect.width + activeOffset * 2f,
                        node.ScreenRect.height + activeOffset * 2f);
                    AddRoundedRectToChromeBuffer(activeRect, cornerRadius + activeOffset, ActiveStateOutlineColor);
                }

                // Just the outer ring "background" - title/rows below are drawn full node width and
                // round their own top/bottom corners themselves, fully covering the inner area, so
                // no separate inner fill pass is needed here.
                float outlineThickness = NodeBorderThickness * _zoom;
                var outerRect = new Rect(
                    node.ScreenRect.x - outlineThickness,
                    node.ScreenRect.y - outlineThickness,
                    node.ScreenRect.width + outlineThickness * 2f,
                    node.ScreenRect.height + outlineThickness * 2f);
                AddRoundedRectToChromeBuffer(outerRect, cornerRadius + outlineThickness, NodeOutlineColor);

                bool titleIsOnlyBand = node.Rows.Count == 0;
                AddRoundedRectToChromeBuffer(node.TitleRect, cornerRadius, TitleBackgroundColor, roundTop: true, roundBottom: titleIsOnlyBand);
                // Title/row labels are deliberately NOT drawn here (see the label pass after both GL
                // flushes below).

                float dividerThickness = NodeBorderThickness * _zoom;
                if (node.Rows.Count > 0)
                {
                    AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, node.TitleRect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), NodeOutlineColor);
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
                        AddFilledRectToChromeBuffer(new Rect(node.ScreenRect.x, row.Rect.yMax - dividerThickness / 2f, node.ScreenRect.width, dividerThickness), TitleBackgroundColor);
                    }
                }
            }

            // One GL batch for every line gathered above - drawn before the chrome batch below so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            _diagnosticLineTimer.Start();
            DrawLineBufferGL(canvasScreenOffset);
            _diagnosticLineTimer.Stop();

            // One GL batch for every rounded-rect/fill gathered above - drawn after the line batch so
            // node/pseudo chrome sits on top of the lines, matching the layering the old per-shape
            // GUI.DrawTexture calls also preserved.
            _diagnosticChromeTimer.Start();
            FlushChromeBufferGL(canvasScreenOffset);
            _diagnosticChromeTimer.Stop();

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

                    _diagnosticLabelTimer.Start();
                    GUI.Label(pseudo.Rect, pseudo.EventName, _eventStyle);
                    _diagnosticLabelTimer.Stop();
                }
            }

            foreach (NodeLayout node in _nodeLayoutCache.Values)
            {
                if (!node.ScreenRect.Overlaps(visibleRect))
                {
                    continue;
                }

                _diagnosticLabelTimer.Start();
                GUI.Label(node.TitleRect, node.Name, _titleStyle);
                _diagnosticLabelTimer.Stop();

                foreach (TransitionRow row in node.Rows)
                {
                    _diagnosticLabelTimer.Start();
                    GUI.Label(row.Rect, row.EventName, _eventStyle);
                    _diagnosticLabelTimer.Stop();
                }
            }

            LogDiagnosticSummaryIfDue();
        }

        foreach (NodeLayout node in _nodeLayoutCache.Values)
        {
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && node.ScreenRect.Contains(Event.current.mousePosition))
            {
                _selectedStateName = node.Name;
                InvalidateSidePanelCache();
                Event.current.Use();
            }
        }
    }

    // ---- TEMPORARY PERF DIAGNOSTICS: delete this method after profiling ----
    // Logs an averaged ms/frame summary for each bucket, plus cull-effectiveness counts, every
    // DiagnosticLogIntervalFrames Repaint frames, then resets the accumulators for the next window.
    private void LogDiagnosticSummaryIfDue()
    {
        _diagnosticFrameCount++;
        if (_diagnosticFrameCount < DiagnosticLogIntervalFrames)
        {
            return;
        }

        double lineMs = _diagnosticLineTimer.Elapsed.TotalMilliseconds / _diagnosticFrameCount;
        double labelMs = _diagnosticLabelTimer.Elapsed.TotalMilliseconds / _diagnosticFrameCount;
        double chromeMs = _diagnosticChromeTimer.Elapsed.TotalMilliseconds / _diagnosticFrameCount;

        _logger.LogInfo(FormattableString.Invariant(
            $"[FsmMaster] PERF avg/frame over {_diagnosticFrameCount} frames: lines={lineMs:F3}ms labels={labelMs:F3}ms chrome={chromeMs:F3}ms | nodes {_diagnosticNodesDrawn}/{_diagnosticNodesTotal} drawn, edges {_diagnosticEdgesDrawn}/{_diagnosticEdgesTotal} drawn"));

        _diagnosticLineTimer.Reset();
        _diagnosticLabelTimer.Reset();
        _diagnosticChromeTimer.Reset();
        _diagnosticFrameCount = 0;
        _diagnosticNodesTotal = 0;
        _diagnosticNodesDrawn = 0;
        _diagnosticEdgesTotal = 0;
        _diagnosticEdgesDrawn = 0;
    }
    // ---- END TEMPORARY PERF DIAGNOSTICS ----

    private void HandlePanAndZoom(Rect canvasRect)
    {
        Event current = Event.current;

        if (current.type == EventType.MouseDown && current.button == 0 && canvasRect.Contains(current.mousePosition))
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
        else if (current.type == EventType.ScrollWheel && canvasRect.Contains(current.mousePosition))
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

        float thickness = TransitionLineThickness * _zoom;

        GL.PushMatrix();
        GL.LoadPixelMatrix(
            -canvasScreenOffset.x,
            Screen.width - canvasScreenOffset.x,
            Screen.height - canvasScreenOffset.y,
            -canvasScreenOffset.y);
        _glMaterial.SetPass(0);

        // Thin lines while the selection UI is hidden, thick otherwise - see _useThinGlLines.
        if (_useThinGlLines)
        {
            GL.Begin(GL.LINES);
            foreach ((Vector2[] points, Color color) in _lineDrawBuffer)
            {
                EmitThinPolyline(points, color);
            }
            GL.End();
        }
        else
        {
            GL.Begin(GL.TRIANGLES);
            foreach ((Vector2[] points, Color color) in _lineDrawBuffer)
            {
                EmitThickPolyline(points, color, thickness);
            }
            GL.End();
        }

        GL.PopMatrix();
    }

    // Plain 1px hard-edged GL.LINES, used when the selection UI is hidden - no thickness or
    // antialiasing control, matching the more minimal look of that mode.
    private static void EmitThinPolyline(Vector2[] points, Color color)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            EmitThinSegment(points[i], points[i + 1], color);
        }

        if (points.Length >= 2)
        {
            EmitThinArrowhead(points[points.Length - 2], points[points.Length - 1], color);
        }
    }

    private static void EmitThinSegment(Vector2 from, Vector2 to, Color color)
    {
        GL.Color(color);
        GL.Vertex3(from.x, from.y, 0f);
        GL.Vertex3(to.x, to.y, 0f);
    }

    private static void EmitThinArrowhead(Vector2 from, Vector2 to, Color color)
    {
        Vector3 backDirection = (Vector3)(from - to) * 0.15f;
        if (backDirection.sqrMagnitude < 0.01f)
        {
            return;
        }

        Vector2 left = Quaternion.Euler(0f, 0f, 25f) * backDirection;
        Vector2 right = Quaternion.Euler(0f, 0f, -25f) * backDirection;
        EmitThinSegment(to, to + left, color);
        EmitThinSegment(to, to + right, color);
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
            EmitThickArrowhead(points[points.Length - 2], points[points.Length - 1], color, thickness);
        }
    }

    private static void EmitThickArrowhead(Vector2 from, Vector2 to, Color color, float thickness)
    {
        Vector3 backDirection = (Vector3)(from - to) * 0.15f;
        if (backDirection.sqrMagnitude < 0.01f)
        {
            return;
        }

        Vector2 left = Quaternion.Euler(0f, 0f, 25f) * backDirection;
        Vector2 right = Quaternion.Euler(0f, 0f, -25f) * backDirection;
        EmitThickSegment(to, to + left, color, thickness);
        EmitThickSegment(to, to + right, color, thickness);
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

    private void DrawSidePanel(FsmInfo fsm)
    {
        if (_sidePanelCachedStateName != _selectedStateName)
        {
            FsmStateInfo? selectedState = null;
            foreach (FsmStateInfo state in fsm.States)
            {
                if (state.Name == _selectedStateName)
                {
                    selectedState = state;
                    break;
                }
            }

            if (selectedState == null)
            {
                _selectedStateName = null;
                InvalidateSidePanelCache();
                return;
            }

            RebuildSidePanelCache(fsm, selectedState);
        }

        _sidePanelScrollPosition = GUILayout.BeginScrollView(_sidePanelScrollPosition);

        GUILayout.Label($"State: {_selectedStateName}");

        GUILayout.Space(8f);
        GUILayout.Label("Actions");
        foreach ((string header, List<(string Label, string Value)> fields) in _sidePanelActionCache!)
        {
            GUILayout.Label(header);
            foreach ((string label, string value) in fields)
            {
                DrawFieldRow(label, value);
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("Variables");
        foreach ((string label, string value) in _sidePanelVariableCache!)
        {
            DrawFieldRow(label, value);
        }

        GUILayout.Space(8f);
        GUILayout.Label("Events");
        foreach (string eventName in _sidePanelEventCache!)
        {
            DrawFieldRow(eventName, "");
        }

        GUILayout.EndScrollView();
    }

    // Computed once per state selection rather than on every OnGUI pass - this (plus the layout
    // cache above) is what removes the per-frame LINQ/delegate allocations that were causing the
    // reported GC stutter while the panel was visible.
    private void RebuildSidePanelCache(FsmInfo fsm, FsmStateInfo state)
    {
        var actionCache = new List<(string Header, List<(string Label, string Value)> Fields)>();
        foreach (FsmActionInfo action in state.Actions)
        {
            var fields = new List<(string Label, string Value)>();
            foreach (FsmActionFieldInfo field in action.Fields)
            {
                fields.Add((field.FieldName, FormatActionField(action.Action, field.FieldValue)));
            }

            actionCache.Add((action.ActionType.Name, fields));
        }

        // Same typed-array set FsmConsoleLogger.LogFsmVariables already enumerates - reusing the
        // same known set rather than re-deriving what's on FsmVariables.
        FsmVariables variables = fsm.Fsm.Variables;
        var variableCache = new List<(string Label, string Value)>();
        foreach (FsmFloat v in variables.FloatVariables) variableCache.Add(($"Float \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmInt v in variables.IntVariables) variableCache.Add(($"Int \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmBool v in variables.BoolVariables) variableCache.Add(($"Bool \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmString v in variables.StringVariables) variableCache.Add(($"String \"{v.Name}\"", v.Value ?? "null"));
        foreach (FsmVector2 v in variables.Vector2Variables) variableCache.Add(($"Vector2 \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmVector3 v in variables.Vector3Variables) variableCache.Add(($"Vector3 \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmRect v in variables.RectVariables) variableCache.Add(($"Rect \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmQuaternion v in variables.QuaternionVariables) variableCache.Add(($"Quaternion \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmColor v in variables.ColorVariables) variableCache.Add(($"Color \"{v.Name}\"", v.Value.ToString()));
        foreach (FsmGameObject v in variables.GameObjectVariables) variableCache.Add(($"GameObject \"{v.Name}\"", v.Value != null ? v.Value.name : "null"));
        foreach (FsmObject v in variables.ObjectVariables) variableCache.Add(($"Object \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null"));
        foreach (FsmMaterial v in variables.MaterialVariables) variableCache.Add(($"Material \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null"));
        foreach (FsmTexture v in variables.TextureVariables) variableCache.Add(($"Texture \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null"));
        foreach (FsmEnum v in variables.EnumVariables) variableCache.Add(($"Enum \"{v.Name}\"", v.Value != null ? v.Value.ToString() : "null"));
        foreach (FsmArray v in variables.ArrayVariables) variableCache.Add(($"Array \"{v.Name}\"", string.Join(", ", v.Values)));

        var eventCache = new List<string>();
        foreach (FsmEvent fsmEvent in fsm.Fsm.Events)
        {
            eventCache.Add(fsmEvent.Name);
        }

        _sidePanelActionCache = actionCache;
        _sidePanelVariableCache = variableCache;
        _sidePanelEventCache = eventCache;
        _sidePanelCachedStateName = state.Name;
    }

    // Delegates scalar formatting to FsmConsoleLogger's own (promoted internal) formatter so both
    // the console dump and this panel describe FsmOwnerDefault/FsmEventTarget/NamedVariable fields
    // identically instead of maintaining two versions of the same switch.
    private static string FormatActionField(FsmStateAction action, object? fieldValue)
    {
        switch (fieldValue)
        {
            case FsmArray fsmArray:
                var arrayParts = new string[fsmArray.Length];
                for (int i = 0; i < fsmArray.Length; i++)
                {
                    arrayParts[i] = FsmConsoleLogger.FormatActionFieldValue(action, fsmArray.Values[i]);
                }
                return string.Join(", ", arrayParts);
            case Array array:
                var parts = new string[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    parts[i] = FsmConsoleLogger.FormatActionFieldValue(action, array.GetValue(i));
                }
                return string.Join(", ", parts);
            default:
                return FsmConsoleLogger.FormatActionFieldValue(action, fieldValue);
        }
    }

    private void DrawFieldRow(string label, string valueText)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(160f));
        GUILayout.Label(valueText);
        GUILayout.EndHorizontal();
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

    private readonly struct IncomingTransition
    {
        public readonly string FromState;
        public readonly string EventName;

        public IncomingTransition(string fromState, string eventName)
        {
            FromState = fromState;
            EventName = eventName;
        }
    }

    // One entry per distinct loaded scene containing at least one live PlayMakerFSM instance -
    // root level of the list panel's scene -> object -> FSM drill-down.
    private sealed class SceneGroup
    {
        public string SceneName = "";
        public List<ObjectGroup> Objects = new();
    }

    // One entry per distinct GameObject (keyed by instance ID, not name - many enemy/object
    // instances in this game share identical names) that has at least one live PlayMakerFSM.
    private sealed class ObjectGroup
    {
        public int InstanceId;
        public string Label = "";
        public List<int> FsmIndices = new();
        public List<string> FsmLabels = new();
    }
}
