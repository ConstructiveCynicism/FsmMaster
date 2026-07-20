namespace FsmMaster;

// How transition lines are drawn - see FsmGraphOverlay.DrawLineBufferGL. Thick is the default,
// full-detail look; Straight is the cheapest.
public enum GraphLineStyle
{
    Thick,
    Thin,
    Straight,
}

// How state/event boxes are drawn - see the chrome-gathering pass in FsmGraphOverlay.DrawCachedGraph.
public enum GraphBoxStyle
{
    Detailed,
    Standard,
}

// Bundles the two graph-rendering detail level settings FsmGraphOverlay reads, so a player who finds
// the graph overlay too expensive on a large FSM (100+ states) can trade visual detail for frame time
// via the loader's own settings UI, without a recompile.
public interface IFsmGraphPerformanceConfig
{
    IFsmConfigValue<GraphLineStyle> LineStyle { get; }

    IFsmConfigValue<GraphBoxStyle> BoxStyle { get; }

    // When true, FsmGraphOverlay times each phase of its graph Repaint (layout rebuild, line/chrome
    // gather, GL emission, labels) and logs a periodic breakdown - a diagnostic aid for finding what
    // makes a large FSM's graph expensive to draw, off by default. See GraphProfiler.
    IFsmConfigValue<bool> DiagnosticsEnabled { get; }

    // Bumped every time any setting behind this config changes, for any reason - lets
    // FsmGraphOverlay's own chrome buffer cache tell "layout is still what it built its geometry
    // against" apart from "layout object reference happens to be the same," without re-diffing every
    // color/style entry on every frame.
    int Generation { get; }
}
