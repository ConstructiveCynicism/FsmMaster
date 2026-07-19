using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// Tracks which states an FSM's live Fsm instance has actually entered since the last reconciliation, via
// FsmStateEnteredPatch's static StateEntered event (a Harmony postfix on Fsm.EnterState, since this
// PlayMaker version has no StateChanged field/event to subscribe to directly). PlayMaker can chain
// several state entries within a single Unity Update() call - one event immediately causing another
// transition, and so on - so polling Fsm.ActiveStateName once per rendered frame (the graph overlay's
// previous approach) silently drops every intermediate state a multi-hop chain passed through within
// that same frame. Observing every EnterState call instead sees every one of them, in order, as they
// happen.
//
// Only ever tracks whichever FSMs FsmGraphOverlay is actually drawing this frame (EnsureTracked is
// called once per DrawGraph pass, for the active tab plus any pinned tabs) - not every live FSM in the
// scene, since most of those never have a graph open to show this on. The StateEntered event itself
// fires for every FSM in the game regardless, but the per-event work here is just a lookup against this
// small tracked set, not a subscription per FSM.
internal sealed class FsmActiveStateTracker
{
    private const float FadeDurationSeconds = 1f;

    private sealed class TrackedFsm
    {
        public Fsm Instance = null!;

        // States entered (via StateEntered) since the last CommitFrame reconciliation - can hold more
        // than one entry when the FSM chained several transitions within a single Update().
        public readonly HashSet<string> EnteredSinceCommit = new();

        // "Was active, no longer is" - stateName -> Time.unscaledTime it stopped being active. Removed
        // once its fade completes (FadeDurationSeconds elapsed) or the same state becomes active again
        // before that (see CommitFrame) - a resumed state shows its normal active look immediately
        // rather than resuming a stale, partially-faded one.
        public readonly Dictionary<string, float> FadingStates = new();
    }

    private readonly Dictionary<string, TrackedFsm> _tracked = new();
    private readonly HashSet<string> _visibleThisFrame = new();

    public FsmActiveStateTracker()
    {
        FsmStateEnteredPatch.StateEntered += OnStateEntered;
    }

    private void OnStateEntered(Fsm instance, FsmState state)
    {
        foreach (KeyValuePair<string, TrackedFsm> pair in _tracked)
        {
            if (ReferenceEquals(pair.Value.Instance, instance))
            {
                pair.Value.EnteredSinceCommit.Add(state.Name);
            }
        }
    }

    // Called once per DrawGraph pass - both the interactive active tab and every frozen pinned-tab
    // ghost - with whatever live Fsm instance that tab is currently showing. Safe to call on every
    // OnGUI event (Layout, Repaint, mouse events all redraw the same graph), not just Repaint, since
    // it's a cheap dictionary lookup once already tracking; marks fsmKey as still wanted this frame so
    // CommitFrame doesn't prune it out from under an actively-drawn tab.
    public void EnsureTracked(string fsmKey, Fsm instance)
    {
        _visibleThisFrame.Add(fsmKey);

        if (_tracked.TryGetValue(fsmKey, out TrackedFsm? existing) && ReferenceEquals(existing.Instance, instance))
        {
            return;
        }

        // Either nothing was tracked under this key yet, or a different Fsm instance now answers to it
        // (a scene reload rebound the tab to a freshly-Awoken FSM - FsmTabManager.RebindAfterRefresh).
        // There's no per-instance subscription to unwind here, unlike a real StateChanged field would
        // have needed - OnStateEntered matches by instance reference every time it fires, so simply
        // replacing the tracked entry is enough.
        _tracked[fsmKey] = new TrackedFsm { Instance = instance };
    }

    // Called once per rendered frame - gated by the caller (FsmGraphOverlay.OnGUI) on
    // Event.current.type == EventType.Repaint, which fires exactly once per frame, unlike the
    // Layout/mouse events OnGUI also receives - after every DrawGraph pass for the frame has already
    // run its own EnsureTracked call.
    public void CommitFrame()
    {
        List<string>? toRemove = null;
        foreach (KeyValuePair<string, TrackedFsm> pair in _tracked)
        {
            string fsmKey = pair.Key;
            TrackedFsm entry = pair.Value;

            // The owning PlayMakerFSM was destroyed (scene unload), or no tab drew this FSM this frame
            // (its tab was closed/unpinned) - nothing left to reconcile.
            if (entry.Instance.FsmComponent == null || !_visibleThisFrame.Contains(fsmKey))
            {
                (toRemove ??= new List<string>()).Add(fsmKey);
                continue;
            }

            string? activeStateName = entry.Instance.ActiveStateName;
            foreach (string stateName in entry.EnteredSinceCommit)
            {
                if (stateName == activeStateName)
                {
                    // Genuinely active again right now - any fade in progress for it was stale, not a
                    // real "was active, now fading back toward default" case.
                    entry.FadingStates.Remove(stateName);
                }
                else
                {
                    entry.FadingStates[stateName] = Time.unscaledTime;
                }
            }
            entry.EnteredSinceCommit.Clear();

            if (entry.FadingStates.Count > 0)
            {
                List<string>? expired = null;
                foreach (KeyValuePair<string, float> fade in entry.FadingStates)
                {
                    if (Time.unscaledTime - fade.Value >= FadeDurationSeconds)
                    {
                        (expired ??= new List<string>()).Add(fade.Key);
                    }
                }

                if (expired != null)
                {
                    foreach (string stateName in expired)
                    {
                        entry.FadingStates.Remove(stateName);
                    }
                }
            }
        }

        if (toRemove != null)
        {
            foreach (string fsmKey in toRemove)
            {
                _tracked.Remove(fsmKey);
            }
        }

        _visibleThisFrame.Clear();
    }

    // 0 = just stopped being active (still essentially the active color), 1 = fade complete (fully the
    // default color) - null when stateName isn't currently fading, whether because it's genuinely
    // active right now or because it settled back to its default look some time ago.
    public float? GetFadeProgress(string fsmKey, string stateName)
    {
        if (!_tracked.TryGetValue(fsmKey, out TrackedFsm? entry) || !entry.FadingStates.TryGetValue(stateName, out float startTime))
        {
            return null;
        }

        return Mathf.Clamp01((Time.unscaledTime - startTime) / FadeDurationSeconds);
    }

    // Lets the renderer skip its own chrome cache while a fade is actually in progress for this FSM -
    // fade progress changes every frame, unlike everything else that cache is keyed on.
    public bool HasAnyFading(string fsmKey) =>
        _tracked.TryGetValue(fsmKey, out TrackedFsm? entry) && entry.FadingStates.Count > 0;

    // Clears every tracked FSM. There's no per-instance subscription to unwind (see EnsureTracked) and
    // this tracker's own subscription to the static StateEntered event lives for the process's lifetime,
    // matching every other Harmony patch in this mod - so this only ever needs to reset tracking state,
    // not tear down a hook.
    public void UnsubscribeAll()
    {
        _tracked.Clear();
        _visibleThisFrame.Clear();
    }
}
