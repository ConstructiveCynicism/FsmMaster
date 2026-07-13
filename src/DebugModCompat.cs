using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using DebugMod.SaveStates;

namespace FsmMaster;

// Optional integration with DebugMod's savestate system. FsmMasterPlugin declares
// DebugMod.DebugMod.Id as a BepInDependency SoftDependency purely for load ordering - it does not make
// DebugMod required to run FsmMaster. This class is what actually keeps the dependency soft: every call
// into DebugMod is gated behind a Chainloader.PluginInfos check before TryCreate ever runs Hook, so the
// assembly can be compiled against but never touched unless DebugMod is actually installed.
internal sealed class DebugModCompat
{
    private const string ActiveEditsCustomDataKey = "FsmMaster.ActiveEdits";

    private readonly FsmEditManager _editManager;
    private readonly ManualLogSource _logger;

    private DebugModCompat(FsmEditManager editManager, ManualLogSource logger)
    {
        _editManager = editManager;
        _logger = logger;
    }

    // Returns null whenever DebugMod isn't installed - the caller then holds no reference to this class
    // at all, so OnDestroy has nothing to unhook.
    public static DebugModCompat? TryCreate(FsmEditManager editManager, ManualLogSource logger)
    {
        if (!Chainloader.PluginInfos.ContainsKey(DebugMod.DebugMod.Id))
        {
            return null;
        }

        var compat = new DebugModCompat(editManager, logger);
        compat.Hook();
        return compat;
    }

    private void Hook()
    {
        SaveState.OnSave += HandleSave;
        SaveState.AfterLoad += HandleAfterLoad;

        DebugMod.DebugMod.Log("FsmMaster detected DebugMod - hooking savestate save/load to persist active FSM edits.");
    }

    // Unsubscribes whatever Hook subscribed above, so a ScriptEngine reload doesn't leave DebugMod's
    // static events double-subscribed to a stale instance of this class.
    public void Unhook()
    {
        SaveState.OnSave -= HandleSave;
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

    // Fires only once DebugMod has finished its own scene-transition and post-load fixups - by this
    // point FsmMasterPlugin's own SceneManager.sceneLoaded handler has already re-scanned and registered
    // the newly loaded scene's live FSMs (FsmMasterPlugin.ApplyPersistedEditsForScene), so ApplyEditSet
    // below has live instances to apply to.
    private void HandleAfterLoad(SaveState state)
    {
        try
        {
            if (!state.data.customData.TryGetValue(ActiveEditsCustomDataKey, out string json) || string.IsNullOrEmpty(json))
            {
                return;
            }

            List<FsmEditSet> editSets = FsmSaveDataStore.DeserializeEditSets(json);
            foreach (FsmEditSet editSet in editSets)
            {
                _editManager.ApplyEditSet(editSet);
            }

            DebugMod.DebugMod.Log($"FsmMaster restored {editSets.Count} FSM edit set(s) from this savestate.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[FsmMaster] Failed to restore FSM edits from DebugMod savestate: {ex.Message}");
        }
    }
}
