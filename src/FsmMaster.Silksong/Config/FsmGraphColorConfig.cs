using BepInEx.Configuration;
using UnityEngine;

namespace FsmMaster;

// Bundles every ConfigEntry<Color> the graph overlay's rendering (FsmGraphOverlay) reads, so the whole
// FSM graph color palette can be retuned live via Configuration Manager's built-in color picker instead
// of being baked into the DLL. Defaults reproduce the palette FsmGraphOverlay originally hardcoded -
// see the per-entry descriptions below for what each one actually draws.
internal sealed class FsmGraphColorConfig : IFsmGraphColorConfig
{
    private const string StateSection = "Graph Colors - State Palette";
    private const string TransitionSection = "Graph Colors - Transition Palette";
    private const string OverlaySection = "Graph Colors - Overlay";

    public IFsmConfigValue<Color>[] StateColors { get; }
    public IFsmConfigValue<Color>[] TransitionColors { get; }
    public IFsmConfigValue<Color> GlobalTransitionColor { get; }
    public IFsmConfigValue<Color> VignetteColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeOutlineColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeTextColor { get; }
    public IFsmConfigValue<Color> NodeOutlineColor { get; }
    public IFsmConfigValue<Color> TransitionRowBackgroundColor { get; }
    public IFsmConfigValue<Color> ActiveStateColor { get; }
    public IFsmConfigValue<Color> ActiveTitleBackgroundColor { get; }
    public IFsmConfigValue<Color> ActiveTitleTextColor { get; }
    public IFsmConfigValue<Color> SelectedStateColor { get; }
    public IFsmConfigValue<Color> DisabledOutlineColor { get; }
    public IFsmConfigValue<Color> DisabledTitleTextColor { get; }
    public IFsmConfigValue<Color> DisabledEventTextColor { get; }
    public IFsmConfigValue<Color> DisabledTransitionLineColor { get; }
    public IFsmConfigValue<Color> DragTransitionColor { get; }

    private FsmGraphColorConfig(
        IFsmConfigValue<Color>[] stateColors,
        IFsmConfigValue<Color>[] transitionColors,
        IFsmConfigValue<Color> globalTransitionColor,
        IFsmConfigValue<Color> vignetteColor,
        IFsmConfigValue<Color> globalPseudoNodeColor,
        IFsmConfigValue<Color> globalPseudoNodeOutlineColor,
        IFsmConfigValue<Color> globalPseudoNodeTextColor,
        IFsmConfigValue<Color> nodeOutlineColor,
        IFsmConfigValue<Color> transitionRowBackgroundColor,
        IFsmConfigValue<Color> activeStateColor,
        IFsmConfigValue<Color> activeTitleBackgroundColor,
        IFsmConfigValue<Color> activeTitleTextColor,
        IFsmConfigValue<Color> selectedStateColor,
        IFsmConfigValue<Color> disabledOutlineColor,
        IFsmConfigValue<Color> disabledTitleTextColor,
        IFsmConfigValue<Color> disabledEventTextColor,
        IFsmConfigValue<Color> disabledTransitionLineColor,
        IFsmConfigValue<Color> dragTransitionColor)
    {
        StateColors = stateColors;
        TransitionColors = transitionColors;
        GlobalTransitionColor = globalTransitionColor;
        VignetteColor = vignetteColor;
        GlobalPseudoNodeColor = globalPseudoNodeColor;
        GlobalPseudoNodeOutlineColor = globalPseudoNodeOutlineColor;
        GlobalPseudoNodeTextColor = globalPseudoNodeTextColor;
        NodeOutlineColor = nodeOutlineColor;
        TransitionRowBackgroundColor = transitionRowBackgroundColor;
        ActiveStateColor = activeStateColor;
        ActiveTitleBackgroundColor = activeTitleBackgroundColor;
        ActiveTitleTextColor = activeTitleTextColor;
        SelectedStateColor = selectedStateColor;
        DisabledOutlineColor = disabledOutlineColor;
        DisabledTitleTextColor = disabledTitleTextColor;
        DisabledEventTextColor = disabledEventTextColor;
        DisabledTransitionLineColor = disabledTransitionLineColor;
        DragTransitionColor = dragTransitionColor;
    }

