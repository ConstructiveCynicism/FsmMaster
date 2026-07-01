using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using Silksong.FsmUtil;
using UnityEngine;

namespace FsmMaster;

// The generalized edit/undo engine every FsmMaster mutation funnels through: variable overrides, action
// field overrides, state neutering, transition retargeting/disabling, and sequencer installs. Each FsmKey
// (see FsmIdentity) gets its pristine values captured lazily on first touch, so ResetFsm can restore a live
// FSM without a scene reload. All mutation is done via Silksong.FsmUtil's existing extension methods
// rather than re-implementing state/transition/action
// bookkeeping FsmUtil already provides.
internal sealed class FsmEditManager
{
    private static readonly string[] ExitEventPriority = { "CANCEL", "FINISHED", "NEXT" };

    private static readonly HashSet<VariableType> SupportedVariableTypes = new()
    {
        VariableType.Float, VariableType.Int, VariableType.Bool, VariableType.String,
        VariableType.Vector2, VariableType.Vector3, VariableType.Rect, VariableType.Quaternion,
        VariableType.Color, VariableType.Enum,
    };

    private readonly ManualLogSource _logger;
    private readonly Dictionary<string, List<Fsm>> _liveInstances = new();
    private readonly Dictionary<string, FsmPristineSnapshot> _pristine = new();

    // The edits currently in effect per FsmKey, as opposed to _pristine's "what to restore" - this is what a
    // caller (e.g. a "save all changes" action) serializes to FsmSaveDataStore.
    private readonly Dictionary<string, FsmEditSet> _activeEdits = new();

    public FsmEditManager(ManualLogSource logger)
    {
        _logger = logger;
    }

    // Called whenever the plugin (re)discovers live FSMs (Awake, each sceneLoaded) - replaces the tracked
    // instance list for this key outright, since the previous scene's instances are gone.
    public void RegisterLiveInstances(string fsmKey, IEnumerable<Fsm> instances)
    {
        _liveInstances[fsmKey] = instances.ToList();
    }

    public IReadOnlyList<Fsm> GetLiveInstances(string fsmKey) =>
        _liveInstances.TryGetValue(fsmKey, out List<Fsm>? instances) ? instances : Array.Empty<Fsm>();

    // Every FsmKey with at least one edit currently in effect this session.
    public IReadOnlyCollection<string> GetEditedFsmKeys() => _activeEdits.Keys;

    public FsmEditSet? GetActiveEditSet(string fsmKey) =>
        _activeEdits.TryGetValue(fsmKey, out FsmEditSet? set) ? set : null;

    public void ApplyEditSet(FsmEditSet editSet)
    {
        if (!_liveInstances.TryGetValue(editSet.FsmKey, out List<Fsm>? instances) || instances.Count == 0)
        {
            _logger.LogWarning($"[FsmMaster] No live instances found for fsm key '{editSet.FsmKey}'; skipping edit set.");
            return;
        }

        foreach (Fsm fsm in instances)
        {
            foreach (VariableOverride ov in editSet.VariableOverrides)
            {
                ApplyVariableOverride(editSet.FsmKey, fsm, ov);
            }

            foreach (ActionFieldOverride ov in editSet.ActionFieldOverrides)
            {
                ApplyActionFieldOverride(editSet.FsmKey, fsm, ov);
            }

            foreach (string stateName in editSet.DisabledStates)
            {
                DisableState(editSet.FsmKey, fsm, stateName);
            }

            foreach (TransitionRetarget retarget in editSet.TransitionRetargets)
            {
                ApplyTransitionRetarget(editSet.FsmKey, fsm, retarget);
            }

            foreach (SequencerOverride seq in editSet.SequencerOverrides)
            {
                InstallSequencer(editSet.FsmKey, fsm, seq);
            }
        }
    }

