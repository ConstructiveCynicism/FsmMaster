// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FsmMaster;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.constructivecynicism.fsmmaster")]
[BepInDependency("org.silksong-modding.modlist", "0.2.0")]
[BepInDependency(DebugMod.DebugMod.Id, BepInDependency.DependencyFlags.SoftDependency)]
public partial class FsmMasterPlugin : BaseUnityPlugin
{
    private IFsmLog? _log;
    private FsmEditManager? _editManager;
    private IDebugModCompat? _debugModCompat;
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
    private ConfigEntry<KeyboardShortcut>? _toggleOverlayHotkey;
    private ConfigEntry<bool>? _firstRunComplete;

    // Persisted settings like panel UI layout that aren't meant to be edited
    private ConfigFile? _uiStateConfig;

    // Reused every frame by Update's CanvasNode.CollectSubtree calls below, instead of a fresh
    // List<CanvasNode> (or the old yield-return Subtree() iterator's per-node enumerator) every
    // single tick regardless of whether either panel is even visible.
    private readonly List<CanvasNode> _rightPanelSubtreeBuffer = new();
    private readonly List<CanvasNode> _monitorPanelSubtreeBuffer = new();
    private int _rightPanelSubtreeVersion = -1;
    private int _monitorPanelSubtreeVersion = -1;

    private static Harmony? _harmony;

    // Static holders for awaiting harmony patches, isolated for hot reloading
    internal static FsmEditManager? ActiveEditManagerForPatches { get; private set; }
    internal static FsmMasterPlugin? ActiveInstanceForPatches { get; private set; }
    internal static bool ForceCursorVisible { get; private set; }

#region API
    // =========================================================================================
    // Public API for other mods
    // =========================================================================================

    /// <summary>
    /// Returns every FSM edit currently in effect this session as a JSON string, in the same format as files/savestate data
    /// </summary>
    /// <returns>A JSON string describing every active edit set. Empty/no edits still returns valid JSON.</returns>
    public static string GetActiveEdits()
    {
        List<FsmEditSet> activeEditSets = ActiveInstanceForPatches?._editManager?.GetAllActiveEditSets()
            ?? new List<FsmEditSet>();
        return FsmSaveDataStore.SerializeEditSets(activeEditSets);
    }

    /// <summary>
    /// Shows or hides the small "Fsm Edits Active" indicator the graph overlay draws in the
    /// bottom-right corner whenever at least one FSM has an edit in effect. On by default.
    /// </summary>
    public static bool ShowEditIndicator
    {
        get => FsmGraphOverlay.ShowEditIndicator;
        set => FsmGraphOverlay.ShowEditIndicator = value;
    }

    /// <summary>
    /// Whether FsmMaster should show UI and hint the toggle hotkey
    /// </summary>
    public static bool FirstRunComplete
    {
        get
        {
            if (ActiveInstanceForPatches is { _firstRunComplete: { } entry })
            {
                return entry.Value;
            }

            return false;
        }
        set
        {
            if (ActiveInstanceForPatches is { _firstRunComplete: { } entry })
            {
                entry.Value = value;
            }
        }
    }
#endregion
    //track cursor status to avoid patching every frame which causes flicker
    private bool _cursorPatchInstalled;

    private static readonly MethodInfo SetCursorVisibleMethod = AccessTools.Method(typeof(InputHandler), nameof(InputHandler.SetCursorVisible));
    private static readonly HarmonyMethod ForceCursorVisiblePrefix = new(typeof(ForceCursorVisiblePatch), nameof(ForceCursorVisiblePatch.Prefix));

    private void PatchCursorOverride() => _harmony?.Patch(SetCursorVisibleMethod, prefix: ForceCursorVisiblePrefix);

    private void UnpatchCursorOverride() => _harmony?.Unpatch(SetCursorVisibleMethod, HarmonyPatchType.Prefix, _harmony.Id);

