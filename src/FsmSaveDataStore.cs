using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FsmMaster;

// Persists FSM edits as JSON, one file per (scene, FsmKey) pair under a per-scene subfolder - not one file
// per scene holding a list of every FsmKey's edits. Uses UnityEngine.JsonUtility rather than a third-party
// JSON library, since JsonUtility ships inside UnityEngine.Modules (already a dependency here) and needs
// nothing new added to the fixed dependency list in CLAUDE.md. Silksong.DebugMod follows the same
// JsonUtility-based approach and the same Application.persistentDataPath-rooted storage convention
// (agent-context/Silksong.DebugMod-main/DebugMod.cs, ModBaseDirectory;
// agent-context/Silksong.DebugMod-main/SaveStates/SaveStateManager.cs).
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

    public static string GetFilePath(string sceneName, string fsmKey) =>
        Path.Combine(GetSceneDirectory(sceneName), $"{SanitizeForFileName(fsmKey)}.json");

    public static FsmEditSet? Load(string sceneName, string fsmKey)
    {
        string filePath = GetFilePath(sceneName, fsmKey);
        if (!File.Exists(filePath))
        {
            return null;
        }

        return FromWire(JsonUtility.FromJson<FsmEditSetWire>(File.ReadAllText(filePath)));
    }

    // Every FsmEditSet persisted for a scene, discovered by listing that scene's subfolder rather than a
    // separate manifest, so the plugin can reapply all of them on scene load without needing to know which
    // FsmKeys exist ahead of time.
    public static List<FsmEditSet> LoadAllForScene(string sceneName)
    {
        var result = new List<FsmEditSet>();
        string sceneDirectory = GetSceneDirectory(sceneName);
        if (!Directory.Exists(sceneDirectory))
        {
            return result;
        }

        foreach (string filePath in Directory.EnumerateFiles(sceneDirectory, "*.json"))
        {
            FsmEditSetWire? wire = JsonUtility.FromJson<FsmEditSetWire>(File.ReadAllText(filePath));
            if (wire != null)
            {
                result.Add(FromWire(wire));
            }
        }

        return result;
    }

    // Returns the JSON text that was written, so a caller can log/inspect exactly what got persisted.
    public static string Save(string sceneName, FsmEditSet edits)
    {
        string sceneDirectory = GetSceneDirectory(sceneName);
        if (!Directory.Exists(sceneDirectory))
        {
            Directory.CreateDirectory(sceneDirectory);
        }

        string json = JsonUtility.ToJson(ToWire(edits), prettyPrint: true);
        File.WriteAllText(GetFilePath(sceneName, edits.FsmKey), json);
        return json;
    }

    // Deletes one FsmKey's persisted overrides for a scene (used by Reset), so the next time that scene
    // loads the FSM comes up unmodified instead of having the override reapplied to it.
    public static void ClearFsmKey(string sceneName, string fsmKey)
    {
        string filePath = GetFilePath(sceneName, fsmKey);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
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
            wire.VariableOverrides.Add(JoinPairs("type", ov.VariableType, "name", ov.Name, "value", ov.StringValue));
        }

        foreach (ActionFieldOverride ov in edits.ActionFieldOverrides)
        {
            wire.ActionFieldOverrides.Add(JoinPairs(
                "state", ov.StateName,
                "action", ov.ActionIndex.ToString(CultureInfo.InvariantCulture),
                "type", ov.ExpectedActionTypeName,
                "field", ov.FieldName,
                "value", ov.StringValue));
        }

        wire.DisabledStates.AddRange(edits.DisabledStates);

        foreach (TransitionRetarget t in edits.TransitionRetargets)
        {
            wire.TransitionRetargets.Add(JoinPairs(
                "state", t.StateName,
                "event", t.EventName,
                "newState", t.NewStateName,
                "newTo", t.NewToState));
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
