using System.Diagnostics;
using System.Text;

namespace FsmMaster;

// Lightweight, allocation-free per-phase timer for the graph overlay's Repaint path, used to find
// which phase actually dominates on a large FSM (a boss FSM with a hundred-plus states can drop the
// frame rate by an order of magnitude while its graph is on screen, and guessing which of gathering,
// GL emission, or label layout is responsible is exactly what this removes). Enabled via
// IFsmGraphPerformanceConfig.DiagnosticsEnabled; while disabled the overlay never calls into it, so it
// adds nothing to the normal render cost.
//
// Timing uses Stopwatch.GetTimestamp() deltas (a raw counter read, no Stopwatch object allocation) and
// accumulates ticks per phase plus a few geometry counters, then logs one formatted summary roughly
// once per SummaryIntervalSeconds and resets. Ticks are converted to milliseconds only at summary time,
// so the per-frame path stays a pair of long reads and an array add.
internal sealed class GraphProfiler
{
    internal enum Phase
    {
        // RebuildNodeLayoutCache - the full state/transition -> screen-geometry walk, only when the
        // layout cache is stale (pan/zoom/edit/scene change), so usually ~0 during steady combat.
        LayoutRebuild,

        // Gathering the transition-line polylines into LineDrawBuffer (never cached across frames).
        LineGather,

        // Gathering node/pseudo-node rounded-rect chrome into ChromeVertexBuffer - the trig-heavy
        // corner tessellation, skipped entirely when the chrome cache is still valid.
        ChromeGather,

        // DrawLineBufferGL - emitting every gathered line as GL geometry (unavoidable per Repaint).
        LineEmit,

        // FlushChromeBufferGL - emitting every gathered chrome triangle (unavoidable per Repaint).
        ChromeEmit,

        // The GUI.Label pass for state names and transition rows (skipped when zoomed out past the
        // legibility floor - see DrawCachedGraph).
        Labels,
    }

    private const int PhaseCount = 6;
    private const double SummaryIntervalSeconds = 2.0;

    private static readonly string[] PhaseNames =
    {
        "LayoutRebuild",
        "LineGather",
        "ChromeGather",
        "LineEmit",
        "ChromeEmit",
        "Labels",
    };

    private readonly IFsmLog _log;
    private readonly long[] _phaseTicks = new long[PhaseCount];

    // Reused so the per-summary formatting allocates nothing beyond the final string handed to the log.
    private readonly StringBuilder _summary = new();

    private int _repaints;
    private long _totalRepaintTicks;

    // Last measured Repaint's geometry, snapshotted rather than accumulated - a per-frame snapshot is
    // what tells "the whole graph is on screen" (visible ~= total) apart from "zoomed into a corner"
    // (visible << total), and averaging counts across the window would blur exactly that.
    private int _nodes;
    private int _rows;
    private int _lineEntries;
    private int _chromeVertices;
    private bool _chromeCacheHit;
    private bool _labelsSkipped;

    private long _windowStart;
    private bool _windowOpen;

    public GraphProfiler(IFsmLog log)
    {
        _log = log;
    }

    // Raw counter read - callers capture this before a phase and hand it back to Record afterward.
    public long Now() => Stopwatch.GetTimestamp();

    public void Record(Phase phase, long startTimestamp)
    {
        _phaseTicks[(int)phase] += Stopwatch.GetTimestamp() - startTimestamp;
    }

    // Called once per Repaint pass of a drawn graph, with that pass's own total elapsed span and the
    // geometry it drew. Emits (and resets) the accumulated window once it has covered
    // SummaryIntervalSeconds of wall time.
    public void FrameComplete(long repaintTicks, int nodes, int rows, int lineEntries, int chromeVertices, bool chromeCacheHit, bool labelsSkipped)
    {
        if (!_windowOpen)
        {
            _windowStart = Stopwatch.GetTimestamp();
            _windowOpen = true;
        }

        _repaints++;
        _totalRepaintTicks += repaintTicks;
        _nodes = nodes;
        _rows = rows;
        _lineEntries = lineEntries;
        _chromeVertices = chromeVertices;
        _chromeCacheHit = chromeCacheHit;
        _labelsSkipped = labelsSkipped;

        double elapsedSeconds = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsedSeconds >= SummaryIntervalSeconds)
        {
            EmitSummary(elapsedSeconds);
            Reset();
        }
    }

    private void EmitSummary(double elapsedSeconds)
    {
        double ticksToMs = 1000.0 / Stopwatch.Frequency;
        double avgRepaintMs = _repaints > 0 ? _totalRepaintTicks * ticksToMs / _repaints : 0.0;

        long measuredTicks = 0;
        for (int i = 0; i < PhaseCount; i++)
        {
            measuredTicks += _phaseTicks[i];
        }

        _summary.Length = 0;
        _summary.Append("[FsmMaster] Graph profile: ")
            .Append(_repaints).Append(" repaints / ")
            .Append(elapsedSeconds.ToString("F1")).Append("s, ")
            .Append(avgRepaintMs.ToString("F2")).Append(" ms/repaint avg\n");
        _summary.Append("  geometry: nodes=").Append(_nodes)
            .Append(" rows=").Append(_rows)
            .Append(" lineEntries=").Append(_lineEntries)
            .Append(" chromeVerts=").Append(_chromeVertices)
            .Append(" chromeCache=").Append(_chromeCacheHit ? "HIT" : "MISS")
            .Append(" labels=").Append(_labelsSkipped ? "SKIPPED" : "drawn")
            .Append('\n');

        for (int i = 0; i < PhaseCount; i++)
        {
            double perRepaintMs = _repaints > 0 ? _phaseTicks[i] * ticksToMs / _repaints : 0.0;
            double share = measuredTicks > 0 ? 100.0 * _phaseTicks[i] / measuredTicks : 0.0;
            _summary.Append("  ")
                .Append(PhaseNames[i].PadRight(14))
                .Append(perRepaintMs.ToString("F2")).Append(" ms/repaint  ")
                .Append(share.ToString("F1")).Append("%\n");
        }

        _log.LogInfo(_summary.ToString());
    }

    private void Reset()
    {
        for (int i = 0; i < PhaseCount; i++)
        {
            _phaseTicks[i] = 0;
        }

        _repaints = 0;
        _totalRepaintTicks = 0;
        _windowOpen = false;
    }
}
