using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FsmMaster;

// Persists FSM edits as JSON, one file per (scene, FsmKey, save name) triple under a per-scene, per-FsmKey
// subfolder - so a single FsmKey can hold several independently named configurations (e.g. "aggressive" vs
// "passive" variants of the same enemy FSM) rather than exactly one save overwriting itself every time. Uses
// UnityEngine.JsonUtility rather than a third-party JSON library, since JsonUtility ships inside
// UnityEngine.Modules (already a dependency here) and needs nothing new added to the fixed dependency list
// in CLAUDE.md. Silksong.DebugMod follows the same JsonUtility-based approach and the same
// Application.persistentDataPath-rooted storage convention (agent-context/Silksong.DebugMod-main/DebugMod.cs,
// ModBaseDirectory; agent-context/Silksong.DebugMod-main/SaveStates/SaveStateManager.cs).
//
// Confirmed in-game: JsonUtility silently drops a List<T> field entirely (not even an empty "[]") whenever
// T is a custom class, regardless of nesting depth or whether the list is populated - List<string> serializes
// fine, List<ActionFieldOverride> (etc.) does not. FsmEditSet itself stays strongly typed for the rest of the
// mod (FsmEditManager, FsmPristineSnapshot, hotkeys); only at this persistence boundary each override is
// flattened into one self-labeled "key=value; key=value" string per FsmEditSetWire list entry, using the
// JsonUtility-safe List<string> shape - readable directly in the file and independent of field order.
internal static class FsmSaveDataStore
{
    private static readonly string DataDirectory = Path.Combine(Application.persistentDataPath, "FsmMasterData");

    private static string SanitizeForFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static string GetSceneDirectory(string sceneName) =>
        Path.Combine(DataDirectory, SanitizeForFileName(sceneName));

    private static string GetFsmDirectory(string sceneName, string fsmKey) =>
        Path.Combine(GetSceneDirectory(sceneName), SanitizeForFileName(fsmKey));

    public static string GetFilePath(string sceneName, string fsmKey, string saveName) =>
        Path.Combine(GetFsmDirectory(sceneName, fsmKey), $"{SanitizeForFileName(saveName)}.json");

