using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// Formats an FsmSnapshot (produced by FsmDataCollector) as console log lines.
internal sealed class FsmConsoleLogger
{
    private readonly ManualLogSource _logger;

    public FsmConsoleLogger(ManualLogSource logger)
    {
        _logger = logger;
    }

    public void LogSnapshot(FsmSnapshot snapshot)
    {
        _logger.LogInfo($"[FsmMaster] Scene \"{snapshot.SceneName}\": {snapshot.Fsms.Count} live PlayMakerFSM instance(s)");

        // snapshot.Fsms is only the cheap identity-only list (see FsmIdentityInfo) - the full
        // reflection walk is done here, on demand, per entry, rather than snapshot collection eagerly
        // paying for it on every scene load whether this dump is ever requested or not.
        foreach (FsmIdentityInfo identity in snapshot.Fsms)
        {
            if (identity.Component == null)
            {
                continue;
            }

            LogFsm(FsmDataCollector.CollectFsmInfo(identity.Component));
        }
    }

    // Logs everything about a single FSM - states, actions (including nested/wrapped variable fields),
    // transitions, global transitions, and FSM variables. LogSnapshot uses this per FSM in a full-scene
    // dump; it's also a standalone entry point for logging just one already-known FSM on demand.
    public void LogFsm(FsmInfo fsm)
    {
        _logger.LogInfo($"[FsmMaster]   FSM \"{fsm.FsmName}\" on \"{fsm.GameObjectName}\" - state \"{fsm.ActiveStateName}\"");
        LogFsmDetails(fsm);
    }

    private void LogFsmDetails(FsmInfo fsm)
    {
        _logger.LogInfo($"[FsmMaster]     {fsm.States.Count} state(s)");
        foreach (FsmStateInfo state in fsm.States)
        {
            _logger.LogInfo($"[FsmMaster]       State \"{state.Name}\"");
            Rect position = state.State.Position;
            _logger.LogInfo(FormattableString.Invariant(
                $"[FsmMaster]         Position=({position.x:F1}, {position.y:F1}, {position.width:F1}, {position.height:F1}) ColorIndex={state.State.ColorIndex}"));
            LogActions(state.Actions);
            foreach (FsmTransitionInfo transition in state.Transitions)
            {
                _logger.LogInfo($"[FsmMaster]         \"{transition.EventName}\" -> \"{transition.ToState}\"");
            }
        }

        if (fsm.GlobalTransitions.Count > 0)
        {
            _logger.LogInfo($"[FsmMaster]     {fsm.GlobalTransitions.Count} global transition(s)");
            foreach (FsmTransitionInfo transition in fsm.GlobalTransitions)
            {
                _logger.LogInfo($"[FsmMaster]       \"{transition.EventName}\" -> \"{transition.ToState}\"");
            }
        }

        LogFsmVariables(fsm.Fsm.Variables);
    }

    private void LogActions(IReadOnlyList<FsmActionInfo> actions)
    {
        _logger.LogInfo($"[FsmMaster]         {actions.Count} action(s)");

        for (int i = 0; i < actions.Count; i++)
        {
            FsmActionInfo action = actions[i];
            _logger.LogInfo($"[FsmMaster]           [{i}] {action.ActionType.Name}");

            foreach (FsmActionFieldInfo field in action.Fields)
            {
                LogActionField(action.Action, field.FieldName, field.FieldValue);
            }
        }
    }

    private void LogActionField(FsmStateAction action, string fieldName, object? fieldValue)
    {
        switch (fieldValue)
        {
            case FsmArray fsmArray:
                LogActionFieldArray(action, WithVariableName(fieldName, fsmArray.Name), fsmArray.Length, i => fsmArray.Values[i]);
                break;
            case Array array:
                LogActionFieldArray(action, fieldName, array.Length, i => array.GetValue(i));
                break;
            default:
                _logger.LogInfo($"[FsmMaster]             {fieldName}: {FormatActionFieldValue(action, fieldValue)}");
                break;
        }
    }

    private static string WithVariableName(string fieldName, string variableName)
    {
        return !string.IsNullOrEmpty(variableName) ? $"{fieldName} (\"{variableName}\")" : fieldName;
    }

    private void LogActionFieldArray(FsmStateAction action, string fieldName, int length, Func<int, object?> getElement)
    {
        _logger.LogInfo($"[FsmMaster]             {fieldName}: {length} element(s)");

        for (int i = 0; i < length; i++)
        {
            object? element = getElement(i);
            _logger.LogInfo($"[FsmMaster]               [{i}]: {FormatActionFieldValue(action, element)}");
        }
    }

