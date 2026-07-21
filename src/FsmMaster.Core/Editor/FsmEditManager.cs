using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// The generalized edit/undo engine every FsmMaster mutation funnels through: variable overrides, action
// field overrides, state neutering, transition retargeting/disabling, and sequencer installs. Each FsmKey
// (see FsmIdentity) gets its pristine values captured lazily on first touch, so ResetFsm can restore a live
// FSM without a scene reload. All mutation is done via PlayMakerFsmOps's existing extension methods
// rather than re-implementing state/transition/action bookkeeping it already provides.
internal sealed class FsmEditManager
{
    private static readonly string[] ExitEventPriority = { "CANCEL", "FINISHED", "NEXT" };

    private static readonly HashSet<VariableType> SupportedVariableTypes = new()
    {
        VariableType.Float, VariableType.Int, VariableType.Bool, VariableType.String,
        VariableType.Vector2, VariableType.Vector3, VariableType.Rect, VariableType.Quaternion,
        VariableType.Color, VariableType.Enum,
    };

    // Element types supported for per-element editing of an Array-typed FSM variable. Narrower than
    // SupportedVariableTypes on purpose: FsmArray.Values stores every element as a boxed primitive
    // keyed only by ElementType (HutongGames.PlayMaker.FsmArray - Values/Get/Set/SaveChanges), with no
    // per-element NamedVariable wrapper to delegate to - vector/rect/quaternion/color-typed arrays pack
    // their elements as Vector4 internally, which isn't verified safe to round-trip through the same
    // plain-float-list format FormatFloats/ParseFloats use for a whole (non-array) variable of those
    // types, so those stay read-only for now rather than risk a wrong cast.
    private static readonly HashSet<VariableType> SupportedArrayElementTypes = new()
    {
        VariableType.Float, VariableType.Int, VariableType.Bool, VariableType.String,
    };

    private readonly IFsmLog _logger;
    private readonly Dictionary<string, List<Fsm>> _liveInstances = new();
    private readonly Dictionary<string, FsmPristineSnapshot> _pristine = new();

    // The edits currently in effect per FsmKey, as opposed to _pristine's "what to restore" - this is what a
    // caller (e.g. a "save all changes" action) serializes to FsmSaveDataStore.
    private readonly Dictionary<string, FsmEditSet> _activeEdits = new();

    // Per-FsmKey undo history - each entry is a closure that replays whichever manager call reverts the
    // edit that pushed it (e.g. undoing a SetVariable call re-invokes SetVariable with the prior value).
    // _isUndoing suppresses PushUndo while an entry is replaying, so popping one never pushes a new one.
    private readonly Dictionary<string, Stack<Action>> _undoStacks = new();
    private bool _isUndoing;

    // FsmKeys with an edit that couldn't be physically installed on some live instance yet because that
    // instance's owning GameObject hasn't activated (InstallSequencer/DisableState both hit this - see
    // their own comments). Retried every frame from PollPendingActivations rather than relying on a
    // one-shot Harmony hook on Fsm's own Preprocess(): neither fsm.Preprocessed nor a Preprocess()
    // postfix reliably predicts the moment a given FsmStateAction is actually safe to touch (see
    // InstallSequencer's comment on state.Actions' lazy-load timing), whereas re-running the real apply
    // call is correct regardless of why the earlier attempt didn't stick.
    private readonly HashSet<string> _pendingActivationFsmKeys = new();

    // Bumped by every method that actually mutates a live Fsm (variable/action-field overrides, state
    // neutering, transition retargeting, sequencer installs, and ResetFsm's restore) - FsmGraphOverlay
    // compares this against the value it last rebuilt its node/transition layout cache from, so a live
    // edit made from outside the graph itself (the right panel's Save/Load/Undo/Reset buttons, or a
    // saved edit set reapplied on scene load) still shows up without the graph's own explicit
    // InvalidateGraphCaches() call, which only covers edits the graph made directly (drag/right-click).
    public int EditGeneration { get; private set; }

    private void BumpEditGeneration() => EditGeneration++;

    public FsmEditManager(IFsmLog logger)
    {
        _logger = logger;
    }

    // Called whenever the plugin (re)discovers live FSMs (Awake, each sceneLoaded) - wholesale
    // replacement of every tracked FsmKey's live instance list, not just the keys present in this
    // scan. The caller always passes every PlayMakerFSM currently alive (FindObjectsByType scans the
    // whole loaded object graph, not just whichever scene just finished loading), so any FsmKey
    // missing from instancesByKey is genuinely gone. Leaving a missing key's old list in place would
    // keep FsmVariableTracker (and anything else calling GetLiveInstances) reading frozen values off
    // Fsm instances whose owning GameObject was already destroyed - Fsm is a plain C# object, not a
    // UnityEngine.Object, so it never becomes Unity's fake-null and nothing else would ever catch
    // this.
    public void ReplaceLiveInstances(Dictionary<string, List<Fsm>> instancesByKey)
    {
        _liveInstances.Clear();
        foreach (KeyValuePair<string, List<Fsm>> entry in instancesByKey)
        {
            _liveInstances[entry.Key] = entry.Value;
        }

        PruneStaleSnapshotEntries();
    }

    // Drops InjectedExitActions/InstalledSequencers entries left behind by a previous scene visit.
    // Each scene (re)load hands DisableState/InstallSequencer a brand new PlayMakerFSM/Fsm/FsmState/
    // FsmStateAction object graph for the same FsmKey (see FsmIdentity), and both of those methods
    // unconditionally append to these lists rather than replace - without this, reapplying a persisted
    // DisableState/Sequencer edit on every revisit (see FsmMasterPlugin.ApplyPersistedEditsForScene)
    // would keep appending forever while the previous scene's now-destroyed Fsm graph stays reachable,
    // and therefore un-collectible, through the reference this class itself holds. Called right after
    // _liveInstances is replaced above, so "alive" always reflects the freshly (re)scanned scene.
    private void PruneStaleSnapshotEntries()
    {
        foreach (KeyValuePair<string, FsmPristineSnapshot> entry in _pristine)
        {
            var alive = new HashSet<Fsm>(GetLiveInstances(entry.Key));

            entry.Value.InjectedExitActions.RemoveAll(action => !alive.Contains(action.State.Fsm));
            entry.Value.InstalledSequencers.RemoveAll(pair => !alive.Contains(pair.Sequencer.State.Fsm));
        }
    }

    public List<Fsm> GetLiveInstances(string fsmKey) =>
        _liveInstances.TryGetValue(fsmKey, out List<Fsm>? instances) ? instances : new List<Fsm>();

    // Called by FsmActivatedPatch's Postfix (FsmMasterPlugin.cs) right before it reapplies a pending edit
    // set, to keep _liveInstances in sync for FSMs built from an FsmTemplate. PlayMakerFSM.Awake() calls
    // InitTemplate(), which replaces the component's Fsm field with a brand new Fsm object wrapping the
    // template the moment its owning GameObject first activates - the placeholder Fsm the initial
    // FindObjectsByType scan captured for this component
    // (see ReplaceLiveInstances) becomes an orphaned object nothing in PlayMaker will ever run again.
    // Without this, ApplyEditSet keeps reapplying edits to that discarded reference while the real,
    // just-activated fsm never receives them - matching how FsmDataCollector.CollectFsmInfo instead always
    // re-reads component.Fsm live rather than caching it, which is why editing the same FSM through the
    // panel while it's still inactive already behaves correctly. Matches the existing entry to replace by
    // owning PlayMakerFSM component, not by Fsm reference, since the reference is exactly what changed.
    public void ReconcileLiveInstance(string fsmKey, Fsm fsm)
    {
        if (!_liveInstances.TryGetValue(fsmKey, out List<Fsm>? instances))
        {
            instances = new List<Fsm>();
            _liveInstances[fsmKey] = instances;
        }

        instances.RemoveAll(existing => existing != fsm && existing.FsmComponent == fsm.FsmComponent);

        if (!instances.Contains(fsm))
        {
            instances.Add(fsm);
        }
    }

