using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using Silksong.FsmUtil;

namespace FsmMaster;

// Live variable-tracking registry - the data layer a future overlay will read from. Nothing here renders
// anything; per the "to be used in the overlay later" scoping this pass only builds the "what's tracked"
// list and an on-demand "what's its current value right now" resolver, targeting one specific field by
// reflection rather than walking every action field the way FsmDataCollector.CollectActions does for
// full-snapshot display.
internal sealed class FsmVariableTracker
{
    private readonly Func<string, IReadOnlyList<Fsm>> _getLiveInstances;
    private readonly List<TrackedVariablePath> _tracked = new();

    public FsmVariableTracker(Func<string, IReadOnlyList<Fsm>> getLiveInstances)
    {
        _getLiveInstances = getLiveInstances;
    }

    public void TrackVariable(string fsmKey, string variableName) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, variableName, null, -1, null));

    public void TrackActionField(string fsmKey, string stateName, int actionIndex, string fieldName) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName));

    private void AddIfAbsent(TrackedVariablePath path)
    {
        if (!_tracked.Contains(path))
        {
            _tracked.Add(path);
        }
    }

    public void UntrackVariable(string fsmKey, string variableName) =>
        _tracked.Remove(new TrackedVariablePath(fsmKey, variableName, null, -1, null));

    public void UntrackActionField(string fsmKey, string stateName, int actionIndex, string fieldName) =>
        _tracked.Remove(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName));

    // Not cached - resolved fresh every call, so the returned values are always current.
    public IReadOnlyList<TrackedVariableValue> GetTracked()
    {
        var results = new List<TrackedVariableValue>(_tracked.Count);

        foreach (TrackedVariablePath path in _tracked)
        {
            Fsm? fsm = _getLiveInstances(path.FsmKey).FirstOrDefault();
            results.Add(new TrackedVariableValue
            {
                FsmKey = path.FsmKey,
                DisplayLabel = path.DisplayLabel,
                CurrentValue = fsm == null ? "<no live instance>" : ResolveCurrentValue(fsm, path),
            });
        }

        return results;
    }

    private static string ResolveCurrentValue(Fsm fsm, TrackedVariablePath path)
    {
        if (path.VariableName != null)
        {
            NamedVariable? variable = fsm.Variables.FindVariable(path.VariableName);
            return variable == null ? "<not found>" : FormatValue(variable.RawValue);
        }

        FsmState? state = fsm.GetState(path.StateName!);
        if (state == null || path.ActionIndex < 0 || path.ActionIndex >= state.Actions.Length)
        {
            return "<not found>";
        }

        FsmStateAction action = state.Actions[path.ActionIndex];
        FieldInfo? field = action.GetType().GetField(path.FieldName!, BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
        {
            return "<not found>";
        }

        object? value = field.GetValue(action);
        return FormatValue(value is NamedVariable nv ? nv.RawValue : value);
    }

    private static string FormatValue(object? value) => value?.ToString() ?? "null";
}

internal sealed class TrackedVariablePath : IEquatable<TrackedVariablePath>
{
    public string FsmKey { get; }
    public string? VariableName { get; }
    public string? StateName { get; }
    public int ActionIndex { get; }
    public string? FieldName { get; }

    public TrackedVariablePath(string fsmKey, string? variableName, string? stateName, int actionIndex, string? fieldName)
    {
        FsmKey = fsmKey;
        VariableName = variableName;
        StateName = stateName;
        ActionIndex = actionIndex;
        FieldName = fieldName;
    }

    public string DisplayLabel => VariableName != null
        ? $"{FsmKey} / {VariableName}"
        : $"{FsmKey} / {StateName}[{ActionIndex}].{FieldName}";

    public bool Equals(TrackedVariablePath? other) =>
        other != null
        && FsmKey == other.FsmKey
        && VariableName == other.VariableName
        && StateName == other.StateName
        && ActionIndex == other.ActionIndex
        && FieldName == other.FieldName;

    public override bool Equals(object? obj) => Equals(obj as TrackedVariablePath);

    public override int GetHashCode() => HashCode.Combine(FsmKey, VariableName, StateName, ActionIndex, FieldName);
}

internal sealed class TrackedVariableValue
{
    public string FsmKey = "";
    public string DisplayLabel = "";
    public string CurrentValue = "";
}
