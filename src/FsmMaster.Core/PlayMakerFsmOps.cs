// SPDX-License-Identifier: EUPL-1.2
// Portions derived from Silksong.FsmUtil, Copyright (c) silksong-modding, licensed under the EUPL-1.2.
// Modifications Copyright (c) 2026 ConstructiveCynicism. Modified since 2026-07-20.
using System;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Trimmed-down port of the Silksong.FsmUtil extension methods this project used to pull in from
// NuGet - only the state/transition/action operations FsmMaster actually calls, with the
// PlayMakerFSM-typed overloads and generic array helpers dropped. See the SPDX header above.
// Shared across every loader (including net35 targets with no FsmUtil package of their own) so the
// edit layer never depends on a platform-specific NuGet package for basic FSM graph mutation.
internal static class PlayMakerFsmOps
{
    public static FsmState? GetState(this Fsm fsm, string stateName)
    {
        foreach (FsmState state in fsm.States)
        {
            if (state.Name == stateName)
            {
                return state;
            }
        }

        return null;
    }

    public static FsmState MustGetState(this Fsm fsm, string stateName)
    {
        FsmState? state = fsm.GetState(stateName);
        if (state == null)
        {
            throw new InvalidOperationException($"State {stateName} not found in FSM {fsm.Name}");
        }

        return state;
    }

    public static FsmTransition? GetTransition(this FsmState state, string eventName)
    {
        foreach (FsmTransition transition in state.Transitions)
        {
            if (transition.EventName == eventName)
            {
                return transition;
            }
        }

        return null;
    }

    public static FsmTransition? GetTransition(this Fsm fsm, string stateName, string eventName) =>
        fsm.MustGetState(stateName).GetTransition(eventName);

    public static FsmTransition? GetGlobalTransition(this Fsm fsm, string globalEventName)
    {
        foreach (FsmTransition transition in fsm.GlobalTransitions)
        {
            if (transition.EventName == globalEventName)
            {
                return transition;
            }
        }

        return null;
    }

    // The interned event lookup (FsmEvent.GetFsmEvent) is used here, matching AddTransition below - state-
    // level transitions are found by interned-event equality elsewhere in the codebase, so a global
    // transition built this way is safe to match against by name.
    public static FsmEvent AddTransition(this Fsm fsm, string stateName, string eventName, string toState)
    {
        FsmState state = fsm.MustGetState(stateName);
        FsmEvent fsmEvent = FsmEvent.GetFsmEvent(eventName);
        state.Transitions = AppendToArray(state.Transitions, new FsmTransition
        {
            ToState = toState,
            FsmEvent = fsmEvent,
        });

        return fsmEvent;
    }

    // Deliberately non-interned, matching the existing (pre-shim) behavior: this builds a plain
    // `new FsmEvent(...)` rather than going through the interning table, so a global transition added
    // this way won't be found by name-based `==` against PlayMakerGlobals' interned event list while the
    // FSM is playing. FsmGraphOverlay already special-cases this when matching transitions for display.
    public static FsmEvent AddGlobalTransition(this Fsm fsm, string globalEventName, string toState)
    {
        var fsmEvent = new FsmEvent(globalEventName) { IsGlobal = true };
        fsm.GlobalTransitions = AppendToArray(fsm.GlobalTransitions, new FsmTransition
        {
            ToState = toState,
            FsmEvent = fsmEvent,
        });

        return fsmEvent;
    }

    public static bool ChangeTransition(this Fsm fsm, string stateName, string eventName, string toState)
    {
        FsmTransition? transition = fsm.MustGetState(stateName).GetTransition(eventName);
        if (transition == null)
        {
            return false;
        }

        transition.ToState = toState;
        return true;
    }

    public static bool ChangeGlobalTransition(this Fsm fsm, string globalEventName, string toState)
    {
        FsmTransition? transition = fsm.GetGlobalTransition(globalEventName);
        if (transition == null)
        {
            return false;
        }

        transition.ToState = toState;
        return true;
    }

    public static void RemoveTransition(this Fsm fsm, string stateName, string eventName)
    {
        FsmState state = fsm.MustGetState(stateName);
        state.Transitions = RemoveFromArray(state.Transitions, t => t.EventName == eventName);
    }

    public static void RemoveGlobalTransition(this Fsm fsm, string globalEventName)
    {
        fsm.GlobalTransitions = RemoveFromArray(fsm.GlobalTransitions, t => t.EventName == globalEventName);
    }

    public static void AddAction(this FsmState state, FsmStateAction action)
    {
        state.Actions = AppendToArray(state.Actions, action);
        action.Init(state);
    }

    public static bool RemoveAction(this FsmState state, int index)
    {
        FsmStateAction[] actions = state.Actions;
        if (index < 0 || index >= actions.Length)
        {
            return false;
        }

        var newActions = new FsmStateAction[actions.Length - 1];
        Array.Copy(actions, 0, newActions, 0, index);
        Array.Copy(actions, index + 1, newActions, index, actions.Length - index - 1);
        state.Actions = newActions;
        return true;
    }

    public static void InsertActionAfter(this FsmStateAction action, FsmStateAction newAction)
    {
        FsmState state = action.State;
        int index = Array.IndexOf(state.Actions, action);
        FsmStateAction[] actions = state.Actions;
        var newActions = new FsmStateAction[actions.Length + 1];
        Array.Copy(actions, 0, newActions, 0, index + 1);
        newActions[index + 1] = newAction;
        Array.Copy(actions, index + 1, newActions, index + 2, actions.Length - index - 1);
        state.Actions = newActions;
        newAction.Init(state);
    }

    private static T[] AppendToArray<T>(T[] array, T value)
    {
        var newArray = new T[array.Length + 1];
        Array.Copy(array, newArray, array.Length);
        newArray[array.Length] = value;
        return newArray;
    }

    private static T[] RemoveFromArray<T>(T[] array, Predicate<T> shouldRemove)
    {
        int removedCount = 0;
        foreach (T item in array)
        {
            if (shouldRemove(item))
            {
                removedCount++;
            }
        }

        if (removedCount == 0)
        {
            return array;
        }

        var newArray = new T[array.Length - removedCount];
        int writeIndex = 0;
        foreach (T item in array)
        {
            if (!shouldRemove(item))
            {
                newArray[writeIndex++] = item;
            }
        }

        return newArray;
    }
}
