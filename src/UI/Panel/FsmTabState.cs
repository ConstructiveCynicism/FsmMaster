using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// Per-tab state for the right panel's FSM tab strip - a class (not a struct) so FsmGraphOverlay can
// mutate an open tab's pan/zoom/selection in place while it's the active tab, and so switching away
// from it and back later restores exactly where the user left off.
internal sealed class FsmTabState
{
    public string FsmKey = "";

    // May go null (and IsLive false) if this tab's backing FSM disappears on a scene transition -
    // kept open as a placeholder rather than auto-closed (see FsmTabManager.RebindAfterRefresh),
    // reconnecting automatically if the player returns to a scene containing this FsmKey again.
    public PlayMakerFSM? Component;
    public bool IsLive = true;

    // Pinned tabs keep their graph drawn by FsmGraphOverlay even while a different tab is active -
    // see FsmGraphOverlay.OnGUI, which draws every pinned-but-inactive tab's graph as a non-interactive
    // "ghost" behind the active tab's own interactive one. Unrelated to IsLive/Component above (a
    // pinned tab whose FSM has gone not-live simply draws nothing, same as an unpinned one would).
    public bool IsPinned;

    // Captured at open time so the tab's label survives a moment where Component is transiently null
    // (e.g. mid scene-transition, before RebindAfterRefresh runs).
    public string GameObjectNameForLabel = "";
    public string FsmNameForLabel = "";

    public Vector2 PanWorldCenter;
    public float Zoom = 1f;
    public string? SelectedStateName;

    // Set by the graph overlay when a transition click resolves to a specific action (see
    // FsmGraphOverlay's transition hit-testing) - consumed and cleared by FsmMasterPlugin.Update once it's
    // called FsmActiveStatePanel.ScrollToAction, the same one-shot "request" shape SelectedStateName's own
    // producer/consumer split already uses.
    public int? PendingScrollActionIndex;
}
