// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FsmMaster;

// DebugMod-1.2.2.1 has no OnSave/BeforeLoad/AfterLoad events or per-savestate custom-data dictionary the
// way Silksong's DebugMod does - its own SaveState.cs/SaveStateManager.cs are plain JSON (de)serialization
// of a fixed data struct, nothing else. Persisting active FSM edits across a savestate load on this branch
// is done entirely by postfixing four of SaveState's own public methods, resolved reflectively. SaveState
// and SaveStateManager are both declared `internal class`, so this class never references either type by
// name anywhere - every member it touches is public on the type itself, which reflection can still resolve
// and Harmony can still patch even though C# source in a different assembly can't write
// `typeof(DebugMod.SaveState)`. Every public entry point here is a harmless no-op if DebugMod isn't
// installed, or if its API surface doesn't match what this class expects.
//
// A savestate load fully replaces this scene's FSM edits with exactly what the savestate captured, rather
// than just layering the saved edits on top of whatever's currently active - any edit made after the save
// (and not part of it) gets reset back to pristine. See ApplySnapshot's own comment for the mechanics.
internal static class DebugModSavestateCompat
{
    private const string SidecarSuffix = ".fsmmaster.json";

    // LoadStateCoro's own tail (the blue-health restore animation, RoomSpecific.DoRoomSpecific) keeps
    // running for a bit after it sets loadingSavestate back to false partway through, not at the very
    // end - a rescan fired the instant the transition is observed can land before every object that same
    // load is still settling has actually finished being positioned/(re)activated. Mirrors the two-phase
    // immediate-rescan-plus-deferred-follow-up pattern the Silksong branch's own DebugMod integration used
    // for the identical "same-room load's own object reconstruction isn't guaranteed done yet" problem.
    private const float SecondRescanDelaySeconds = 1f;

    private static FieldInfo? _pathField;
    private static FieldInfo? _loadingSavestateField;
    private static bool _hooked;
    private static bool _wasLoadingSavestate;
    private static float? _pendingSecondRescanAt;

    // Captured once when a load's own true->false transition is first observed, then reused for the
    // deferred second pass - both passes must apply the exact same snapshot, since a stray edit the player
    // makes in the ~1s window between them would otherwise get silently reset by the deferred pass if it
    // re-read a live field instead of a value frozen at the moment the load was detected.
    private static List<FsmEditSet>? _snapshotForRescan;

    // Bridges a LoadStateFromFile postfix to the LoadTempState postfix that follows it on the same
    // SaveState instance - see PostfixLoadTempState's own comment for why instance identity, not slot
    // number, is what makes this safe against DebugMod's own menu-refresh calls.
    private static object? _pendingFileLoadInstance;
    private static List<FsmEditSet>? _pendingFileLoadEditSets;

    // What the next LoadTempState postfix should apply - set by PostfixLoadStateFromFile/PostfixSaveTempState
    // (indirectly, via _quickslotStash) and consumed by PostfixLoadTempState, which is the one point every
    // load path actually funnels through (see that method's own comment).
    private static List<FsmEditSet>? _quickslotStash;

    internal static void TryHook(Harmony harmony)
    {
        Assembly? debugModAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "DebugMod");
        if (debugModAssembly == null)
        {
            return;
        }

        Type? saveStateType = debugModAssembly.GetType("DebugMod.SaveState");
        Type? saveStateManagerType = debugModAssembly.GetType("DebugMod.SaveStateManager");
        if (saveStateType == null || saveStateManagerType == null)
        {
            FsmMaster.Instance?.LogWarn("DebugMod detected but its SaveState/SaveStateManager types could not be resolved; savestate integration disabled.");
            return;
        }