    // Called every frame from FsmMasterPlugin.Update() - retries every FsmKey that InstallSequencer or
    // DisableState had to leave partly unapplied because some live instance's owning GameObject hadn't
    // activated yet. This exists instead of relying solely on FsmActivatedPatch's one-shot Postfix
    // because neither fsm.Preprocessed nor a Preprocess() hook has proven to reliably fire/predict the
    // moment a given instance is actually safe to touch (see InstallSequencer's own comment) - re-running
    // the real apply call converges regardless of why the earlier attempt didn't stick.
    public void PollPendingActivations()
    {
        if (_pendingActivationFsmKeys.Count == 0)
        {
            return;
        }

        foreach (string fsmKey in _pendingActivationFsmKeys.ToArray())
        {
            _pendingActivationFsmKeys.Remove(fsmKey);

            if (GetActiveEditSet(fsmKey) is not { } editSet)
            {
                continue;
            }

            // Mirrors what FsmActivatedPatch's Postfix does for the one instance it fires for: an
            // instance built from an FsmTemplate gets a brand new Fsm object swapped into its owning
            // PlayMakerFSM the moment it first activates (see ReconcileLiveInstance's own comment), so
            // the Fsm reference already sitting in _liveInstances can be an orphaned placeholder even once
            // the object is active - re-read every tracked component's current Fsm fresh (PlayMakerFSM.Fsm
            // is never cached, unlike this class's own list) and reconcile before reapplying.
            foreach (Fsm stale in GetLiveInstances(fsmKey).ToArray())
            {
                if (stale.FsmComponent is { } component && component.Fsm is { } fresh)
                {
                    ReconcileLiveInstance(fsmKey, fresh);
                }
            }

            ApplyEditSet(editSet);
        }
    }

    // Every FsmKey with at least one edit currently in effect this session.
    public ICollection<string> GetEditedFsmKeys() => _activeEdits.Keys;

    public FsmEditSet? GetActiveEditSet(string fsmKey) =>
        _activeEdits.TryGetValue(fsmKey, out FsmEditSet? set) ? set : null;

    // Drops fsmKey's _activeEdits entry once every override list on it has been emptied back out -
    // called after EnableState/EnableTransition/RemoveSequencer remove what may have been the only edit
    // recorded for this key, so GetEditedFsmKeys (and anything reading it, e.g. FsmGraphOverlay's "Fsm
    // Edits Active" indicator) stops reporting a key that no longer has any edit in effect. Ad-hoc
    // SetVariable/SetActionField overrides also call this once their value has been dialed back to match
    // the pristine one, so manually undoing an edit by hand (without the Reset button) clears the
    // indicator - and the saved edit set - the same way Reset does.
    private void PruneActiveEditSetIfEmpty(string fsmKey)
    {
        if (_activeEdits.TryGetValue(fsmKey, out FsmEditSet? set) && set.IsEmpty)
        {
            _activeEdits.Remove(fsmKey);
        }
    }

    // Records editSet as the active edit set for its FsmKey without touching any live Fsm - lets a caller
    // that knows what edits *should* be in effect for a key before any matching instance exists yet (e.g.
    // DebugModCompat priming a savestate's target edits before Silksong tears down and rebuilds the room)
    // register that intent early. FsmActivatedPatch's Postfix (FsmMasterPlugin.cs) already calls
    // GetActiveEditSet/ApplyEditSet for every Fsm the moment it finishes Preprocess(), so once this has run,
    // the very first instance built for fsmKey picks up the primed edits before its own Start()/OnEnter -
    // no separate apply pass, and nothing left to race.
    public void PrimeActiveEditSet(FsmEditSet editSet) => _activeEdits[editSet.FsmKey] = editSet;

    // Applies every variable override, action field override, disabled state, transition retarget, and
    // sequencer override in editSet to every live Fsm instance sharing editSet.FsmKey.
    public void ApplyEditSet(FsmEditSet editSet)
    {
        if (!_liveInstances.TryGetValue(editSet.FsmKey, out List<Fsm>? instances) || instances.Count == 0)
        {
            _logger.LogWarning($"[FsmMaster] No live instances found for fsm key '{editSet.FsmKey}'; skipping edit set.");
            return;
        }

        // editSet is frequently the exact same FsmEditSet instance tracked in _activeEdits (e.g.
        // FsmActivatedPatch's Postfix calls GetActiveEditSet then feeds the result straight back in
        // here), and every Apply*/DisableState/InstallSequencer call below mutates that live instance's
        // own override lists via RemoveAll+Add. Enumerating editSet's lists directly in that case means
        // mutating a List<T> while foreach is iterating it, which throws InvalidOperationException on
        // the very first entry. Snapshotting each list up front decouples the enumeration from whatever
        // GetOrCreateActiveEditSet(fsmKey) further down mutates, whether or not it's the same object.
        var variableOverrides = editSet.VariableOverrides.ToArray();
        var actionFieldOverrides = editSet.ActionFieldOverrides.ToArray();
        var disabledStates = editSet.DisabledStates.ToArray();
        var transitionRetargets = editSet.TransitionRetargets.ToArray();
        var sequencerOverrides = editSet.SequencerOverrides.ToArray();

        foreach (Fsm fsm in instances)
        {
            foreach (VariableOverride ov in variableOverrides)
            {
                ApplyVariableOverride(editSet.FsmKey, fsm, ov);
            }

            foreach (ActionFieldOverride ov in actionFieldOverrides)
            {
                ApplyActionFieldOverride(editSet.FsmKey, fsm, ov);
            }

            foreach (string stateName in disabledStates)
            {
                DisableState(editSet.FsmKey, fsm, stateName);
            }

            foreach (TransitionRetarget retarget in transitionRetargets)
            {
                ApplyTransitionRetarget(editSet.FsmKey, fsm, retarget);
            }

            foreach (SequencerOverride seq in sequencerOverrides)
            {
                InstallSequencer(editSet.FsmKey, fsm, seq);
            }
        }
    }