    internal FsmVariableTracker? VariableTracker => _variableTracker;

    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} {BuildInfo.ReleaseName} ({Id}) has loaded!");
        _log = new BepInExLog(Logger);

        _harmony = Harmony.CreateAndPatchAll(typeof(FsmActivatedPatch));
        _harmony.PatchAll(typeof(GameFileLoadedPatch));
        _harmony.PatchAll(typeof(FocusOnHoverSuppressionPatch));

        _toggleOverlayHotkey = Config.Bind(
            "Hotkeys",
            "Toggle Overlay",
            new KeyboardShortcut(KeyCode.F3),
            "Shows or hides the FSM graph overlay and its right-side panel.");
        ConfigEntry<KeyboardShortcut> toggleMinimalViewHotkey = Config.Bind(
            "Hotkeys",
            "Toggle Minimal View",
            KeyboardShortcut.Empty,
            "While the overlay is visible, switches between the full selection UI and a minimal graph-only view. Unbound by default.");

        FsmGraphColorConfig graphColors = FsmGraphColorConfig.Bind(Config);
        FsmGraphPerformanceConfig graphPerformance = FsmGraphPerformanceConfig.Bind(Config);

        _autoLoadConfig = Config.Bind(
            "General",
            "Auto Load Last Configuration",
            false,
            "When enabled, each FSM's most recently saved/loaded named configuration is automatically "
                + "reapplied whenever a scene containing that FSM loads. Toggled in-game by the panel's Auto button.");

        Directory.CreateDirectory(FsmSaveDataStore.DataDirectory);
        _uiStateConfig = new ConfigFile(Path.Combine(FsmSaveDataStore.DataDirectory, $"{Id}.UIState.cfg"), saveOnInit: false);

        _rightPanelLayout = FsmPanelLayoutConfig.Bind(_uiStateConfig, "FsmMaster Panel");
        _monitorPanelLayout = FsmPanelLayoutConfig.Bind(_uiStateConfig, "Monitor Panel");

        var hiddenFromConfigManager = new ConfigurationManagerAttributes { Browsable = false };
        _firstRunComplete = _uiStateConfig.Bind(
            "General",
            "First Run Complete",
            false,
            new ConfigDescription(
                "Set automatically once the mod has shown its first-run hotkey hint after a save file loads. Not meant to be hand-edited.",
                null,
                hiddenFromConfigManager));

        _editManager = new FsmEditManager(_log);
        ActiveEditManagerForPatches = _editManager;
        ActiveInstanceForPatches = this;
        _debugModCompat = DebugModCompatFactory.TryCreate(_editManager, RescanLiveFsmsForDebugModLoad, Logger);
        _variableTracker = new FsmVariableTracker(fsmKey => _editManager.GetLiveInstances(fsmKey));
        _tabManager = new FsmTabManager();
        _graphOverlay = new FsmGraphOverlay(
            _log,
            _editManager,
            _tabManager,
            new BepInExHotkey(_toggleOverlayHotkey),
            new BepInExHotkey(toggleMinimalViewHotkey),
            graphColors,
            graphPerformance);

        //SceneName exception handling, important for custom scenes/errors
        string sceneName = FsmSceneNaming.GetSafeSceneName(() => SceneManager.GetActiveScene().name, _log);
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay.RefreshSnapshot(sceneName, fsms);
        _tabManager.RebindAfterRefresh(groups);

        BuildRightPanel();

        TryUnlockUiInput();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }
    private bool _uiInputUnlocked;

    private void TryUnlockUiInput()
    {
        if (_uiInputUnlocked)
        {
            return;
        }

        InputHandler? inputHandler = GameManager.instance?.inputHandler;
        if (inputHandler == null)
        {
            return;
        }

        inputHandler.StartUIInput();
        _uiInputUnlocked = true;
    }

    //Shows UI on first run, suppress with the API if needed
    internal void ShowFirstRunUiIfNeeded()
    {
        if (_firstRunComplete is not { Value: false } || _graphOverlay == null || _rightPanel == null || _uiCommon == null || _toggleOverlayHotkey == null)
        {
            return;
        }

        _graphOverlay.Show();
        _rightPanel.ShowStatus($"{_toggleOverlayHotkey.Value} to toggle UI", _uiCommon.AccentColor, durationSeconds: 12f);
        _firstRunComplete.Value = true;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;

        _debugModCompat?.Unhook();
        _debugModCompat = null;

        _editManager?.RevertAllForUnload();
        _editManager = null;
        ActiveEditManagerForPatches = null;
        ActiveInstanceForPatches = null;
        _variableTracker = null;
        _tabManager = null;

        _graphOverlay?.Shutdown();
        _graphOverlay = null;
        ForceCursorVisible = false;

        DestroyRightPanel();

        // UnpatchSelf removes every patch owned by this Harmony instance's Id, including
        // ForceCursorVisiblePatch's manually-applied prefix if it was still installed - no separate
        // UnpatchCursorOverride call needed here.
        _harmony?.UnpatchSelf();
        _harmony = null;
        _cursorPatchInstalled = false;

        _uiStateConfig = null;
    }

    private void Update()
    {
        _editManager?.PollPendingActivations();
        _debugModCompat?.PollPendingReload();

        if (!_uiInputUnlocked)
        {
            TryUnlockUiInput();
        }

        _graphOverlay?.Update();

        // Mirrors the graph overlay's own toggle-overlay hotkey state (see FsmGraphOverlay.Update)
        // onto ForceCursorVisiblePatch's static flag - Silksong's InputHandler recalculates and
        // (re-)applies Cursor.visible every frame based on its own pause/menu state (see
        // InputHandler.SetCursorVisible, patched below), so forcing Cursor.visible ourselves from
        // Update/LateUpdate/OnGUI would just race that per-frame write and flicker. Forcing the one
        // value InputHandler itself ends up writing has no second writer to race against.
        bool overlayVisible = _graphOverlay?.IsVisible ?? false;

        // Only patched in while the overlay is actually open - see _cursorPatchInstalled's own comment.
        // Compared against the previous frame's state so Patch/Unpatch is only ever called on an actual
        // visibility change, not every frame.
        if (overlayVisible != _cursorPatchInstalled)
        {
            if (overlayVisible)
            {
                PatchCursorOverride();
            }
            else
            {
                UnpatchCursorOverride();
            }

            _cursorPatchInstalled = overlayVisible;
        }

        ForceCursorVisible = overlayVisible;

        // The overlay's uGUI panels don't receive pointer events until the player has opened the pause
        // menu at least once: InControlInputModule.allowMouseInput starts false and stays false until
        // the game's own pause code first sets it, and while it's false the module suppresses all of its
        // own click/hover dispatch even though the underlying GraphicRaycaster resolves hits correctly
        // the whole time (the raycast lands on the right widget by name, but nothing ever fires). Forced
        // true here while the overlay is open rather than waiting for the pause menu to do it.
        if (overlayVisible && EventSystem.current?.currentInputModule is InControlInputModule inControlInputModule)
        {
            inControlInputModule.allowMouseInput = true;
        }

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
            bool rightPanelActive = overlayVisible && _graphOverlay!.SelectionUiVisible;
            _rightPanel.ActiveSelf = rightPanelActive;

            // Everything below only affects what the panel *shows*, so it's skipped wholesale whenever
            // the panel isn't actually up - refreshing action-field values re-reads the live FSM by
            // reflection (RefreshLiveValues) and the subtree walk/per-node Update all cost real work
            // every frame, none of which is observable while the overlay is off. Setting ActiveSelf
            // above is idempotent (see CanvasNode.ActiveSelf), so the panel still deactivates cleanly on
            // the frame the overlay closes; on the frame it reopens, Refresh/RefreshLiveValues and the
            // StructureVersion-gated CollectSubtree below all re-run and bring the rows current again.
            if (rightPanelActive)
            {
                FsmTabState? activeTab = _tabManager?.GetActive();
                FsmInfo? activeFsm = activeTab is { IsLive: true } ? _graphOverlay?.ResolveFsmInfo(activeTab.FsmKey) : null;
                _rightPanel.ActiveStatePanel.Refresh(activeFsm, activeTab?.SelectedStateName, activeTab?.FsmKey);

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
        }

        if (_monitorPanel != null)
        {
            // Deliberately NOT gated on SelectionUiVisible the way _rightPanel.ActiveSelf is above -
            // the monitor panel must survive the toggle-minimal-view hotkey (only locking/fading, see
            // Locked below) rather than disappearing with the rest of the selection UI.
            _monitorPanel.ActiveSelf = overlayVisible;

            // Same visibility gate as the right panel: RefreshRows polls every tracked variable by
            // reflection (see FsmVariableTracker.GetTracked) every frame, which is pure waste while the
            // monitor isn't drawn. RefreshRows self-heals on the first visible frame - it diffs each
            // row's value against the last displayed one and updates whatever changed while hidden.
            if (overlayVisible)
            {
                _monitorPanel.Locked = !_graphOverlay!.SelectionUiVisible;
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
    }

    private void OnGUI()
    {
        Rect? rightPanelScreenRect = null;
        if (_rightPanel is { ActiveInHierarchy: true })
        {
            rightPanelScreenRect = new Rect(_rightPanel.Position.x, _rightPanel.Position.y, _rightPanel.Size.x, _rightPanel.Size.y);
        }

        // Both panels are freely draggable/resizable now (see FsmRightPanel/FsmMonitorPanel's own
        // resize handles) - the overlay needs the monitor panel's own current rect too, alongside the
        // right panel's, so it can skip over wherever that panel actually is instead of assuming it
        // only ever sits within the right panel's own docked strip (see FsmGraphOverlay.OnGUI). The
        // panel reports this itself rather than it being rebuilt from Position/Size here: in minimal
        // view it shrinks to just the rows it's still showing (see FsmMonitorPanel.ScreenRect).
        Rect? monitorPanelScreenRect = null;
        if (_monitorPanel is { ActiveInHierarchy: true })
        {
            monitorPanelScreenRect = _monitorPanel.ScreenRect;
        }

        // The Open dropdown (Scene/Object/FSM picker) isn't clipped to the right panel's own rect and
        // can extend past it - see FsmRightPanel.OpenDropdownScreenRect - so it needs its own vignette
        // hole rather than relying on rightPanelScreenRect to cover it.
        Rect? openDropdownScreenRect = _rightPanel?.OpenDropdownScreenRect;

        _graphOverlay?.OnGUI(_tabManager?.GetActive(), rightPanelScreenRect, monitorPanelScreenRect, openDropdownScreenRect);
    }

    // Creates a backup Event System if needed
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
            _log!,
            _rightPanelLayout!,
            new BepInExConfigValue<bool>(_autoLoadConfig!),
            () => _graphOverlay?.Hide());

        _rightPanel.ActiveSelf = false;
        _rightPanel.Build(_canvasGameObject.transform);

        _monitorPanel = new FsmMonitorPanel(_uiCommon, _variableTracker!, _monitorPanelLayout!);
        _monitorPanel.ActiveSelf = false;
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

    //Debug savestates can mess with scene loading, rescan manually when savestates done
    internal void RescanLiveFsmsForDebugModLoad()
    {
        string sceneName = FsmSceneNaming.GetSafeSceneName(() => SceneManager.GetActiveScene().name, _log);
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay?.RefreshSnapshot(sceneName, fsms);
        _tabManager?.RebindAfterRefresh(groups);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        string loadedSceneName = FsmSceneNaming.GetSafeSceneName(() => scene.name, _log);
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(loadedSceneName, fsms);

        // Have to refresh on reload
        _graphOverlay?.RefreshSnapshot(FsmSceneNaming.GetSafeSceneName(() => SceneManager.GetActiveScene().name, _log), fsms);

        // Track dead tabs in case they come back
        _tabManager?.RebindAfterRefresh(groups);
    }

    //Find all edits in scene, track them, apply them
    private Dictionary<string, List<PlayMakerFSM>> ApplyPersistedEditsForScene(string sceneName, PlayMakerFSM[] components)
    {
        if (_editManager == null || _tabManager == null)
        {
            return new Dictionary<string, List<PlayMakerFSM>>();
        }

        Dictionary<string, List<PlayMakerFSM>> groups = FsmIdentity.DiscoverFsmGroups(components);
        _editManager.ReplaceLiveInstances(groups.ToDictionary(g => g.Key, g => g.Value.Select(c => c.Fsm).ToList()));

        if (_autoLoadConfig is not { Value: true })
        {
            return groups;
        }

        IEnumerable<string> openFsmKeysPresent = _tabManager.Tabs
            .Select(tab => tab.FsmKey)
            .Where(groups.ContainsKey);

        foreach (FsmEditSet editSet in FsmSaveDataStore.LoadLastChosenForScene(sceneName, openFsmKeysPresent))
        {
            _editManager.ApplyEditSet(editSet);
        }

        return groups;
    }

    // Reverts a live FSM to its original state
    public void ResetFsm(string sceneName, string fsmKey)
    {
        _editManager?.ResetFsm(fsmKey);
        FsmSaveDataStore.ClearAllSavesForFsm(sceneName, fsmKey);
    }
}

