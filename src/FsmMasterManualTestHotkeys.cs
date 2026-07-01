using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using Silksong.FsmUtil;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FsmMaster;

// Temporary manual-verification hotkeys for the generic edit engine in FsmEditManager - not a permanent
// feature, and not part of the read/write layer itself. Hardcodes one specific object/FSM/state/variable/
// action to drive the "verify against real gameplay" step called for in CLAUDE.md's build order before this
// layer is trusted; remove this file (or at least keep it out of any commit) once that pass is done, since
// baking one enemy's FSM data permanently into the mod is exactly what CLAUDE.md's "no hardcoded FSM data"
// rule is guarding against. Every hotkey below calls the same public FsmEditManager methods any real caller
// (JSON load on scene load, a future overlay button) would use - nothing is special-cased in the engine.
public partial class FsmMasterPlugin
{
    private const string TestObjectName = "Bone Hunter Child";
    private const string TestFsmName = "Control";

    private void Update()
    {
        if (_editManager == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Alpha0))
        {
            RunTest0_SetBattlerTrue();
        }

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            RunTest1_SetReactDelays();
        }

        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            RunTest2_RetargetCampBattlePauseFinished();
        }

        if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            RunTest3_SaveAllChanges();
        }

        if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            RunTest4_ResetAllChanges();
        }

        if (Input.GetKeyDown(KeyCode.Alpha5))
        {
            RunTest5_DisableFarState();
        }

        if (Input.GetKeyDown(KeyCode.Alpha6))
        {
            RunTest6_InstallFarSequencer();
        }

        if (Input.GetKeyDown(KeyCode.Alpha7))
        {
            RunTest7_LogFsmData();
        }
    }

    // Resolves only the exact GameObject named TestObjectName (Unity's GameObject.Find is an exact-name
    // match), not every scene-duplicate sharing its FsmKey - these tests target the one specific object
    // asked for, not the general duplicate-propagation behavior FsmEditManager also supports. Re-registers
    // that single instance under fsmKey so FsmEditManager's own pristine/reset bookkeeping (which otherwise
    // has no notion of "just this one object") never touches a same-named duplicate either.
    private IReadOnlyList<Fsm> GetTestInstances(string fsmKey)
    {
        GameObject? gameObject = GameObject.Find(TestObjectName);
        PlayMakerFSM? component = gameObject != null ? gameObject.GetFsmPreprocessed(TestFsmName) : null;

        if (component == null)
        {
            Logger.LogWarning($"[FsmMaster][Test] No live instance found for exact object '{TestObjectName}' / fsm '{TestFsmName}'.");
            return Array.Empty<Fsm>();
        }

        var instances = new List<Fsm> { component.Fsm };
        _editManager!.RegisterLiveInstances(fsmKey, instances);
        return instances;
    }

    private void RunTest0_SetBattlerTrue()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        var ov = new VariableOverride { VariableType = "Bool", Name = "Battler", StringValue = "true" };
        foreach (Fsm fsm in instances)
        {
            _editManager!.ApplyVariableOverride(fsmKey, fsm, ov);
        }

        Logger.LogInfo($"[FsmMaster][Test] 0: Battler -> true ({instances.Count} instance(s))");
    }

    private void RunTest1_SetReactDelays()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        foreach (Fsm fsm in instances)
        {
            FsmState? state = fsm.GetState("Camp Idle");
            if (state == null)
            {
                Logger.LogWarning("[FsmMaster][Test] 1: state 'Camp Idle' not found.");
                continue;
            }

            int actionIndex = state.IndexFirstActionMatching(a => a.GetType().Name == "CheckHeroPerformanceRegion");
            if (actionIndex < 0)
            {
                Logger.LogWarning("[FsmMaster][Test] 1: action 'CheckHeroPerformanceRegion' not found on 'Camp Idle'.");
                continue;
            }

            _editManager!.ApplyActionFieldOverride(fsmKey, fsm, new ActionFieldOverride
            {
                StateName = "Camp Idle",
                ActionIndex = actionIndex,
                ExpectedActionTypeName = "CheckHeroPerformanceRegion",
                FieldName = "MinReactDelay",
                StringValue = "10",
            });

            _editManager!.ApplyActionFieldOverride(fsmKey, fsm, new ActionFieldOverride
            {
                StateName = "Camp Idle",
                ActionIndex = actionIndex,
                ExpectedActionTypeName = "CheckHeroPerformanceRegion",
                FieldName = "MaxReactDelay",
                StringValue = "10.1",
            });
        }

        Logger.LogInfo($"[FsmMaster][Test] 1: MinReactDelay -> 10, MaxReactDelay -> 10.1 ({instances.Count} instance(s))");
    }

    private void RunTest2_RetargetCampBattlePauseFinished()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        var retarget = new TransitionRetarget
        {
            StateName = "Camp Battle Pause",
            EventName = "FINISHED",
            NewStateName = "Camp Battle Pause", // pure retarget - no relocation
            NewToState = "Evade Antic",
        };

        foreach (Fsm fsm in instances)
        {
            _editManager!.ApplyTransitionRetarget(fsmKey, fsm, retarget);
        }

        Logger.LogInfo($"[FsmMaster][Test] 2: Camp Battle Pause / FINISHED -> Evade Antic ({instances.Count} instance(s))");
    }

    // Saves every FsmKey with an edit currently in effect this session (not just the test object) to its own
    // (scene, FsmKey) JSON file, so they reapply next time this scene loads.
    private void RunTest3_SaveAllChanges()
    {
        string sceneName = SceneManager.GetActiveScene().name;

        int savedCount = 0;
        foreach (string fsmKey in _editManager!.GetEditedFsmKeys())
        {
            FsmEditSet? active = _editManager.GetActiveEditSet(fsmKey);
            if (active == null)
            {
                continue;
            }

            string json = FsmSaveDataStore.Save(sceneName, active);
            Logger.LogInfo($"[FsmMaster][Test] 3: saved fsm key '{fsmKey}' to '{FsmSaveDataStore.GetFilePath(sceneName, fsmKey)}':\n{json}");
            savedCount++;
        }

        Logger.LogInfo($"[FsmMaster][Test] 3: saved {savedCount} fsm edit set(s) for scene '{sceneName}'.");
    }

    // Live-reverts and un-persists every FsmKey with an edit currently in effect this session, via the same
    // public FsmMasterPlugin.ResetFsm a future overlay "reset" button would call.
    private void RunTest4_ResetAllChanges()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        var fsmKeys = new List<string>(_editManager!.GetEditedFsmKeys());

        foreach (string fsmKey in fsmKeys)
        {
            ResetFsm(sceneName, fsmKey);
        }

        Logger.LogInfo($"[FsmMaster][Test] 4: reset {fsmKeys.Count} fsm(s).");
    }

    private void RunTest5_DisableFarState()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        foreach (Fsm fsm in instances)
        {
            _editManager!.DisableState(fsmKey, fsm, "Far");
        }

        Logger.LogInfo($"[FsmMaster][Test] 5: Far -> disabled ({instances.Count} instance(s))");
    }

    // "2 2 1 2 2 3" refers to 1-indexed slots in the random-event action's own candidate event list (the
    // same list FsmActionSequencer.ExtractEventCandidates already resolves), not literal event names - so
    // slot 1 means "whichever event that action's first configured slot points at", etc.
    private void RunTest6_InstallFarSequencer()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        FsmState? state = instances[0].GetState("Far");
        if (state == null)
        {
            Logger.LogWarning("[FsmMaster][Test] 6: state 'Far' not found.");
            return;
        }

        int actionIndex = FsmActionSequencer.IndexFirstRandomEventAction(state);
        if (actionIndex < 0)
        {
            Logger.LogWarning("[FsmMaster][Test] 6: no random-event action found on 'Far'.");
            return;
        }

        FsmEvent[] candidates = FsmActionSequencer.ExtractEventCandidates(state.Actions[actionIndex], state);
        int[] slots = { 2, 2, 1, 2, 2, 3 };
        var pattern = new List<string>();
        foreach (int slot in slots)
        {
            int idx = slot - 1;
            if (idx < 0 || idx >= candidates.Length)
            {
                Logger.LogWarning($"[FsmMaster][Test] 6: event slot {slot} is out of range (only {candidates.Length} candidate event(s)).");
                return;
            }

            pattern.Add(candidates[idx].Name);
        }

        var seq = new SequencerOverride
        {
            StateName = "Far",
            ActionIndex = actionIndex,
            Pattern = pattern,
            RepeatCount = 2,
        };

        foreach (Fsm fsm in instances)
        {
            _editManager!.InstallSequencer(fsmKey, fsm, seq);
        }

        Logger.LogInfo($"[FsmMaster][Test] 6: Far -> sequence [{string.Join(", ", pattern)}] x2 then restore original");
    }

    // Dumps everything FsmDataCollector/FsmConsoleLogger already know how to read for this FSM - states,
    // actions (including nested/wrapped variable fields), transitions/events, and FSM variables - reusing
    // the existing generic collector/logger pair rather than writing a second way to walk the same data.
    private void RunTest7_LogFsmData()
    {
        string fsmKey = FsmIdentity.GetFsmKey(TestObjectName, TestFsmName);
        IReadOnlyList<Fsm> instances = GetTestInstances(fsmKey);
        if (instances.Count == 0)
        {
            return;
        }

        var consoleLogger = new FsmConsoleLogger(Logger);
        foreach (Fsm fsm in instances)
        {
            PlayMakerFSM? component = fsm.FsmComponent;
            if (component == null)
            {
                Logger.LogWarning("[FsmMaster][Test] 7: fsm has no owning PlayMakerFSM component; skipping.");
                continue;
            }

            consoleLogger.LogFsm(FsmDataCollector.CollectFsmInfo(component));
        }

        Logger.LogInfo($"[FsmMaster][Test] 7: logged {instances.Count} fsm instance(s) for '{fsmKey}'.");
    }
}
