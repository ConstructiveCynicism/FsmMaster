using System;
using BepInEx.Logging;
using UnityEngine;

namespace FsmMaster;

// Composition root for FsmMaster's right-side uGUI panel - anchored top-right, matching where
// Silksong.DebugMod's own MainPanel sits (agent-context/Silksong.DebugMod-main/UI/MainPanel.cs).
// Built up incrementally across the UI overhaul's build order: the Open/Save/Load button row, the
// Open button's Scene/Object/FSM dropdown, the FSM tab strip, and the Actions/Events/Variables
// active-state panel.
internal sealed class FsmRightPanel : CanvasPanel
{
    private const int PanelWidth = 340;
    private const int PanelHeight = 480;
    private const int ScreenMargin = 10;
    private const float ButtonRowHeight = 28f;
    private const float ButtonGap = 4f;
    private const float SectionGap = 8f;

    public FsmTabStripPanel TabStrip { get; }
    public FsmActiveStatePanel ActiveStatePanel { get; }

    private readonly CanvasButton _openButton;
    private readonly CanvasButton _saveButton;
    private readonly CanvasButton _loadButton;

    private int _lastScreenWidth = -1;
    private int _lastScreenHeight = -1;

    public FsmRightPanel(UICommon ui, FsmTabManager tabManager, FsmEditManager editManager, Func<FsmSnapshot?> getSnapshot, ManualLogSource logger)
        : base("FsmRightPanel")
    {
        Reposition();

        CanvasImage background = Add(new CanvasImage("Background", ui) { IsBackground = true, Tint = ui.PanelBackground });
        background.AddBorder(ui.PanelBorder);

        _openButton = Add(new CanvasButton("OpenButton", ui));
        _openButton.Text.Text = "Open";

        _saveButton = Add(new CanvasButton("SaveButton", ui));
        _saveButton.Text.Text = "Save";
        _saveButton.OnClicked += () => SaveActiveTab(tabManager, editManager, logger);

        _loadButton = Add(new CanvasButton("LoadButton", ui));
        _loadButton.Text.Text = "Load";
        _loadButton.OnClicked += () => LoadActiveTab(tabManager, editManager, logger);

        LayoutButtonRow();

        TabStrip = Add(new FsmTabStripPanel(ui, tabManager));
        LayoutTabStrip();

        ActiveStatePanel = Add(new FsmActiveStatePanel(ui));
        LayoutActiveStatePanel();

        // Added last so it renders/hit-tests on top of the tab strip and active-state panel below it
        // whenever it's shown (later-added siblings render in front - see CanvasButton's own hover
        // border for the same convention).
        FsmOpenDropdown openDropdown = Add(new FsmOpenDropdown(ui, tabManager, getSnapshot, _openButton));
        openDropdown.LocalPosition = new Vector2(10f, 10f + ButtonRowHeight + 4f);
        _openButton.OnClicked += openDropdown.Toggle;

        OnUpdate += ReflowOnResolutionChange;
    }

    // Both operate on whichever tab is currently active, no-op when nothing is open. Scene name comes
    // from the FSM's own scene (active.Component.gameObject.scene.name), not the globally-active
    // scene, matching how FsmSaveDataStore/FsmDrilldownHierarchy already key by gameObject.scene.name.
    private static void SaveActiveTab(FsmTabManager tabManager, FsmEditManager editManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Save: no active tab.");
            return;
        }

        FsmEditSet editSet = editManager.GetActiveEditSet(active.FsmKey) ?? new FsmEditSet { FsmKey = active.FsmKey };
        string sceneName = active.Component.gameObject.scene.name;
        string json = FsmSaveDataStore.Save(sceneName, editSet);
        logger.LogInfo($"[FsmMaster] Saved edits for '{active.FsmKey}' in scene '{sceneName}':\n{json}");
    }

    private static void LoadActiveTab(FsmTabManager tabManager, FsmEditManager editManager, ManualLogSource logger)
    {
        FsmTabState? active = tabManager.GetActive();
        if (active?.Component == null)
        {
            logger.LogInfo("[FsmMaster] Load: no active tab.");
            return;
        }

        string sceneName = active.Component.gameObject.scene.name;
        FsmEditSet? editSet = FsmSaveDataStore.Load(sceneName, active.FsmKey);
        if (editSet == null)
        {
            logger.LogInfo($"[FsmMaster] Load: no saved edits found for '{active.FsmKey}' in scene '{sceneName}'.");
            return;
        }

        editManager.ApplyEditSet(editSet);
        logger.LogInfo($"[FsmMaster] Loaded edits for '{active.FsmKey}' in scene '{sceneName}'.");
    }

    private void LayoutButtonRow()
    {
        float rowWidth = Size.x - 20f;
        float buttonWidth = (rowWidth - ButtonGap * 2f) / 3f;

        _openButton.LocalPosition = new Vector2(10f, 10f);
        _openButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _saveButton.LocalPosition = new Vector2(10f + buttonWidth + ButtonGap, 10f);
        _saveButton.Size = new Vector2(buttonWidth, ButtonRowHeight);

        _loadButton.LocalPosition = new Vector2(10f + (buttonWidth + ButtonGap) * 2f, 10f);
        _loadButton.Size = new Vector2(rowWidth - (buttonWidth + ButtonGap) * 2f, ButtonRowHeight);
    }

    private void LayoutTabStrip()
    {
        TabStrip.LocalPosition = new Vector2(10f, 10f + ButtonRowHeight + SectionGap);
        TabStrip.Size = new Vector2(Size.x - 20f, FsmTabStripPanel.TotalHeight);
    }

    private void LayoutActiveStatePanel()
    {
        float y = 10f + ButtonRowHeight + SectionGap + FsmTabStripPanel.TotalHeight + SectionGap;
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
    }

    private void Reposition()
    {
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;

        Size = new Vector2(UICommon.ScaleWidth(PanelWidth), UICommon.ScaleHeight(PanelHeight));
        LocalPosition = new Vector2(Screen.width - UICommon.ScaleWidth(ScreenMargin) - Size.x, UICommon.ScaleHeight(ScreenMargin));
    }
}
