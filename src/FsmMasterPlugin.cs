using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FsmMaster;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.ace9653.fsmmaster")]
[BepInDependency("org.silksong-modding.fsmutil")]
public partial class FsmMasterPlugin : BaseUnityPlugin
{
    private FsmEditManager? _editManager;
    private FsmVariableTracker? _variableTracker;
    private FsmTabManager? _tabManager;
    private FsmGraphOverlay? _graphOverlay;

    private UICommon? _uiCommon;
    private GameObject? _canvasGameObject;
    private bool _ownsEventSystem;
    private FsmRightPanel? _rightPanel;
    private FsmMonitorPanel? _monitorPanel;
    private FsmPanelLayoutConfig? _rightPanelLayout;
    private FsmPanelLayoutConfig? _monitorPanelLayout;
    private ConfigEntry<bool>? _autoLoadConfig;

    // Reused every frame by Update's CanvasNode.CollectSubtree calls below, instead of a fresh
    // List<CanvasNode> (or the old yield-return Subtree() iterator's per-node enumerator) every
    // single tick regardless of whether either panel is even visible.
    private readonly List<CanvasNode> _rightPanelSubtreeBuffer = new();
    private readonly List<CanvasNode> _monitorPanelSubtreeBuffer = new();

    // Last CanvasPanel.StructureVersion each buffer above was actually rebuilt from - CollectSubtree
    // itself is skipped entirely (not just re-run into the same buffer) whenever neither tree's shape
    // has changed since the previous frame, since several composite widgets' own ChildList() overrides
    // still allocate a small enumerator per call (see CanvasPanel.StructureVersion's own comment) and
    // there is no reason to pay that cost on a frame where nothing was added, removed, or cleared.
    private int _rightPanelSubtreeVersion = -1;
    private int _monitorPanelSubtreeVersion = -1;

    private static Harmony? _harmony;

    internal FsmVariableTracker? VariableTracker => _variableTracker;

    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");

        _harmony = Harmony.CreateAndPatchAll(typeof(FsmMasterPlugin).Assembly);

        ConfigEntry<KeyboardShortcut> toggleOverlayHotkey = Config.Bind(
            "Hotkeys",
            "Toggle Overlay",
            new KeyboardShortcut(KeyCode.Alpha0),
            "Shows or hides the FSM graph overlay and its right-side panel.");
        ConfigEntry<KeyboardShortcut> toggleMinimalViewHotkey = Config.Bind(
            "Hotkeys",
            "Toggle Minimal View",
            new KeyboardShortcut(KeyCode.Alpha1),
            "While the overlay is visible, switches between the full selection UI and a minimal graph-only view.");

        FsmGraphColorConfig graphColors = FsmGraphColorConfig.Bind(Config);
        FsmGraphPerformanceConfig graphPerformance = FsmGraphPerformanceConfig.Bind(Config);
        _rightPanelLayout = FsmPanelLayoutConfig.Bind(Config, "FsmMaster Panel");
        _monitorPanelLayout = FsmPanelLayoutConfig.Bind(Config, "Monitor Panel");

        _autoLoadConfig = Config.Bind(
            "General",
            "Auto Load Last Configuration",
            true,
            "When enabled, each FSM's most recently saved/loaded named configuration is automatically "
                + "reapplied whenever a scene containing that FSM loads. Toggled in-game by the panel's Auto button.");

        _editManager = new FsmEditManager(Logger);
        _variableTracker = new FsmVariableTracker(fsmKey => _editManager.GetLiveInstances(fsmKey));
        _tabManager = new FsmTabManager();
        _graphOverlay = new FsmGraphOverlay(Logger, _editManager, _tabManager, toggleOverlayHotkey, toggleMinimalViewHotkey, graphColors, graphPerformance);