    public void ResetFsm(string fsmKey)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            return;
        }

        _liveInstances.TryGetValue(fsmKey, out List<Fsm>? instances);
        RestoreSnapshot(snapshot, instances ?? new List<Fsm>());
        _pristine.Remove(fsmKey);
        _activeEdits.Remove(fsmKey);
    }

    // Reverts every edit applied this session without touching persisted JSON, so a ScriptEngine hot-reload
    // unload/reload cycle doesn't leave live FSMs double-patched.
    public void RevertAllForUnload()
    {
        foreach (KeyValuePair<string, FsmPristineSnapshot> entry in _pristine)
        {
            _liveInstances.TryGetValue(entry.Key, out List<Fsm>? instances);
            RestoreSnapshot(entry.Value, instances ?? new List<Fsm>());
        }

        _pristine.Clear();
        _liveInstances.Clear();
        _activeEdits.Clear();
    }

    // ---- Variables ----

    public void ApplyVariableOverride(string fsmKey, Fsm fsm, VariableOverride ov)
    {
        NamedVariable? variable = fsm.Variables.FindVariable(ov.Name);
        if (variable == null)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' not found on fsm '{fsm.Name}'; skipping.");
            return;
        }

        if (!SupportedVariableTypes.Contains(variable.VariableType))
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' has unsupported type '{variable.VariableType}' (object/array-reference variables are out of scope for this pass); skipping.");
            return;
        }

        if (variable.VariableType.ToString() != ov.VariableType)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' is now type '{variable.VariableType}', expected '{ov.VariableType}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        if (!snapshot.OriginalValues.VariableOverrides.Exists(v => v.Name == ov.Name))
        {
            snapshot.OriginalValues.VariableOverrides.Add(new VariableOverride
            {
                VariableType = variable.VariableType.ToString(),
                Name = ov.Name,
                StringValue = FormatNamedVariable(variable),
            });
        }

        AssignNamedVariable(variable, ov.StringValue);

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.VariableOverrides.RemoveAll(v => v.Name == ov.Name);
        active.VariableOverrides.Add(ov);
    }

    // ---- Action fields ----

    public void ApplyActionFieldOverride(string fsmKey, Fsm fsm, ActionFieldOverride ov)
    {
        FsmState? state = fsm.GetState(ov.StateName);
        if (state == null || ov.ActionIndex < 0 || ov.ActionIndex >= state.Actions.Length)
        {
            _logger.LogWarning($"[FsmMaster] State '{ov.StateName}' action index {ov.ActionIndex} not found on fsm '{fsm.Name}'; skipping.");
            return;
        }

        FsmStateAction action = state.Actions[ov.ActionIndex];
        if (action.GetType().Name != ov.ExpectedActionTypeName)
        {
            _logger.LogWarning($"[FsmMaster] Action at '{ov.StateName}'[{ov.ActionIndex}] on fsm '{fsm.Name}' is '{action.GetType().Name}', expected '{ov.ExpectedActionTypeName}'; skipping.");
            return;
        }

        FieldInfo? field = action.GetType().GetField(ov.FieldName, BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}' not found on action '{ov.ExpectedActionTypeName}'; skipping.");
            return;
        }

        object? currentValue = field.GetValue(action);
        if (!TryFormatValue(currentValue, out string formattedCurrent))
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}' on '{ov.ExpectedActionTypeName}' has unsupported type '{currentValue?.GetType().Name ?? "null"}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        if (!snapshot.OriginalValues.ActionFieldOverrides.Exists(f =>
                f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName))
        {
            snapshot.OriginalValues.ActionFieldOverrides.Add(new ActionFieldOverride
            {
                StateName = ov.StateName,
                ActionIndex = ov.ActionIndex,
                ExpectedActionTypeName = ov.ExpectedActionTypeName,
                FieldName = ov.FieldName,
                StringValue = formattedCurrent,
            });
        }

        TryAssignFieldValue(action, field, currentValue, ov.StringValue);

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.ActionFieldOverrides.RemoveAll(f =>
            f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName);
        active.ActionFieldOverrides.Add(ov);
    }

    private static void RestoreActionField(Fsm fsm, ActionFieldOverride ov)
    {
        FsmState? state = fsm.GetState(ov.StateName);
        if (state == null || ov.ActionIndex < 0 || ov.ActionIndex >= state.Actions.Length)
        {
            return;
        }

        FsmStateAction action = state.Actions[ov.ActionIndex];
        FieldInfo? field = action.GetType().GetField(ov.FieldName, BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
        {
            return;
        }

        TryAssignFieldValue(action, field, field.GetValue(action), ov.StringValue);
    }

    // ---- States ----

    public void DisableState(string fsmKey, Fsm fsm, string stateName)
    {
        FsmState? state = fsm.GetState(stateName);
        if (state == null)
        {
            _logger.LogWarning($"[FsmMaster] State '{stateName}' not found on fsm '{fsm.Name}'; skipping disable.");
            return;
        }

        if (state.Actions.Any(a => a is SendExitEventAction))
        {
            return; // this instance was already neutered this session
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);

        var disabledByUs = new List<int>();
        for (int i = 0; i < state.Actions.Length; i++)
        {
            if (state.Actions[i].Enabled)
            {
                state.Actions[i].Enabled = false;
                disabledByUs.Add(i);
            }
        }

        snapshot.NeuteredActionIndices[stateName] = disabledByUs;

        FsmEvent? exitEvent = FindExitEvent(state);
        if (exitEvent != null)
        {
            var exitAction = new SendExitEventAction(exitEvent);
            state.AddAction(exitAction);
            snapshot.InjectedExitActions.Add(exitAction);
        }
        else
        {
            _logger.LogWarning($"[FsmMaster] State '{stateName}' on fsm '{fsm.Name}' has no CANCEL/FINISHED/NEXT transition and no FsmEvent-valued action field to fall back on; leaving it inert with no exit.");
        }

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        if (!active.DisabledStates.Contains(stateName))
        {
            active.DisabledStates.Add(stateName);
        }
    }

    // Priority order requested: a real CANCEL/FINISHED/NEXT transition (state-level or global) on the state
    // being neutered, else the last FsmEvent-valued field found while scanning the state's own actions in
    // original order (the same reflection walk FsmDataCollector.CollectActions uses for read-only display).
    private static FsmEvent? FindExitEvent(FsmState state)
    {
        foreach (string candidate in ExitEventPriority)
        {
            FsmTransition? transition = state.GetTransition(candidate) ?? state.Fsm.GetGlobalTransition(candidate);
            if (transition != null)
            {
                return transition.FsmEvent;
            }
        }

        FsmEvent? lastFound = null;
        foreach (FsmStateAction action in state.Actions)
        {
            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                switch (field.GetValue(action))
                {
                    case FsmEvent fsmEvent:
                        lastFound = fsmEvent;
                        break;
                    case FsmEvent[] { Length: > 0 } fsmEvents:
                        lastFound = fsmEvents[^1];
                        break;
                }
            }
        }

        return lastFound;
    }

    private sealed class SendExitEventAction : FsmStateAction
    {
        private readonly FsmEvent _exitEvent;

        public SendExitEventAction(FsmEvent exitEvent)
        {
            _exitEvent = exitEvent;
        }

        public override void OnEnter()
        {
            Fsm.Event(_exitEvent);
            Finish();
        }
    }

    // ---- Transitions (also covers "disable an event" and relocating a transition's origin state) ----

    public void ApplyTransitionRetarget(string fsmKey, Fsm fsm, TransitionRetarget retarget)
    {
        bool isGlobal = string.IsNullOrEmpty(retarget.StateName);
        FsmTransition? transition = isGlobal
            ? fsm.GetGlobalTransition(retarget.EventName)
            : fsm.GetTransition(retarget.StateName, retarget.EventName);

        if (transition == null)
        {
            string where = isGlobal ? "global transitions" : $"state '{retarget.StateName}'";
            _logger.LogWarning($"[FsmMaster] Transition for event '{retarget.EventName}' not found on {where} on fsm '{fsm.Name}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        if (!snapshot.OriginalValues.TransitionRetargets.Exists(t =>
                t.StateName == retarget.StateName && t.EventName == retarget.EventName))
        {
            bool disabling = retarget.NewToState == TransitionRetarget.DisabledMarker;

            // The instructions that would put this transition back exactly as found: locate it at wherever
            // this edit is about to send it (NewStateName - or, for a pure disable, nowhere ever moved it,
            // so its own StateName), and relocate/retarget it back to its original origin and destination.
            snapshot.OriginalValues.TransitionRetargets.Add(new TransitionRetarget
            {
                StateName = disabling ? retarget.StateName : retarget.NewStateName,
                EventName = retarget.EventName,
                NewStateName = retarget.StateName,
                NewToState = transition.ToState,
            });
        }

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.TransitionRetargets.RemoveAll(t => t.StateName == retarget.StateName && t.EventName == retarget.EventName);
        active.TransitionRetargets.Add(retarget);

        if (retarget.NewToState == TransitionRetarget.DisabledMarker)
        {
            RemoveTransitionAt(fsm, retarget.StateName, retarget.EventName);
            return;
        }

        RelocateTransition(fsm, retarget.StateName, retarget.NewStateName, retarget.EventName, retarget.NewToState);
    }

    // Used only when replaying a pristine snapshot, whose StateName/NewStateName/NewToState already encode
    // "restore to here" directly (see the capture step above), so this needs no lookup/existence check the
    // way ApplyTransitionRetarget does for new edits.
    private static void RestoreTransition(Fsm fsm, TransitionRetarget original) =>
        RelocateTransition(fsm, original.StateName, original.NewStateName, original.EventName, original.NewToState);

    // Moves a transition from one origin ("" = global) to another, retargeting/creating it at the new
    // origin either way. A relocation stays in effect until something (only ever RestoreTransition today)
    // moves it back - PlayMaker has no notion of a transition remembering where it "really" belongs.
    private static void RelocateTransition(Fsm fsm, string fromStateName, string toStateName, string eventName, string newToState)
    {
        if (fromStateName != toStateName)
        {
            RemoveTransitionAt(fsm, fromStateName, eventName);
        }

        AddOrChangeTransitionAt(fsm, toStateName, eventName, newToState);
    }

    private static void RemoveTransitionAt(Fsm fsm, string stateName, string eventName)
    {
        if (string.IsNullOrEmpty(stateName))
        {
            fsm.RemoveGlobalTransition(eventName);
        }
        else
        {
            fsm.RemoveTransition(stateName, eventName);
        }
    }

    private static void AddOrChangeTransitionAt(Fsm fsm, string stateName, string eventName, string toState)
    {
        bool isGlobal = string.IsNullOrEmpty(stateName);
        bool changed = isGlobal
            ? fsm.ChangeGlobalTransition(eventName, toState)
            : fsm.ChangeTransition(stateName, eventName, toState);

        if (!changed)
        {
            if (isGlobal)
            {
                fsm.AddGlobalTransition(eventName, toState);
            }
            else
            {
                fsm.AddTransition(stateName, eventName, toState);
            }
        }
    }

    // ---- Sequencer ----

    public void InstallSequencer(string fsmKey, Fsm fsm, SequencerOverride seq)
    {
        FsmState? state = fsm.GetState(seq.StateName);
        if (state == null)
        {
            _logger.LogWarning($"[FsmMaster] State '{seq.StateName}' not found on fsm '{fsm.Name}'; skipping sequencer install.");
            return;
        }

        if (state.Actions.Any(a => a is SequenceSendEventAction))
        {
            return; // this instance already has a sequencer installed this session
        }

        if (seq.Pattern.Count == 0)
        {
            _logger.LogWarning($"[FsmMaster] Sequencer pattern for state '{seq.StateName}' on fsm '{fsm.Name}' is empty; skipping.");
            return;
        }

        bool explicitIndexLooksValid = seq.ActionIndex >= 0
            && seq.ActionIndex < state.Actions.Length
            && state.Actions[seq.ActionIndex].GetType().Name.Contains("Random");
        int idx = explicitIndexLooksValid ? seq.ActionIndex : FsmActionSequencer.IndexFirstRandomEventAction(state);

        if (idx < 0)
        {
            _logger.LogWarning($"[FsmMaster] No random-event action found on state '{seq.StateName}' on fsm '{fsm.Name}'; skipping sequencer install.");
            return;
        }

        FsmStateAction original = state.Actions[idx];
        original.Enabled = false;

        FsmEvent[] pattern = seq.Pattern.Select(FsmEvent.GetFsmEvent).ToArray();
        var sequencer = new SequenceSendEventAction(pattern, seq.RepeatCount, original, state);
        original.InsertActionAfter(sequencer);

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        snapshot.InstalledSequencers.Add((original, sequencer));

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.SequencerOverrides.RemoveAll(s => s.StateName == seq.StateName);
        active.SequencerOverrides.Add(seq);
    }

    // ---- Snapshot plumbing ----

    private FsmPristineSnapshot GetOrCreateSnapshot(string fsmKey)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            snapshot = new FsmPristineSnapshot();
            _pristine[fsmKey] = snapshot;
        }

        return snapshot;
    }

    private FsmEditSet GetOrCreateActiveEditSet(string fsmKey)
    {
        if (!_activeEdits.TryGetValue(fsmKey, out FsmEditSet? set))
        {
            set = new FsmEditSet { FsmKey = fsmKey };
            _activeEdits[fsmKey] = set;
        }

        return set;
    }

    private static void RestoreSnapshot(FsmPristineSnapshot snapshot, List<Fsm> instances)
    {
        foreach (Fsm fsm in instances)
        {
            foreach (VariableOverride ov in snapshot.OriginalValues.VariableOverrides)
            {
                NamedVariable? variable = fsm.Variables.FindVariable(ov.Name);
                if (variable != null)
                {
                    AssignNamedVariable(variable, ov.StringValue);
                }
            }

            foreach (ActionFieldOverride ov in snapshot.OriginalValues.ActionFieldOverrides)
            {
                RestoreActionField(fsm, ov);
            }

            foreach (TransitionRetarget original in snapshot.OriginalValues.TransitionRetargets)
            {
                RestoreTransition(fsm, original);
            }
        }

        foreach (KeyValuePair<string, List<int>> entry in snapshot.NeuteredActionIndices)
        {
            foreach (Fsm fsm in instances)
            {
                FsmState? state = fsm.GetState(entry.Key);
                if (state == null)
                {
                    continue;
                }

                foreach (int i in entry.Value)
                {
                    if (i >= 0 && i < state.Actions.Length)
                    {
                        state.Actions[i].Enabled = true;
                    }
                }
            }
        }

        foreach (FsmStateAction exitAction in snapshot.InjectedExitActions)
        {
            FsmState state = exitAction.State;
            int idx = Array.IndexOf(state.Actions, exitAction);
            if (idx >= 0)
            {
                state.RemoveAction(idx);
            }
        }

        foreach ((FsmStateAction original, FsmStateAction sequencer) in snapshot.InstalledSequencers)
        {
            original.Enabled = true;
            FsmState state = sequencer.State;
            int idx = Array.IndexOf(state.Actions, sequencer);
            if (idx >= 0)
            {
                state.RemoveAction(idx);
            }
        }
    }

    // ---- Value formatting/parsing shared by variables and action fields ----
    // Every value round-trips as an invariant-culture string (see FsmEditModels) since JsonUtility can't
    // model a polymorphic value field.

    private static bool TryFormatValue(object? value, out string formatted)
    {
        switch (value)
        {
            case NamedVariable nv when SupportedVariableTypes.Contains(nv.VariableType):
                formatted = FormatNamedVariable(nv);
                return true;
            case bool b:
                formatted = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i:
                formatted = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case float f:
                formatted = f.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case string s:
                formatted = s;
                return true;
            case Enum e:
                formatted = Convert.ToInt32(e).ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                formatted = "";
                return false;
        }
    }

    private static bool TryAssignFieldValue(FsmStateAction action, FieldInfo field, object? currentValue, string stringValue)
    {
        switch (currentValue)
        {
            case NamedVariable nv when SupportedVariableTypes.Contains(nv.VariableType):
                AssignNamedVariable(nv, stringValue);
                return true;
            case bool:
                field.SetValue(action, bool.Parse(stringValue));
                return true;
            case int:
                field.SetValue(action, int.Parse(stringValue, CultureInfo.InvariantCulture));
                return true;
            case float:
                field.SetValue(action, float.Parse(stringValue, CultureInfo.InvariantCulture));
                return true;
            case string:
                field.SetValue(action, stringValue);
                return true;
            case Enum:
                field.SetValue(action, Enum.ToObject(field.FieldType, int.Parse(stringValue, CultureInfo.InvariantCulture)));
                return true;
            default:
                return false;
        }
    }

    private static string FormatNamedVariable(NamedVariable variable) => variable.VariableType switch
    {
        VariableType.Float => ((FsmFloat)variable).Value.ToString("R", CultureInfo.InvariantCulture),
        VariableType.Int => ((FsmInt)variable).Value.ToString(CultureInfo.InvariantCulture),
        VariableType.Bool => ((FsmBool)variable).Value.ToString(CultureInfo.InvariantCulture),
        VariableType.String => ((FsmString)variable).Value ?? "",
        VariableType.Vector2 => FormatFloats(((FsmVector2)variable).Value.x, ((FsmVector2)variable).Value.y),
        VariableType.Vector3 => FormatFloats(((FsmVector3)variable).Value.x, ((FsmVector3)variable).Value.y, ((FsmVector3)variable).Value.z),
        VariableType.Rect => FormatFloats(((FsmRect)variable).Value.x, ((FsmRect)variable).Value.y, ((FsmRect)variable).Value.width, ((FsmRect)variable).Value.height),
        VariableType.Quaternion => FormatFloats(((FsmQuaternion)variable).Value.x, ((FsmQuaternion)variable).Value.y, ((FsmQuaternion)variable).Value.z, ((FsmQuaternion)variable).Value.w),
        VariableType.Color => FormatFloats(((FsmColor)variable).Value.r, ((FsmColor)variable).Value.g, ((FsmColor)variable).Value.b, ((FsmColor)variable).Value.a),
        VariableType.Enum => ((FsmEnum)variable).ToInt().ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    private static void AssignNamedVariable(NamedVariable variable, string stringValue)
    {
        switch (variable.VariableType)
        {
            case VariableType.Float:
                ((FsmFloat)variable).Value = float.Parse(stringValue, CultureInfo.InvariantCulture);
                break;
            case VariableType.Int:
                ((FsmInt)variable).Value = int.Parse(stringValue, CultureInfo.InvariantCulture);
                break;
            case VariableType.Bool:
                ((FsmBool)variable).Value = bool.Parse(stringValue);
                break;
            case VariableType.String:
                ((FsmString)variable).Value = stringValue;
                break;
            case VariableType.Vector2:
                float[] v2 = ParseFloats(stringValue, 2);
                ((FsmVector2)variable).Value = new Vector2(v2[0], v2[1]);
                break;
            case VariableType.Vector3:
                float[] v3 = ParseFloats(stringValue, 3);
                ((FsmVector3)variable).Value = new Vector3(v3[0], v3[1], v3[2]);
                break;
            case VariableType.Rect:
                float[] r = ParseFloats(stringValue, 4);
                ((FsmRect)variable).Value = new Rect(r[0], r[1], r[2], r[3]);
                break;
            case VariableType.Quaternion:
                float[] q = ParseFloats(stringValue, 4);
                ((FsmQuaternion)variable).Value = new Quaternion(q[0], q[1], q[2], q[3]);
                break;
            case VariableType.Color:
                float[] c = ParseFloats(stringValue, 4);
                ((FsmColor)variable).Value = new Color(c[0], c[1], c[2], c[3]);
                break;
            case VariableType.Enum:
                var fsmEnum = (FsmEnum)variable;
                fsmEnum.Value = (Enum)Enum.ToObject(fsmEnum.EnumType, int.Parse(stringValue, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string FormatFloats(params float[] values) =>
        string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    private static float[] ParseFloats(string s, int count)
    {
        string[] parts = s.Split(',');
        var result = new float[count];
        for (int i = 0; i < count && i < parts.Length; i++)
        {
            result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        }

        return result;
    }
}
