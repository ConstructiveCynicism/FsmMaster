using System;
using System.Reflection;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FsmMaster;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.ace9653.fsmmaster")]
public partial class FsmMasterPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
        LogLiveFsms();
    }

    private void LogLiveFsms()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);

        Logger.LogInfo($"[FsmMaster] Scene \"{sceneName}\": {fsms.Length} live PlayMakerFSM instance(s)");

        foreach (PlayMakerFSM fsm in fsms)
        {
            Logger.LogInfo($"[FsmMaster]   FSM \"{fsm.FsmName}\" on \"{fsm.gameObject.name}\" - state \"{fsm.ActiveStateName}\"");
            LogFsmDetails(fsm.Fsm);
        }
    }

    private void LogFsmDetails(Fsm fsm)
    {
        Logger.LogInfo($"[FsmMaster]     {fsm.States.Length} state(s)");
        foreach (FsmState state in fsm.States)
        {
            Logger.LogInfo($"[FsmMaster]       State \"{state.Name}\"");
            LogStateActions(state);
            foreach (FsmTransition transition in state.Transitions)
            {
                Logger.LogInfo($"[FsmMaster]         \"{transition.EventName}\" -> \"{transition.ToState}\"");
            }
        }

        if (fsm.GlobalTransitions.Length > 0)
        {
            Logger.LogInfo($"[FsmMaster]     {fsm.GlobalTransitions.Length} global transition(s)");
            foreach (FsmTransition transition in fsm.GlobalTransitions)
            {
                Logger.LogInfo($"[FsmMaster]       \"{transition.EventName}\" -> \"{transition.ToState}\"");
            }
        }

        LogFsmVariables(fsm.Variables);
    }

    private void LogStateActions(FsmState state)
    {
        FsmStateAction[] actions = state.Actions;
        Logger.LogInfo($"[FsmMaster]         {actions.Length} action(s)");

        for (int i = 0; i < actions.Length; i++)
        {
            FsmStateAction action = actions[i];
            Type actionType = action.GetType();
            Logger.LogInfo($"[FsmMaster]           [{i}] {actionType.Name}");

            foreach (FieldInfo field in actionType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.DeclaringType == typeof(FsmStateAction))
                {
                    continue;
                }

                LogActionField(action, field.Name, field.GetValue(action));
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
                Logger.LogInfo($"[FsmMaster]             {fieldName}: {FormatActionFieldValue(action, fieldValue)}");
                break;
        }
    }

    private static string WithVariableName(string fieldName, string variableName)
    {
        return !string.IsNullOrEmpty(variableName) ? $"{fieldName} (\"{variableName}\")" : fieldName;
    }

    private void LogActionFieldArray(FsmStateAction action, string fieldName, int length, Func<int, object?> getElement)
    {
        Logger.LogInfo($"[FsmMaster]             {fieldName}: {length} element(s)");

        for (int i = 0; i < length; i++)
        {
            object? element = getElement(i);
            Logger.LogInfo($"[FsmMaster]               [{i}]: {FormatActionFieldValue(action, element)}");
        }
    }

    private string FormatActionFieldValue(FsmStateAction action, object? fieldValue)
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
    private string FormatOwnerDefault(Fsm fsm, FsmOwnerDefault ownerDefault)
    {
        GameObject resolvedGameObject = fsm.GetOwnerDefaultTarget(ownerDefault);
        return resolvedGameObject != null ? $"[{resolvedGameObject.name}]" : "[none]";
    }

    private string FormatEventTarget(Fsm fsm, FsmEventTarget eventTarget)
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
        Logger.LogInfo("[FsmMaster]     Variables");

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
            Logger.LogInfo($"[FsmMaster]       {typeName} \"{item.Name}\": {getValue(item)}");
        }
    }
}