        _pathField = saveStateManagerType.GetField("path", BindingFlags.Public | BindingFlags.Static);
        _loadingSavestateField = saveStateType.GetField("loadingSavestate", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? saveToFile = saveStateType.GetMethod("SaveStateToFile", new[] { typeof(int) });
        MethodInfo? loadFromFile = saveStateType.GetMethod("LoadStateFromFile", new[] { typeof(int) });
        MethodInfo? saveTempState = saveStateType.GetMethod("SaveTempState", Type.EmptyTypes);
        MethodInfo? loadTempState = saveStateType.GetMethod("LoadTempState", Type.EmptyTypes);

        if (_pathField == null || _loadingSavestateField == null || saveToFile == null || loadFromFile == null || saveTempState == null || loadTempState == null)
        {
            FsmMaster.Instance?.LogWarn("DebugMod detected but its savestate API surface didn't match what FsmMaster expects; savestate integration disabled.");
            return;
        }

        harmony.Patch(saveToFile, postfix: new HarmonyMethod(typeof(DebugModSavestateCompat), nameof(PostfixSaveStateToFile)));
        harmony.Patch(loadFromFile, postfix: new HarmonyMethod(typeof(DebugModSavestateCompat), nameof(PostfixLoadStateFromFile)));
        harmony.Patch(saveTempState, postfix: new HarmonyMethod(typeof(DebugModSavestateCompat), nameof(PostfixSaveTempState)));
        harmony.Patch(loadTempState, postfix: new HarmonyMethod(typeof(DebugModSavestateCompat), nameof(PostfixLoadTempState)));

        _hooked = true;
        FsmMaster.Instance?.Log("FsmMaster detected DebugMod - hooking savestate save/load to persist active FSM edits.");
    }

    // Called once per frame from FsmMasterDriver.Update(). SaveState.loadingSavestate is DebugMod's own
    // "a savestate load coroutine is actively running" flag (set true at the very start of its scene-
    // rebuilding coroutine, cleared once that coroutine has finished restoring player/scene state) - the
    // point every load path (file-slot, SkipOne, quickslot) actually finishes settling, mirrored here the
    // same way today's DebugModCompat.PollPendingReload polled GameManager.isLoading on the Silksong branch.
    internal static void PollLoadingTransition()
    {
        if (!_hooked || _loadingSavestateField == null)
        {
            return;
        }

        bool isLoading = _loadingSavestateField.GetValue(null) is true;
        if (_wasLoadingSavestate && !isLoading)
        {
            _snapshotForRescan = _quickslotStash;
            FsmMasterDriver.Instance?.ApplySavestateSnapshot(_snapshotForRescan);
            _pendingSecondRescanAt = Time.unscaledTime + SecondRescanDelaySeconds;
        }

        _wasLoadingSavestate = isLoading;

        if (_pendingSecondRescanAt is { } dueAt && Time.unscaledTime >= dueAt)
        {
            _pendingSecondRescanAt = null;
            FsmMasterDriver.Instance?.ApplySavestateSnapshot(_snapshotForRescan);
            _snapshotForRescan = null;
        }
    }

    private static void PostfixSaveStateToFile(int __0)
    {
        try
        {
            FsmEditManager? editManager = FsmMasterDriver.Instance?.EditManager;
            if (editManager == null)
            {
                return;
            }

            List<FsmEditSet> activeEdits = CollectActiveEdits(editManager);
            string? directory = _pathField?.GetValue(null) as string;
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            // Written even when empty (rather than skipped) - a slot re-saved after every FsmMaster edit
            // was reverted needs its own sidecar overwritten to reflect that, or a later load of this same
            // slot would apply a stale, non-empty edit set left over from whatever was saved into it before.
            File.WriteAllText(directory + "savestate" + __0 + SidecarSuffix, FsmSaveDataStore.SerializeEditSets(activeEdits));
            if (activeEdits.Count > 0)
            {
                FsmMaster.Instance?.Log($"FsmMaster saved {activeEdits.Count} active FSM edit set(s) into savestate slot {__0}.");
            }
        }
        catch (Exception ex)
        {
            FsmMaster.Instance?.LogWarn($"Failed to save FSM edits into DebugMod savestate slot {__0}: {ex.Message}");
        }
    }

