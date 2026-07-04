using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Composition root for FsmMaster's right-side uGUI panel - anchored top-right, matching where
// Silksong.DebugMod's own MainPanel sits (agent-context/Silksong.DebugMod-main/UI/MainPanel.cs).
// Built up incrementally across the UI overhaul's build order: the title bar (drag-to-reposition),
// the Open/Save/Load/Undo/Reset/Graph button row, the Open button's Scene/Object/FSM dropdown, the
// FSM tab strip, the Actions/Events/Variables active-state panel, and a resize handle in the bottom-
// left corner (bottom-left, not bottom-right, since this panel is docked flush against the right
// edge of the screen - see OnResizeDragged) - drag/resize handling mirrors FsmMonitorPanel's own
// (title-bar drag surface, corner CanvasResizeHandle), the one difference being this panel has no
// separate "locked" state to gate on.
internal sealed class FsmRightPanel : CanvasPanel
{
    private const int PanelWidth = 340;
    private const int PanelHeight = 500;
    private const int ScreenMargin = 10;
    private const float TitleBarHeightDesign = 22f;
    private const float ButtonRowHeight = 28f;
    private const float ButtonGap = 4f;
    private const float SectionGap = 8f;
    private const float ResizeHandleSizeDesign = 14f;
    private const int MinPanelWidth = 260;
    private const int MinPanelHeight = 320;
    private const float AutoButtonWidthDesign = 50f;

    private static float TitleBarHeight => UICommon.ScaleHeight(TitleBarHeightDesign);
    private static float ResizeHandleSize => UICommon.ScaleWidth(ResizeHandleSizeDesign);
    private static float MinWidth => UICommon.ScaleWidth(MinPanelWidth);
    private static float MinHeight => UICommon.ScaleHeight(MinPanelHeight);
    private static float AutoButtonWidth => UICommon.ScaleWidth(AutoButtonWidthDesign);

    // Topmost y-coordinate any content below the title bar starts from - the button row's own former
    // literal `10f` margin, now offset underneath the title bar rather than the panel's own top edge.
    private static float ContentTop => TitleBarHeight + 10f;

    public FsmTabStripPanel TabStrip { get; }
    public FsmActiveStatePanel ActiveStatePanel { get; }

    // The Open dropdown is a child of this panel but isn't clipped to its parent's rect - it grows
    // taller/wider than the panel's own Size as scenes/objects/FSMs are listed (see
    // FsmOpenDropdown.RebuildRows), and the panel itself can be resized down to MinWidth/MinHeight,
    // both of which routinely leave part of the dropdown sticking out past the panel's base rect.
    // Exposed here so FsmMasterPlugin.OnGUI can pass it to the graph overlay's vignette as its own
    // hole, alongside the panel's base rect - see FsmGraphOverlay.DrawVignette, which otherwise has
    // no way to know the dropdown extends past the rect it's already cutting a hole for.
    public Rect? OpenDropdownScreenRect => _openDropdown.ActiveInHierarchy
        ? new Rect(_openDropdown.Position.x, _openDropdown.Position.y, _openDropdown.Size.x, _openDropdown.Size.y)
        : null;

    private readonly CanvasImage _background;
    private readonly CanvasButton _dragSurface;
    private readonly CanvasText _titleText;
    private readonly CanvasButton _autoButton;
    private readonly CanvasButton _openButton;
    private readonly CanvasButton _saveButton;
    private readonly CanvasButton _loadButton;
    private readonly CanvasButton _undoButton;
    private readonly CanvasButton _resetButton;
    private readonly CanvasButton _graphButton;
    private readonly FsmOpenDropdown _openDropdown;
    private readonly FsmSaveDialog _saveDialog;
    private readonly FsmLoadDialog _loadDialog;
    private readonly CanvasResizeHandle _resizeHandle;
    private readonly FsmPanelLayoutConfig _layout;

    private int _lastScreenWidth = -1;
    private int _lastScreenHeight = -1;