    // Restores every live instance of fsmKey to its pristine snapshot and drops all edit/undo bookkeeping
    // for that key, so a subsequent edit starts from a clean slate.
    public void ResetFsm(string fsmKey)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            return;
        }

        _liveInstances.TryGetValue(fsmKey, out List<Fsm>? instances);
        RestoreSnapshot(snapshot, instances ?? new List<Fsm>());
        _pristine.Remove(fsmKey);
        _activeEdits.Remove(fsmKey);
        _undoStacks.Remove(fsmKey);
        BumpEditGeneration();
    }

    // Reverts every edit applied this session without touching persisted JSON, so a ScriptEngine hot-reload
    // unload/reload cycle doesn't leave live FSMs double-patched.
    public void RevertAllForUnload()
    {
        foreach (KeyValuePair<string, FsmPristineSnapshot> entry in _pristine)
        {
            _liveInstances.TryGetValue(entry.Key, out List<Fsm>? instances);
            RestoreSnapshot(entry.Value, instances ?? new List<Fsm>());
        }

        _pristine.Clear();
        _liveInstances.Clear();
        _activeEdits.Clear();
        _undoStacks.Clear();
    }

    // ---- Undo ----

    public bool HasUndo(string fsmKey) => _undoStacks.TryGetValue(fsmKey, out Stack<Action>? stack) && stack.Count > 0;

    // Pops and replays the most recent undoable edit for fsmKey. Replayed entries are themselves calls
    // back into this same class's edit methods (e.g. SetVariable with the prior value), so _isUndoing
    // guards PushUndo for the duration to stop that replay from pushing a fresh entry onto the stack.
    public void Undo(string fsmKey)
    {
        if (!_undoStacks.TryGetValue(fsmKey, out Stack<Action>? stack) || stack.Count == 0)
        {
            return;
        }

        Action undo = stack.Pop();
        _isUndoing = true;
        try
        {
            undo();
        }
        finally
        {
            _isUndoing = false;
        }
    }

    private void PushUndo(string fsmKey, Action undo)
    {
        if (_isUndoing)
        {
            return;
        }

        if (!_undoStacks.TryGetValue(fsmKey, out Stack<Action>? stack))
        {
            stack = new Stack<Action>();
            _undoStacks[fsmKey] = stack;
        }

        stack.Push(undo);
    }

    // ---- Ad-hoc single-value edits (the UI's own entry points) ----

    // Applies one variable edit to every live instance sharing fsmKey (the same per-instance loop
    // shape ApplyEditSet already uses for a whole edit set) - the entry point FsmActiveStatePanel's
    // text fields/toggle buttons call directly, without building a full FsmEditSet themselves.
    public void SetVariable(string fsmKey, string variableName, string variableType, string stringValue)
    {
        string? previousValue = GetCurrentVariableValue(fsmKey, variableName);

        var ov = new VariableOverride { VariableType = variableType, Name = variableName, StringValue = stringValue };
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyVariableOverride(fsmKey, fsm, ov);
        }

        if (previousValue != null)
        {
            PushUndo(fsmKey, () => SetVariable(fsmKey, variableName, variableType, previousValue));
        }
    }

    // Same shape as SetVariable, for one field on a specific state's action instead of a whole variable.
    public void SetActionField(string fsmKey, string stateName, int actionIndex, string expectedActionTypeName, string fieldName, string stringValue)
    {
        string? previousValue = GetCurrentActionFieldValue(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName);

        var ov = new ActionFieldOverride
        {
            StateName = stateName,
            ActionIndex = actionIndex,
            ExpectedActionTypeName = expectedActionTypeName,
            FieldName = fieldName,
            StringValue = stringValue,
        };
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyActionFieldOverride(fsmKey, fsm, ov);
        }

        if (previousValue != null)
        {
            PushUndo(fsmKey, () => SetActionField(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, previousValue));
        }
    }

    // Same shape as SetVariable, for one element of an Array-typed variable - see
    // ApplyVariableArrayElementOverride and SupportedArrayElementTypes for what's actually editable.
    public void SetVariableArrayElement(string fsmKey, string variableName, int arrayIndex, string elementStringValue)
    {
        string? previousValue = GetCurrentVariableArrayElementValue(fsmKey, variableName, arrayIndex);

        var ov = new VariableOverride
        {
            VariableType = VariableType.Array.ToString(),
            Name = variableName,
            ArrayIndex = arrayIndex,
            StringValue = elementStringValue,
        };
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyVariableOverride(fsmKey, fsm, ov);
        }

        if (previousValue != null)
        {
            PushUndo(fsmKey, () => SetVariableArrayElement(fsmKey, variableName, arrayIndex, previousValue));
        }
    }

    // Same shape as SetActionField, for one element of a Fsm<Type>[]-typed field.
    public void SetActionFieldArrayElement(string fsmKey, string stateName, int actionIndex, string expectedActionTypeName, string fieldName, int arrayIndex, string elementStringValue)
    {
        string? previousValue = GetCurrentActionFieldArrayElementValue(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, arrayIndex);

        var ov = new ActionFieldOverride
        {
            StateName = stateName,
            ActionIndex = actionIndex,
            ExpectedActionTypeName = expectedActionTypeName,
            FieldName = fieldName,
            ArrayIndex = arrayIndex,
            StringValue = elementStringValue,
        };
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyActionFieldOverride(fsmKey, fsm, ov);
        }

        if (previousValue != null)
        {
            PushUndo(fsmKey, () => SetActionFieldArrayElement(fsmKey, stateName, actionIndex, expectedActionTypeName, fieldName, arrayIndex, previousValue));
        }
    }

    // ---- Undo-capture lookups ----
    // Read the current live value the same way the edit path itself does (FormatNamedVariable/
    // FormatArrayElement/TryFormatValue), so the value handed to PushUndo round-trips through the same
    // parser the forward edit used. Return null (skip pushing undo) rather than guessing when nothing
    // live matches - e.g. the field/variable was never found, matching how the edit methods above already
    // no-op with a warning in that case.

    private string? GetCurrentVariableValue(string fsmKey, string variableName)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            NamedVariable? variable = fsm.Variables.FindVariable(variableName);
            if (variable != null && SupportedVariableTypes.Contains(variable.VariableType))
            {
                return FormatNamedVariable(variable);
            }
        }

        return null;
    }

    private string? GetCurrentVariableArrayElementValue(string fsmKey, string variableName, int arrayIndex)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            if (fsm.Variables.FindVariable(variableName) is FsmArray array && arrayIndex < array.Length)
            {
                return FormatArrayElement(array.ElementType, array.Get(arrayIndex));
            }
        }

        return null;
    }

    private string? GetCurrentActionFieldValue(string fsmKey, string stateName, int actionIndex, string expectedActionTypeName, string fieldName)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            if (TryFindActionField(fsm, stateName, actionIndex, expectedActionTypeName, fieldName, out FsmStateAction? action, out FieldInfo? field)
                && TryFormatValue(field.GetValue(action), out string formatted))
            {
                return formatted;
            }
        }

        return null;
    }

    private string? GetCurrentActionFieldArrayElementValue(string fsmKey, string stateName, int actionIndex, string expectedActionTypeName, string fieldName, int arrayIndex)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            if (TryFindActionField(fsm, stateName, actionIndex, expectedActionTypeName, fieldName, out FsmStateAction? action, out FieldInfo? field)
                && field.GetValue(action) is Array array
                && arrayIndex < array.Length
                && TryFormatValue(array.GetValue(arrayIndex), out string formatted))
            {
                return formatted;
            }
        }

        return null;
    }

    // Looks up the action at (stateName, actionIndex) and the named field on it, failing if the action's
    // runtime type no longer matches expectedActionTypeName (the FSM was edited since the override was
    // recorded) or the field doesn't exist.
    private static bool TryFindActionField(Fsm fsm, string stateName, int actionIndex, string expectedActionTypeName, string fieldName,
        [NotNullWhen(true)] out FsmStateAction? action, [NotNullWhen(true)] out FieldInfo? field)
    {
        action = null;
        field = null;

        FsmState? state = fsm.GetState(stateName);
        if (state == null || actionIndex < 0 || actionIndex >= state.Actions.Length)
        {
            return false;
        }

        FsmStateAction candidate = state.Actions[actionIndex];
        if (candidate.GetType().Name != expectedActionTypeName)
        {
            return false;
        }

        FieldInfo? candidateField = candidate.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (candidateField == null)
        {
            return false;
        }

        action = candidate;
        field = candidateField;
        return true;
    }

    // ---- Variables ----

    // Applies one variable override to a single live Fsm instance: captures the pristine value on first
    // touch, assigns the new value, and records the override in that instance's active edit set.
    public void ApplyVariableOverride(string fsmKey, Fsm fsm, VariableOverride ov)
    {
        NamedVariable? variable = fsm.Variables.FindVariable(ov.Name);
        if (variable == null)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' not found on fsm '{fsm.Name}'; skipping.");
            return;
        }

        if (ov.ArrayIndex >= 0)
        {
            ApplyVariableArrayElementOverride(fsmKey, fsm, variable, ov);
            return;
        }

        if (!SupportedVariableTypes.Contains(variable.VariableType))
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' has unsupported type '{variable.VariableType}' (object/array-reference variables are out of scope for this pass); skipping.");
            return;
        }

        if (variable.VariableType.ToString() != ov.VariableType)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' is now type '{variable.VariableType}', expected '{ov.VariableType}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        VariableOverride? pristineEntry = snapshot.OriginalValues.VariableOverrides.Find(v => v.Name == ov.Name && v.ArrayIndex == -1);
        if (pristineEntry == null)
        {
            pristineEntry = new VariableOverride
            {
                VariableType = variable.VariableType.ToString(),
                Name = ov.Name,
                StringValue = FormatNamedVariable(variable),
            };
            snapshot.OriginalValues.VariableOverrides.Add(pristineEntry);
        }

        AssignNamedVariable(variable, ov.StringValue);

        // Re-read the live value through the same canonical formatter the pristine snapshot was captured
        // with (rather than string-comparing ov.StringValue directly), since a user-typed value can differ
        // textually from FormatNamedVariable's output (e.g. "1.0" vs "1") while still being the same value.
        // If the edit now matches pristine, drop it from the active set instead of recording it, so manually
        // dialing a variable back to its original value clears the "Fsm Edits Active" indicator and the
        // saved edit set the same way the Reset button does.
        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.VariableOverrides.RemoveAll(v => v.Name == ov.Name && v.ArrayIndex == -1);
        if (FormatNamedVariable(variable) != pristineEntry.StringValue)
        {
            active.VariableOverrides.Add(ov);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        BumpEditGeneration();
    }

    // ---- Array elements ----
    // FsmArray (variable-typed arrays) and Fsm<Type>[] (action-field arrays) are unrelated shapes -
    // FsmArray stores every element as a boxed primitive keyed by its own ElementType (no per-element
    // NamedVariable to delegate to), while a Fsm<Type>[] field's elements already ARE NamedVariable
    // instances the same as any other field - see AssignArrayElement vs. AssignActionFieldArrayElement.

    private void ApplyVariableArrayElementOverride(string fsmKey, Fsm fsm, NamedVariable variable, VariableOverride ov)
    {
        if (variable is not FsmArray array)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' is not an array; skipping element edit.");
            return;
        }

        if (!SupportedArrayElementTypes.Contains(array.ElementType))
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' has unsupported array element type '{array.ElementType}'; skipping.");
            return;
        }

        if (ov.ArrayIndex >= array.Length)
        {
            _logger.LogWarning($"[FsmMaster] Variable '{ov.Name}' on fsm '{fsm.Name}' array index {ov.ArrayIndex} is out of range (length {array.Length}); skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        VariableOverride? pristineEntry = snapshot.OriginalValues.VariableOverrides.Find(v => v.Name == ov.Name && v.ArrayIndex == ov.ArrayIndex);
        if (pristineEntry == null)
        {
            pristineEntry = new VariableOverride
            {
                VariableType = ov.VariableType,
                Name = ov.Name,
                ArrayIndex = ov.ArrayIndex,
                StringValue = FormatArrayElement(array.ElementType, array.Get(ov.ArrayIndex)),
            };
            snapshot.OriginalValues.VariableOverrides.Add(pristineEntry);
        }

        AssignArrayElement(array, ov.ArrayIndex, ov.StringValue);

        // See ApplyVariableOverride's matching comment - compares through the canonical formatter rather
        // than ov.StringValue directly, so dialing an element back to its original value clears the edit.
        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.VariableOverrides.RemoveAll(v => v.Name == ov.Name && v.ArrayIndex == ov.ArrayIndex);
        if (FormatArrayElement(array.ElementType, array.Get(ov.ArrayIndex)) != pristineEntry.StringValue)
        {
            active.VariableOverrides.Add(ov);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        BumpEditGeneration();
    }

    private static void AssignArrayElement(FsmArray array, int index, string stringValue)
    {
        array.Set(index, ParseArrayElement(array.ElementType, stringValue));

        // Set() alone only updates FsmArray's boxed Values[] cache (and, in-editor only, an internal
        // Save call) - SaveChanges is the public call that flushes every boxed value back into the
        // real typed backing arrays (floatValues/intValues/etc.) PlayMaker actions actually read from
        // at runtime.
        array.SaveChanges();
    }

    internal static bool IsSupportedArrayElementType(VariableType elementType) => SupportedArrayElementTypes.Contains(elementType);

    internal static string FormatArrayElement(VariableType elementType, object? rawValue) => elementType switch
    {
        VariableType.Float => Convert.ToSingle(rawValue, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        VariableType.Int => Convert.ToInt32(rawValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        VariableType.Bool => Convert.ToBoolean(rawValue).ToString(CultureInfo.InvariantCulture),
        VariableType.String => rawValue as string ?? "",
        _ => throw new NotSupportedException($"Array element type '{elementType}' is not supported for editing."),
    };

    private static object ParseArrayElement(VariableType elementType, string stringValue) => elementType switch
    {
        VariableType.Float => float.Parse(stringValue, CultureInfo.InvariantCulture),
        VariableType.Int => int.Parse(stringValue, CultureInfo.InvariantCulture),
        VariableType.Bool => bool.Parse(stringValue),
        VariableType.String => stringValue,
        _ => throw new NotSupportedException($"Array element type '{elementType}' is not supported for editing."),
    };

    // ---- Action fields ----

    // Applies one field-level override to a specific action on a single live Fsm instance: captures the
    // pristine value on first touch, assigns the new value via reflection, and records the override in
    // that instance's active edit set.
    public void ApplyActionFieldOverride(string fsmKey, Fsm fsm, ActionFieldOverride ov)
    {
        FsmState? state = fsm.GetState(ov.StateName);
        if (state == null || ov.ActionIndex < 0 || ov.ActionIndex >= state.Actions.Length)
        {
            _logger.LogWarning($"[FsmMaster] State '{ov.StateName}' action index {ov.ActionIndex} not found on fsm '{fsm.Name}'; skipping.");
            return;
        }

        FsmStateAction action = state.Actions[ov.ActionIndex];
        if (action.GetType().Name != ov.ExpectedActionTypeName)
        {
            _logger.LogWarning($"[FsmMaster] Action at '{ov.StateName}'[{ov.ActionIndex}] on fsm '{fsm.Name}' is '{action.GetType().Name}', expected '{ov.ExpectedActionTypeName}'; skipping.");
            return;
        }

        FieldInfo? field = action.GetType().GetField(ov.FieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}' not found on action '{ov.ExpectedActionTypeName}'; skipping.");
            return;
        }

        object? currentValue = field.GetValue(action);

        if (ov.ArrayIndex >= 0)
        {
            ApplyActionFieldArrayElementOverride(fsmKey, ov, currentValue);
            return;
        }

        if (!TryFormatValue(currentValue, out string formattedCurrent))
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}' on '{ov.ExpectedActionTypeName}' has unsupported type '{currentValue?.GetType().Name ?? "null"}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        ActionFieldOverride? pristineEntry = snapshot.OriginalValues.ActionFieldOverrides.Find(f =>
            f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName && f.ArrayIndex == -1);
        if (pristineEntry == null)
        {
            pristineEntry = new ActionFieldOverride
            {
                StateName = ov.StateName,
                ActionIndex = ov.ActionIndex,
                ExpectedActionTypeName = ov.ExpectedActionTypeName,
                FieldName = ov.FieldName,
                StringValue = formattedCurrent,
            };
            snapshot.OriginalValues.ActionFieldOverrides.Add(pristineEntry);
        }

        TryAssignFieldValue(action, field, currentValue, ov.StringValue);

        // See ApplyVariableOverride's matching comment - re-format the live value through the same
        // TryFormatValue path the pristine snapshot was captured with, so dialing a field back to its
        // original value (by hand, without Reset) drops the edit instead of leaving a no-op override behind.
        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.ActionFieldOverrides.RemoveAll(f =>
            f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName && f.ArrayIndex == -1);
        if (!TryFormatValue(field.GetValue(action), out string formattedAfter) || formattedAfter != pristineEntry.StringValue)
        {
            active.ActionFieldOverrides.Add(ov);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        BumpEditGeneration();
    }

    // A Fsm<Type>[] field's elements already ARE NamedVariable instances (PlayMaker action fields never
    // expose a raw primitive array), so this reuses TryFormatValue/AssignNamedVariable exactly as the
    // whole-field path does, just applied to one element instead of the field itself - the field
    // reference never changes.
    private void ApplyActionFieldArrayElementOverride(string fsmKey, ActionFieldOverride ov, object? fieldValue)
    {
        if (fieldValue is not Array array || ov.ArrayIndex >= array.Length)
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}' on '{ov.ExpectedActionTypeName}' has no array element at index {ov.ArrayIndex}; skipping.");
            return;
        }

        object? element = array.GetValue(ov.ArrayIndex);
        if (!TryFormatValue(element, out string formattedCurrent))
        {
            _logger.LogWarning($"[FsmMaster] Field '{ov.FieldName}[{ov.ArrayIndex}]' on '{ov.ExpectedActionTypeName}' has unsupported type '{element?.GetType().Name ?? "null"}'; skipping.");
            return;
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        ActionFieldOverride? pristineEntry = snapshot.OriginalValues.ActionFieldOverrides.Find(f =>
            f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName && f.ArrayIndex == ov.ArrayIndex);
        if (pristineEntry == null)
        {
            pristineEntry = new ActionFieldOverride
            {
                StateName = ov.StateName,
                ActionIndex = ov.ActionIndex,
                ExpectedActionTypeName = ov.ExpectedActionTypeName,
                FieldName = ov.FieldName,
                ArrayIndex = ov.ArrayIndex,
                StringValue = formattedCurrent,
            };
            snapshot.OriginalValues.ActionFieldOverrides.Add(pristineEntry);
        }

        // Guaranteed a NamedVariable of a supported type by the TryFormatValue check above.
        AssignNamedVariable((NamedVariable)element, ov.StringValue);

        // See ApplyVariableOverride's matching comment.
        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.ActionFieldOverrides.RemoveAll(f =>
            f.StateName == ov.StateName && f.ActionIndex == ov.ActionIndex && f.FieldName == ov.FieldName && f.ArrayIndex == ov.ArrayIndex);
        if (!TryFormatValue(element, out string formattedAfter) || formattedAfter != pristineEntry.StringValue)
        {
            active.ActionFieldOverrides.Add(ov);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        BumpEditGeneration();
    }

    // Writes ov's recorded pristine value back onto the live action field (or array element), undoing
    // whatever ApplyActionFieldOverride/ApplyActionFieldArrayElementOverride did. No-ops quietly if the
    // state/action/field no longer matches, since a snapshot can outlive the action it was captured from.
    private static void RestoreActionField(Fsm fsm, ActionFieldOverride ov)
    {
        FsmState? state = fsm.GetState(ov.StateName);
        if (state == null || ov.ActionIndex < 0 || ov.ActionIndex >= state.Actions.Length)
        {
            return;
        }

        FsmStateAction action = state.Actions[ov.ActionIndex];
        FieldInfo? field = action.GetType().GetField(ov.FieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            return;
        }

        object? currentValue = field.GetValue(action);

        if (ov.ArrayIndex >= 0)
        {
            if (currentValue is Array array && ov.ArrayIndex < array.Length && array.GetValue(ov.ArrayIndex) is NamedVariable nv)
            {
                AssignNamedVariable(nv, ov.StringValue);
            }

            return;
        }

        TryAssignFieldValue(action, field, currentValue, ov.StringValue);
    }

    // ---- States ----

    // Applies to every live instance sharing fsmKey - the graph overlay's right-click-to-disable entry
    // point, mirroring the SetVariable/SetActionField "apply to every live instance" shape.
    public void DisableState(string fsmKey, string stateName)
    {
        bool wasAlreadyDisabled = _pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? existing) && existing.NeuteredActionIndices.ContainsKey(stateName);

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            DisableState(fsmKey, fsm, stateName);
        }

        if (!wasAlreadyDisabled)
        {
            PushUndo(fsmKey, () => EnableState(fsmKey, stateName));
        }
    }

    // Undoes one state's DisableState without resetting every other edit this session - re-enables the
    // actions DisableState neutered and removes the exit action it injected, on every live instance, then
    // clears this state's own bookkeeping once.
    public void EnableState(string fsmKey, string stateName)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot) || !snapshot.NeuteredActionIndices.TryGetValue(stateName, out List<int>? neuteredIndices))
        {
            return;
        }

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            FsmState? state = fsm.GetState(stateName);
            if (state == null)
            {
                continue;
            }

            foreach (int i in neuteredIndices)
            {
                if (i >= 0 && i < state.Actions.Length)
                {
                    state.Actions[i].Enabled = true;
                }
            }

            FsmStateAction? injectedExitAction = state.Actions.FirstOrDefault(a => a is SendExitEventAction);
            if (injectedExitAction != null)
            {
                int idx = Array.IndexOf(state.Actions, injectedExitAction);
                if (idx >= 0)
                {
                    state.RemoveAction(idx);
                }

                snapshot.InjectedExitActions.Remove(injectedExitAction);
            }
        }

        snapshot.NeuteredActionIndices.Remove(stateName);

        if (_activeEdits.TryGetValue(fsmKey, out FsmEditSet? active))
        {
            active.DisabledStates.Remove(stateName);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        PushUndo(fsmKey, () => DisableState(fsmKey, stateName));
        BumpEditGeneration();
    }

    // Neuters one state on a single live Fsm instance: disables every action that was enabled and injects
    // a synthetic exit action so the state still transitions out via FindExitEvent's chosen event.
    public void DisableState(string fsmKey, Fsm fsm, string stateName)
    {
        FsmState? state = fsm.GetState(stateName);
        if (state == null)
        {
            _logger.LogWarning($"[FsmMaster] State '{stateName}' not found on fsm '{fsm.Name}'; skipping disable.");
            return;
        }

        if (state.Actions.Any(a => a is SendExitEventAction))
        {
            return; // this instance was already neutered this session
        }

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);

        // Skipped on a catch-up re-invocation (see below) - the first pass already disabled everything
        // that was enabled, so re-scanning "what's currently enabled" here a second time would find
        // nothing and silently overwrite the recorded indices with an empty list, leaving EnableState/
        // ResetFsm unable to restore them.
        if (!snapshot.NeuteredActionIndices.ContainsKey(stateName))
        {
            var disabledByUs = new List<int>();
            for (int i = 0; i < state.Actions.Length; i++)
            {
                if (state.Actions[i].Enabled)
                {
                    state.Actions[i].Enabled = false;
                    disabledByUs.Add(i);
                }
            }

            snapshot.NeuteredActionIndices[stateName] = disabledByUs;
        }

        FsmEvent? exitEvent = FindExitEvent(state);
        if (exitEvent != null)
        {
            var exitAction = new SendExitEventAction(exitEvent);
            state.AddAction(exitAction);
            snapshot.InjectedExitActions.Add(exitAction);
        }
        else if (!state.IsInitialized)
        {
            // FindExitEvent can't check global transitions yet - state.Fsm (FsmState's own owning-Fsm
            // backref, set by Fsm.InitData()) is still null because this instance's owning GameObject
            // hasn't been active yet, so PlayMakerFSM.Awake() never ran. Actions are already disabled
            // above; FsmActivatedPatch (FsmMasterPlugin.cs) reapplies this edit set once this instance's
            // own Fsm.Preprocess() actually runs, which is preceded by InitData() setting state.Fsm - the
            // re-invocation above then skips straight to this exit-event lookup and (assuming a
            // CANCEL/FINISHED/NEXT transition exists) completes the wiring then.
            _logger.LogWarning($"[FsmMaster] Fsm '{fsm.Name}' hasn't activated yet (owning object inactive); state '{stateName}' has its actions disabled but its exit event can't be resolved until this instance activates.");
            _pendingActivationFsmKeys.Add(fsmKey);
        }
        else
        {
            _logger.LogWarning($"[FsmMaster] State '{stateName}' on fsm '{fsm.Name}' has no CANCEL/FINISHED/NEXT transition and no FsmEvent-valued action field to fall back on; leaving it inert with no exit.");
        }

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        if (!active.DisabledStates.Contains(stateName))
        {
            active.DisabledStates.Add(stateName);
        }

        BumpEditGeneration();
    }

    // Priority order requested: a real CANCEL/FINISHED/NEXT transition (state-level or global) on the state
    // being neutered, else the last FsmEvent-valued field found while scanning the state's own actions in
    // original order (the same reflection walk FsmDataCollector.CollectActions uses for read-only display).
    // Internal (not private) so the graph overlay can ask "which one transition stays reachable" for a
    // disabled state, using this exact same priority - re-running this against an already-disabled state is
    // safe/idempotent, since SendExitEventAction stores its event in a private field the reflection walk
    // below never sees.
    internal static FsmEvent? FindExitEvent(FsmState state)
    {
        foreach (string candidate in ExitEventPriority)
        {
            // state.Transitions is populated straight from scene deserialization, so a state-level
            // match works even on an FSM that has never run Preprocess. state.Fsm, by contrast, is
            // [NonSerialized] on FsmState and stays null until Fsm.InitData() runs (Preprocess()'s own
            // owning Fsm reference is unrelated to this) - only reachable once the FSM's owning
            // GameObject has been active at least once. Guard the global-transition fallback on
            // state.IsInitialized instead of dereferencing state.Fsm directly (its own getter logs an
            // error and returns null rather than throwing, but calling GetGlobalTransition on that null
            // result would NRE) - an inactive FSM with only a global CANCEL/FINISHED/NEXT transition
            // just can't resolve its exit event yet; see DisableState's own IsInitialized branch for how
            // that gets caught up once the FSM activates.
            FsmTransition? transition = state.GetTransition(candidate)
                ?? (state.IsInitialized ? state.Fsm.GetGlobalTransition(candidate) : null);
            if (transition != null)
            {
                return transition.FsmEvent;
            }
        }

        FsmEvent? lastFound = null;
        foreach (FsmStateAction action in state.Actions)
        {
            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                switch (field.GetValue(action))
                {
                    case FsmEvent fsmEvent:
                        lastFound = fsmEvent;
                        break;
                    case FsmEvent[] { Length: > 0 } fsmEvents:
                        // fsmEvents[^1] (System.Index) isn't available on net35.
                        lastFound = fsmEvents[fsmEvents.Length - 1];
                        break;
                }
            }
        }

        return lastFound;
    }

    private sealed class SendExitEventAction : FsmStateAction
    {
        private readonly FsmEvent _exitEvent;

        public SendExitEventAction(FsmEvent exitEvent)
        {
            _exitEvent = exitEvent;
        }

        public override void OnEnter()
        {
            Fsm.Event(_exitEvent);
            Finish();
        }
    }

    // ---- Transitions (also covers "disable an event" and relocating a transition's origin state) ----

    // Applies one transition retarget to a single live Fsm instance: locates the transition (state-level
    // or global), captures the instructions to restore it on first touch, then relocates/retargets it or
    // removes it outright if retarget.NewToState is the disabled marker.
    public void ApplyTransitionRetarget(string fsmKey, Fsm fsm, TransitionRetarget retarget)
    {
        bool isGlobal = string.IsNullOrEmpty(retarget.StateName);
        FsmTransition? transition = isGlobal
            ? fsm.GetGlobalTransition(retarget.EventName)
            : fsm.GetTransition(retarget.StateName, retarget.EventName);

        if (transition == null)
        {
            string where = isGlobal ? "global transitions" : $"state '{retarget.StateName}'";
            _logger.LogWarning($"[FsmMaster] Transition for event '{retarget.EventName}' not found on {where} on fsm '{fsm.Name}'; skipping.");
            return;
        }

        string newEventName = EffectiveNewEventName(retarget);

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        if (!snapshot.OriginalValues.TransitionRetargets.Exists(t =>
                t.StateName == retarget.StateName && t.EventName == retarget.EventName))
        {
            bool disabling = retarget.NewToState == TransitionRetarget.DisabledMarker;

            // The instructions that would put this transition back exactly as found: locate it at wherever
            // this edit is about to send it (NewStateName/newEventName - or, for a pure disable, nowhere
            // ever moved it, so its own StateName/EventName), and relocate/retarget it back to its original
            // origin, event, and destination.
            snapshot.OriginalValues.TransitionRetargets.Add(new TransitionRetarget
            {
                StateName = disabling ? retarget.StateName : retarget.NewStateName,
                EventName = disabling ? retarget.EventName : newEventName,
                NewStateName = retarget.StateName,
                NewEventName = retarget.EventName,
                NewToState = transition.ToState,
            });
        }

        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.TransitionRetargets.RemoveAll(t => t.StateName == retarget.StateName && t.EventName == retarget.EventName);
        active.TransitionRetargets.Add(retarget);
        BumpEditGeneration();

        if (retarget.NewToState == TransitionRetarget.DisabledMarker)
        {
            RemoveTransitionAt(fsm, retarget.StateName, retarget.EventName);
            return;
        }

        RelocateTransition(fsm, retarget.StateName, retarget.NewStateName, retarget.EventName, newEventName, retarget.NewToState);
    }

    // Used only when replaying a pristine snapshot, whose StateName/NewStateName/EventName/NewEventName/
    // NewToState already encode "restore to here" directly (see the capture step above), so this needs no
    // lookup/existence check the way ApplyTransitionRetarget does for new edits.
    private static void RestoreTransition(Fsm fsm, TransitionRetarget original) =>
        RelocateTransition(fsm, original.StateName, original.NewStateName, original.EventName, EffectiveNewEventName(original), original.NewToState);

    // Moves a transition from one origin/event ("" state = global) to another, retargeting/creating it at
    // the new origin/event either way. A relocation stays in effect until something (only ever
    // RestoreTransition today) moves it back - PlayMaker has no notion of a transition remembering where it
    // "really" belongs.
    private static void RelocateTransition(Fsm fsm, string fromStateName, string toStateName, string fromEventName, string toEventName, string newToState)
    {
        if (fromStateName != toStateName || fromEventName != toEventName)
        {
            RemoveTransitionAt(fsm, fromStateName, fromEventName);
        }

        AddOrChangeTransitionAt(fsm, toStateName, toEventName, newToState);
    }

    // Blank NewEventName means "same event as before this edit" - every existing caller (plain ToState
    // retarget, relocate, disable) leaves NewEventName unset; only RetargetTransitionEvent's drag-onto-an-
    // existing-event-node rebind ever sets it to something different from EventName. Internal (not
    // private) so the graph overlay can reconcile its own (frozen at scene-load) snapshot rows against
    // the active edit set's current effective event name when rendering - see FsmGraphOverlay's
    // RebuildNodeLayoutCache.
    internal static string EffectiveNewEventName(TransitionRetarget retarget) =>
        string.IsNullOrEmpty(retarget.NewEventName) ? retarget.EventName : retarget.NewEventName;

    // Retargets a transition's ToState only, applied to every live instance sharing fsmKey - the graph
    // overlay's drag-an-endpoint-onto-a-different-state entry point. stateName == "" targets a global
    // transition, matching the convention used throughout this file.
    public void RetargetTransitionToState(string fsmKey, string stateName, string eventName, string newToState)
    {
        string? previousToState = GetCurrentTransitionToState(fsmKey, stateName, eventName);

        var retarget = new TransitionRetarget
        {
            StateName = stateName,
            EventName = eventName,
            NewStateName = stateName,
            NewToState = newToState,
        };

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyTransitionRetarget(fsmKey, fsm, retarget);
        }

        if (previousToState != null)
        {
            PushUndo(fsmKey, () => RetargetTransitionToState(fsmKey, stateName, eventName, previousToState));
        }
    }

    private string? GetCurrentTransitionToState(string fsmKey, string stateName, string eventName)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            FsmTransition? transition = string.IsNullOrEmpty(stateName)
                ? fsm.GetGlobalTransition(eventName)
                : fsm.GetTransition(stateName, eventName);
            if (transition != null)
            {
                return transition.ToState;
            }
        }

        return null;
    }

    // Applies to every live instance sharing fsmKey - the graph overlay's right-click-to-disable entry
    // point for a single transition (state+event), as opposed to DisableState's whole-state neutering.
    // stateName == "" targets a global transition, matching the convention used throughout this file.
    public void DisableTransition(string fsmKey, string stateName, string eventName)
    {
        bool wasAlreadyDisabled = _pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? existing)
            && existing.OriginalValues.TransitionRetargets.Exists(t => t.NewStateName == stateName && EffectiveNewEventName(t) == eventName);

        var retarget = new TransitionRetarget
        {
            StateName = stateName,
            EventName = eventName,
            NewStateName = stateName,
            NewToState = TransitionRetarget.DisabledMarker,
        };

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            ApplyTransitionRetarget(fsmKey, fsm, retarget);
        }

        if (!wasAlreadyDisabled)
        {
            PushUndo(fsmKey, () => EnableTransition(fsmKey, stateName, eventName));
        }
    }

    // Undoes one transition's DisableTransition without resetting every other edit this session - looks up
    // the pristine entry captured when it was disabled and restores from it, then drops the bookkeeping for
    // just this one transition.
    public void EnableTransition(string fsmKey, string stateName, string eventName)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            return;
        }

        TransitionRetarget? original = snapshot.OriginalValues.TransitionRetargets.Find(t =>
            t.NewStateName == stateName && EffectiveNewEventName(t) == eventName);
        if (original == null)
        {
            return;
        }

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            RestoreTransition(fsm, original);
        }

        snapshot.OriginalValues.TransitionRetargets.Remove(original);

        if (_activeEdits.TryGetValue(fsmKey, out FsmEditSet? active))
        {
            active.TransitionRetargets.RemoveAll(t => t.StateName == stateName && t.EventName == eventName);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        PushUndo(fsmKey, () => DisableTransition(fsmKey, stateName, eventName));
        BumpEditGeneration();
    }

    // Drag-a-transition-endpoint-onto-an-existing-event-node rebind: relocates the transition identified by
    // (stateName, eventName) to originate from newOwnerStateName ("" = global) and fire under newEventName
    // instead, keeping its current destination (toState - read by the caller from the live transition being
    // dragged, since every live instance is expected to share the same ToState for a given FSM template).
    // Whatever transition previously occupied the (newOwnerStateName, newEventName) slot is deleted first,
    // since PlayMaker only allows one transition per event on a given state/global list.
    public void RetargetTransitionEvent(string fsmKey, string stateName, string eventName, string newOwnerStateName, string newEventName, string toState)
    {
        bool isSameSlot = newOwnerStateName == stateName && newEventName == eventName;

        var retarget = new TransitionRetarget
        {
            StateName = stateName,
            EventName = eventName,
            NewStateName = newOwnerStateName,
            NewEventName = newEventName,
            NewToState = toState,
        };

        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            if (!isSameSlot)
            {
                FsmTransition? conflicting = string.IsNullOrEmpty(newOwnerStateName)
                    ? fsm.GetGlobalTransition(newEventName)
                    : fsm.GetTransition(newOwnerStateName, newEventName);
                if (conflicting != null)
                {
                    RemoveTransitionAt(fsm, newOwnerStateName, newEventName);
                }
            }

            ApplyTransitionRetarget(fsmKey, fsm, retarget);
        }

        // Note: if a conflicting transition occupied (newOwnerStateName, newEventName) and got deleted
        // above, this undo can't bring it back - it only knows how to move the dragged transition back
        // to its own prior slot, not resurrect a different one that was overwritten.
        if (!isSameSlot)
        {
            PushUndo(fsmKey, () => RetargetTransitionEvent(fsmKey, newOwnerStateName, newEventName, stateName, eventName, toState));
        }
    }

    private static void RemoveTransitionAt(Fsm fsm, string stateName, string eventName)
    {
        if (string.IsNullOrEmpty(stateName))
        {
            fsm.RemoveGlobalTransition(eventName);
        }
        else
        {
            fsm.RemoveTransition(stateName, eventName);
        }
    }

    // Retargets the transition at (stateName, eventName) if one already exists there; otherwise creates
    // it fresh, since ChangeTransition/ChangeGlobalTransition return false rather than creating one.
    private static void AddOrChangeTransitionAt(Fsm fsm, string stateName, string eventName, string toState)
    {
        bool isGlobal = string.IsNullOrEmpty(stateName);
        bool changed = isGlobal
            ? fsm.ChangeGlobalTransition(eventName, toState)
            : fsm.ChangeTransition(stateName, eventName, toState);

        if (!changed)
        {
            if (isGlobal)
            {
                fsm.AddGlobalTransition(eventName, toState);
            }
            else
            {
                fsm.AddTransition(stateName, eventName, toState);
            }
        }
    }

    // ---- Sequencer ----

    // UI entry point (the Sequencer window's own "apply immediately" convention, matching
    // SetVariable/SetActionField) - install-or-replace applied to every live instance sharing fsmKey.
    public void InstallSequencer(string fsmKey, SequencerOverride seq)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            InstallSequencer(fsmKey, fsm, seq);
        }
    }

    // seq.ActionIndex is a RANK, not a raw array index: "the Nth action in this state whose type name
    // contains 'Random'" (see FsmActionSequencer.IndexRandomEventAction). A raw array index would shift
    // out from under a second sequencer the moment a first one gets installed elsewhere in the same
    // state (InsertActionAfter shifts every later action's index by one) - rank stays stable regardless,
    // since a synthetic SequenceSendEventAction's own type name never contains "Random" and so never
    // participates in the count. This is what lets a state with more than one Random-event-family action
    // have each one independently installed/updated/removed without disturbing the others.
    public void InstallSequencer(string fsmKey, Fsm fsm, SequencerOverride seq)
    {
        FsmState? state = fsm.GetState(seq.StateName);
        if (state == null)
        {
            _logger.LogWarning($"[FsmMaster] State '{seq.StateName}' not found on fsm '{fsm.Name}'; skipping sequencer install.");
            return;
        }

        if (seq.Pattern.Count == 0)
        {
            _logger.LogWarning($"[FsmMaster] Sequencer pattern for state '{seq.StateName}' on fsm '{fsm.Name}' is empty; skipping.");
            return;
        }

        int idx = FsmActionSequencer.IndexRandomEventAction(state, seq.ActionIndex);
        if (idx < 0)
        {
            _logger.LogWarning($"[FsmMaster] No random-event action at rank {seq.ActionIndex} on state '{seq.StateName}' on fsm '{fsm.Name}'; skipping sequencer install.");
            return;
        }

        // Recorded up front, before the original.State guard below - a fsmKey can have multiple live
        // instances (e.g. several copies of the same enemy in a room), and this bookkeeping is shared
        // across all of them rather than per-instance, so an instance that can't be physically mutated
        // right now shouldn't stop the override from being recorded (and later reapplied via ApplyEditSet)
        // for every instance that can.
        FsmEditSet active = GetOrCreateActiveEditSet(fsmKey);
        active.SequencerOverrides.RemoveAll(s => s.StateName == seq.StateName && s.ActionIndex == seq.ActionIndex);
        active.SequencerOverrides.Add(seq);

        FsmStateAction original = state.Actions[idx];

        // Checked directly on the action rather than via fsm.Preprocessed: that flag was tried first and
        // turned out not to reliably predict this in practice (still saw the NRE below with it reading
        // true), most likely because state.Actions lazily builds its action array on first access -
        // something else in FsmMaster reading Actions before this Fsm's own Preprocess()/Awake() ever
        // runs would leave a fresh, un-Init'd action here even once Preprocessed later flips true.
        // FsmStateAction.State is only ever set by the action's own Init(FsmState) call, which is exactly
        // what PlayMakerFsmOps's InsertActionAfter dereferences, so checking it directly guarantees this
        // can't NRE regardless of why it's still unset. Skip the physical mutation and leave the override
        // recorded above; FsmActivatedPatch (FsmMasterPlugin.cs) reapplies it via ApplyEditSet once this
        // Fsm's own Preprocess() actually runs and Init's every action, so the override still lands on
        // this instance the moment it activates within the same scene - it just can't happen right now.
        if (original.State == null)
        {
            _logger.LogWarning($"[FsmMaster] Fsm '{fsm.Name}' hasn't activated yet (owning object inactive); sequencer override for state '{seq.StateName}' recorded but not installed on this instance.");
            _pendingActivationFsmKeys.Add(fsmKey);
            BumpEditGeneration();
            return;
        }

        // Tears down any sequencer already installed against this exact original action before
        // reinstalling, so this method doubles as a live-update path (reorder/add/remove a block,
        // change loop count) rather than only a one-shot "install if absent" - matched by object
        // reference (not state alone), so replacing this rank's sequencer never disturbs a different
        // rank's independently-installed one in the same state. The empty-pattern check above must stay
        // ahead of this call, or a would-be-empty replace would leave the original action disabled with
        // nothing installed in its place.
        RemoveInstalledSequencerFor(fsmKey, original, restoreOriginalEnabled: false);

        // RemoveInstalledSequencerFor only clears a sequencer this key's own InstalledSequencers
        // bookkeeping still references. A DebugMod savestate reload can desync that bookkeeping from
        // what is physically sitting in the live action array: PruneStaleSnapshotEntries drops the
        // tracked pair whenever the live Fsm object handed back by the fresh post-load rescan doesn't
        // match the one the pair was recorded against, but if the reload actually reused the same
        // live Fsm/state.Actions array untouched (a same-room load doesn't always tear the scene down),
        // the physical SequenceSendEventAction installed before the save is still sitting right where
        // it was even though the bookkeeping just forgot about it. Left in place, the insert below would
        // stack a second sequencer next to it and both would fire Fsm.Event on every state entry, which
        // is indistinguishable from the sequencer "not applying" - so sweep any such orphan out of the
        // array directly before installing, independent of whether bookkeeping remembers it.
        while (idx + 1 < state.Actions.Length && state.Actions[idx + 1] is SequenceSendEventAction)
        {
            state.RemoveAction(idx + 1);
        }

        original.Enabled = false;

        FsmEvent[] pattern = seq.Pattern.Select(FsmEvent.GetFsmEvent).ToArray();
        var sequencer = new SequenceSendEventAction(pattern, seq.RepeatCount, original, state);
        original.InsertActionAfter(sequencer);

        FsmPristineSnapshot snapshot = GetOrCreateSnapshot(fsmKey);
        snapshot.InstalledSequencers.Add((original, sequencer));

        BumpEditGeneration();
    }

    // Fully removes the sequencer installed at (stateName, actionRank) and restores the original
    // random-event action's Enabled=true, on every live instance sharing fsmKey - the Sequencer tab's
    // own entry point both for "the pattern list went empty" and for a block's own close button,
    // distinct from a live-update (see InstallSequencer's own RemoveInstalledSequencerFor call, which
    // tears down without restoring since it's about to reinstall a fresh sequencer immediately after).
    // No PushUndo counterpart - InstallSequencer never wired one up either, so sequencer edits stay
    // outside the undo stack.
    public void RemoveSequencer(string fsmKey, string stateName, int actionRank)
    {
        foreach (Fsm fsm in GetLiveInstances(fsmKey))
        {
            FsmState? state = fsm.GetState(stateName);
            if (state == null)
            {
                continue;
            }

            int idx = FsmActionSequencer.IndexRandomEventAction(state, actionRank);
            if (idx < 0)
            {
                continue;
            }

            RemoveInstalledSequencerFor(fsmKey, state.Actions[idx], restoreOriginalEnabled: true);
        }

        if (_activeEdits.TryGetValue(fsmKey, out FsmEditSet? active))
        {
            active.SequencerOverrides.RemoveAll(s => s.StateName == stateName && s.ActionIndex == actionRank);
        }

        PruneActiveEditSetIfEmpty(fsmKey);
        BumpEditGeneration();
    }

    // Removes whichever SequenceSendEventAction is currently installed against this exact original
    // action (if any) and drops its FsmPristineSnapshot.InstalledSequencers bookkeeping entry - matched
    // by object reference, not by state alone, so a state with more than one independently-sequenced
    // Random-event-family action never has one's install/remove disturb another's.
    // restoreOriginalEnabled=false is used by InstallSequencer, which is about to disable the original
    // action again immediately anyway; true is used by RemoveSequencer's full "put it back" path.
    private void RemoveInstalledSequencerFor(string fsmKey, FsmStateAction original, bool restoreOriginalEnabled)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            return;
        }

        int pairIndex = snapshot.InstalledSequencers.FindIndex(p => p.Original == original);
        if (pairIndex < 0)
        {
            return;
        }

        (FsmStateAction originalAction, FsmStateAction sequencer) = snapshot.InstalledSequencers[pairIndex];
        snapshot.InstalledSequencers.RemoveAt(pairIndex);

        if (restoreOriginalEnabled)
        {
            originalAction.Enabled = true;
        }

        FsmState state = sequencer.State;
        int idx = Array.IndexOf(state.Actions, sequencer);
        if (idx >= 0)
        {
            state.RemoveAction(idx);
        }
    }

    // ---- Snapshot plumbing ----

    private FsmPristineSnapshot GetOrCreateSnapshot(string fsmKey)
    {
        if (!_pristine.TryGetValue(fsmKey, out FsmPristineSnapshot? snapshot))
        {
            snapshot = new FsmPristineSnapshot();
            _pristine[fsmKey] = snapshot;
        }

        return snapshot;
    }

    private FsmEditSet GetOrCreateActiveEditSet(string fsmKey)
    {
        if (!_activeEdits.TryGetValue(fsmKey, out FsmEditSet? set))
        {
            set = new FsmEditSet { FsmKey = fsmKey };
            _activeEdits[fsmKey] = set;
        }

        return set;
    }

    // The core revert engine behind ResetFsm and RevertAllForUnload: puts every live instance in
    // instances back to snapshot's pristine state - reassigns overridden variables/action fields,
    // restores relocated transitions, re-enables actions DisableState neutered, and removes the exit
    // actions and sequencers this class injected.
    private static void RestoreSnapshot(FsmPristineSnapshot snapshot, List<Fsm> instances)
    {
        foreach (Fsm fsm in instances)
        {
            foreach (VariableOverride ov in snapshot.OriginalValues.VariableOverrides)
            {
                NamedVariable? variable = fsm.Variables.FindVariable(ov.Name);
                if (variable == null)
                {
                    continue;
                }

                if (ov.ArrayIndex >= 0)
                {
                    if (variable is FsmArray array && ov.ArrayIndex < array.Length)
                    {
                        AssignArrayElement(array, ov.ArrayIndex, ov.StringValue);
                    }

                    continue;
                }

                AssignNamedVariable(variable, ov.StringValue);
            }

            foreach (ActionFieldOverride ov in snapshot.OriginalValues.ActionFieldOverrides)
            {
                RestoreActionField(fsm, ov);
            }

            foreach (TransitionRetarget original in snapshot.OriginalValues.TransitionRetargets)
            {
                RestoreTransition(fsm, original);
            }
        }

        foreach (KeyValuePair<string, List<int>> entry in snapshot.NeuteredActionIndices)
        {
            foreach (Fsm fsm in instances)
            {
                FsmState? state = fsm.GetState(entry.Key);
                if (state == null)
                {
                    continue;
                }

                foreach (int i in entry.Value)
                {
                    if (i >= 0 && i < state.Actions.Length)
                    {
                        state.Actions[i].Enabled = true;
                    }
                }
            }
        }

        foreach (FsmStateAction exitAction in snapshot.InjectedExitActions)
        {
            FsmState state = exitAction.State;
            int idx = Array.IndexOf(state.Actions, exitAction);
            if (idx >= 0)
            {
                state.RemoveAction(idx);
            }
        }

        foreach ((FsmStateAction original, FsmStateAction sequencer) in snapshot.InstalledSequencers)
        {
            original.Enabled = true;
            FsmState state = sequencer.State;
            int idx = Array.IndexOf(state.Actions, sequencer);
            if (idx >= 0)
            {
                state.RemoveAction(idx);
            }
        }
    }

    // ---- Value formatting/parsing shared by variables and action fields ----
    // Every value round-trips as an invariant-culture string (see FsmEditModels) since JsonUtility can't
    // model a polymorphic value field.

    // Internal (not private) so FsmActiveStatePanel can reuse this exact switch as the single source of
    // truth for both "is this value editable" and "what string to display/prefill" - avoids the UI
    // re-implementing its own parallel type check that could drift out of sync with this one.
    internal static bool TryFormatValue(object? value, out string formatted)
    {
        switch (value)
        {
            case NamedVariable nv when SupportedVariableTypes.Contains(nv.VariableType):
                formatted = FormatNamedVariable(nv);
                return true;
            case bool b:
                formatted = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i:
                formatted = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case float f:
                formatted = f.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case string s:
                formatted = s;
                return true;
            case Enum e:
                formatted = Convert.ToInt32(e).ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                formatted = "";
                return false;
        }
    }

    private static bool TryAssignFieldValue(FsmStateAction action, FieldInfo field, object? currentValue, string stringValue)
    {
        switch (currentValue)
        {
            case NamedVariable nv when SupportedVariableTypes.Contains(nv.VariableType):
                AssignNamedVariable(nv, stringValue);
                return true;
            case bool:
                field.SetValue(action, bool.Parse(stringValue));
                return true;
            case int:
                field.SetValue(action, int.Parse(stringValue, CultureInfo.InvariantCulture));
                return true;
            case float:
                field.SetValue(action, float.Parse(stringValue, CultureInfo.InvariantCulture));
                return true;
            case string:
                field.SetValue(action, stringValue);
                return true;
            case Enum:
                field.SetValue(action, Enum.ToObject(field.FieldType, int.Parse(stringValue, CultureInfo.InvariantCulture)));
                return true;
            default:
                return false;
        }
    }

    private static string FormatNamedVariable(NamedVariable variable) => variable.VariableType switch
    {
        VariableType.Float => ((FsmFloat)variable).Value.ToString("R", CultureInfo.InvariantCulture),
        VariableType.Int => ((FsmInt)variable).Value.ToString(CultureInfo.InvariantCulture),
        VariableType.Bool => ((FsmBool)variable).Value.ToString(CultureInfo.InvariantCulture),
        VariableType.String => ((FsmString)variable).Value ?? "",
        VariableType.Vector2 => FormatFloats(((FsmVector2)variable).Value.x, ((FsmVector2)variable).Value.y),
        VariableType.Vector3 => FormatFloats(((FsmVector3)variable).Value.x, ((FsmVector3)variable).Value.y, ((FsmVector3)variable).Value.z),
        VariableType.Rect => FormatFloats(((FsmRect)variable).Value.x, ((FsmRect)variable).Value.y, ((FsmRect)variable).Value.width, ((FsmRect)variable).Value.height),
        VariableType.Quaternion => FormatFloats(((FsmQuaternion)variable).Value.x, ((FsmQuaternion)variable).Value.y, ((FsmQuaternion)variable).Value.z, ((FsmQuaternion)variable).Value.w),
        VariableType.Color => FormatFloats(((FsmColor)variable).Value.r, ((FsmColor)variable).Value.g, ((FsmColor)variable).Value.b, ((FsmColor)variable).Value.a),
        // NamedVariable.ToInt() is a Silksong-only PlayMaker addition (absent from hk1221/hk1432's
        // build - see platform-inventory.md's surface delta table), so this goes through the enum's
        // own boxed Value via Convert.ToInt32 instead, which works identically on every TFM.
        VariableType.Enum => Convert.ToInt32(((FsmEnum)variable).Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    private static void AssignNamedVariable(NamedVariable variable, string stringValue)
    {
        switch (variable.VariableType)
        {
            case VariableType.Float:
                ((FsmFloat)variable).Value = float.Parse(stringValue, CultureInfo.InvariantCulture);
                break;
            case VariableType.Int:
                ((FsmInt)variable).Value = int.Parse(stringValue, CultureInfo.InvariantCulture);
                break;
            case VariableType.Bool:
                ((FsmBool)variable).Value = bool.Parse(stringValue);
                break;
            case VariableType.String:
                ((FsmString)variable).Value = stringValue;
                break;
            case VariableType.Vector2:
                float[] v2 = ParseFloats(stringValue, 2);
                ((FsmVector2)variable).Value = new Vector2(v2[0], v2[1]);
                break;
            case VariableType.Vector3:
                float[] v3 = ParseFloats(stringValue, 3);
                ((FsmVector3)variable).Value = new Vector3(v3[0], v3[1], v3[2]);
                break;
            case VariableType.Rect:
                float[] r = ParseFloats(stringValue, 4);
                ((FsmRect)variable).Value = new Rect(r[0], r[1], r[2], r[3]);
                break;
            case VariableType.Quaternion:
                float[] q = ParseFloats(stringValue, 4);
                ((FsmQuaternion)variable).Value = new Quaternion(q[0], q[1], q[2], q[3]);
                break;
            case VariableType.Color:
                float[] c = ParseFloats(stringValue, 4);
                ((FsmColor)variable).Value = new Color(c[0], c[1], c[2], c[3]);
                break;
            case VariableType.Enum:
                var fsmEnum = (FsmEnum)variable;
                fsmEnum.Value = (Enum)Enum.ToObject(fsmEnum.EnumType, int.Parse(stringValue, CultureInfo.InvariantCulture));
                break;
        }
    }

    // string.Join(string, IEnumerable<string>) isn't available on net35 - only the string[] overload is.
    private static string FormatFloats(params float[] values) =>
        string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray());

    private static float[] ParseFloats(string s, int count)
    {
        string[] parts = s.Split(',');
        var result = new float[count];
        for (int i = 0; i < count && i < parts.Length; i++)
        {
            result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        }

        return result;
    }
}
