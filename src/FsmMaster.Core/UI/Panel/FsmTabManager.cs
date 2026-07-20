using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Owns the set of currently "open" FSM tabs and which one is active - the single source of truth
// both the IMGUI list panel and the uGUI tab strip/Open dropdown drive selection through, so there's
// exactly one notion of "what's currently being viewed" rather than two parallel selection mechanisms.
internal sealed class FsmTabManager
{
    private readonly List<FsmTabState> _tabs = new();

    public IReadOnlyList<FsmTabState> Tabs => _tabs;
    public int ActiveTabIndex { get; private set; } = -1;

    public FsmTabState? GetActive() =>
        ActiveTabIndex >= 0 && ActiveTabIndex < _tabs.Count ? _tabs[ActiveTabIndex] : null;

    // Focuses an already-open tab for this FSM if one exists, otherwise opens a new one seeded with a
    // fresh fit-to-view pan/zoom. Takes the cheap identity-only entry (see FsmIdentityInfo) rather than
    // a full FsmInfo - the reflection-heavy state walk FitViewToFsm needs is only ever done here, once,
    // for the specific FSM actually being opened, via FsmDataCollector.CollectFsmInfo.
    public FsmTabState OpenOrFocus(FsmIdentityInfo fsm)
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

        FsmTabState tab = AddTab(fsm, fsmKey);
        ActiveTabIndex = _tabs.Count - 1;
        return tab;
    }

    // Opens a tab for each given FSM that isn't already open, without touching ActiveTabIndex unless
    // nothing was active yet - used by FsmGraphOverlay.AutoOpenEditedTabs when the overlay transitions
    // from hidden to visible, so FSMs with edits in effect (see FsmEditManager.GetEditedFsmKeys) show
    // up as tabs automatically without stealing focus away from whichever tab the player already had
    // active going into the previous hide.
    public void EnsureOpen(IEnumerable<FsmIdentityInfo> fsms)
    {
        foreach (FsmIdentityInfo fsm in fsms)
        {
            string fsmKey = FsmIdentity.GetFsmKey(fsm.Component);
            if (_tabs.Exists(tab => tab.FsmKey == fsmKey))
            {
                continue;
            }

            AddTab(fsm, fsmKey);
        }

        if (ActiveTabIndex < 0 && _tabs.Count > 0)
        {
            ActiveTabIndex = 0;
        }
    }

    private FsmTabState AddTab(FsmIdentityInfo fsm, string fsmKey)
    {
        var tab = new FsmTabState
        {
            FsmKey = fsmKey,
            Component = fsm.Component,
            GameObjectNameForLabel = fsm.GameObjectName,
            FsmNameForLabel = fsm.FsmName,
        };
        (tab.PanWorldCenter, tab.Zoom) = FsmGraphOverlay.FitViewToFsm(FsmDataCollector.CollectFsmInfo(fsm.Component));

        _tabs.Add(tab);
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
    // snapshot is marked not-live rather than closed, so it reconnects automatically if the player
    // returns to a scene containing that FsmKey again.
    //
    // Takes the FsmKey groups FsmIdentity.DiscoverFsmGroups already computed for the edit manager
    // (see FsmMasterPlugin.ApplyPersistedEditsForScene) rather than re-deriving keys itself from the
    // snapshot - GetFsmKey does a regex match plus string allocation per live FSM, and re-walking
    // every FSM in the room a second time just to rebuild the same key set was pure duplicated cost on
    // every single scene transition.
    public void RebindAfterRefresh(IReadOnlyDictionary<string, List<PlayMakerFSM>> groupsByFsmKey)
    {
        foreach (FsmTabState tab in _tabs)
        {
            if (groupsByFsmKey.TryGetValue(tab.FsmKey, out List<PlayMakerFSM>? components) && components.Count > 0)
            {
                tab.Component = components[0];
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