        // Scene.name has been observed to return null (not "") when Awake fires before the initial
        // scene has finished loading - a timing race, not something confirmed to be platform-specific,
        // but reported so far only on Linux. Falls back to string.Empty so FsmSaveDataStore's path
        // building never NREs on it; OnSceneLoaded re-runs this same call with the real scene name once
        // it's actually available, so this fallback only ever matters for this one redundant call.
        string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay.RefreshSnapshot(sceneName, fsms);
        _tabManager.RebindAfterRefresh(_graphOverlay.CurrentSnapshot!);

        BuildRightPanel();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;

        _editManager?.RevertAllForUnload();
        _editManager = null;
        _variableTracker = null;
        _tabManager = null;

        _graphOverlay?.Shutdown();
        _graphOverlay = null;

        DestroyRightPanel();

        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private void Update()
    {
        _graphOverlay?.Update();

        if (_rightPanel != null)
        {
            // Mirrors the graph overlay's own toggle-overlay/toggle-minimal-view hotkey state onto the
            // uGUI right panel, which has no IMGUI presence of its own for those hotkeys to toggle
            // directly - toggle-overlay hides everything (whole tool off), toggle-minimal-view is the
            // secondary "minimal view" toggle nested within that (see FsmGraphOverlay.Update's own
            // comment). Setting ActiveSelf from here (not from
            // the panel's own OnUpdate) is required - a node's own OnUpdate only runs while it's
            // already active, so it can never turn itself back on (see CanvasScrollbar.ShouldBeVisible
            // for the same reasoning applied to the tab strip's scrollbar).
            _rightPanel.ActiveSelf = _graphOverlay != null && _graphOverlay.IsVisible && _graphOverlay.SelectionUiVisible;

            FsmTabState? activeTab = _tabManager?.GetActive();
            FsmInfo? activeFsm = activeTab != null ? _graphOverlay?.ResolveFsmInfo(activeTab.FsmKey) : null;
            _rightPanel.ActiveStatePanel.Refresh(activeFsm, activeTab?.SelectedStateName);

            // One-shot request from a transition click in the graph overlay (see
            // FsmGraphOverlay.HandleTransitionLeftClick) - consumed and cleared here rather than left on
            // the tab, so it doesn't re-fire on every subsequent frame this tab stays active.
            if (activeTab?.PendingScrollActionIndex is { } pendingScrollActionIndex)
            {
                _rightPanel.ActiveStatePanel.ScrollToAction(pendingScrollActionIndex);
                activeTab.PendingScrollActionIndex = null;
            }

            // Refresh's own fsm/state/subtab cache only gates rebuilding row *structure* - it must not
            // gate the *values* those rows display, or the Load/Reset buttons (which mutate the live
            // FSM without changing which tab/state is selected) would appear to do nothing until the
            // user switches away and back. This re-reads and redisplays every already-built row's
            // current value every frame regardless of whether Refresh() just rebuilt anything.
            _rightPanel.ActiveStatePanel.RefreshLiveValues();

            if (_rightPanelSubtreeVersion != CanvasPanel.StructureVersion)
            {
                _rightPanelSubtreeBuffer.Clear();
                _rightPanel.CollectSubtree(_rightPanelSubtreeBuffer);
                _rightPanelSubtreeVersion = CanvasPanel.StructureVersion;
            }

            foreach (CanvasNode node in _rightPanelSubtreeBuffer)
            {
                if (node.ActiveInHierarchy)
                {
                    node.Update();
                }
            }
        }

        if (_monitorPanel != null)
        {
            // Deliberately NOT gated on SelectionUiVisible the way _rightPanel.ActiveSelf is above -
            // the monitor panel must survive the toggle-minimal-view hotkey (only locking/fading, see
            // Locked below) rather than disappearing with the rest of the selection UI.
            _monitorPanel.ActiveSelf = _graphOverlay != null && _graphOverlay.IsVisible;
            _monitorPanel.Locked = _graphOverlay != null && !_graphOverlay.SelectionUiVisible;
            _monitorPanel.RefreshRows(_variableTracker!);

            if (_monitorPanelSubtreeVersion != CanvasPanel.StructureVersion)
            {
                _monitorPanelSubtreeBuffer.Clear();
                _monitorPanel.CollectSubtree(_monitorPanelSubtreeBuffer);
                _monitorPanelSubtreeVersion = CanvasPanel.StructureVersion;
            }

            foreach (CanvasNode node in _monitorPanelSubtreeBuffer)
            {
                if (node.ActiveInHierarchy)
                {
                    node.Update();
                }
            }
        }
    }


