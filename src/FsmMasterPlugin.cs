using System.Collections.Generic;
using System.Linq;
using BepInEx;
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

    internal FsmVariableTracker? VariableTracker => _variableTracker;

    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");

        _editManager = new FsmEditManager(Logger);
        _variableTracker = new FsmVariableTracker(fsmKey => _editManager.GetLiveInstances(fsmKey));
        _tabManager = new FsmTabManager();
        _graphOverlay = new FsmGraphOverlay(Logger);

        string sceneName = SceneManager.GetActiveScene().name;
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);

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
    }

    private void Update()
    {
        _graphOverlay?.Update();

        if (_rightPanel != null)
        {
            // Mirrors the graph overlay's own "0"/"1" hotkey state onto the uGUI right panel, which
            // has no IMGUI presence of its own for those keys to toggle directly - "0" hides
            // everything (whole tool off), "1" is the secondary "minimal view" toggle nested within
            // that (see FsmGraphOverlay.Update's own comment). Setting ActiveSelf from here (not from
            // the panel's own OnUpdate) is required - a node's own OnUpdate only runs while it's
            // already active, so it can never turn itself back on (see CanvasScrollbar.ShouldBeVisible
            // for the same reasoning applied to the tab strip's scrollbar).
            _rightPanel.ActiveSelf = _graphOverlay != null && _graphOverlay.IsVisible && _graphOverlay.SelectionUiVisible;

            FsmTabState? activeTab = _tabManager?.GetActive();
            FsmInfo? activeFsm = activeTab != null ? _graphOverlay?.ResolveFsmInfo(activeTab.FsmKey) : null;
            _rightPanel.ActiveStatePanel.Refresh(activeFsm, activeTab?.SelectedStateName);

            foreach (CanvasNode node in _rightPanel.Subtree())
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

        _graphOverlay?.OnGUI(_tabManager?.GetActive(), rightPanelScreenRect);
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
        _rightPanel = new FsmRightPanel(_uiCommon, _tabManager!, _editManager!, () => _graphOverlay?.CurrentSnapshot, Logger);
        _rightPanel.Build(_canvasGameObject.transform);
    }

    private void DestroyRightPanel()
    {
        _rightPanel?.Destroy();
        _rightPanel = null;

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
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);
        ApplyPersistedEditsForScene(scene.name, fsms);

        // The overlay's own snapshot goes stale on every scene load - PlayMakerFSM instances from the
        // previous scene are destroyed and need re-collecting. Uses SceneManager.GetActiveScene().name
        // (matching Awake), not the loaded `scene` parameter, since that's what RefreshSnapshot's own
        // FsmDataCollector.CollectSnapshot call has always keyed its snapshot by.
        _graphOverlay?.RefreshSnapshot(SceneManager.GetActiveScene().name, fsms);

        // Re-resolves every open tab's live PlayMakerFSM by FsmKey against the fresh snapshot - a tab
        // whose FSM isn't present here gets marked not-live (kept open as a placeholder) rather than
        // closed, per FsmTabManager.RebindAfterRefresh's contract.
        if (_graphOverlay?.CurrentSnapshot != null)
        {
            _tabManager?.RebindAfterRefresh(_graphOverlay.CurrentSnapshot);
        }
    }

    // Groups the freshly discovered live FSMs by FsmKey (see FsmIdentity, which also covers scene-authored
    // duplicate objects sharing an FSM), registers them with the edit manager, and reapplies whatever this
    // scene's save file has recorded for any key that's actually present.
    private void ApplyPersistedEditsForScene(string sceneName, PlayMakerFSM[] components)
    {
        if (_editManager == null)
        {
            return;
        }

        Dictionary<string, List<PlayMakerFSM>> groups = FsmIdentity.DiscoverFsmGroups(components);
        foreach (KeyValuePair<string, List<PlayMakerFSM>> group in groups)
        {
            _editManager.RegisterLiveInstances(group.Key, group.Value.Select(c => c.Fsm));
        }

        foreach (FsmEditSet editSet in FsmSaveDataStore.LoadAllForScene(sceneName))
        {
            if (groups.ContainsKey(editSet.FsmKey))
            {
                _editManager.ApplyEditSet(editSet);
            }
        }
    }

    // Reverts a live FSM to its pristine values and strips its persisted overrides, so this scene coming up
    // again later leaves it unmodified too.
    public void ResetFsm(string sceneName, string fsmKey)
    {
        _editManager?.ResetFsm(fsmKey);
        FsmSaveDataStore.ClearFsmKey(sceneName, fsmKey);
    }
}
