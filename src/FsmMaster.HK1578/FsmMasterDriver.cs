// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FsmMaster;

// Modding.Mod (this loader generation's base) isn't a MonoBehaviour and gets exactly one Initialize()
// call with no per-frame callback of its own - this component, created from FsmMasterMod.Initialize on
// a DontDestroyOnLoad GameObject, is what actually hosts Update()/OnGUI() for the rest of the mod's
// lifetime. This is also the composition root: it owns the edit manager, tab manager, graph overlay,
// and the uGUI right/monitor panels, and drives their per-frame Update/OnGUI wiring - same role as the
// hk1221 loader's own FsmMasterDriver, ported to MonoMod On.* hooks instead of Harmony patches.
internal class FsmMasterDriver : MonoBehaviour
{
    // Read by FsmActivationPatches.OnFsmPreprocess (a static hook, which can't reach an instance's own
    // fields) to reconcile/reapply edits the moment an FSM activates. Set in Awake - there's no
    // OnDestroy to clear it from, matching every other static handle on this loader (hooks installed
    // once and never removed; this loader generation has no hot-reload).
    internal static FsmMasterDriver? Instance { get; private set; }

    internal FsmEditManager? EditManager { get; private set; }

    private IFsmLog? _log;
    private FsmVariableTracker? _variableTracker;
    private FsmTabManager? _tabManager;
    private FsmGraphOverlay? _graphOverlay;
    private FsmMasterGlobalSettings? _settings;

    private UICommon? _uiCommon;
    private GameObject? _canvasGameObject;
    private FsmRightPanel? _rightPanel;
    private FsmMonitorPanel? _monitorPanel;

    // Reused every frame by CanvasNode.CollectSubtree calls below, instead of a fresh List<CanvasNode>
    // every single tick regardless of whether either panel is even visible.
    private readonly List<CanvasNode> _rightPanelSubtreeBuffer = new();
    private readonly List<CanvasNode> _monitorPanelSubtreeBuffer = new();

    // Last CanvasPanel.StructureVersion each buffer above was actually rebuilt from - CollectSubtree
    // itself is skipped entirely whenever neither tree's shape has changed since the previous frame.
    private int _rightPanelSubtreeVersion = -1;
    private int _monitorPanelSubtreeVersion = -1;

    // OnFsmEnterState is installed/uninstalled on demand rather than for the process's whole lifetime
    // like every other hook on this loader - mirrors hk1221's own FsmStateEnteredPatch on-demand
    // pattern (a confirmed-in-testing game-breaking issue there from leaving a Fsm.EnterState hook
    // permanently active while nothing consumed it - the same caution applies here even though this
    // loader generation hasn't independently reproduced that failure, since the hot path itself -
    // firing on every state transition of every live FSM - is identical). Tracks whether it's currently
    // installed so += / -= is only ever called on an actual visibility change, not every frame.
    private bool _stateTrackingHookInstalled;

    private static void PatchStateTracking() => On.HutongGames.PlayMaker.Fsm.EnterState += FsmActivationPatches.OnFsmEnterState;

    private static void UnpatchStateTracking() => On.HutongGames.PlayMaker.Fsm.EnterState -= FsmActivationPatches.OnFsmEnterState;

