// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// Tracks which states an FSM's live Fsm instance has actually entered since the last reconciliation.
//
// On netstandard2.1 (Silksong), this hooks Fsm.StateChanged - a plain public Action<FsmState> field,
// invoked synchronously from EnterState right before state.OnEnter() runs. PlayMaker can chain several
// state entries within a single Unity Update() call - one event immediately causing another transition,
// and so on - so polling Fsm.ActiveStateName once per rendered frame silently drops every intermediate
// state a multi-hop chain passed through within that same frame. Hooking StateChanged instead observes
// every one of them, in order, as they happen.
//
// net35's PlayMaker build has no StateChanged field at all (confirmed against the real hk1221
// PlayMaker.dll - see platform-inventory.md's PlayMaker surface delta table: it's a Silksong-only
// addition), and net472 is built once and shared by both hk1432 (whose PlayMaker build is identical to
// hk1221's, no StateChanged) and hk1578 (which does have it) - a single Core.dll can't safely call a
// field one of its two net472 loaders' actual PlayMaker.dll doesn't have. Both TFMs instead subscribe
// to FsmStateChangeBridge, fed by each loader's own EnterState hook (a Harmony postfix on hk1221, a
// MonoMod On.Fsm.EnterState hook on hk1432/hk1578) - see that bridge's own comment for why Core can't
// hook either mechanism directly. Same per-instance, every-state-entered fidelity as the
// StateChanged-field path, just routed through one process-wide event instead of a field on Fsm itself.
//
// Only ever tracks whichever FSMs FsmGraphOverlay is actually drawing this frame (EnsureTracked is
// called once per DrawGraph pass, for the active tab plus any pinned tabs) - not every live FSM in the
// scene, since most of those never have a graph open to show this on.
internal sealed class FsmActiveStateTracker
{
    private const float FadeDurationSeconds = 1f;

    private sealed class TrackedFsm
    {
        public Fsm Instance = null!;
        public Action<FsmState> Handler = null!;
#if NET35 || NET472
        // Bridge-side handler, wired to only fire for this entry's own Instance - FsmStateChangeBridge
        // is a single process-wide event covering every live Fsm, not a per-instance one.
        public Action<Fsm, FsmState>? BridgeHandler;
#endif

        // States entered since the last CommitFrame reconciliation - on every TFM but net35, can hold
        // more than one entry when the FSM chained several transitions within a single Update().
        public readonly HashSet<string> EnteredSinceCommit = new();

        // "Was active, no longer is" - stateName -> Time.unscaledTime it stopped being active. Removed
        // once its fade completes (FadeDurationSeconds elapsed) or the same state becomes active again
        // before that (see CommitFrame) - a resumed state shows its normal active look immediately
        // rather than resuming a stale, partially-faded one.
        public readonly Dictionary<string, float> FadingStates = new();
    }

    private readonly Dictionary<string, TrackedFsm> _tracked = new();
    private readonly HashSet<string> _visibleThisFrame = new();

    // Called once per DrawGraph pass - both the interactive active tab and every frozen pinned-tab
    // ghost - with whatever live Fsm instance that tab is currently showing. Safe to call on every
    // OnGUI event (Layout, Repaint, mouse events all redraw the same graph), not just Repaint, since
    // it's a cheap dictionary lookup once already tracking; marks fsmKey as still wanted this frame so
    // CommitFrame doesn't prune it out from under an actively-drawn tab.
    public void EnsureTracked(string fsmKey, Fsm instance)
    {
        _visibleThisFrame.Add(fsmKey);

        if (_tracked.TryGetValue(fsmKey, out TrackedFsm? existing))
        {
            if (ReferenceEquals(existing.Instance, instance))
            {
                return;
            }

            // A different Fsm instance now answers to this FsmKey - a scene reload rebound the tab to
            // a freshly-Awoken FSM (FsmTabManager.RebindAfterRefresh). The old instance's own
            // subscription would otherwise sit there forever, since nothing else ever tears it down
            // once its owning scene is gone.
#if NET35 || NET472
            FsmStateChangeBridge.StateEntered -= existing.BridgeHandler;
#else
            existing.Instance.StateChanged -= existing.Handler;
#endif
            _tracked.Remove(fsmKey);
        }

        var entry = new TrackedFsm { Instance = instance };
#if NET35 || NET472
        entry.BridgeHandler = (fsm, state) =>
        {
            if (ReferenceEquals(fsm, entry.Instance) && state != null)
            {
                entry.EnteredSinceCommit.Add(state.Name);
            }
        };
        FsmStateChangeBridge.StateEntered += entry.BridgeHandler;
#else
        entry.Handler = state => entry.EnteredSinceCommit.Add(state.Name);
        instance.StateChanged += entry.Handler;
#endif
        _tracked[fsmKey] = entry;
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
            // (its tab was closed/unpinned) - nothing left to reconcile. The instance itself, being a
            // plain C# object rather than a UnityEngine.Object, is left for the GC once this entry (the
            // last thing holding a reference to it) is dropped.
            if (entry.Instance.FsmComponent == null || !_visibleThisFrame.Contains(fsmKey))
            {
#if NET35 || NET472
                FsmStateChangeBridge.StateEntered -= entry.BridgeHandler;
#else
                entry.Instance.StateChanged -= entry.Handler;
#endif
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

    // Unsubscribes every live hook - called from FsmGraphOverlay.Shutdown() (itself called from
    // FsmMasterPlugin.OnDestroy), so a ScriptEngine reload never leaves this Awake's handler closures
    // still subscribed to a live Fsm's StateChanged field alongside whatever the next Awake's own
    // instance subscribes afresh, which would otherwise leave two independent trackers' handlers both
    // firing on every future state change.
    public void UnsubscribeAll()
    {
        foreach (TrackedFsm entry in _tracked.Values)
        {
#if NET35 || NET472
            FsmStateChangeBridge.StateEntered -= entry.BridgeHandler;
#else
            entry.Instance.StateChanged -= entry.Handler;
#endif
        }

        _tracked.Clear();
        _visibleThisFrame.Clear();
    }
}