    public static FsmGraphColorConfig Bind(ConfigFile config)
    {
        // Default fill colors indexed by FsmState.ColorIndex - EXCEPT index 1: a plain blue would
        // collide visually with the active state's own blue/cyan highlighting (see the Active State
        // Color default below), so it's rotated to an otherwise-unused magenta/pink hue instead,
        // keeping the same saturation and value (brightness) a plain blue would have had.
        var stateDefaults = new[]
        {
            new Color(128f / 255f, 128f / 255f, 128f / 255f),
            new Color(201f / 255f, 116f / 255f, 173f / 255f),
            new Color(58f / 255f, 182f / 255f, 166f / 255f),
            new Color(93f / 255f, 164f / 255f, 53f / 255f),
            new Color(225f / 255f, 254f / 255f, 50f / 255f),
            new Color(235f / 255f, 131f / 255f, 46f / 255f),
            new Color(187f / 255f, 75f / 255f, 75f / 255f),
            new Color(117f / 255f, 53f / 255f, 164f / 255f),
        };

        // A lighter palette paired to the same colorIndex - used for a state's own transition
        // names/lines by default, so they read as visually associated with that state's node color
        // while staying less saturated than the node itself. Index 0 (PlayMaker's "no color set"
        // default) pairs with plain white rather than a tinted entry.
        var transitionDefaults = new[]
        {
            Color.white,
            new Color(248f / 255f, 197f / 255f, 231f / 255f),
            new Color(159f / 255f, 225f / 255f, 216f / 255f),
            new Color(183f / 255f, 225f / 255f, 159f / 255f),
            new Color(225f / 255f, 254f / 255f, 102f / 255f),
            new Color(255f / 255f, 198f / 255f, 152f / 255f),
            new Color(225f / 255f, 159f / 255f, 160f / 255f),
            new Color(197f / 255f, 159f / 255f, 225f / 255f),
        };

        var stateColors = new IFsmConfigValue<Color>[stateDefaults.Length];
        for (int i = 0; i < stateDefaults.Length; i++)
        {
            stateColors[i] = BindColor(config, StateSection, $"State Color {i}", stateDefaults[i],
                $"Fill color for a state whose FsmState.ColorIndex is {i} (colorIndex 0 is PlayMaker's \"no color set\" default).");
        }

        var transitionColors = new IFsmConfigValue<Color>[transitionDefaults.Length];
        for (int i = 0; i < transitionDefaults.Length; i++)
        {
            transitionColors[i] = BindColor(config, TransitionSection, $"Transition Color {i}", transitionDefaults[i],
                $"Transition name/line color for a state whose FsmState.ColorIndex is {i}.");
        }

        // Kept as a local default rather than read back from the ActiveStateColor entry below, so this
        // entry's own default is stable regardless of Config.Bind's declaration order.
        Color activeStateDefault = new(0f, 1f, 1f);

        return new FsmGraphColorConfig(
            stateColors,
            transitionColors,
            globalTransitionColor: BindColor(config, OverlaySection, "Global Transition Color", new Color(0.6f, 0.6f, 0.6f),
                "Line color for a global transition's connecting arrow."),
            vignetteColor: BindColor(config, OverlaySection, "Vignette Color", new Color(0f, 0f, 0f, 0.6f),
                "Dimming fill drawn over the graph wherever the selection panel isn't."),
            globalPseudoNodeColor: BindColor(config, OverlaySection, "Global Pseudo Node Color", new Color(0.82f, 0.82f, 0.82f),
                "Fill color of a global transition's pseudo-node box."),
            globalPseudoNodeOutlineColor: BindColor(config, OverlaySection, "Global Pseudo Node Outline Color", Color.black,
                "Outline color of a global transition's pseudo-node box."),
            globalPseudoNodeTextColor: BindColor(config, OverlaySection, "Global Pseudo Node Text Color", Color.black,
                "Event label color on a global transition's pseudo-node box."),
            nodeOutlineColor: BindColor(config, OverlaySection, "Node Outline Color", Color.white,
                "Default (non-active, non-disabled) inner ring and title/row divider color on a state node."),
            transitionRowBackgroundColor: BindColor(config, OverlaySection, "Transition Row Background Color", new Color(0.2f, 0.2f, 0.2f),
                "Background color of a state's transition rows."),
            activeStateColor: BindColor(config, OverlaySection, "Active State Color", activeStateDefault,
                "Outer halo, inner-ring fallback, and outgoing-line color for the FSM's currently active state."),
            activeTitleBackgroundColor: BindColor(config, OverlaySection, "Active Title Background Color", Color.Lerp(activeStateDefault, Color.white, 0.5f),
                "Title band background for the currently active state."),
            activeTitleTextColor: BindColor(config, OverlaySection, "Active Title Text Color", Color.black,
                "Title text color for the currently active state."),
            selectedStateColor: BindColor(config, OverlaySection, "Selected State Color", Color.yellow,
                "Outline and outgoing-line color for whichever state is currently selected."),
            disabledOutlineColor: BindColor(config, OverlaySection, "Disabled Outline Color", new Color(0.5f, 0.5f, 0.5f),
                "Inner ring and title/row divider color for a disabled state."),
            disabledTitleTextColor: BindColor(config, OverlaySection, "Disabled Title Text Color", new Color(0.75f, 0.75f, 0.75f),
                "Title text color for a disabled state."),
            disabledEventTextColor: BindColor(config, OverlaySection, "Disabled Event Text Color", Color.black,
                "Transition row text color for a disabled state."),
            disabledTransitionLineColor: BindColor(config, OverlaySection, "Disabled Transition Line Color", new Color(0.55f, 0.55f, 0.55f),
                "Line color for a disabled transition."),
            dragTransitionColor: BindColor(config, OverlaySection, "Drag Transition Color", new Color(0f, 1f, 0f),
                "Rubber-band preview line color while dragging a transition endpoint."));
    }

    // BepInEx.ConfigurationManager has no fold/collapse of its own for a category (only the whole
    // plugin header collapses) - flagging every color entry IsAdvanced instead hides all 32 of them
    // behind that mod's own "Advanced settings" checkbox, the closest equivalent it actually offers.
    private static IFsmConfigValue<Color> BindColor(ConfigFile config, string section, string key, Color defaultValue, string description)
    {
        ConfigEntry<Color> entry = config.Bind(section, key, defaultValue,
            new ConfigDescription(description, null, new ConfigurationManagerAttributes { IsAdvanced = true }));
        return new BepInExConfigValue<Color>(entry);
    }
}