    // Fires far more often than a real "the player chose to load this slot" action - DebugMod's own
    // SaveStateManager.RefreshStateMenu calls LoadStateFromFile on every file in the current page just to
    // read display metadata, e.g. every time the savestate panel opens or changes page. Stashing the
    // result here rather than acting on it directly avoids reapplying an unrelated slot's edits from a menu
    // browse - PostfixLoadTempState only ever consumes this if it fires on the exact same SaveState
    // instance, which only happens for a real load (see that method's own comment).
    private static void PostfixLoadStateFromFile(object __instance, int __0)
    {
        try
        {
            string? directory = _pathField?.GetValue(null) as string;
            string? path = string.IsNullOrEmpty(directory) ? null : directory + "savestate" + __0 + SidecarSuffix;

            _pendingFileLoadInstance = __instance;
            _pendingFileLoadEditSets = path != null && File.Exists(path)
                ? FsmSaveDataStore.DeserializeEditSets(File.ReadAllText(path))
                : new List<FsmEditSet>();
        }
        catch (Exception ex)
        {
            _pendingFileLoadInstance = null;
            _pendingFileLoadEditSets = null;
            FsmMaster.Instance?.LogWarn($"Failed to read FSM edits from DebugMod savestate slot {__0}: {ex.Message}");
        }
    }

    // FsmEditSet.VariableOverrides/ActionFieldOverrides/DisabledStates/etc. are the same live List<T>
    // instances GetActiveEditSet hands back from FsmEditManager's own _activeEdits - stashing that
    // reference directly would leave this "snapshot" pointing at an object a later edit (e.g. re-enabling
    // a state) could still mutate in place before the quickslot is ever loaded, silently corrupting a
    // save that already happened. Round-tripping through the same JSON (de)serialization the file-slot
    // path already gets for free from writing to and reading back from disk produces a genuinely
    // independent copy instead.
    private static void PostfixSaveTempState()
    {
        try
        {
            FsmEditManager? editManager = FsmMasterDriver.Instance?.EditManager;
            if (editManager == null)
            {
                return;
            }

            List<FsmEditSet> activeEdits = CollectActiveEdits(editManager);
            _quickslotStash = FsmSaveDataStore.DeserializeEditSets(FsmSaveDataStore.SerializeEditSets(activeEdits));
        }
        catch (Exception ex)
        {
            FsmMaster.Instance?.LogWarn($"Failed to stash FSM edits into DebugMod quickslot: {ex.Message}");
        }
    }

    // The one point every load path (Memory/quickslot, SkipOne "load new state from file", and the second
    // half of the two-step "load file into quickslot, then load quickslot" flow) actually funnels through
    // to start DebugMod's own scene-rebuilding coroutine - see this class's own top comment for why the
    // actual reapply happens later, off PollLoadingTransition, rather than here.
    //
    // __instance identity (not slot number) decides which pending data applies: SkipOne's own
    // NewLoadStateFromFile calls LoadStateFromFile then LoadTempState back-to-back on the same instance
    // with nothing else able to interleave on Unity's single main thread, so the instance check is exact
    // for that path regardless of how many unrelated menu-refresh calls happened earlier in the session. A
    // pure quickslot load (SaveStateManager.LoadNewState's Memory branch) calls LoadTempState directly on
    // quickState, which LoadStateFromFile is never called on, so it correctly falls through to the
    // in-memory stash instead. The two-step File-type flow (SaveStateManager.LoadCoroHelper's File branch)
    // deep-copies the loaded slot's data onto quickState.data without ever calling LoadTempState itself -
    // so loading a file into the quickslot and only later loading the quickslot from a separate action is
    // a known, accepted gap this doesn't cover: there's no safe instance-identity bridge across two
    // separate user actions on two different SaveState instances.
    private static void PostfixLoadTempState(object __instance)
    {
        if (ReferenceEquals(__instance, _pendingFileLoadInstance))
        {
            _quickslotStash = _pendingFileLoadEditSets;
        }

        _pendingFileLoadInstance = null;
        _pendingFileLoadEditSets = null;
    }

    private static List<FsmEditSet> CollectActiveEdits(FsmEditManager editManager)
    {
        var activeEdits = new List<FsmEditSet>();
        foreach (string fsmKey in editManager.GetEditedFsmKeys())
        {
            // GetEditedFsmKeys returns every FsmKey edited at any point this session, including ones for
            // objects in rooms the player has since left - only bundle in edits whose FSM is actually
            // live right now, matching FsmMasterDriver.ApplyPersistedEditsForScene's own filtering.
            if (editManager.GetLiveInstances(fsmKey).Count == 0)
            {
                continue;
            }

            if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
            {
                activeEdits.Add(editSet);
            }
        }

        return activeEdits;
    }
}
