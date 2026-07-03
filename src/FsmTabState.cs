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

    // Captured at open time so the tab's label survives a moment where Component is transiently null
    // (e.g. mid scene-transition, before RebindAfterRefresh runs).
    public string GameObjectNameForLabel = "";
    public string FsmNameForLabel = "";

    public Vector2 PanWorldCenter;
    public float Zoom = 1f;
    public string? SelectedStateName;
}
