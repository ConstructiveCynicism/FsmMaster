// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Generic replacement for a RandomEvent-family action (SendRandomEvent/V2/V3/V3ActiveBool/V4, or the base
// RandomEvent action) that advances through a fixed, ordered sequence of events instead of picking one at
// random. FsmState.OnEnter only calls OnEnter once per state entry and this instance stays in
// state.Actions across entries, so _index persists and the sequence advances once per re-entry rather than
// within a single one. An explicit ordered sequence already supports non-consecutive repeats
// (e.g. 1-2-1-1-2-2-1-3-1) without any special-casing - it's just the flattened event list.
//
// When repeatCount is positive, the action tracks how many full passes through the sequence have completed
// and, once that count is reached, restores the original RandomEvent-family action instead of continuing:
// re-enabling it, removing itself from the state, and invoking the original action's OnEnter so the state
// doesn't skip a beat on that entry.
internal sealed class SequenceSendEventAction : FsmStateAction
{
    private readonly FsmEvent[] _sequence;
    private readonly int _repeatCount;
    private readonly FsmStateAction _originalAction;
    private readonly FsmState _state;
    private int _index;
    private int _completedCycles;

    public SequenceSendEventAction(FsmEvent[] sequence, int repeatCount, FsmStateAction originalAction, FsmState state)
    {
        if (sequence.Length == 0)
        {
            throw new ArgumentException("Sequence must contain at least one event.", nameof(sequence));
        }

        _sequence = sequence;
        _repeatCount = repeatCount;
        _originalAction = originalAction;
        _state = state;
    }

    public override void OnEnter()
    {
        if (_repeatCount > 0 && _completedCycles >= _repeatCount)
        {
            RestoreOriginalAction();
            _originalAction.OnEnter();
            return;
        }

        Fsm.Event(_sequence[_index]);
        _index++;
        if (_index >= _sequence.Length)
        {
            _index = 0;
            _completedCycles++;
        }

        Finish();
    }

    private void RestoreOriginalAction()
    {
        _originalAction.Enabled = true;
        int idx = Array.IndexOf(_state.Actions, this);
        if (idx >= 0)
        {
            _state.RemoveAction(idx);
        }
    }
}

internal static class FsmActionSequencer
{
    // Finds the array index of the rank-th (0-based) action in a state that looks like a RandomEvent-
    // family action (RandomEvent, SendRandomEvent, V2/V3/V3ActiveBool/V4, SendRandomEventFair/
    // FairConditional, or any future variant). All of PlayMaker's built-in random-event actions follow
    // the naming convention of containing "Random" in the type name, so this generalizes across variants
    // instead of hardcoding one specific type. Ranking (rather than a raw array index) is what lets a
    // state with more than one such action have each one independently sequenced: a synthetic
    // SequenceSendEventAction's own type name never contains "Random", so installing/removing sequencers
    // elsewhere in the same state only ever shifts array positions, never the rank order among the
    // actions that actually match.
    public static int IndexRandomEventAction(FsmState state, int rank)
    {
        int seen = 0;
        for (int i = 0; i < state.Actions.Length; i++)
        {
            if (state.Actions[i].GetType().Name.Contains("Random"))
            {
                if (seen == rank)
                {
                    return i;
                }

                seen++;
            }
        }

        return -1;
    }

    // Finds the ordered list of candidate FsmEvents a RandomEvent-family action would have picked from.
    // Works via reflection over the action's own public FsmEvent[] field (the shape shared by
    // SendRandomEvent/V2/V3/V3ActiveBool/V4) rather than hardcoding any one action type, so it generalizes
    // to any FSM/state/action. Falls back to the containing state's own transitions, which is how the base
    // RandomEvent action (no dedicated events field) sources its candidates instead.
    public static FsmEvent[] ExtractEventCandidates(FsmStateAction sourceAction, FsmState state)
    {
        FieldInfo? eventArrayField = sourceAction.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(f => f.FieldType == typeof(FsmEvent[]));

        if (eventArrayField != null && eventArrayField.GetValue(sourceAction) is FsmEvent[] events)
        {
            return events;
        }

        return state.Transitions.Select(t => t.FsmEvent).ToArray();
    }

    // Expands an ordered event list plus a per-event repeat count into a flat, cyclable sequence.
    // E.g. events = [e0, e1], repeatCounts = [2, 1] -> [e0, e0, e1].
    public static FsmEvent[] ExpandPattern(FsmEvent[] events, int[] repeatCounts)
    {
        var result = new List<FsmEvent>();
        for (int i = 0; i < repeatCounts.Length; i++)
        {
            for (int j = 0; j < repeatCounts[i]; j++)
            {
                result.Add(events[i]);
            }
        }

        return result.ToArray();
    }
}
