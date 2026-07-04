using System;
using System.Collections.Generic;
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

    // Bumped on every actual add/remove (not on a no-op untrack of something not tracked) -
    // lets a UI panel (FsmMonitorPanel) cheaply detect "did the tracked set change" without
    // diffing the list every frame, so it only needs to rebuild its row structure on a real
    // change and can otherwise just refresh already-built rows' values.
    public int Version { get; private set; }

    public FsmVariableTracker(Func<string, IReadOnlyList<Fsm>> getLiveInstances)
    {
        _getLiveInstances = getLiveInstances;
    }

    public void TrackVariable(string fsmKey, string variableName) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, variableName, null, -1, null));

    public void TrackActionField(string fsmKey, string stateName, int actionIndex, string fieldName) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName));

    // Watches whether a specific state is the FSM's current active state, rather than a
    // variable/action-field value - distinguished from TrackActionField's path shape by
    // ActionIndex/FieldName staying at their "unused" sentinel (-1/null) here, since a real
    // action-field track always has ActionIndex >= 0 and a non-null FieldName.
    public void TrackState(string fsmKey, string stateName) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, null, stateName, -1, null));

    // One element of an FsmArray-typed variable, distinguished from a whole-variable track by
    // ArrayIndex being set - see TrackedVariablePath.DisplayLabel/ResolveCurrentValue.
    public void TrackVariableArrayElement(string fsmKey, string variableName, int arrayIndex) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, variableName, null, -1, null, arrayIndex));

    // One element of an action field's Fsm<Type>[] array, distinguished from a whole-field track by
    // ArrayIndex being set.
    public void TrackActionFieldArrayElement(string fsmKey, string stateName, int actionIndex, string fieldName, int arrayIndex) =>
        AddIfAbsent(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName, arrayIndex));

    private void AddIfAbsent(TrackedVariablePath path)
    {
        if (!_tracked.Contains(path))
        {
            _tracked.Add(path);
            Version++;
        }
    }

    public void UntrackVariable(string fsmKey, string variableName)
    {
        if (_tracked.Remove(new TrackedVariablePath(fsmKey, variableName, null, -1, null)))
        {
            Version++;
        }
    }

    public void UntrackActionField(string fsmKey, string stateName, int actionIndex, string fieldName)
    {
        if (_tracked.Remove(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName)))
        {
            Version++;
        }
    }

    public void UntrackState(string fsmKey, string stateName)
    {
        if (_tracked.Remove(new TrackedVariablePath(fsmKey, null, stateName, -1, null)))
        {
            Version++;
        }
    }

    public void UntrackVariableArrayElement(string fsmKey, string variableName, int arrayIndex)
    {
        if (_tracked.Remove(new TrackedVariablePath(fsmKey, variableName, null, -1, null, arrayIndex)))
        {
            Version++;
        }
    }

    public void UntrackActionFieldArrayElement(string fsmKey, string stateName, int actionIndex, string fieldName, int arrayIndex)
    {
        if (_tracked.Remove(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName, arrayIndex)))
        {
            Version++;
        }
    }

    public bool IsVariableTracked(string fsmKey, string variableName) =>
        _tracked.Contains(new TrackedVariablePath(fsmKey, variableName, null, -1, null));

    public bool IsActionFieldTracked(string fsmKey, string stateName, int actionIndex, string fieldName) =>
        _tracked.Contains(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName));

    public bool IsStateTracked(string fsmKey, string stateName) =>
        _tracked.Contains(new TrackedVariablePath(fsmKey, null, stateName, -1, null));

    public bool IsVariableArrayElementTracked(string fsmKey, string variableName, int arrayIndex) =>
        _tracked.Contains(new TrackedVariablePath(fsmKey, variableName, null, -1, null, arrayIndex));

    public bool IsActionFieldArrayElementTracked(string fsmKey, string stateName, int actionIndex, string fieldName, int arrayIndex) =>
        _tracked.Contains(new TrackedVariablePath(fsmKey, null, stateName, actionIndex, fieldName, arrayIndex));

    // Called every frame by FsmMonitorPanel.RefreshRows, so the returned values are always current -
    // but the buffer/entries themselves are reused across calls (grown/shrunk to match _tracked.Count)
    // rather than reallocated, since a fresh List<TrackedVariableValue> plus one new
    // TrackedVariableValue per tracked path, every frame, was pure GC churn for data that's about to be
    // overwritten again next frame anyway.
    private readonly List<TrackedVariableValue> _resultBuffer = new();

    public IReadOnlyList<TrackedVariableValue> GetTracked()
    {
        while (_resultBuffer.Count < _tracked.Count)
        {
            _resultBuffer.Add(new TrackedVariableValue());
        }

        if (_resultBuffer.Count > _tracked.Count)
        {
            _resultBuffer.RemoveRange(_tracked.Count, _resultBuffer.Count - _tracked.Count);
        }

        for (int i = 0; i < _tracked.Count; i++)
        {
            TrackedVariablePath path = _tracked[i];

            // Manual indexer access instead of LINQ's FirstOrDefault(): _getLiveInstances returns
            // IReadOnlyList<Fsm>, which doesn't implement IList<T>, so FirstOrDefault can't take its
            // fast path and instead boxes an enumerator every call.
            IReadOnlyList<Fsm> instances = _getLiveInstances(path.FsmKey);
            Fsm? fsm = instances.Count > 0 ? instances[0] : null;

            TrackedVariableValue value = _resultBuffer[i];
            value.FsmKey = path.FsmKey;
            value.DisplayLabel = path.DisplayLabel;
            value.CurrentValue = fsm == null ? "<no live instance>" : ResolveCurrentValue(fsm, path);
        }

        return _resultBuffer;
    }

    private static string ResolveCurrentValue(Fsm fsm, TrackedVariablePath path)
    {
        if (path.VariableName != null)
        {
            NamedVariable? variable = fsm.Variables.FindVariable(path.VariableName);
            if (variable == null)
            {
                return "<not found>";
            }

            if (path.ArrayIndex is int variableArrayIndex)
            {
                if (variable is not FsmArray array || variableArrayIndex >= array.Length)
                {
                    return "<not found>";
                }

                return FsmEditManager.FormatArrayElement(array.ElementType, array.Get(variableArrayIndex));
            }

            return FormatValue(variable.RawValue);
        }

        // State watch (no field to read) - checked before the action-field lookup below, which
        // assumes ActionIndex >= 0.
        if (path.ActionIndex < 0 && path.FieldName == null)
        {
            FsmState? watchedState = fsm.GetState(path.StateName!);
            return watchedState == null ? "<not found>" : (fsm.ActiveStateName == path.StateName ? "Active" : "Inactive");
        }

        FsmState? state = fsm.GetState(path.StateName!);
        if (state == null || path.ActionIndex < 0 || path.ActionIndex >= state.Actions.Length)
        {
            return "<not found>";
        }

        FsmStateAction action = state.Actions[path.ActionIndex];
        FieldInfo? field = action.GetType().GetField(path.FieldName!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            return "<not found>";
        }

        object? value = field.GetValue(action);

        if (path.ArrayIndex is int fieldArrayIndex)
        {
            if (value is not Array fieldArray || fieldArrayIndex >= fieldArray.Length)
            {
                return "<not found>";
            }

            object? element = fieldArray.GetValue(fieldArrayIndex);
            return FormatValue(element is NamedVariable elementNv ? elementNv.RawValue : element);
        }

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

    // Set only for a single array-element track (either a VariableName's FsmArray element or a
    // FieldName's Fsm<Type>[] element) - null for every whole-variable/whole-field/state track above,
    // which is what keeps those shapes distinguishable from an element track sharing the same name.
    public int? ArrayIndex { get; }

    // Computed once here rather than as a property re-formatted on every access - a path's identity
    // fields never change after construction, so there's no reason to re-run the string interpolation
    // every time GetTracked() reads it (i.e. every frame while this path stays tracked).
    public string DisplayLabel { get; }

    public TrackedVariablePath(string fsmKey, string? variableName, string? stateName, int actionIndex, string? fieldName, int? arrayIndex = null)
    {
        FsmKey = fsmKey;
        VariableName = variableName;
        StateName = stateName;
        ActionIndex = actionIndex;
        FieldName = fieldName;
        ArrayIndex = arrayIndex;

        DisplayLabel = VariableName != null
            ? (arrayIndex is int variableArrayIndex ? $"{fsmKey} / {variableName}[{variableArrayIndex}]" : $"{fsmKey} / {variableName}")
            : FieldName != null
                ? (arrayIndex is int fieldArrayIndex ? $"{fsmKey} / {stateName}[{actionIndex}].{fieldName}[{fieldArrayIndex}]" : $"{fsmKey} / {stateName}[{actionIndex}].{fieldName}")
                : $"{fsmKey} / {stateName} (state)";
    }

    public bool Equals(TrackedVariablePath? other) =>
        other != null
        && FsmKey == other.FsmKey
        && VariableName == other.VariableName
        && StateName == other.StateName
        && ActionIndex == other.ActionIndex
        && FieldName == other.FieldName
        && ArrayIndex == other.ArrayIndex;

    public override bool Equals(object? obj) => Equals(obj as TrackedVariablePath);

    public override int GetHashCode() => HashCode.Combine(FsmKey, VariableName, StateName, ActionIndex, FieldName, ArrayIndex);
}

internal sealed class TrackedVariableValue
{
    public string FsmKey = "";
    public string DisplayLabel = "";
    public string CurrentValue = "";
}