    private void Awake()
    {
        Instance = this;

        _settings = FsmMasterMod.Settings;
        _log = new ModLog(FsmMasterMod.Instance!.Log, FsmMasterMod.Instance!.LogWarn, FsmMasterMod.Instance!.LogError);

        EditManager = new FsmEditManager(_log);
        _variableTracker = new FsmVariableTracker(fsmKey => EditManager.GetLiveInstances(fsmKey));
        _tabManager = new FsmTabManager();
        _graphOverlay = new FsmGraphOverlay(
            _log,
            EditManager,
            _tabManager,
            new KeyCodeHotkey(() => _settings.ToggleOverlayHotkey),
            new KeyCodeHotkey(() => _settings.ToggleMinimalViewHotkey),
            new FsmMasterGraphColorConfig(_settings),
            new FsmMasterGraphPerformanceConfig(_settings));

        // Fully qualified rather than relying on `using UnityEngine.SceneManagement;` - Assembly-CSharp.dll
        // declares its own global-namespace SceneManager class (a Team Cherry gameplay type unrelated to
        // Unity's), which silently wins over the using-imported one for an unqualified reference (a
        // type in the global namespace always beats one brought in by `using`, with no ambiguity error).
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;

        PlayMakerFSM[] fsms = FindAllPlayMakerFsms();

        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay.RefreshSnapshot(sceneName, fsms);
        _tabManager.RebindAfterRefresh(groups);

        BuildRightPanel();

        // Same InControl input-module gap the hk1221 loader hit on this same game engine generation -
        // the overlay's uGUI panels aren't clickable until the player pauses once, since
        // InControlInputModule evidently doesn't dispatch pointer events while
        // InControl.InputManager.Enabled is false and nothing else sets it true before that. Forced on
        // at startup rather than waiting for the game's own pause-menu code to do it.
        InputManager.Enabled = true;

        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Update()
    {
        EditManager?.PollPendingActivations();
        DebugModSavestateCompat.PollLoadingTransition();

        _graphOverlay?.Update();

        // Mirrors the graph overlay's own toggle-overlay hotkey state onto ForceCursorVisiblePatch's
        // static flag - see that patch's own comment for why this loader can leave the hook permanently
        // installed rather than patching it in/out on demand.
        ForceCursorVisiblePatch.ForceVisible = _graphOverlay?.IsVisible ?? false;

        // hk1221's own FsmMasterDriver.Update fix (forcing InControlInputModule.allowMouseInput) doesn't
        // apply here: decompiling this loader's Assembly-CSharp showed UIManager.inputModule is actually
        // typed InControl.HollowKnightInputModule, which derives from UnityEngine.EventSystems'
        // StandaloneInputModule directly rather than from InControlInputModule at all - the
        // "EventSystem.current.currentInputModule is InControlInputModule" cast this used to do could
        // never succeed, so the block never ran. InputHandler.StopUIInput()/StartUIInput() (confirmed by
        // decompiling every call site in Assembly-CSharp) toggle this same allowMouseInput field on and
        // off constantly during ordinary gameplay - dialogue prompts, tutorate popups, the pause menu
        // itself - so a one-time unlock at startup (Silksong's own approach, which only ever needed to
        // flip the equivalent field once because nothing else in Silksong's InputHandler ever clears it
        // again) isn't enough here either. Forced true every frame the overlay is open instead, mirroring
        // hk1221's per-frame approach but against the field the game's UIManager actually exposes.
        if ((_graphOverlay?.IsVisible ?? false) && UIManager.instance?.inputModule != null)
        {
            UIManager.instance.inputModule.allowMouseInput = true;
        }

        // Only hooked in while the overlay is actually open - see _stateTrackingHookInstalled's own
        // comment. Compared against the previous frame's state so += / -= is only ever called on an
        // actual visibility change, not every frame.
        bool overlayVisible = _graphOverlay?.IsVisible ?? false;
        if (overlayVisible != _stateTrackingHookInstalled)
        {
            if (overlayVisible)
            {
                PatchStateTracking();
            }
            else
            {
                UnpatchStateTracking();
            }

            _stateTrackingHookInstalled = overlayVisible;
        }

        if (_rightPanel != null)
        {
            // Setting ActiveSelf from here (not the panel's own OnUpdate) is required - a node's own
            // OnUpdate only runs while it's already active, so it can never turn itself back on (see
            // CanvasScrollbar.ShouldBeVisible for the same reasoning applied to the tab strip's
            // scrollbar).
            bool rightPanelActive = overlayVisible && _graphOverlay!.SelectionUiVisible;
            _rightPanel.ActiveSelf = rightPanelActive;

            // Everything below only affects what the panel *shows*, so it's skipped wholesale whenever
            // the panel isn't up - RefreshLiveValues re-reads the live FSM by reflection and the subtree
            // walk/per-node Update all cost real work every frame, none of it observable while the
            // overlay is off. Setting ActiveSelf above is idempotent (see CanvasNode.ActiveSelf), so the
            // panel still deactivates cleanly on the frame the overlay closes; on the frame it reopens,
            // Refresh/RefreshLiveValues and the StructureVersion-gated CollectSubtree below re-run and
            // bring the rows current again.
            if (rightPanelActive)
            {
                FsmTabState? activeTab = _tabManager?.GetActive();
                FsmInfo? activeFsm = activeTab is { IsLive: true } ? _graphOverlay?.ResolveFsmInfo(activeTab.FsmKey) : null;
                _rightPanel.ActiveStatePanel.Refresh(activeFsm, activeTab?.SelectedStateName, activeTab?.FsmKey);

                // One-shot request from a transition click in the graph overlay - consumed and cleared here
                // rather than left on the tab, so it doesn't re-fire on every subsequent frame this tab
                // stays active.
                if (activeTab?.PendingScrollActionIndex is { } pendingScrollActionIndex)
                {
                    _rightPanel.ActiveStatePanel.ScrollToAction(pendingScrollActionIndex);
                    activeTab.PendingScrollActionIndex = null;
                }

                // Refresh's own fsm/state/subtab cache only gates rebuilding row *structure* - it must not
                // gate the *values* those rows display, or the Load/Reset buttons (which mutate the live
                // FSM without changing which tab/state is selected) would appear to do nothing until the
                // user switches away and back.
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
            // reflection (see FsmVariableTracker.GetTracked) every frame, pure waste while the monitor
            // isn't drawn. RefreshRows self-heals on the first visible frame - it diffs each row's value
            // against the last displayed one and updates whatever changed while hidden.
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

        // Both panels are freely draggable/resizable - the overlay needs the monitor panel's own
        // current rect too, alongside the right panel's, so it can skip over wherever that panel
        // actually is instead of assuming it only ever sits within the right panel's own docked strip.
        // The panel reports this itself rather than it being rebuilt from Position/Size here: in minimal
        // view it shrinks to just the rows it's still showing (see FsmMonitorPanel.ScreenRect).
        Rect? monitorPanelScreenRect = null;
        if (_monitorPanel is { ActiveInHierarchy: true })
        {
            monitorPanelScreenRect = _monitorPanel.ScreenRect;
        }

        // The Open dropdown (Scene/Object/FSM picker) isn't clipped to the right panel's own rect and
        // can extend past it - it needs its own vignette hole rather than relying on
        // rightPanelScreenRect to cover it.
        Rect? openDropdownScreenRect = _rightPanel?.OpenDropdownScreenRect;

        _graphOverlay?.OnGUI(_tabManager?.GetActive(), rightPanelScreenRect, monitorPanelScreenRect, openDropdownScreenRect);
    }

    // Builds the right-side uGUI panel's Canvas/EventSystem/widget tree. Reuses an EventSystem already
    // present in the scene if one exists - HK1's own InControlInputModule (see CanvasTextField.cs) is
    // already running as a persistent, scene-independent component, so this fallback creation path is
    // expected to never actually trigger in practice.
    private void BuildRightPanel()
    {
        if (FindObjectOfType<EventSystem>() == null)
        {
            var eventSystemObject = new GameObject("FsmMasterEventSystem");
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<StandaloneInputModule>();
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
            EditManager!,
            _variableTracker!,
            () => _graphOverlay?.CurrentSnapshot,
            _log!,
            new FsmMasterPanelLayoutConfig(
                () => _settings!.RightPanelPosition, v => _settings!.RightPanelPosition = v,
                () => _settings!.RightPanelSize, v => _settings!.RightPanelSize = v),
            new FieldConfigValue<bool>(() => _settings!.AutoLoadLastConfiguration, v => _settings!.AutoLoadLastConfiguration = v),
            () => _graphOverlay?.Hide());

        // CanvasNode defaults ActiveSelf to true, so without this, Build() below instantiates both
        // panels' GameObjects already active - visible for one frame even while the overlay is toggled
        // off, until Update (which is what actually syncs ActiveSelf to the overlay's own default-off
        // visibility) next runs. Setting this before Build() means the very first SetActive call
        // already passes false, matching Update's own steady-state value.
        _rightPanel.ActiveSelf = false;
        _rightPanel.Build(_canvasGameObject.transform);

        _monitorPanel = new FsmMonitorPanel(
            _uiCommon,
            _variableTracker!,
            new FsmMasterPanelLayoutConfig(
                () => _settings!.MonitorPanelPosition, v => _settings!.MonitorPanelPosition = v,
                () => _settings!.MonitorPanelSize, v => _settings!.MonitorPanelSize = v));
        _monitorPanel.ActiveSelf = false;
        _monitorPanel.Build(_canvasGameObject.transform);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        PlayMakerFSM[] fsms = FindAllPlayMakerFsms();
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(scene.name, fsms);

        // The overlay's own snapshot goes stale on every scene load - PlayMakerFSM instances from the
        // previous scene are destroyed and need re-collecting. Uses UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
        // (matching Awake), not the loaded `scene` parameter, since that's what RefreshSnapshot's own
        // FsmDataCollector.CollectSnapshot call has always keyed its snapshot by.
        _graphOverlay?.RefreshSnapshot(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty, fsms);

        // Re-resolves every open tab's live PlayMakerFSM by FsmKey against the groups just computed
        // above - a tab whose FSM isn't present here gets marked not-live (kept open as a placeholder)
        // rather than closed, per FsmTabManager.RebindAfterRefresh's contract.
        _tabManager?.RebindAfterRefresh(groups);
    }

    // FindObjectsOfType<T>() has no includeInactive overload on this Unity version, same as hk1221 -
    // an FSM whose owning GameObject starts out inactive (common for enemies/objects that only enable
    // themselves once a trigger/cutscene/state check fires) would otherwise never appear in the graph
    // overlay at all. Resources.FindObjectsOfTypeAll<T>() includes inactive objects, but also matches
    // objects that were never instantiated into any loaded scene at all - a prefab template sitting in
    // memory, for instance.
    //
    // gameObject.scene.IsValid() alone false-negatives on a large class of genuinely live, persistent
    // objects on this engine generation (confirmed on hk1221's identical Unity version via
    // FsmActivatedPatch diagnostic logging - charm-slot and Hunter's Journal UI template FSMs report an
    // invalid scene despite having already run Awake()/Preprocess() for real). Fsm.Preprocessed is kept
    // alongside scene.IsValid() (not replacing it) for the same reason: a freshly-instantiated-but-not-
    // yet-Preprocessed object still needs to match via the scene check.
    // Not a LINQ .Where(...) - Enumerable.Where's Func<TSource,bool> predicate is one of the closed
    // generic instantiations missing from hk1432's stripped mscorlib.dll (see ConfigGetter<T>'s own
    // comment in Config/FieldConfigValue.cs for the full story), and unlike FieldConfigValue's own
    // delegates this one can't be swapped for an FsmMaster-owned type - Where's signature is fixed by
    // System.Core. A plain filtering loop sidesteps needing that instantiation at all.
    private static PlayMakerFSM[] FindAllPlayMakerFsms()
    {
        PlayMakerFSM[] all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
        var matching = new List<PlayMakerFSM>(all.Length);
        foreach (PlayMakerFSM fsm in all)
        {
            if (fsm.gameObject.scene.IsValid() || fsm.Fsm.Preprocessed)
            {
                matching.Add(fsm);
            }
        }

        return matching.ToArray();
    }

    // Called from DebugModSavestateCompat.PollLoadingTransition once the DebugMod savestate load it's
    // watching finishes, with exactly the edit sets that savestate captured (null/empty if it never
    // saved any FsmMaster data at all). Rescans live FSMs the same way OnSceneLoaded does - a same-room
    // savestate load doesn't necessarily destroy and recreate every FSM's owning GameObject, so
    // FsmActivationPatches.OnFsmPreprocess (which only fires from a fresh Fsm.Preprocess()) can't be
    // relied on alone to reapply anything onto a live instance that survived the load unchanged.
    //
    // A savestate load fully replaces this scene's FSM edits with exactly what it captured, rather than
    // only ever adding to whatever's currently active: every currently-edited key snapshotEditSets doesn't
    // mention gets reset to pristine (an edit made after the save, and not part of it, shouldn't survive
    // loading a point in time before that edit existed), and every key the snapshot does mention gets
    // reset then reapplied fresh, so an edit made between the save and this load can't linger stacked on
    // top of the restored one.
    internal void ApplySavestateSnapshot(List<FsmEditSet>? snapshotEditSets)
    {
        if (EditManager == null || _tabManager == null || _graphOverlay == null)
        {
            return;
        }

        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
        PlayMakerFSM[] fsms = FindAllPlayMakerFsms();
        Dictionary<string, List<PlayMakerFSM>> groups = ApplyPersistedEditsForScene(sceneName, fsms);
        _graphOverlay.RefreshSnapshot(sceneName, fsms);
        _tabManager.RebindAfterRefresh(groups);

        var snapshotKeys = new HashSet<string>();
        if (snapshotEditSets != null)
        {
            foreach (FsmEditSet editSet in snapshotEditSets)
            {
                snapshotKeys.Add(editSet.FsmKey);
            }
        }

        foreach (string fsmKey in new List<string>(EditManager.GetEditedFsmKeys()))
        {
            if (!snapshotKeys.Contains(fsmKey))
            {
                EditManager.ResetFsm(fsmKey);
            }
        }

        if (snapshotEditSets != null)
        {
            foreach (FsmEditSet editSet in snapshotEditSets)
            {
                EditManager.ResetFsm(editSet.FsmKey);
                EditManager.ApplyEditSet(editSet);
            }
        }
    }

    // Groups the freshly discovered live FSMs by FsmKey, registers them with the edit manager, and -
    // while the Auto button is on - reapplies whichever named save was last chosen for each key that
    // has a graph tab currently open and is actually present in this scan. Restricted to open tabs
    // rather than every FSM the scan discovers: silently reapplying a saved preset onto an FSM the
    // player has never looked at would be surprising. Returns the FsmKey groups it computed, so the
    // caller can feed the same grouping straight into FsmTabManager.RebindAfterRefresh instead of that
    // method re-deriving keys from scratch with a second regex-driven walk over every live FSM.
    private Dictionary<string, List<PlayMakerFSM>> ApplyPersistedEditsForScene(string sceneName, PlayMakerFSM[] components)
    {
        if (EditManager == null || _tabManager == null)
        {
            return new Dictionary<string, List<PlayMakerFSM>>();
        }

        Dictionary<string, List<PlayMakerFSM>> groups = FsmIdentity.DiscoverFsmGroups(components);

        var instancesByKey = new Dictionary<string, List<Fsm>>();
        foreach (KeyValuePair<string, List<PlayMakerFSM>> group in groups)
        {
            var fsms = new List<Fsm>(group.Value.Count);
            foreach (PlayMakerFSM component in group.Value)
            {
                fsms.Add(component.Fsm);
            }

            instancesByKey[group.Key] = fsms;
        }
        EditManager.ReplaceLiveInstances(instancesByKey);

        if (!_settings!.AutoLoadLastConfiguration)
        {
            return groups;
        }

        var openFsmKeysPresent = new List<string>();
        foreach (FsmTabState tab in _tabManager.Tabs)
        {
            if (groups.ContainsKey(tab.FsmKey))
            {
                openFsmKeysPresent.Add(tab.FsmKey);
            }
        }

        foreach (FsmEditSet editSet in FsmSaveDataStore.LoadLastChosenForScene(sceneName, openFsmKeysPresent))
        {
            EditManager.ApplyEditSet(editSet);
        }

        return groups;
    }
}
