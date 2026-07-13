using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Walks the live PlayMakerFSM/Fsm object graph and returns plain structured data.
// No logging here - this is shared by the console logger today and will be shared
// by the overlay/graph UI later, so it must not know or care how its output is displayed.
internal static class FsmDataCollector
{
    // Deliberately cheap - no reflection, no state/action walk - since this runs unconditionally on
    // every scene load regardless of whether the overlay is ever opened. The expensive per-FSM walk
    // (CollectFsmInfo) only ever runs on demand for whichever one FSM is actually being viewed - see
    // FsmGraphOverlay.ResolveFsmInfo.
    public static FsmSnapshot CollectSnapshot(string sceneName, PlayMakerFSM[] components)
    {
        var fsms = new List<FsmIdentityInfo>(components.Length);
        foreach (PlayMakerFSM component in components)
        {
            fsms.Add(new FsmIdentityInfo
            {
                Component = component,
                FsmName = component.FsmName,
                GameObjectName = component.gameObject.name,
            });
        }

        return new FsmSnapshot { SceneName = sceneName, Fsms = fsms };
    }

    public static FsmInfo CollectFsmInfo(PlayMakerFSM component)
    {
        Fsm fsm = component.Fsm;

        var states = new List<FsmStateInfo>(fsm.States.Length);
        foreach (FsmState state in fsm.States)
        {
            states.Add(CollectStateInfo(state));
        }

        return new FsmInfo
        {
            Component = component,
            Fsm = fsm,
            FsmName = component.FsmName,
            GameObjectName = component.gameObject.name,
            ActiveStateName = component.ActiveStateName,
            States = states,
            GlobalTransitions = CollectTransitions(fsm.GlobalTransitions),
        };
    }

    private static FsmStateInfo CollectStateInfo(FsmState state)
    {
        return new FsmStateInfo
        {
            State = state,
            Name = state.Name,
            Actions = CollectActions(state.Actions),
            Transitions = CollectTransitions(state.Transitions),
        };
    }

    // Per-action-type reflection field list, memoized across every action instance/state/FSM this is
    // ever asked to collect - CollectSnapshot runs unconditionally on every scene load (i.e. every room
    // transition), and the same handful of PlayMaker action types (Wait, SendEvent, ...) recur across
    // dozens of enemies each carrying many actions, so re-running GetFields (which allocates a fresh
    // array every call) and the two filters below per *instance* rather than per *type* was pure,
    // repeated waste for data that never changes between calls for a given type.
    private static readonly Dictionary<Type, FieldInfo[]> RelevantFieldsByActionType = new();

    private static FieldInfo[] GetRelevantFields(Type actionType)
    {
        if (RelevantFieldsByActionType.TryGetValue(actionType, out FieldInfo[]? cached))
        {
            return cached;
        }

        var relevant = new List<FieldInfo>();

        // NonPublic alongside Public surfaces an action's private runtime bookkeeping fields
        // (e.g. Wait's startTime/timer, which PlayMaker itself never serializes or shows in its
        // own editor) so they can be inspected/tracked/edited the same as any configured
        // parameter - see FsmActionFieldInfo.IsHidden.
        foreach (FieldInfo field in actionType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.DeclaringType == typeof(FsmStateAction))
            {
                continue;
            }

            // Skips auto-property backing fields (<Name>k__BackingField) that NonPublic would
            // otherwise surface under their ugly compiler-generated name - PlayMaker actions are
            // plain fields almost universally, but this guards against the rare action written
            // with a private auto-property instead.
            if (field.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                continue;
            }

            relevant.Add(field);
        }

        FieldInfo[] result = relevant.ToArray();
        RelevantFieldsByActionType[actionType] = result;
        return result;
    }

    private static IReadOnlyList<FsmActionInfo> CollectActions(FsmStateAction[] actions)
    {
        var result = new List<FsmActionInfo>(actions.Length);

        foreach (FsmStateAction action in actions)
        {
            Type actionType = action.GetType();
            FieldInfo[] relevantFields = GetRelevantFields(actionType);
            var fields = new List<FsmActionFieldInfo>(relevantFields.Length);

            foreach (FieldInfo field in relevantFields)
            {
                fields.Add(new FsmActionFieldInfo { FieldName = field.Name, FieldValue = field.GetValue(action), Field = field, IsHidden = !field.IsPublic });
            }

            result.Add(new FsmActionInfo { Action = action, ActionType = actionType, Fields = fields });
        }

        return result;
    }

    private static IReadOnlyList<FsmTransitionInfo> CollectTransitions(FsmTransition[] transitions)
    {
        var result = new List<FsmTransitionInfo>(transitions.Length);

        foreach (FsmTransition transition in transitions)
        {
            result.Add(new FsmTransitionInfo { EventName = transition.EventName, ToState = transition.ToState });
        }

        return result;
    }
}
