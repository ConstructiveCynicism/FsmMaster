// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace FsmMaster;

// DebugMod's SaveState/SaveStateManager types on this loader generation (confirmed against
// HollowKnight.DebugMod-1.4.10.2's own Source/SaveStates/SaveState.cs and SaveStateManager.cs) are the
// same shape as the old Modding API's version this compat class was first written against: plain JSON
// (de)serialization of a fixed data struct, no OnSave/BeforeLoad/AfterLoad events or custom-data
// dictionary of their own. Persisting active FSM edits across a savestate load is done entirely by
// detouring four of SaveState's own public methods, resolved reflectively - both SaveState and
// SaveStateManager are declared `internal class`, so this class never references either type by name
// anywhere. This loader generation has no HarmonyX, so the detour itself uses MonoMod.RuntimeDetour's
// low-level Hook API directly (unlike the hk1221 loader's Harmony-based version, and unlike
// FsmActivationPatches' On.*-hooks-via-HookGen, since HookGen only covers the game/PlayMaker
// assemblies MMHOOK_*.dll was generated against - DebugMod.dll has no such generated wrapper). Every
// public entry point here is a harmless no-op if DebugMod isn't installed, or if its API surface
// doesn't match what this class expects.
//
// agent-context: the four target method signatures (SaveStateToFile(int), LoadStateFromFile(int),
// SaveTempState(), LoadTempState(bool loadDuped = false)) and the loadingSavestate/path member shapes
// were confirmed by reading .claude/agent-context/hk1578/HollowKnight.DebugMod-1.4.10.2/Source/SaveStates/
// SaveState.cs and SaveStateManager.cs directly, not guessed - the one delta from hk1221's version is
// that loadingSavestate is a property here (get; private set;), not a plain field, and LoadTempState
// takes a bool parameter. The MonoMod.RuntimeDetour.Hook delegate signatures below (using `object` for
// SaveState's own instance parameter, since the real type is inaccessible from this assembly) compile
// against the real DLL but have not been exercised in a live game session - if savestate integration
// doesn't fire in practice, start by confirming TryHook actually reaches the end of its try blocks
// without logging a warning (MonoMod.RuntimeDetour.Hook throws at construction time if a delegate
// signature is incompatible with its target method, so a clean TryHook run at least proves the four
// signatures line up structurally with the real DLL).
//
// A savestate load fully replaces this scene's FSM edits with exactly what the savestate captured,
// rather than just layering the saved edits on top of whatever's currently active - any edit made
// after the save (and not part of it) gets reset back to pristine. See ApplySavestateSnapshot's own
// comment (FsmMasterDriver.cs) for the mechanics.
internal static class DebugModSavestateCompat
{
    private const string SidecarSuffix = ".fsmmaster.json";

    // Mirrors hk1221's own SecondRescanDelaySeconds - LoadStateCoro's own tail (the blue-health restore
    // animation, RoomSpecific.DoRoomSpecific) keeps running for a bit after loadingSavestate flips back
    // to false partway through, not at the very end - a rescan fired the instant the transition is
    // observed can land before every object that same load is still settling has actually finished
    // being positioned/(re)activated.
    private const float SecondRescanDelaySeconds = 1f;

    private static FieldInfo? _pathField;
    private static PropertyInfo? _loadingSavestateProperty;
    private static bool _hooked;
    private static bool _wasLoadingSavestate;
    private static float? _pendingSecondRescanAt;

    private static List<FsmEditSet>? _snapshotForRescan;

    private static object? _pendingFileLoadInstance;
    private static List<FsmEditSet>? _pendingFileLoadEditSets;

    private static List<FsmEditSet>? _quickslotStash;

    // Delegate shapes matching DebugMod.SaveState's own public instance methods - `object` stands in
    // for the inaccessible SaveState type itself (a reference type, so this widening is safe for
    // MonoMod.RuntimeDetour's IL-level trampoline). Each Orig* delegate is the original method's own
    // signature (self first, like an open-instance delegate); each Hook* delegate prepends the Orig*
    // trampoline, mirroring HookGen's own orig/self/args convention - MonoMod.RuntimeDetour.Hook's
    // constructor requires this exact shape to detour rather than fully replace the target method.
    private delegate void OrigSaveStateToFile(object self, int paramSlot);
    private delegate void HookSaveStateToFile(OrigSaveStateToFile orig, object self, int paramSlot);

    private delegate void OrigLoadStateFromFile(object self, int paramSlot);
    private delegate void HookLoadStateFromFile(OrigLoadStateFromFile orig, object self, int paramSlot);

    private delegate void OrigSaveTempState(object self);
    private delegate void HookSaveTempState(OrigSaveTempState orig, object self);

    private delegate void OrigLoadTempState(object self, bool loadDuped);
    private delegate void HookLoadTempState(OrigLoadTempState orig, object self, bool loadDuped);

    private static Hook? _saveStateToFileHook;
    private static Hook? _loadStateFromFileHook;
    private static Hook? _saveTempStateHook;
    private static Hook? _loadTempStateHook;

