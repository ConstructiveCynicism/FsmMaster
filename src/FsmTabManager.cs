using System.Collections.Generic;

namespace FsmMaster;

// Owns the set of currently "open" FSM tabs and which one is active - the single source of truth
// both the old IMGUI list panel (until it's deleted) and the new uGUI tab strip/Open dropdown drive
// selection through, so there's exactly one notion of "what's currently being viewed" rather than two
// parallel selection mechanisms during the UI overhaul's transition period.
internal sealed class FsmTabManager
{
    private readonly List<FsmTabState> _tabs = new();

    public IReadOnlyList<FsmTabState> Tabs => _tabs;
    public int ActiveTabIndex { get; private set; } = -1;

    public FsmTabState? GetActive() =>
        ActiveTabIndex >= 0 && ActiveTabIndex < _tabs.Count ? _tabs[ActiveTabIndex] : null;

    // Focuses an already-open tab for this FSM if one exists, otherwise opens a new one seeded with a
    // fresh fit-to-view pan/zoom.
    public FsmTabState OpenOrFocus(FsmInfo fsm)
    {
        string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);

        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].FsmKey == fsmKey)
            {
                ActiveTabIndex = i;
                return _tabs[i];
            }
        }

        var tab = new FsmTabState
        {
            FsmKey = fsmKey,
            Component = fsm.Component,
            GameObjectNameForLabel = fsm.GameObjectName,
            FsmNameForLabel = fsm.FsmName,
        };
        (tab.PanWorldCenter, tab.Zoom) = FsmGraphOverlay.FitViewToFsm(fsm);

        _tabs.Add(tab);
        ActiveTabIndex = _tabs.Count - 1;
        return tab;
    }

    // Focuses an already-open tab by reference (used by the tab strip's own click handler, which
    // already holds the FsmTabState it's rendering rather than an FsmInfo to re-derive a key from).
    public void Focus(FsmTabState tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index >= 0)
        {
            ActiveTabIndex = index;
        }
    }

    public void Close(FsmTabState tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);

        if (ActiveTabIndex == index)
        {
            ActiveTabIndex = _tabs.Count > 0 ? System.Math.Min(index, _tabs.Count - 1) : -1;
        }
        else if (ActiveTabIndex > index)
        {
            ActiveTabIndex--;
        }
    }

    // Called after every FsmDataCollector snapshot refresh (initial Awake, each scene load) - re-
    // resolves each open tab's live PlayMakerFSM by FsmKey. A tab whose FSM isn't present in the new
    // snapshot is marked not-live rather than closed (confirmed UX decision), so it reconnects
    // automatically if the player returns to a scene containing that FsmKey again.
    public void RebindAfterRefresh(FsmSnapshot snapshot)
    {
        var byKey = new Dictionary<string, FsmInfo>();
        foreach (FsmInfo fsm in snapshot.Fsms)
        {
            byKey[FsmIdentity.GetFsmKey(fsm.Component)] = fsm;
        }

        foreach (FsmTabState tab in _tabs)
        {
            if (byKey.TryGetValue(tab.FsmKey, out FsmInfo? fsm))
            {
                tab.Component = fsm.Component;
                tab.IsLive = true;
            }
            else
            {
                tab.Component = null;
                tab.IsLive = false;
            }
        }
    }
}
