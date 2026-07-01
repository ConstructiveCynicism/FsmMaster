using System;
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