    private void OnGUI()
    {
        Rect? rightPanelScreenRect = null;
        if (_rightPanel is { ActiveInHierarchy: true })
        {
            rightPanelScreenRect = new Rect(_rightPanel.Position.x, _rightPanel.Position.y, _rightPanel.Size.x, _rightPanel.Size.y);
        }

        // Both panels are freely draggable/resizable now (see FsmRightPanel/FsmMonitorPanel's own
        // resize handles) - the vignette needs the monitor panel's own current rect too, alongside the
        // right panel's, so it can skip over wherever that panel actually is instead of assuming it
        // only ever sits within the right panel's own docked strip (see FsmGraphOverlay.OnGUI).
        Rect? monitorPanelScreenRect = null;
        if (_monitorPanel is { ActiveInHierarchy: true })
        {
            monitorPanelScreenRect = new Rect(_monitorPanel.Position.x, _monitorPanel.Position.y, _monitorPanel.Size.x, _monitorPanel.Size.y);
        }

        // The Open dropdown (Scene/Object/FSM picker) isn't clipped to the right panel's own rect and
        // can extend past it - see FsmRightPanel.OpenDropdownScreenRect - so it needs its own vignette
        // hole rather than relying on rightPanelScreenRect to cover it.
        Rect? openDropdownScreenRect = _rightPanel?.OpenDropdownScreenRect;

        _graphOverlay?.OnGUI(_tabManager?.GetActive(), rightPanelScreenRect, monitorPanelScreenRect, openDropdownScreenRect);
    }

    // Builds the right-side uGUI panel's Canvas/EventSystem/widget tree - see FsmRightPanel. Reuses
    // an EventSystem already present in the scene if one exists: Silksong is itself a Unity-UI-driven
    // game, and Silksong.DebugMod - a real, working mod - never constructs its own, only ever reads
    // EventSystem.current (confirmed by grep across agent-context/Silksong.DebugMod-main), which is
    // strong precedent one is already there. Only creates one as a defensive fallback, tracked via
    // _ownsEventSystem so OnDestroy never tears down a scene-owned EventSystem it doesn't own.
    //
    // Flagged assumption: whether Silksong's own EventSystem (if present) is a persistent
    // DontDestroyOnLoad singleton or gets recreated per scene load hasn't been independently verified
    // here - if a scene transition ever leaves the canvas briefly without one, or produces a
    // "multiple EventSystems" warning, this detection needs to move to OnSceneLoaded as well.
    private void BuildRightPanel()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
        {
            var eventSystemObject = new GameObject("FsmMasterEventSystem");
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<StandaloneInputModule>();
            _ownsEventSystem = true;
        }

        _canvasGameObject = new GameObject("FsmMasterCanvas");
        _canvasGameObject.transform.SetParent(transform, false);
        Canvas canvas = _canvasGameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvasGameObject.AddComponent<GraphicRaycaster>();

        _uiCommon = new UICommon();
        _rightPanel = new FsmRightPanel(
            _uiCommon,
            _tabManager!,
            _editManager!,
            _variableTracker!,
            () => _graphOverlay?.CurrentSnapshot,
            () => _graphOverlay?.GraphVisible ?? true,
            visible => { if (_graphOverlay != null) _graphOverlay.GraphVisible = visible; },
            Logger,
            _rightPanelLayout!,
            _autoLoadConfig!);
        _rightPanel.Build(_canvasGameObject.transform);