// Hook preprocess to apply edits to inactive objects
[HarmonyPatch(typeof(Fsm), "Preprocess", new System.Type[] { })]
internal static class FsmActivatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(Fsm __instance)
    {
        if (FsmMasterPlugin.ActiveEditManagerForPatches is not { } editManager)
        {
            return;
        }

        PlayMakerFSM? component = __instance.FsmComponent;
        if (component == null)
        {
            return;
        }

        string fsmKey = FsmIdentity.GetFsmKey(component);

        // Playmaker can hold a stale reference to an FSM
        editManager.ReconcileLiveInstance(fsmKey, __instance);

        if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
        {
            editManager.ApplyEditSet(editSet);
        }
    }
}

// Wait to fire first run UI so mods can override it
[HarmonyPatch(typeof(GameManager), nameof(GameManager.SetLoadedGameData), typeof(SaveGameData), typeof(int))]
internal static class GameFileLoadedPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (FsmMasterPlugin.ActiveInstanceForPatches is { } instance)
        {
            instance.ShowFirstRunUiIfNeeded();
        }
    }
}

internal static class ForceCursorVisiblePatch
{
    internal static void Prefix(ref bool value)
    {
        if (FsmMasterPlugin.ForceCursorVisible)
        {
            value = true;
        }
    }
}