    internal static void TryHook()
    {
        // ModHooks.GetMod resolves against ModLoader.ModInstanceNameMap, which this loader generation
        // populates for every loaded mod regardless of whether it declared FsmMaster as a dependency -
        // this runs unconditionally from Initialize() whether or not DebugMod is even installed, and
        // GetMod returns null rather than throwing when it isn't. hk1221's own ModHooks/ModLoader have
        // neither GetMod nor a name-map dictionary, so that loader still scans
        // AppDomain.CurrentDomain.GetAssemblies() by name instead - see its own DebugModSavestateCompat.cs.
        IMod? debugMod = ModHooks.GetMod("DebugMod");
        if (debugMod == null)
        {
            return;
        }

        Assembly debugModAssembly = debugMod.GetType().Assembly;

        Type? saveStateType = debugModAssembly.GetType("DebugMod.SaveState");
        Type? saveStateManagerType = debugModAssembly.GetType("DebugMod.SaveStateManager");
        if (saveStateType == null || saveStateManagerType == null)
        {
            FsmMasterMod.Instance?.LogWarn("DebugMod detected but its SaveState/SaveStateManager types could not be resolved; savestate integration disabled.");
            return;
        }

        _pathField = saveStateManagerType.GetField("path", BindingFlags.Public | BindingFlags.Static);
        _loadingSavestateProperty = saveStateType.GetProperty("loadingSavestate", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? saveToFile = saveStateType.GetMethod("SaveStateToFile", new[] { typeof(int) });
        MethodInfo? loadFromFile = saveStateType.GetMethod("LoadStateFromFile", new[] { typeof(int) });
        MethodInfo? saveTempState = saveStateType.GetMethod("SaveTempState", Type.EmptyTypes);
        MethodInfo? loadTempState = saveStateType.GetMethod("LoadTempState", new[] { typeof(bool) });

        if (_pathField == null || _loadingSavestateProperty == null || saveToFile == null || loadFromFile == null || saveTempState == null || loadTempState == null)
        {
            FsmMasterMod.Instance?.LogWarn("DebugMod detected but its savestate API surface didn't match what FsmMaster expects; savestate integration disabled.");
            return;
        }

        try
        {
            _saveStateToFileHook = new Hook(saveToFile, new HookSaveStateToFile((orig, self, paramSlot) =>
            {
                orig(self, paramSlot);
                PostfixSaveStateToFile(paramSlot);
            }));
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogWarn($"Failed to hook DebugMod.SaveState.SaveStateToFile: {ex.Message}");
        }

        _hooked = _saveStateToFileHook != null;
        if (!_hooked)
        {
            return;
        }

        try
        {
            _loadStateFromFileHook = new Hook(loadFromFile, new HookLoadStateFromFile((orig, self, paramSlot) =>
            {
                orig(self, paramSlot);
                PostfixLoadStateFromFile(self, paramSlot);
            }));
            _saveTempStateHook = new Hook(saveTempState, new HookSaveTempState((orig, self) =>
            {
                orig(self);
                PostfixSaveTempState();
            }));
            _loadTempStateHook = new Hook(loadTempState, new HookLoadTempState((orig, self, loadDuped) =>
            {
                orig(self, loadDuped);
                PostfixLoadTempState(self);
            }));
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogWarn($"Failed to hook DebugMod.SaveState's remaining savestate methods: {ex.Message}");
        }

        FsmMasterMod.Instance?.Log("FsmMaster detected DebugMod - hooking savestate save/load to persist active FSM edits.");
    }

    // Called once per frame from FsmMasterDriver.Update(). SaveState.loadingSavestate is DebugMod's own
    // "a savestate load coroutine is actively running" flag - the point every load path (file-slot,
    // SkipOne, quickslot) actually finishes settling.
    internal static void PollLoadingTransition()
    {
        if (!_hooked || _loadingSavestateProperty == null)
        {
            return;
        }

        // Explicit index argument: the single-argument GetValue overload only exists from .NET 4.5 on,
        // and hk1432 compiles against the 3.5 class library.
        bool isLoading = _loadingSavestateProperty.GetValue(null, null) is true;
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

    private static void PostfixSaveStateToFile(int paramSlot)
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
            File.WriteAllText(directory + "savestate" + paramSlot + SidecarSuffix, FsmSaveDataStore.SerializeEditSets(activeEdits));
            if (activeEdits.Count > 0)
            {
                FsmMasterMod.Instance?.Log($"FsmMaster saved {activeEdits.Count} active FSM edit set(s) into savestate slot {paramSlot}.");
            }
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogWarn($"Failed to save FSM edits into DebugMod savestate slot {paramSlot}: {ex.Message}");
        }
    }

    // Fires far more often than a real "the player chose to load this slot" action - DebugMod's own
    // SaveStateManager.RefreshStateMenu calls LoadStateFromFile on every file in the current page just
    // to read display metadata. Stashing the result here rather than acting on it directly avoids
    // reapplying an unrelated slot's edits from a menu browse - PostfixLoadTempState only ever consumes
    // this if it fires on the exact same SaveState instance, which only happens for a real load.
    private static void PostfixLoadStateFromFile(object instance, int paramSlot)
    {
        try
        {
            string? directory = _pathField?.GetValue(null) as string;
            string? path = string.IsNullOrEmpty(directory) ? null : directory + "savestate" + paramSlot + SidecarSuffix;

            _pendingFileLoadInstance = instance;
            _pendingFileLoadEditSets = path != null && File.Exists(path)
                ? FsmSaveDataStore.DeserializeEditSets(File.ReadAllText(path))
                : new List<FsmEditSet>();
        }
        catch (Exception ex)
        {
            _pendingFileLoadInstance = null;
            _pendingFileLoadEditSets = null;
            FsmMasterMod.Instance?.LogWarn($"Failed to read FSM edits from DebugMod savestate slot {paramSlot}: {ex.Message}");
        }
    }

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
            FsmMasterMod.Instance?.LogWarn($"Failed to stash FSM edits into DebugMod quickslot: {ex.Message}");
        }
    }

    private static void PostfixLoadTempState(object instance)
    {
        if (ReferenceEquals(instance, _pendingFileLoadInstance))
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
