// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DebugMod.SaveStates;
using UnityEngine;

namespace FsmMaster;

// Optional integration with DebugMod's savestate system. FsmMasterPlugin declares
// DebugMod.DebugMod.Id as a BepInDependency SoftDependency purely for load ordering - it does not make
// DebugMod required to run FsmMaster. Every call into DebugMod from this class only ever runs once
// DebugModCompatFactory's Chainloader.PluginInfos check has already confirmed DebugMod is installed -
// see that class for why the check itself has to live outside this one. FsmMasterPlugin only ever holds
// this behind the DebugMod-agnostic IDebugModCompat interface, never as a DebugModCompat-typed field.
internal sealed class DebugModCompat : IDebugModCompat
{
    private const string ActiveEditsCustomDataKey = "FsmMaster.ActiveEdits";

    // How long PollPendingReload will keep waiting on GameManager.isLoading before giving up and
    // reapplying anyway - purely a safety net against an indefinite poll if isLoading somehow never
    // clears; see HandleAfterLoad's own comment for why this wait exists at all.
    private const float PendingReloadTimeoutSeconds = 5f;

    private readonly FsmEditManager _editManager;
    private readonly Action _rescanLiveFsms;
    private readonly ManualLogSource _logger;

    // Untyped, not SaveState - a SaveState-typed field here is enough on its own to make
    // Assembly.GetTypes() throw for the whole FsmMaster.dll whenever DebugMod isn't installed, since
    // Mono resolves every field's type to compute a class's layout. That's not just a concern for our own
    // code (see DebugModCompatFactory's own comment for why Awake is protected from it) - Silksong's own
    // TeamCherry.NestedFadeGroup.OnEnable calls Assembly.GetTypes() over every loaded assembly during
    // menu transitions with no tolerance for a partially-unloadable one, which crashes there on a cold
    // launch (before ScriptEngine hot-reload would have re-loaded this assembly after that code already
    // ran once). Cast back to SaveState at the two points below that actually use it.
    private object? _pendingReloadState;
    private float _pendingReloadDeadline;

    // Internal, not private - only ever called from DebugModCompatFactory.CreateAndHook, in a separate
    // class, after that factory's own Chainloader check has already confirmed DebugMod is installed.
    internal DebugModCompat(FsmEditManager editManager, Action rescanLiveFsms, ManualLogSource logger)
    {
        _editManager = editManager;
        _rescanLiveFsms = rescanLiveFsms;
        _logger = logger;
    }

    internal void Hook()
    {
        SaveState.OnSave += HandleSave;
        SaveState.BeforeLoad += HandlePrimeBeforeLoad;
        SaveState.AfterLoad += HandleAfterLoad;

        DebugMod.DebugMod.Log("FsmMaster detected DebugMod - hooking savestate save/load to persist active FSM edits.");
    }

    // Unsubscribes whatever Hook subscribed above, so a ScriptEngine reload doesn't leave DebugMod's
    // static events double-subscribed to a stale instance of this class.
    public void Unhook()
    {
        SaveState.OnSave -= HandleSave;
        SaveState.BeforeLoad -= HandlePrimeBeforeLoad;
        SaveState.AfterLoad -= HandleAfterLoad;
    }

    // Stashes every FSM edit currently in effect into this savestate's own custom data dictionary,
    // the same mechanism any DebugMod-integrated mod uses to persist its own state into a savestate.
    private void HandleSave(SaveState state)
    {
        try
        {
            var activeEdits = new List<FsmEditSet>();
            foreach (string fsmKey in _editManager.GetEditedFsmKeys())
            {
                // GetEditedFsmKeys returns every FsmKey edited at any point this session, including ones
                // for objects in rooms the player has since left - a tab stays open (and its edit set
                // stays tracked) even once its FSM is no longer live. Without this check, saving in one
                // room would also bundle in edits that belong to a completely different room's objects,
                // which then tries and fails to reapply itself against whatever's actually present on load.
                if (_editManager.GetLiveInstances(fsmKey).Count == 0)
                {
                    continue;
                }

                if (_editManager.GetActiveEditSet(fsmKey) is { } editSet)
                {
                    activeEdits.Add(editSet);
                }
            }

            if (activeEdits.Count == 0)
            {
                return;
            }

            state.data.customData[ActiveEditsCustomDataKey] = FsmSaveDataStore.SerializeEditSets(activeEdits);
            DebugMod.DebugMod.Log($"FsmMaster saved {activeEdits.Count} active FSM edit set(s) into this savestate.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[FsmMaster] Failed to save FSM edits into DebugMod savestate: {ex.Message}");
        }
    }

