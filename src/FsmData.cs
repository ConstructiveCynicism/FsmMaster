using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace FsmMaster;

internal sealed class FsmSnapshot
{
    public string SceneName { get; set; } = "";
    public IReadOnlyList<FsmInfo> Fsms { get; set; } = Array.Empty<FsmInfo>();
}

internal sealed class FsmInfo
{
    public PlayMakerFSM Component { get; set; } = null!;
    public Fsm Fsm { get; set; } = null!;
    public string FsmName { get; set; } = "";
    public string GameObjectName { get; set; } = "";
    public string ActiveStateName { get; set; } = "";
    public IReadOnlyList<FsmStateInfo> States { get; set; } = Array.Empty<FsmStateInfo>();
    public IReadOnlyList<FsmTransitionInfo> GlobalTransitions { get; set; } = Array.Empty<FsmTransitionInfo>();
}

internal sealed class FsmStateInfo
{
    public FsmState State { get; set; } = null!;
    public string Name { get; set; } = "";
    public IReadOnlyList<FsmActionInfo> Actions { get; set; } = Array.Empty<FsmActionInfo>();
    public IReadOnlyList<FsmTransitionInfo> Transitions { get; set; } = Array.Empty<FsmTransitionInfo>();
}

internal sealed class FsmActionInfo
{
    public FsmStateAction Action { get; set; } = null!;
    public Type ActionType { get; set; } = null!;
    public IReadOnlyList<FsmActionFieldInfo> Fields { get; set; } = Array.Empty<FsmActionFieldInfo>();
}

internal sealed class FsmActionFieldInfo
{
    public string FieldName { get; set; } = "";
    public object? FieldValue { get; set; }
}

internal sealed class FsmTransitionInfo
{
    public string EventName { get; set; } = "";
    public string ToState { get; set; } = "";
}
