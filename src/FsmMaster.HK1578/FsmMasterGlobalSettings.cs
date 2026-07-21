using Newtonsoft.Json;
using UnityEngine;

namespace FsmMaster;

// Plain POCO, JSON-serialized whole by this loader generation's IGlobalSettings<T> plumbing (unlike
// the old Modding API's GetBool/SetBool-backed IModSettings shape hk1221's own settings class needs) -
// see the old class for what the GetX/SetX indirection was working around; none of that applies here.
public class FsmMasterGlobalSettings
{
    public bool AutoLoadLastConfiguration { get; set; }

    public bool FirstRunComplete { get; set; }

    // Hotkeys - plain KeyCode, no modifier-combo support, matching every other loader.
    public KeyCode ToggleOverlayHotkey { get; set; } = KeyCode.F3;

    public KeyCode ToggleMinimalViewHotkey { get; set; } = KeyCode.None;

    public GraphLineStyle LineStyle { get; set; } = GraphLineStyle.Thin;

    public GraphBoxStyle BoxStyle { get; set; } = GraphBoxStyle.Detailed;

    // Logs a periodic per-phase timing breakdown of the graph overlay's rendering while the overlay is
    // open - a diagnostic aid for large FSMs, off by default. No live settings UI on this loader
    // (ICustomMenuMod covers keybinds/toggles, not a general property grid), so this is toggled by
    // hand-editing the settings JSON.
    public bool GraphDiagnostics { get; set; }

    // Every Color member below is explicitly routed through ColorJsonConverter - see that class for
    // why an unannotated UnityEngine.Color silently serializes to `{}` here and reads back fully
    // transparent. Member-level attributes are honoured regardless of the serializer settings the
    // Modding API chooses, so this needs no hook into its own converter list.

    // Indexed by FsmState.ColorIndex. Index 1 is rotated to magenta/pink rather than plain blue, to
    // avoid colliding visually with ActiveStateColor's own blue/cyan highlight.
    [JsonConverter(typeof(ColorPaletteJsonConverter))]
    public Color[] StateColors { get; set; } =
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
    [JsonConverter(typeof(ColorPaletteJsonConverter))]
    public Color[] TransitionColors { get; set; } =
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

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color GlobalTransitionColor { get; set; } = new(0.6f, 0.6f, 0.6f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color VignetteColor { get; set; } = new(0f, 0f, 0f, 0.6f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color GlobalPseudoNodeColor { get; set; } = new(0.82f, 0.82f, 0.82f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color GlobalPseudoNodeOutlineColor { get; set; } = Color.black;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color GlobalPseudoNodeTextColor { get; set; } = Color.black;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color NodeOutlineColor { get; set; } = Color.white;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color TransitionRowBackgroundColor { get; set; } = new(0.2f, 0.2f, 0.2f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color ActiveStateColor { get; set; } = new(0f, 1f, 1f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color ActiveTitleBackgroundColor { get; set; } = Color.Lerp(new Color(0f, 1f, 1f), Color.white, 0.5f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color ActiveTitleTextColor { get; set; } = Color.black;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color SelectedStateColor { get; set; } = Color.yellow;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color DisabledOutlineColor { get; set; } = new(0.5f, 0.5f, 0.5f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color DisabledTitleTextColor { get; set; } = new(0.75f, 0.75f, 0.75f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color DisabledEventTextColor { get; set; } = Color.black;

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color DisabledTransitionLineColor { get; set; } = new(0.55f, 0.55f, 0.55f);

    [JsonConverter(typeof(ColorJsonConverter))]
    public Color DragTransitionColor { get; set; } = new(0f, 1f, 0f);

    // Panel layout - (-1, -1) is the "not yet saved" sentinel; the owning panel falls back to its own
    // screen-relative default placement until the player drags/resizes it.
    public Vector2 RightPanelPosition { get; set; } = new(-1f, -1f);
    public Vector2 RightPanelSize { get; set; } = new(-1f, -1f);
    public Vector2 MonitorPanelPosition { get; set; } = new(-1f, -1f);
    public Vector2 MonitorPanelSize { get; set; } = new(-1f, -1f);

    // Shared by FsmRightPanel/FsmMonitorPanel's own Reposition - true once a panel's been dragged/
    // resized at least once, false while it's still sitting on the (-1, -1) sentinel above.
    public static bool HasSavedLayout(Vector2 value) => value.x >= 0f && value.y >= 0f;
}