        _monitorPanel = new FsmMonitorPanel(_uiCommon, _variableTracker!, _monitorPanelLayout!);
        _monitorPanel.Build(_canvasGameObject.transform);
    }

    private void DestroyRightPanel()
    {
        _rightPanel?.Destroy();
        _rightPanel = null;

        _monitorPanel?.Destroy();
        _monitorPanel = null;

        if (_canvasGameObject != null)
        {
            Destroy(_canvasGameObject);
            _canvasGameObject = null;
        }

        if (_ownsEventSystem)
        {
            EventSystem? eventSystem = FindFirstObjectByType<EventSystem>();
            if (eventSystem != null)
            {
                Destroy(eventSystem.gameObject);
            }

            _ownsEventSystem = false;
        }

        _uiCommon?.Destroy();
        _uiCommon = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        ApplyPersistedEditsForScene(scene.name, fsms);

        // The overlay's own snapshot goes stale on every scene load - PlayMakerFSM instances from the
        // previous scene are destroyed and need re-collecting. Uses SceneManager.GetActiveScene().name
        // (matching Awake), not the loaded `scene` parameter, since that's what RefreshSnapshot's own
        // FsmDataCollector.CollectSnapshot call has always keyed its snapshot by.
        _graphOverlay?.RefreshSnapshot(SceneManager.GetActiveScene().name ?? string.Empty, fsms);

        // Re-resolves every open tab's live PlayMakerFSM by FsmKey against the fresh snapshot - a tab
        // whose FSM isn't present here gets marked not-live (kept open as a placeholder) rather than
        // closed, per FsmTabManager.RebindAfterRefresh's contract.
        if (_graphOverlay?.CurrentSnapshot != null)
        {
            _tabManager?.RebindAfterRefresh(_graphOverlay.CurrentSnapshot);
        }
    }

    // Groups the freshly discovered live FSMs by FsmKey (see FsmIdentity, which also covers scene-authored
    // duplicate objects sharing an FSM), registers them with the edit manager, and - while the Auto button
    // is on - reapplies whichever named save was last chosen for each key that has a graph tab currently
    // open (see FsmTabManager) and is actually present in this scan. Restricted to open tabs rather than
    // every FSM the scan discovers: silently reapplying a saved preset onto an FSM the player has never
    // looked at was both surprising and, combined with DisableState/InstallSequencer's per-live-instance
    // object bookkeeping (see FsmEditManager.PruneStaleSnapshotEntries), meant every such FSM accumulated
    // pristine-snapshot state on every scene revisit for the rest of the session regardless of whether the
    // player cared about it. Multiple named saves can exist per FsmKey (see FsmSaveDataStore), so this
    // deliberately only ever reapplies the one remembered choice, not every save ever made for that FsmKey.
    private void ApplyPersistedEditsForScene(string sceneName, PlayMakerFSM[] components)
    {
        if (_editManager == null || _tabManager == null)
        {
            return;
        }

        Dictionary<string, List<PlayMakerFSM>> groups = FsmIdentity.DiscoverFsmGroups(components);
        _editManager.ReplaceLiveInstances(groups.ToDictionary(g => g.Key, g => g.Value.Select(c => c.Fsm).ToList()));

        if (_autoLoadConfig is not { Value: true })
        {
            return;
        }

        IEnumerable<string> openFsmKeysPresent = _tabManager.Tabs
            .Select(tab => tab.FsmKey)
            .Where(groups.ContainsKey);

        foreach (FsmEditSet editSet in FsmSaveDataStore.LoadLastChosenForScene(sceneName, openFsmKeysPresent))
        {
            _editManager.ApplyEditSet(editSet);
        }
    }

    // Reverts a live FSM to its pristine values and strips every named save (plus the remembered last-
    // chosen one) it has persisted, so this scene coming up again later leaves it unmodified too.
    public void ResetFsm(string sceneName, string fsmKey)
    {
        _editManager?.ResetFsm(fsmKey);
        FsmSaveDataStore.ClearAllSavesForFsm(sceneName, fsmKey);
    }
}