    public FsmRightPanel(UICommon ui, FsmTabManager tabManager, FsmEditManager editManager, FsmVariableTracker tracker, Func<FsmSnapshot?> getSnapshot, Func<bool> getGraphVisible, Action<bool> setGraphVisible, ManualLogSource logger, FsmPanelLayoutConfig layout, ConfigEntry<bool> autoLoadConfig)
        : base("FsmRightPanel")
    {
        _layout = layout;
        Reposition();

        _background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        _background.AddBorder(ui.PanelBorder);

        _dragSurface = Add(new CanvasButton("DragSurface", ui) { Tint = Color.clear });
        _dragSurface.RemoveBorder();

        _titleText = Add(new CanvasText("Title", ui) { Text = "FsmMaster" });
        _titleText.Font = ui.HeaderFont;
        _titleText.FontStyle = FontStyle.Bold;
        _titleText.Alignment = TextAnchor.MiddleLeft;

        // Global toggle, not tied to any one tab's FSM - lives in the title bar rather than the
        // per-tab action row below (Open/Save/Load/Undo/Reset/Graph all act on whichever tab is
        // active), so it stays visually separate from those. Default-on per autoLoadConfig's own
        // ConfigEntry default (see FsmMasterPlugin.Awake); label stays "Auto" regardless of state,
        // with Toggled's tint (via CanvasButton.Toggled) as the on/off indicator.
        _autoButton = Add(new CanvasButton("AutoButton", ui));
        _autoButton.Text.Text = "Auto";
        _autoButton.Toggled = autoLoadConfig.Value;
        _autoButton.OnClicked += () =>
        {
            autoLoadConfig.Value = !autoLoadConfig.Value;
            _autoButton.Toggled = autoLoadConfig.Value;
        };

        _openButton = Add(new CanvasButton("OpenButton", ui));
        _openButton.Text.Text = "Open";

        _saveButton = Add(new CanvasButton("SaveButton", ui));
        _saveButton.Text.Text = "Save";
        _saveButton.OnClicked += () => OpenSaveDialog(tabManager, logger);

        _loadButton = Add(new CanvasButton("LoadButton", ui));
        _loadButton.Text.Text = "Load";
        _loadButton.OnClicked += () => OpenLoadDialog(tabManager, logger);

        _undoButton = Add(new CanvasButton("UndoButton", ui));
        _undoButton.Text.Text = "Undo";
        _undoButton.OnClicked += () => UndoActiveTab(tabManager, editManager, logger);

        _resetButton = Add(new CanvasButton("ResetButton", ui));
        _resetButton.Text.Text = "Reset";
        _resetButton.OnClicked += () => ResetActiveTab(tabManager, editManager, logger);

        _graphButton = Add(new CanvasButton("GraphButton", ui));
        _graphButton.Toggled = getGraphVisible();
        _graphButton.Text.Text = _graphButton.Toggled ? "Hide" : "Show";
        _graphButton.OnClicked += () =>
        {
            bool next = !getGraphVisible();
            setGraphVisible(next);
            _graphButton.Toggled = next;
            _graphButton.Text.Text = next ? "Hide" : "Show";
        };

        LayoutButtonRow();

        TabStrip = Add(new FsmTabStripPanel(ui, tabManager));
        LayoutTabStrip();

        ActiveStatePanel = Add(new FsmActiveStatePanel(ui, editManager, tracker, logger));
        LayoutActiveStatePanel();

        // Added last so it renders/hit-tests on top of the tab strip and active-state panel below it
        // whenever it's shown (later-added siblings render in front - see CanvasButton's own hover
        // border for the same convention). FsmOpenDropdown.Show additionally calls SetAsLastSibling
        // on itself, so this stays true even if a future change adds more children to this panel after
        // construction.
        _openDropdown = Add(new FsmOpenDropdown(ui, tabManager, getSnapshot, _openButton));
        _openDropdown.LocalPosition = new Vector2(10f, ContentTop + ButtonRowHeight + 4f);
        _openButton.OnClicked += _openDropdown.Toggle;

        _saveDialog = Add(new FsmSaveDialog(ui, _saveButton));
        _saveDialog.OnConfirm += (sceneName, fsmKey, saveName) => ConfirmSave(editManager, logger, sceneName, fsmKey, saveName);

        _loadDialog = Add(new FsmLoadDialog(ui, _loadButton));
        _loadDialog.OnSelected += (sceneName, fsmKey, saveName) => ConfirmLoad(editManager, logger, sceneName, fsmKey, saveName);

        _resizeHandle = Add(new CanvasResizeHandle("ResizeHandle", ui));
        _resizeHandle.OnDragDelta += OnResizeDragged;
        _resizeHandle.OnDragEnd += OnResizeDragEnded;
        LayoutResizeHandle();

        OnUpdate += ReflowOnResolutionChange;
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        _dragSurface.AddEventTrigger(EventTriggerType.Drag, OnDragged);
        _dragSurface.AddEventTrigger(EventTriggerType.EndDrag, OnDragEnded);
    }

