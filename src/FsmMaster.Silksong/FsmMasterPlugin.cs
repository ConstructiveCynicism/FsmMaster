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
[BepInDependency("org.silksong-modding.fsmutil")]
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

    // Second ConfigFile, separate from the inherited BaseUnityPlugin.Config, for settings that are
    // never meant to be hand-tuned via ConfigurationManager - the panel layout floats (auto-saved by
    // dragging/resizing) and the first-run flag below. ConfigurationManager only ever discovers a
    // plugin's settings through its standard Config property (confirmed against
    // agent-context/Silksong.DebugMod-main/DebugMod.cs - Settings.InitMenu binds every
    // ConfigurationManager-tunable entry to the plugin's own inherited Config, while its much larger
    // settings blob - including its own first-run flag and saved panel positions - lives in a plain
    // JSON file under a persistentDataPath-rooted ModBaseDirectory instead, entirely outside
    // ConfigFile/ConfigurationManager). Anything a player might reasonably want to tweak from
    // ConfigurationManager (hotkeys, graph colors, performance, auto-load) stays on the inherited
    // Config for exactly that reason; only the auto-managed state below moves here, next to the FSM
    // edit presets it's the UI for.
    private ConfigFile? _uiStateConfig;

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

    // Mirrors _editManager - Harmony patches are static and can't reach this instance's own fields, so
    // this is the one static handle FsmActivatedPatch (bottom of this file) needs to look up and reapply
    // any pending edit set once a previously-inactive Fsm finally finishes its own Preprocess(). Set/cleared
    // symmetrically alongside _editManager in Awake/OnDestroy per this project's hot-reload contract.
    internal static FsmEditManager? ActiveEditManagerForPatches { get; private set; }

    // Read by GameFileLoadedPatch (bottom of this file) - Harmony patches are static and can't reach
    // this instance's own fields, so this is the one static handle it needs to fire the first-run
    // hotkey hint once a save file actually loads. Set/cleared symmetrically alongside
    // ActiveEditManagerForPatches in Awake/OnDestroy per this project's hot-reload contract.
    internal static FsmMasterPlugin? ActiveInstanceForPatches { get; private set; }

    // Read by ForceCursorVisiblePatch (bottom of this file) - Harmony patches are static, so this is
    // the one static handle it needs to check whether the overlay currently wants the cursor forced
    // visible. Set every frame in Update from the graph overlay's own IsVisible, and reset false in
    // OnDestroy alongside everything else torn down there, per this project's hot-reload contract.
    internal static bool ForceCursorVisible { get; private set; }

    // Public (not internal, unlike the handles above) so other mods with a BepInDependency on this
    // plugin can read or override whether the first-run hotkey hint has already fired - e.g. to
    // suppress it for a custom onboarding flow, or force it to show again. Backed directly by the
    // persisted "First Run Complete" config entry (bound in Awake) so a write here sticks across
    // scene loads and future sessions the same way the player's own toggle would. Returns false and
    // no-ops on set if read/written before this plugin's Awake has run.
    public static bool FirstRunComplete
    {
        get
        {
            // Pattern-matched rather than `?.` - ActiveInstanceForPatches is a UnityEngine.Object
            // subtype, and null-propagation through those bypasses Unity's overloaded == null check
            // for destroyed-but-not-yet-collected instances (see GameFileLoadedPatch below for the
            // same convention).
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

    // ForceCursorVisiblePatch is patched/unpatched on demand (see PatchCursorOverride/UnpatchCursorOverride)
    // rather than left installed for the plugin's whole lifetime like FsmActivatedPatch - unlike
    // Fsm.Preprocess (which only fires once per FSM activation), InputHandler.SetCursorVisible runs every
    // single frame regardless of whether the overlay is even open, so leaving this prefix permanently
    // installed means every frame pays a Harmony trampoline call just to check one bool. Tracks whether
    // the patch is currently installed so Update only calls Patch/Unpatch on an actual visibility change,
    // not every frame.
    private bool _cursorPatchInstalled;

    private static readonly MethodInfo SetCursorVisibleMethod = AccessTools.Method(typeof(InputHandler), nameof(InputHandler.SetCursorVisible));
    private static readonly HarmonyMethod ForceCursorVisiblePrefix = new(typeof(ForceCursorVisiblePatch), nameof(ForceCursorVisiblePatch.Prefix));

    private void PatchCursorOverride() => _harmony?.Patch(SetCursorVisibleMethod, prefix: ForceCursorVisiblePrefix);

    private void UnpatchCursorOverride() => _harmony?.Unpatch(SetCursorVisibleMethod, HarmonyPatchType.Prefix, _harmony.Id);

    internal FsmVariableTracker? VariableTracker => _variableTracker;

    private void Awake()
    {
        // BuildInfo.ReleaseName ("silk_<version>") is generated at build time and matches the filename
        // of the release this DLL came from, so a log excerpt is enough to identify the exact build.
        Logger.LogInfo($"Plugin {Name} {BuildInfo.ReleaseName} ({Id}) has loaded!");
        _log = new BepInExLog(Logger);

        // Only FsmActivatedPatch, GameFileLoadedPatch, and FocusOnHoverSuppressionPatch are
        // attribute-patched here - ForceCursorVisiblePatch is patched/unpatched on demand instead (see
        // PatchCursorOverride/UnpatchCursorOverride).
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

        // See _uiStateConfig's own doc comment for why this stays off the ConfigurationManager-visible
        // Config above.
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

        // Scene.name has been observed to return null (not "") when Awake fires before the initial
        // scene has finished loading - a timing race, not something confirmed to be platform-specific,
        // but reported so far only on Linux. Falls back to string.Empty so FsmSaveDataStore's path
        // building never NREs on it; OnSceneLoaded re-runs this same call with the real scene name once
        // it's actually available, so this fallback only ever matters for this one redundant call.
        string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay.RefreshSnapshot(sceneName, fsms);
        _tabManager.RebindAfterRefresh(groups);

        BuildRightPanel();

        TryUnlockUiInput();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    // The mouse doesn't work at all - not just against this mod's own UI, but anywhere in the game -
    // until the player has opened the pause menu once. InputHandler tracks this with its own
    // acceptingInput field and UIManager.inputModule.allowMouseInput, both false at boot and both only
    // ever flipped true by InputHandler.StartUIInput() - the exact method the game's own pause-menu
    // code calls. Called here once instead of waiting for that first pause. This is a one-time nudge
    // to shared state the game itself would set true anyway on first pause, not a value this plugin
    // owns, so it's never undone: the game's own StopUIInput/StartUIInput calls keep managing it
    // normally from here on regardless of this plugin's state.
    //
    // GameManager.instance can still be null this early (BepInEx's Awake fires before the game's own
    // managers are guaranteed to exist), so this can't just run once in Awake - retried from Update
    // every frame until it succeeds, since a skipped frame here has no other effect worth guarding
    // against.
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

    // First-run hint: opens the overlay and shows the toggle-hotkey hint the first time a save file
    // actually loads, rather than immediately at plugin Awake (which also runs at the main menu,
    // before any file is loaded). Called from GameFileLoadedPatch below. No-ops on every load after
    // the first, since firstRunComplete.Value is set true right away so it never repeats even if the
    // player loads several different files in one session.
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

    // Builds the right-side uGUI panel's Canvas/EventSystem/widget tree - see FsmRightPanel. Reuses
    // an EventSystem already present in the scene if one exists, since Silksong's own UI is expected to
    // have one running already. Only creates one as a defensive fallback, tracked via _ownsEventSystem
    // so OnDestroy never tears down a scene-owned EventSystem it doesn't own.
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
            _log!,
            _rightPanelLayout!,
            new BepInExConfigValue<bool>(_autoLoadConfig!),
            () => _graphOverlay?.Hide());

        // CanvasNode defaults ActiveSelf to true, so without this, Build() below instantiates both
        // panels' GameObjects already active - visible for one frame on a ScriptEngine hot reload
        // (Awake's AddComponent-driven Build runs immediately; Update, which is what actually syncs
        // ActiveSelf to the overlay's own default-off visibility, doesn't run until Unity's next
        // Update phase) even while the overlay is toggled off. Setting this before Build() means the
        // very first SetActive call already passes false, matching Update's own steady-state value.
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

    // Re-scans every live FSM in the currently active scene and refreshes tab/graph/edit state exactly
    // like Awake's own initial scan does - passed into DebugModCompat as the hook it runs before
    // reapplying a savestate's persisted edits. Needed because DebugMod's own savestate loader doesn't
    // reliably tear the scene down and reload it when the savestate's target room is the room already
    // active (RoomSpecific.cs in DebugMod has its own comment acknowledging this), so
    // SceneManager.sceneLoaded - and therefore OnSceneLoaded below - isn't guaranteed to fire for that
    // load. Keyed by SceneManager.GetActiveScene().name rather than a specific "just loaded" scene
    // parameter, matching Awake, since this call has no such parameter available.
    internal void RescanLiveFsmsForDebugModLoad()
    {
        string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay?.RefreshSnapshot(sceneName, fsms);
        _tabManager?.RebindAfterRefresh(groups);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(scene.name, fsms);

        // The overlay's own snapshot goes stale on every scene load - PlayMakerFSM instances from the
        // previous scene are destroyed and need re-collecting. Uses SceneManager.GetActiveScene().name
        // (matching Awake), not the loaded `scene` parameter, since that's what RefreshSnapshot's own
        // FsmDataCollector.CollectSnapshot call has always keyed its snapshot by.
        _graphOverlay?.RefreshSnapshot(SceneManager.GetActiveScene().name ?? string.Empty, fsms);

        // Re-resolves every open tab's live PlayMakerFSM by FsmKey against the groups just computed
        // above - a tab whose FSM isn't present here gets marked not-live (kept open as a placeholder)
        // rather than closed, per FsmTabManager.RebindAfterRefresh's contract.
        _tabManager?.RebindAfterRefresh(groups);
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
    // Returns the FsmKey groups it computed, so the caller can feed the same grouping straight into
    // FsmTabManager.RebindAfterRefresh instead of that method re-deriving keys from scratch with a
    // second regex-driven walk over every live FSM in the room.
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

    // Reverts a live FSM to its pristine values and strips every named save (plus the remembered last-
    // chosen one) it has persisted, so this scene coming up again later leaves it unmodified too.
    public void ResetFsm(string sceneName, string fsmKey)
    {
        _editManager?.ResetFsm(fsmKey);
        FsmSaveDataStore.ClearAllSavesForFsm(sceneName, fsmKey);
    }
}

// Closes the lifecycle gap flagged in FsmEditManager.InstallSequencer: an edit made against an FSM whose
// owning GameObject was inactive at the time (so PlayMaker never ran Preprocess() on it, leaving every
// FsmStateAction.State backref null) gets recorded but not physically installed on that instance, and
// nothing previously caught it activating later within the same scene - only a fresh scene load
// (FsmMasterPlugin.ApplyPersistedEditsForScene) re-ran ApplyEditSet at all.
//
// Targets Fsm's own private, parameterless Preprocess() rather than PlayMakerFSM.Awake(): every path that
// initializes an Fsm for the first time - PlayMakerFSM.Awake() -> Init(), or a direct
// Preprocess(MonoBehaviour) call some other game code might make - funnels through this exact method,
// which is also the one that actually sets Preprocessed = true and calls action.Init(state) for every
// action, so a Postfix here is guaranteed to run exactly once per Fsm, right after its actions' State
// backrefs become safe to touch. Fsm.Owner (hence FsmComponent) is set before either caller reaches this
// method, so __instance.FsmComponent is never null here.
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

        // Must run before GetActiveEditSet/ApplyEditSet below - see ReconcileLiveInstance's own comment
        // for why _liveInstances can otherwise still hold a stale, pre-activation Fsm reference for this
        // exact component at this point.
        editManager.ReconcileLiveInstance(fsmKey, __instance);

        if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
        {
            editManager.ApplyEditSet(editSet);
        }
    }
}