    // Fires at the very start of SaveState.Load, before DebugMod calls GameManager.BeginSceneTransition -
    // the point where Silksong actually destroys and rebuilds the target room's GameObjects (confirmed
    // against SaveStates/SaveState.cs: BeforeLoad?.Invoke fires immediately inside LoadImpl, well before
    // its own BeginSceneTransition call further down the same coroutine). Priming here, rather than
    // waiting for AfterLoad, closes the race where a freshly rebuilt FSM's Fsm.Preprocess() - and
    // therefore FsmActivatedPatch's Postfix - fires before FsmMaster has recorded which edits this load
    // is supposed to bring back, so the instance runs its own first OnEnter/Update unedited. No live FSM
    // exists yet to apply anything to, so this only updates FsmEditManager's own bookkeeping of which edit
    // set counts as "active" per key - see FsmEditManager.PrimeActiveEditSet's own comment.
    private void HandlePrimeBeforeLoad(SaveState state)
    {
        try
        {
            if (!state.data.customData.TryGetValue(ActiveEditsCustomDataKey, out string json) || string.IsNullOrEmpty(json))
            {
                return;
            }

            foreach (FsmEditSet editSet in FsmSaveDataStore.DeserializeEditSets(json))
            {
                _editManager.PrimeActiveEditSet(editSet);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[FsmMaster] Failed to prime FSM edits ahead of DebugMod savestate load: {ex.Message}");
        }
    }

    // Fires once DebugMod has finished its own scene-transition and post-load fixups. FsmMasterPlugin's
    // own SceneManager.sceneLoaded handler has usually already re-scanned and registered the newly
    // loaded scene's live FSMs by this point, but not always: when a savestate's target room is the room
    // already active, DebugMod's loader doesn't reliably tear the scene down and reload it through Unity's
    // own SceneManager, so the sceneLoaded event - and therefore that handler - may never fire for this
    // load. Forcing the same rescan here first keeps ReapplyPersistedEdits below from working off a stale
    // or missing live instance regardless of which case this load turned out to be.
    private void HandleAfterLoad(SaveState state)
    {
        ReapplyPersistedEdits(state);

        // Silksong still fully destroys and recreates the room's GameObjects for a same-room load (its
        // own engine log calls this out explicitly - "Destroying GuidComponents to prevent collisions"),
        // it just doesn't go through Unity's SceneManager to do it, which is why AfterLoad above can fire
        // (and the rescan above can run) before that reconstruction has actually finished: DebugMod's own
        // wait for scene-name-change is a no-op for a same-room load since the active scene's name never
        // changes, so nothing upstream of AfterLoad forces a real wait for it here. A component caught
        // mid-reconstruction can briefly report a stale FsmName, sending the reapply above to the wrong
        // live instance under the right FsmKey. GameManager.isLoading is the exact condition DebugMod's
        // own SaveState.LoadImpl waits on immediately after firing AfterLoad before considering its own
        // load complete, so once that clears, re-running the same reapply corrects anything the immediate
        // pass above caught mid-flight - and is a harmless no-op for the more common case where the
        // immediate pass already landed correctly.
        //
        // This poll is only about _liveInstances/_pristine bookkeeping staying accurate (and the graph/UI
        // panels reflecting it) - it is not what makes a freshly rebuilt FSM come back with the right
        // values. That part is already settled by the time this method runs: HandlePrimeBeforeLoad primed
        // FsmEditManager's active edit set for every affected key before Silksong tore the room down, so
        // each new instance's own Fsm.Preprocess() (FsmActivatedPatch's Postfix) applied the correct edits
        // the moment it was constructed, without waiting on GameManager.isLoading at all.
        //
        // UnsafeInstance, not instance - the latter falls back to a FindObjectOfType search and logs
        // "Couldn't find a Game Manager" whenever it comes up empty, which a plain null-check like this
        // one would trigger on every call while none exists yet. UnsafeInstance just returns the cached
        // singleton field with no search and no log, which is all a null-check here ever needed - see
        // DebugMod's own TimeScale.cs (agent-context/Silksong.DebugMod-main/MonoBehaviours/TimeScale.cs)
        // for the same GameManager.UnsafeInstance != null pattern.
        if (GameManager.UnsafeInstance != null && GameManager.UnsafeInstance.isLoading)
        {
            _pendingReloadState = state;
            _pendingReloadDeadline = Time.realtimeSinceStartup + PendingReloadTimeoutSeconds;
        }
    }

    // Called every frame from FsmMasterPlugin.Update() - see HandleAfterLoad's own comment for why a
    // same-room savestate load needs this deferred second pass once Silksong's own scene reconstruction
    // has actually finished, rather than trusting the state HandleAfterLoad's immediate rescan captured.
    public void PollPendingReload()
    {
        if (_pendingReloadState == null)
        {
            return;
        }

        bool stillLoading = GameManager.UnsafeInstance != null && GameManager.UnsafeInstance.isLoading;
        if (stillLoading && Time.realtimeSinceStartup < _pendingReloadDeadline)
        {
            return;
        }

        if (stillLoading)
        {
            _logger.LogWarning("[FsmMaster] GameManager.isLoading never cleared after a savestate load; reapplying anyway.");
        }

        var state = (SaveState)_pendingReloadState;
        _pendingReloadState = null;
        ReapplyPersistedEdits(state);
    }

    // Rescans live FSMs and reapplies whatever this savestate's own custom data (plus any edit still
    // active for a key that data didn't cover - see the loop below's own comment) says should be in
    // effect. Called once immediately from HandleAfterLoad and, for a same-room load, a second time from
    // PollPendingReload once Silksong's own scene reconstruction has actually settled.
    private void ReapplyPersistedEdits(SaveState state)
    {
        try
        {
            _rescanLiveFsms();

            var restoredKeys = new HashSet<string>();
            if (state.data.customData.TryGetValue(ActiveEditsCustomDataKey, out string json) && !string.IsNullOrEmpty(json))
            {
                List<FsmEditSet> editSets = FsmSaveDataStore.DeserializeEditSets(json);
                foreach (FsmEditSet editSet in editSets)
                {
                    _editManager.ApplyEditSet(editSet);
                    restoredKeys.Add(editSet.FsmKey);
                }

                DebugMod.DebugMod.Log($"FsmMaster restored {editSets.Count} FSM edit set(s) from this savestate.");
            }

            // A same-room load can respawn a live FSM instance (see _rescanLiveFsms's own comment)
            // without going through the restore loop above, e.g. when the target savestate was captured
            // before an edit (such as an installed sequencer) was made this session. Left alone, that
            // edit would stay recorded as active in _editManager while only ever having been physically
            // installed on the pre-load instance PruneStaleSnapshotEntries just dropped as dead - so
            // reapply every edit set still active for a key this savestate's own data didn't already
            // cover, the same convergence call PollPendingActivations uses for a late-activating instance.
            foreach (string fsmKey in new List<string>(_editManager.GetEditedFsmKeys()))
            {
                if (restoredKeys.Contains(fsmKey))
                {
                    continue;
                }

                if (_editManager.GetActiveEditSet(fsmKey) is { } editSet)
                {
                    _editManager.ApplyEditSet(editSet);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[FsmMaster] Failed to restore FSM edits from DebugMod savestate: {ex.Message}");
        }
    }
}
