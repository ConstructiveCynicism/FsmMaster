using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Cheap, per-scene collection of every live FSM's identity, without any per-state/action detail.
internal sealed class FsmSnapshot
{
    public string SceneName { get; set; } = "";
    public List<FsmIdentityInfo> Fsms { get; set; } = new();
}

// Cheap identity-only entry for one live PlayMakerFSM - RefreshSnapshot builds one of these per FSM
// unconditionally on every scene load, so it deliberately carries no reflected state/action/field data
// (see FsmInfo for that). The full, reflection-heavy walk (FsmDataCollector.CollectFsmInfo) only ever
// runs on demand, for whichever one FSM a graph tab is actually open/focused on - see
// FsmGraphOverlay.ResolveFsmInfo - instead of eagerly for every FSM the scene scan discovers whether
// it's ever viewed or not.
internal sealed class FsmIdentityInfo
{
    public PlayMakerFSM Component { get; set; } = null!;
    public string FsmName { get; set; } = "";
    public string GameObjectName { get; set; } = "";
}

// Full reflected snapshot of one FSM's states/actions/transitions, collected on demand for whichever
// FSM a graph tab actually has open.
internal sealed class FsmInfo
{
    public PlayMakerFSM Component { get; set; } = null!;
    public Fsm Fsm { get; set; } = null!;
    public string FsmName { get; set; } = "";
    public string GameObjectName { get; set; } = "";
    public string ActiveStateName { get; set; } = "";
    public List<FsmStateInfo> States { get; set; } = new();
    public List<FsmTransitionInfo> GlobalTransitions { get; set; } = new();
}

// One state's own actions and outgoing transitions.
internal sealed class FsmStateInfo
{
    public FsmState State { get; set; } = null!;
    public string Name { get; set; } = "";
    public List<FsmActionInfo> Actions { get; set; } = new();
    public List<FsmTransitionInfo> Transitions { get; set; } = new();
}

// One state action instance plus its reflected field list.
internal sealed class FsmActionInfo
{
    public FsmStateAction Action { get; set; } = null!;
    public Type ActionType { get; set; } = null!;
    public List<FsmActionFieldInfo> Fields { get; set; } = new();
}

// One reflected field on an action, with its current value and display metadata.
internal sealed class FsmActionFieldInfo
{
    public string FieldName { get; set; } = "";
    public object? FieldValue { get; set; }

    // The reflection handle CollectActions already had in hand when it read FieldValue - carried here
    // so a caller (FsmActiveStatePanel) can re-read a raw (non-NamedVariable) field's current value
    // after an edit without re-deriving GetType().GetField(name) itself.
    public FieldInfo Field { get; set; } = null!;

    // True for a field PlayMaker itself never serializes/shows in its editor (private runtime
    // bookkeeping like Wait's startTime/timer) - set from !FieldInfo.IsPublic in FsmDataCollector.
    // Purely a display hint for FsmActiveStatePanel; read/write/tracking treat these identically to
    // any other field.
    public bool IsHidden { get; set; }
}

// One outgoing transition's triggering event and target state.
internal sealed class FsmTransitionInfo
{
    public string EventName { get; set; } = "";
    public string ToState { get; set; } = "";
}