    private void OnDragged(PointerEventData eventData)
    {
        Vector2 next = new(LocalPosition.x + eventData.delta.x, LocalPosition.y - eventData.delta.y);
        next.x = Mathf.Clamp(next.x, 0f, Mathf.Max(0f, Screen.width - Size.x));
        next.y = Mathf.Clamp(next.y, 0f, Mathf.Max(0f, Screen.height - Size.y));
        LocalPosition = next;
    }

    // Persists once per drag gesture (not per-frame - see CanvasResizeHandle.OnDragEnd's own comment
    // for why) rather than every OnDragged delta, so dragging the panel doesn't hammer the config file
    // with a write on every mouse-move event.
    private void OnDragEnded(PointerEventData _)
    {
        _layout.Position.Value = LocalPosition;
    }

    // Bottom-LEFT corner drag, not bottom-right: this panel is docked flush against the right edge of
    // the screen (Reposition anchors LocalPosition.x to Screen.width - Size.x), so there's essentially
    // no room to grow rightward - a bottom-right handle would hit the screen edge almost immediately.
    // Width therefore grows/shrinks by keeping the RIGHT edge fixed and moving the LEFT edge instead
    // (dragging the handle left grows the panel, right shrinks it); height still grows downward, which
    // has plenty of room since the panel is only pinned to the top of the screen.
    private void OnResizeDragged(Vector2 delta)
    {
        float rightEdge = LocalPosition.x + Size.x;
        float newWidth = Mathf.Clamp(Size.x - delta.x, MinWidth, Mathf.Max(MinWidth, rightEdge));
        float newHeight = Mathf.Clamp(Size.y - delta.y, MinHeight, Mathf.Max(MinHeight, Screen.height - LocalPosition.y));

        LocalPosition = new Vector2(rightEdge - newWidth, LocalPosition.y);
        Size = new Vector2(newWidth, newHeight);

        LayoutButtonRow();
        LayoutTabStrip();
        LayoutActiveStatePanel();
        LayoutResizeHandle();
    }

    // Persists once per resize gesture, matching OnDragEnded above - both Size and LocalPosition, since
    // this resize's own right-edge-fixed math (see OnResizeDragged) moves LocalPosition.x too.
    private void OnResizeDragEnded()
    {
        _layout.Position.Value = LocalPosition;
        _layout.Size.Value = Size;
    }

    private void LayoutResizeHandle()
    {
        _resizeHandle.LocalPosition = new Vector2(0f, Size.y - ResizeHandleSize);
        _resizeHandle.Size = new Vector2(ResizeHandleSize, ResizeHandleSize);
    }

    // Pressing the toggle-overlay/toggle-minimal-view hotkeys (see FsmGraphOverlay.Update) can hide
    // this whole panel while a text field somewhere inside ActiveStatePanel is focused -
    // GameObject.SetActive(false) on an ancestor doesn't
    // reliably fire the focused InputField's own onEndEdit, so this is the explicit release path that
    // stops FsmMaster's own hotkeys and (best-effort) the player's input lock from getting stuck.
    protected override void OnUpdateActive()
    {
        if (!ActiveSelf)
        {
            CanvasTextField.ForceReleaseFocus();
        }

        base.OnUpdateActive();
    }

    // Both operate on whichever tab is currently active, no-op when nothing is open. Scene name comes
    // from the FSM's own scene (active.Component.gameObject.scene.name), not the globally-active
    // scene, matching how FsmSaveDataStore/FsmDrilldownHierarchy already key by gameObject.scene.name.
    // Neither one persists/loads anything itself - each just opens the corresponding popup
    // (FsmSaveDialog's text field, FsmLoadDialog's row list), which raises OnConfirm/OnSelected once
    // the user actually picks a name - see ConfirmSave/ConfirmLoad below.
    private void OpenSaveDialog(FsmTabManager tabManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Save: no active tab.");
            return;
        }