    internal static string FormatActionFieldValue(FsmStateAction action, object? fieldValue)
    {
        switch (fieldValue)
        {
            case null:
                return "null";
            case NamedVariable namedVariable:
                string value = namedVariable.RawValue?.ToString() ?? "null";
                return !string.IsNullOrEmpty(namedVariable.Name) ? $"\"{namedVariable.Name}\": {value}" : value;
            case FsmEvent fsmEvent:
                return fsmEvent.Name;
            case FsmOwnerDefault fsmOwnerDefault:
                return FormatOwnerDefault(action.Fsm, fsmOwnerDefault);
            case FsmEventTarget fsmEventTarget:
                return FormatEventTarget(action.Fsm, fsmEventTarget);
            default:
                return fieldValue.ToString();
        }
    }

    // FsmOwnerDefault only carries the "use owner" / "specify object" *option*, not an
    // identity on its own - Fsm.GetOwnerDefaultTarget resolves it to the actual GameObject
    // PlayMaker would target at runtime, which is far more useful to log than the option name.
    internal static string FormatOwnerDefault(Fsm? fsm, FsmOwnerDefault ownerDefault)
    {
        // fsm is null when the action's owning FSM has never initialized (e.g. the FSM's
        // GameObject was still disabled when the panel snapshot was taken) - PlayMaker only
        // assigns FsmStateAction.Fsm during its own init, so this isn't an error case to log.
        if (fsm == null)
        {
            return "[uninitialized]";
        }

        GameObject resolvedGameObject = fsm.GetOwnerDefaultTarget(ownerDefault);
        return resolvedGameObject != null ? $"[{resolvedGameObject.name}]" : "[none]";
    }

    internal static string FormatEventTarget(Fsm? fsm, FsmEventTarget eventTarget)
    {
        switch (eventTarget.target)
        {
            case FsmEventTarget.EventTarget.Self:
                return "EventTarget(Self)";
            case FsmEventTarget.EventTarget.GameObject:
                return $"EventTarget(GameObject): {FormatOwnerDefault(fsm, eventTarget.gameObject)}";
            case FsmEventTarget.EventTarget.GameObjectFSM:
                string fsmName = eventTarget.fsmName.Value ?? "";
                return $"EventTarget(GameObjectFSM): {FormatOwnerDefault(fsm, eventTarget.gameObject)}.{fsmName}";
            case FsmEventTarget.EventTarget.FSMComponent:
                PlayMakerFSM targetFsmComponent = eventTarget.fsmComponent;
                string fsmComponentDesc = targetFsmComponent != null
                    ? $"{targetFsmComponent.gameObject.name}.{targetFsmComponent.FsmName}"
                    : "none";
                return $"EventTarget(FSMComponent): [{fsmComponentDesc}]";
            default:
                return $"EventTarget({eventTarget.target})";
        }
    }

    private void LogFsmVariables(FsmVariables variables)
    {
        _logger.LogInfo("[FsmMaster]     Variables");

        LogVariableArray("Float", variables.FloatVariables, v => v.Value);
        LogVariableArray("Int", variables.IntVariables, v => v.Value);
        LogVariableArray("Bool", variables.BoolVariables, v => v.Value);
        LogVariableArray("String", variables.StringVariables, v => v.Value);
        LogVariableArray("Vector2", variables.Vector2Variables, v => v.Value);
        LogVariableArray("Vector3", variables.Vector3Variables, v => v.Value);
        LogVariableArray("Rect", variables.RectVariables, v => v.Value);
        LogVariableArray("Quaternion", variables.QuaternionVariables, v => v.Value);
        LogVariableArray("Color", variables.ColorVariables, v => v.Value);
        LogVariableArray("GameObject", variables.GameObjectVariables, v => v.Value);
        LogVariableArray("Object", variables.ObjectVariables, v => v.Value);
        LogVariableArray("Material", variables.MaterialVariables, v => v.Value);
        LogVariableArray("Texture", variables.TextureVariables, v => v.Value);
        LogVariableArray("Enum", variables.EnumVariables, v => v.Value);
        // FsmArray has no single Value - its payload lives in one of several typed backing
        // arrays selected by its Type, so Values (boxed, generic across all of them) is used
        // instead of any one type-specific field.
        LogVariableArray("Array", variables.ArrayVariables, v => string.Join(", ", v.Values));
    }

    private void LogVariableArray<T>(string typeName, T[] items, Func<T, object> getValue) where T : NamedVariable
    {
        if (items.Length == 0)
        {
            return;
        }

        foreach (T item in items)
        {
            _logger.LogInfo($"[FsmMaster]       {typeName} \"{item.Name}\": {getValue(item)}");
        }
    }
}