    public static FsmEditSet? Load(string sceneName, string fsmKey, string saveName)
    {
        string filePath = GetFilePath(sceneName, fsmKey, saveName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        return FromWire(JsonUtility.FromJson<FsmEditSetWire>(File.ReadAllText(filePath)));
    }

    // Every save name that exists for one FsmKey in a scene, discovered by listing that FsmKey's own
    // subfolder rather than a separate manifest - this is what populates the Load dialog's row list.
    // Sorted case-insensitively so the list order doesn't depend on filesystem enumeration order.
    public static List<string> ListSaveNames(string sceneName, string fsmKey)
    {
        var result = new List<string>();
        string fsmDirectory = GetFsmDirectory(sceneName, fsmKey);
        if (!Directory.Exists(fsmDirectory))
        {
            return result;
        }

        foreach (string filePath in Directory.EnumerateFiles(fsmDirectory, "*.json"))
        {
            result.Add(Path.GetFileNameWithoutExtension(filePath));
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // Returns the JSON text that was written, so a caller can log/inspect exactly what got persisted.
    public static string Save(string sceneName, string saveName, FsmEditSet edits)
    {
        string fsmDirectory = GetFsmDirectory(sceneName, edits.FsmKey);
        if (!Directory.Exists(fsmDirectory))
        {
            Directory.CreateDirectory(fsmDirectory);
        }

        string json = JsonUtility.ToJson(ToWire(edits), prettyPrint: true);
        File.WriteAllText(GetFilePath(sceneName, edits.FsmKey, saveName), json);
        return json;
    }

    // Deletes every named save for one FsmKey in a scene, plus its remembered last-chosen entry (used by
    // FsmMasterPlugin.ResetFsm) - so the next time this scene loads, this FsmKey comes up unmodified
    // instead of having any of its saved configurations reapplied to it.
    public static void ClearAllSavesForFsm(string sceneName, string fsmKey)
    {
        string fsmDirectory = GetFsmDirectory(sceneName, fsmKey);
        if (Directory.Exists(fsmDirectory))
        {
            Directory.Delete(fsmDirectory, recursive: true);
        }

        Dictionary<string, string> manifest = LoadLastChosenManifest(sceneName);
        if (manifest.Remove(fsmKey))
        {
            SaveLastChosenManifest(sceneName, manifest);
        }
    }

    // Every FsmEditSet this scene should auto-reapply on load - one per fsmKey in fsmKeysPresent that has
    // a remembered last-chosen save name (see SetLastChosenSaveName), skipping any fsmKey with no such
    // choice recorded rather than guessing which of its (possibly several) named saves to use.
    public static List<FsmEditSet> LoadLastChosenForScene(string sceneName, IEnumerable<string> fsmKeysPresent)
    {
        var result = new List<FsmEditSet>();
        Dictionary<string, string> manifest = LoadLastChosenManifest(sceneName);

        foreach (string fsmKey in fsmKeysPresent)
        {
            if (!manifest.TryGetValue(fsmKey, out string? saveName))
            {
                continue;
            }

            FsmEditSet? editSet = Load(sceneName, fsmKey, saveName);
            if (editSet != null)
            {
                result.Add(editSet);
            }
        }

        return result;
    }

    public static string? GetLastChosenSaveName(string sceneName, string fsmKey) =>
        LoadLastChosenManifest(sceneName).TryGetValue(fsmKey, out string? saveName) ? saveName : null;

    // Recorded whenever the user explicitly saves or loads a named configuration for this FsmKey (see
    // FsmRightPanel's save/load dialog handlers) - the next save/load supersedes whatever was chosen
    // before, and this is what LoadLastChosenForScene reapplies on the next scene load.
    public static void SetLastChosenSaveName(string sceneName, string fsmKey, string saveName)
    {
        Dictionary<string, string> manifest = LoadLastChosenManifest(sceneName);
        manifest[fsmKey] = saveName;
        SaveLastChosenManifest(sceneName, manifest);
    }

    // ---- Last-chosen-save-per-FsmKey manifest ----
    // One small file per scene (not one file per FsmKey folder) so remembering "which save is active" for
    // an FsmKey can never collide with a user-chosen save name in that same FsmKey's own folder.

    private static string GetLastChosenManifestPath(string sceneName) =>
        Path.Combine(GetSceneDirectory(sceneName), "_last-chosen.json");

    [Serializable]
    private sealed class LastChosenManifestWire
    {
        public List<string> Entries = new(); // "fsmKey=saveName", one per FsmKey with a remembered choice
    }

    private static Dictionary<string, string> LoadLastChosenManifest(string sceneName)
    {
        var result = new Dictionary<string, string>();
        string path = GetLastChosenManifestPath(sceneName);
        if (!File.Exists(path))
        {
            return result;
        }

        LastChosenManifestWire? wire = JsonUtility.FromJson<LastChosenManifestWire>(File.ReadAllText(path));
        if (wire == null)
        {
            return result;
        }

        foreach (string entry in wire.Entries)
        {
            int eq = entry.IndexOf('=');
            if (eq > 0)
            {
                result[entry[..eq]] = entry[(eq + 1)..];
            }
        }

        return result;
    }

    private static void SaveLastChosenManifest(string sceneName, Dictionary<string, string> manifest)
    {
        string sceneDirectory = GetSceneDirectory(sceneName);
        if (!Directory.Exists(sceneDirectory))
        {
            Directory.CreateDirectory(sceneDirectory);
        }

        var wire = new LastChosenManifestWire();
        foreach (KeyValuePair<string, string> entry in manifest)
        {
            wire.Entries.Add($"{entry.Key}={entry.Value}");
        }

        File.WriteAllText(GetLastChosenManifestPath(sceneName), JsonUtility.ToJson(wire, prettyPrint: true));
    }

    // ---- Wire format conversion ----

    private const string PairSeparator = "; ";
    private const string KeyValueSeparator = "=";
    private const string PatternSeparator = ", ";

    [Serializable]
    private sealed class FsmEditSetWire
    {
        public string FsmKey = "";
        public List<string> VariableOverrides = new();
        public List<string> ActionFieldOverrides = new();
        public List<string> DisabledStates = new();
        public List<string> TransitionRetargets = new();
        public List<string> SequencerOverrides = new();
    }

    // keysAndValues alternates key, value, key, value, ... e.g. JoinPairs("state", "Camp Idle", "field",
    // "MinReactDelay") -> "state=Camp Idle; field=MinReactDelay". Self-labeled so the file is directly
    // readable and each entry is independent of field order.
    private static string JoinPairs(params string[] keysAndValues)
    {
        var parts = new List<string>(keysAndValues.Length / 2);
        for (int i = 0; i + 1 < keysAndValues.Length; i += 2)
        {
            parts.Add($"{keysAndValues[i]}{KeyValueSeparator}{keysAndValues[i + 1]}");
        }

        return string.Join(PairSeparator, parts);
    }

    private static Dictionary<string, string> SplitPairs(string joined)
    {
        var result = new Dictionary<string, string>();
        foreach (string part in joined.Split(PairSeparator))
        {
            int eq = part.IndexOf(KeyValueSeparator, StringComparison.Ordinal);
            if (eq >= 0)
            {
                result[part[..eq]] = part[(eq + 1)..];
            }
        }

        return result;
    }

    private static FsmEditSetWire ToWire(FsmEditSet edits)
    {
        var wire = new FsmEditSetWire { FsmKey = edits.FsmKey };

        foreach (VariableOverride ov in edits.VariableOverrides)
        {
            wire.VariableOverrides.Add(JoinPairs(
                "type", ov.VariableType,
                "name", ov.Name,
                "array", ov.ArrayIndex.ToString(CultureInfo.InvariantCulture),
                "value", ov.StringValue));
        }

        foreach (ActionFieldOverride ov in edits.ActionFieldOverrides)
        {
            wire.ActionFieldOverrides.Add(JoinPairs(
                "state", ov.StateName,
                "action", ov.ActionIndex.ToString(CultureInfo.InvariantCulture),
                "type", ov.ExpectedActionTypeName,
                "field", ov.FieldName,
                "array", ov.ArrayIndex.ToString(CultureInfo.InvariantCulture),
                "value", ov.StringValue));
        }

        wire.DisabledStates.AddRange(edits.DisabledStates);

        foreach (TransitionRetarget t in edits.TransitionRetargets)
        {
            wire.TransitionRetargets.Add(JoinPairs(
                "state", t.StateName,
                "event", t.EventName,
                "newState", t.NewStateName,
                "newTo", t.NewToState,
                "newEvent", t.NewEventName));
        }

        foreach (SequencerOverride seq in edits.SequencerOverrides)
        {
            wire.SequencerOverrides.Add(JoinPairs(
                "state", seq.StateName,
                "action", seq.ActionIndex.ToString(CultureInfo.InvariantCulture),
                "repeat", seq.RepeatCount.ToString(CultureInfo.InvariantCulture),
                "pattern", string.Join(PatternSeparator, seq.Pattern)));
        }

        return wire;
    }

    private static FsmEditSet FromWire(FsmEditSetWire? wire)
    {
        var edits = new FsmEditSet();
        if (wire == null)
        {
            return edits;
        }

        edits.FsmKey = wire.FsmKey;

        foreach (string s in wire.VariableOverrides)
        {
            Dictionary<string, string> p = SplitPairs(s);
            edits.VariableOverrides.Add(new VariableOverride
            {
                VariableType = p["type"],
                Name = p["name"],
                // Missing "array" key = a file saved before per-element array editing existed - treat
                // as -1 (the whole value), same as ArrayOverride's own default.
                ArrayIndex = p.TryGetValue("array", out string? arrayIndex) ? int.Parse(arrayIndex, CultureInfo.InvariantCulture) : -1,
                StringValue = p["value"],
            });
        }

        foreach (string s in wire.ActionFieldOverrides)
        {
            Dictionary<string, string> p = SplitPairs(s);
            edits.ActionFieldOverrides.Add(new ActionFieldOverride
            {
                StateName = p["state"],
                ActionIndex = int.Parse(p["action"], CultureInfo.InvariantCulture),
                ExpectedActionTypeName = p["type"],
                FieldName = p["field"],
                ArrayIndex = p.TryGetValue("array", out string? arrayIndex) ? int.Parse(arrayIndex, CultureInfo.InvariantCulture) : -1,
                StringValue = p["value"],
            });
        }

        edits.DisabledStates.AddRange(wire.DisabledStates);

        foreach (string s in wire.TransitionRetargets)
        {
            Dictionary<string, string> p = SplitPairs(s);
            edits.TransitionRetargets.Add(new TransitionRetarget
            {
                StateName = p["state"],
                EventName = p["event"],
                NewStateName = p["newState"],
                NewToState = p["newTo"],
                // Missing "newEvent" key = a file saved before event-rebind editing existed - treat as ""
                // (same event as before this edit), matching TransitionRetarget.NewEventName's own default.
                NewEventName = p.TryGetValue("newEvent", out string? newEvent) ? newEvent : "",
            });
        }

        foreach (string s in wire.SequencerOverrides)
        {
            Dictionary<string, string> p = SplitPairs(s);
            var seq = new SequencerOverride
            {
                StateName = p["state"],
                ActionIndex = int.Parse(p["action"], CultureInfo.InvariantCulture),
                RepeatCount = int.Parse(p["repeat"], CultureInfo.InvariantCulture),
            };

            if (p.TryGetValue("pattern", out string? pattern) && pattern.Length > 0)
            {
                seq.Pattern.AddRange(pattern.Split(PatternSeparator));
            }

            edits.SequencerOverrides.Add(seq);
        }

        return edits;
    }
}