        string sceneName = active.Component.gameObject.scene.name;
        string defaultName = FsmSaveDataStore.GetLastChosenSaveName(sceneName, active.FsmKey) ?? "";
        _saveDialog.Show(sceneName, active.FsmKey, defaultName);
    }

    private void OpenLoadDialog(FsmTabManager tabManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Load: no active tab.");
            return;
        }

        string sceneName = active.Component.gameObject.scene.name;
        _loadDialog.Show(sceneName, active.FsmKey);
    }

    // Saves the active edit set for fsmKey under the user-chosen name and remembers it as the
    // configuration to auto-reapply for (sceneName, fsmKey) next time this scene loads (see
    // FsmSaveDataStore.SetLastChosenSaveName / FsmMasterPlugin.ApplyPersistedEditsForScene).
    private void ConfirmSave(FsmEditManager editManager, ManualLogSource logger, string sceneName, string fsmKey, string saveName)
    {
        FsmEditSet editSet = editManager.GetActiveEditSet(fsmKey) ?? new FsmEditSet { FsmKey = fsmKey };
        string json = FsmSaveDataStore.Save(sceneName, saveName, editSet);
        FsmSaveDataStore.SetLastChosenSaveName(sceneName, fsmKey, saveName);
        logger.LogInfo($"[FsmMaster] Saved '{saveName}' for '{fsmKey}' in scene '{sceneName}':\n{json}");
    }

    // Applies the chosen named save to the live FSM and remembers it as the last-chosen configuration
    // for (sceneName, fsmKey), same as ConfirmSave.
    private void ConfirmLoad(FsmEditManager editManager, ManualLogSource logger, string sceneName, string fsmKey, string saveName)
    {
        FsmEditSet? editSet = FsmSaveDataStore.Load(sceneName, fsmKey, saveName);
        if (editSet == null)
        {
            logger.LogInfo($"[FsmMaster] Load: '{saveName}' not found for '{fsmKey}' in scene '{sceneName}'.");
            return;
        }

        editManager.ApplyEditSet(editSet);
        FsmSaveDataStore.SetLastChosenSaveName(sceneName, fsmKey, saveName);
        logger.LogInfo($"[FsmMaster] Loaded '{saveName}' for '{fsmKey}' in scene '{sceneName}'.");
    }

    // Pops the active tab's most recent undoable edit, if any - a no-op (with a log line) when the
    // active FSM has no edit history, matching Save/Load's own "no active tab" no-op shape.
    private static void UndoActiveTab(FsmTabManager tabManager, FsmEditManager editManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Undo: no active tab.");
            return;
        }

        if (!editManager.HasUndo(active.FsmKey))
        {
            logger.LogInfo($"[FsmMaster] Undo: nothing to undo for '{active.FsmKey}'.");
            return;
        }

        editManager.Undo(active.FsmKey);
        logger.LogInfo($"[FsmMaster] Undid last edit for '{active.FsmKey}'.");
    }

    // Reverts every edit made this session on the active tab's FSM back to its pristine, as-loaded
    // values (FsmEditManager.ResetFsm) - distinct from Undo, which only steps back one edit at a time.
    private static void ResetActiveTab(FsmTabManager tabManager, FsmEditManager editManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Reset: no active tab.");
            return;
        }

        editManager.ResetFsm(active.FsmKey);
        logger.LogInfo($"[FsmMaster] Reset all edits for '{active.FsmKey}'.");
    }

    private void LayoutButtonRow()
    {
        // The background/border image is otherwise never touched after construction - IsBackground
        // only auto-copies the parent's Size once, at Build() time (see CanvasImage.Build) - so
        // without this, dragging the resize handle grows every *interior* widget below (which all key
        // off this panel's own Size already) while the background/border itself stays frozen at
        // whatever size it was built at, letting the now-wider content spill out past its own border.
        _background.Size = Size;

        _dragSurface.LocalPosition = Vector2.zero;
        _dragSurface.Size = new Vector2(Size.x, TitleBarHeight);

        _titleText.LocalPosition = new Vector2(6f, 0f);
        _titleText.Size = new Vector2(Mathf.Max(0f, Size.x - 12f - AutoButtonWidth - 4f), TitleBarHeight);

        _autoButton.LocalPosition = new Vector2(Size.x - AutoButtonWidth - 6f, 2f);
        _autoButton.Size = new Vector2(AutoButtonWidth, Mathf.Max(0f, TitleBarHeight - 4f));

        float rowY = ContentTop;
        float rowWidth = Size.x - 20f;
        float buttonWidth = (rowWidth - ButtonGap * 5f) / 6f;

        _openButton.LocalPosition = new Vector2(10f, rowY);
        _openButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _saveButton.LocalPosition = new Vector2(10f + buttonWidth + ButtonGap, rowY);
        _saveButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _loadButton.LocalPosition = new Vector2(10f + (buttonWidth + ButtonGap) * 2f, rowY);
        _loadButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _undoButton.LocalPosition = new Vector2(10f + (buttonWidth + ButtonGap) * 3f, rowY);
        _undoButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _resetButton.LocalPosition = new Vector2(10f + (buttonWidth + ButtonGap) * 4f, rowY);
        _resetButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _graphButton.LocalPosition = new Vector2(10f + (buttonWidth + ButtonGap) * 5f, rowY);
        _graphButton.Size = new Vector2(rowWidth - (buttonWidth + ButtonGap) * 5f, ButtonRowHeight);
    }

    private void LayoutTabStrip()
    {
        TabStrip.LocalPosition = new Vector2(10f, ContentTop + ButtonRowHeight + SectionGap);
        TabStrip.Size = new Vector2(Size.x - 20f, FsmTabStripPanel.TotalHeight);
    }

    private void LayoutActiveStatePanel()
    {
        float y = ContentTop + ButtonRowHeight + SectionGap + FsmTabStripPanel.TotalHeight + SectionGap;
        ActiveStatePanel.LocalPosition = new Vector2(10f, y);
        ActiveStatePanel.Size = new Vector2(Size.x - 20f, Mathf.Max(0f, Size.y - y - 10f));
    }

    // Size/LocalPosition are only ever computed from Screen.width/height at construction time -
    // re-checked every frame so a resolution/window-size change repositions the panel and cascades
    // through Size's own OnUpdateSize chain (down to ActiveStatePanel.Layout, etc.) instead of leaving
    // it anchored to the resolution that was active when the plugin loaded. A full canvas rebuild
    // (matching Silksong.DebugMod's debounced approach) is deferred to a later polish pass; this
    // panel has no baked, resolution-dependent textures, so a plain in-place recompute is sufficient.
    private void ReflowOnResolutionChange()
    {
        if (Screen.width == _lastScreenWidth && Screen.height == _lastScreenHeight)
        {
            return;
        }

        Reposition();
        LayoutButtonRow();
        LayoutTabStrip();
        LayoutActiveStatePanel();
        LayoutResizeHandle();
    }

    // Falls back to the screen-relative default corner only when nothing's been saved yet
    // (FsmPanelLayoutConfig's own (-1, -1) sentinel) - otherwise restores the user's last dragged/
    // resized layout, clamped back into the current screen bounds (same clamp OnDragged already
    // applies) so a saved position/size from a larger resolution never leaves the panel off-screen.
    private void Reposition()
    {
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;

        Size = _layout.HasSavedSize
            ? new Vector2(Mathf.Max(MinWidth, _layout.Size.Value.x), Mathf.Max(MinHeight, _layout.Size.Value.y))
            : new Vector2(UICommon.ScaleWidth(PanelWidth), UICommon.ScaleHeight(PanelHeight));

        Vector2 defaultPosition = new(Screen.width - UICommon.ScaleWidth(ScreenMargin) - Size.x, UICommon.ScaleHeight(ScreenMargin));
        Vector2 desiredPosition = _layout.HasSavedPosition ? _layout.Position.Value : defaultPosition;
        LocalPosition = new Vector2(
            Mathf.Clamp(desiredPosition.x, 0f, Mathf.Max(0f, Screen.width - Size.x)),
            Mathf.Clamp(desiredPosition.y, 0f, Mathf.Max(0f, Screen.height - Size.y)));
    }
}
