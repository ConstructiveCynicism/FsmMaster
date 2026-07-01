using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Walks the live PlayMakerFSM/Fsm object graph and returns plain structured data.
// No logging here - this is shared by the console logger today and will be shared
// by the overlay/graph UI later, so it must not know or care how its output is displayed.
internal static class FsmDataCollector
{
    public static FsmSnapshot CollectSnapshot(string sceneName, PlayMakerFSM[] components)
    {
        var fsms = new List<FsmInfo>(components.Length);
        foreach (PlayMakerFSM component in components)
        {
            fsms.Add(CollectFsmInfo(component));
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

    private static IReadOnlyList<FsmActionInfo> CollectActions(FsmStateAction[] actions)
    {
        var result = new List<FsmActionInfo>(actions.Length);

        foreach (FsmStateAction action in actions)
        {
            Type actionType = action.GetType();
            var fields = new List<FsmActionFieldInfo>();

            foreach (FieldInfo field in actionType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.DeclaringType == typeof(FsmStateAction))
                {
                    continue;
                }

                fields.Add(new FsmActionFieldInfo { FieldName = field.Name, FieldValue = field.GetValue(action) });
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
