using BepInEx.Configuration;

namespace FsmMaster;

// How transition lines are drawn - see FsmGraphOverlay.DrawLineBufferGL. Ordered roughly cheapest-last
// isn't right either way; Thick is the default, full-detail look, Straight is the cheapest.
internal enum GraphLineStyle
{
    Thick,
    Thin,
    Straight,
}

// How state/event boxes are drawn - see the chrome-gathering pass in FsmGraphOverlay.DrawCachedGraph.
internal enum GraphBoxStyle
{
    Detailed,
    Standard,
}

// Bundles the two graph-rendering detail level ConfigEntrys FsmGraphOverlay reads, so a player who
// finds the graph overlay too expensive on a large FSM (100+ states) can trade visual detail for frame
// time via Configuration Manager, without a recompile.
internal sealed class FsmGraphPerformanceConfig
{
    public ConfigEntry<GraphLineStyle> LineStyle { get; }
    public ConfigEntry<GraphBoxStyle> BoxStyle { get; }

    // Bumped once for every setting in `config` that changes for any reason (Configuration Manager's UI,
    // a hand-edited config file being reloaded, ...) - FsmGraphOverlay's node/pseudo-node chrome buffer
    // (the rounded-rect tessellation that Detailed box style pays for) is cached across frames rather
    // than rebuilt every Repaint, keyed partly on this counter, so a color retuned live via Configuration
    // Manager (see FsmGraphColorConfig's own doc comment on that being a supported workflow) still shows
    // up on the very next frame instead of only after some unrelated cache-busting pan/zoom/selection
    // change. Deliberately whole-ConfigFile rather than narrowed to just the color/box/line entries the
    // graph actually reads - BepInEx's ConfigFile only exposes one SettingChanged event for the entire
    // file, and an occasional wasted rebuild from an unrelated setting (a hotkey, a panel layout number)
    // changing is harmless next to the cost this cache is avoiding.
    public int Generation { get; private set; }

    private FsmGraphPerformanceConfig(ConfigEntry<GraphLineStyle> lineStyle, ConfigEntry<GraphBoxStyle> boxStyle)
    {
        LineStyle = lineStyle;
        BoxStyle = boxStyle;
    }

    public static FsmGraphPerformanceConfig Bind(ConfigFile config)
    {
        const string section = "Performance";

        ConfigEntry<GraphLineStyle> lineStyle = config.Bind(
            section,
            "Line Style",
            GraphLineStyle.Thick,
            "How transition lines are drawn. Thick: antialiased curved lines with arrowheads (most "
                + "detailed, most expensive). Thin: the same curves drawn as hard-edged 1px lines with "
                + "arrowheads. Straight: a plain straight segment between each transition's endpoints, no "
                + "arrowhead (cheapest).");

        ConfigEntry<GraphBoxStyle> boxStyle = config.Bind(
            section,
            "Box Style",
            GraphBoxStyle.Detailed,
            "How state/event boxes are drawn. Detailed: rounded corners, a border ring on every box, and "
                + "divider lines between the title and its transition rows. Standard: square corners, no "
                + "border ring unless the state is active or selected, and no divider lines (cheapest).");

        var instance = new FsmGraphPerformanceConfig(lineStyle, boxStyle);
        config.SettingChanged += (_, _) => instance.Generation++;
        return instance;
    }
}