// Fires the first-run hotkey hint once a save file actually loads - continuing an existing save or
// starting a new one both funnel through this same call - rather than at plugin Awake, which also
// runs at the main menu before any file is loaded and would otherwise pop the overlay open over the
// title screen. [HarmonyPatch] here is attribute-driven like FsmActivatedPatch (registered explicitly
// in Awake via _harmony.PatchAll(typeof(GameFileLoadedPatch)) since CreateAndPatchAll above only scans
// FsmActivatedPatch), not on-demand like ForceCursorVisiblePatch below, since this only ever needs to
// be installed/removed once per plugin lifetime.
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

// Forces the cursor visible while the graph overlay is open, without fighting Silksong's own
// per-frame cursor handling. InputHandler.SetCursorVisible is the single call site the game's own
// InputHandler uses to (re-)apply Cursor.visible every frame based on its own pause/menu state, so
// this prefixes it to override the incoming value. Reacting to the cursor from this plugin's own
// Update/LateUpdate/OnGUI instead - writing Cursor.visible a second time after InputHandler already
// wrote it - raced that per-frame write and produced visible flicker, worse the later in the frame
// the second write happened. Forcing the one value InputHandler itself ends up writing removes the
// second writer entirely.
// Patched/unpatched on demand by FsmMasterPlugin.PatchCursorOverride/UnpatchCursorOverride rather than
// via attribute-driven PatchAll - see FsmMasterPlugin._cursorPatchInstalled's own comment for why. No
// [HarmonyPatch] target attribute needed here since the patch is applied manually against
// SetCursorVisibleMethod instead.
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
