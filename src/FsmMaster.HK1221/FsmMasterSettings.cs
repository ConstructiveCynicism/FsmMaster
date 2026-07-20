using Modding;
using UnityEngine;

namespace FsmMaster;

public class FsmMasterSaveSettings : IModSettings { }

// GraphLineStyle/GraphBoxStyle live in Core (IFsmGraphPerformanceConfig.cs) - shared across every
// loader rather than redeclared per platform.
public class FsmMasterGlobalSettings : IModSettings
{
    public bool AutoLoadLastConfiguration
    {
        get => GetBool(false);
        set => SetBool(value);
    }

    public bool FirstRunComplete
    {
        get => GetBool(false);
        set => SetBool(value);
    }

    // Hotkeys - plain KeyCode, no modifier-combo support. The Silksong branch's KeyboardShortcut
    // defaults (F3, and Empty for the minimal-view toggle) never actually used a modifier combo, so
    // nothing is lost by storing a single KeyCode here instead.
    public KeyCode ToggleOverlayHotkey
    {
        get => (KeyCode)GetInt((int)KeyCode.F3);
        set => SetInt((int)value);
    }

    public KeyCode ToggleMinimalViewHotkey
    {
        get => (KeyCode)GetInt((int)KeyCode.None);
        set => SetInt((int)value);
    }

    public GraphLineStyle LineStyle
    {
        get => (GraphLineStyle)GetInt((int)GraphLineStyle.Thin);
        set => SetInt((int)value);
    }

    public GraphBoxStyle BoxStyle
    {
        get => (GraphBoxStyle)GetInt((int)GraphBoxStyle.Detailed);
        set => SetInt((int)value);
    }

    // Logs a periodic per-phase timing breakdown of the graph overlay's rendering while the overlay is
    // open - a diagnostic aid for large FSMs, off by default. No live settings UI on this loader (see
    // HK1221GraphPerformanceConfig), so this is toggled by hand-editing the settings JSON.
    public bool GraphDiagnostics
    {
        get => GetBool(false);
        set => SetBool(value);
    }

    // Graph colors - plain fields rather than GetX/SetX-backed properties: IModSettings only exposes
    // scalar bool/int/float/string storage (StringValues/IntValues/BoolValues/FloatValues are its only
    // backing dictionaries), so Color values ride along on the framework's whole-object serialization
    // of GlobalSettings instead, the same way DebugMod's own GlobalSettings.binds field (a
    // SerializableIntDictionary) does.

    // Indexed by FsmState.ColorIndex. Index 1 is rotated to magenta/pink rather than plain blue, to
    // avoid colliding visually with ActiveStateColor's own blue/cyan highlight.
    public Color[] StateColors =
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

    // A lighter palette paired to the same colorIndex, for a state's own transition names/lines.
    public Color[] TransitionColors =
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

    public Color GlobalTransitionColor = new(0.6f, 0.6f, 0.6f);
    public Color VignetteColor = new(0f, 0f, 0f, 0.6f);
    public Color GlobalPseudoNodeColor = new(0.82f, 0.82f, 0.82f);
    public Color GlobalPseudoNodeOutlineColor = Color.black;
    public Color GlobalPseudoNodeTextColor = Color.black;
    public Color NodeOutlineColor = Color.white;
    public Color TransitionRowBackgroundColor = new(0.2f, 0.2f, 0.2f);
    public Color ActiveStateColor = new(0f, 1f, 1f);
    public Color ActiveTitleBackgroundColor = Color.Lerp(new Color(0f, 1f, 1f), Color.white, 0.5f);
    public Color ActiveTitleTextColor = Color.black;
    public Color SelectedStateColor = Color.yellow;
    public Color DisabledOutlineColor = new(0.5f, 0.5f, 0.5f);
    public Color DisabledTitleTextColor = new(0.75f, 0.75f, 0.75f);
    public Color DisabledEventTextColor = Color.black;
    public Color DisabledTransitionLineColor = new(0.55f, 0.55f, 0.55f);
    public Color DragTransitionColor = new(0f, 1f, 0f);

    // Panel layout - (-1, -1) is the "not yet saved" sentinel; the owning panel falls back to its own
    // screen-relative default placement until the player drags/resizes it.
    public Vector2 RightPanelPosition = new(-1f, -1f);
    public Vector2 RightPanelSize = new(-1f, -1f);
    public Vector2 MonitorPanelPosition = new(-1f, -1f);
    public Vector2 MonitorPanelSize = new(-1f, -1f);

    // Shared by FsmRightPanel/FsmMonitorPanel's own Reposition - true once a panel's been dragged/
    // resized at least once, false while it's still sitting on the (-1, -1) sentinel above.
    public static bool HasSavedLayout(Vector2 value) => value.x >= 0f && value.y >= 0f;
}
