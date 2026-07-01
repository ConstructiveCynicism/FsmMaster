using System.IO;
using UnityEngine;

namespace FsmMaster;

// Persists per-scene FSM edits as JSON so they can be reapplied the next time that scene loads. Uses
// UnityEngine.JsonUtility rather than a third-party JSON library, since JsonUtility ships inside
// UnityEngine.Modules (already a dependency here) and needs nothing new added to the fixed dependency list
// in CLAUDE.md. Silksong.DebugMod follows the same JsonUtility-based approach and the same
// Application.persistentDataPath-rooted storage convention (agent-context/Silksong.DebugMod-main/DebugMod.cs,
// ModBaseDirectory; agent-context/Silksong.DebugMod-main/SaveStates/SaveStateManager.cs).
internal static class FsmSaveDataStore
{
    private static readonly string DataDirectory = Path.Combine(Application.persistentDataPath, "FsmMasterData");

    private static string GetFilePath(string sceneName) => Path.Combine(DataDirectory, $"{sceneName}.json");

    public static SceneEdits Load(string sceneName)
    {
        string filePath = GetFilePath(sceneName);
        if (!File.Exists(filePath))
        {
            return new SceneEdits { SceneName = sceneName };
        }

        SceneEdits? loaded = JsonUtility.FromJson<SceneEdits>(File.ReadAllText(filePath));
        return loaded ?? new SceneEdits { SceneName = sceneName };
    }

    public static void Save(SceneEdits edits)
    {
        if (!Directory.Exists(DataDirectory))
        {
            Directory.CreateDirectory(DataDirectory);
        }

        File.WriteAllText(GetFilePath(edits.SceneName), JsonUtility.ToJson(edits, prettyPrint: true));
    }

    // Removes one FsmKey's persisted overrides from a scene's save file (used by Reset), so the next time
    // that scene loads the FSM comes up unmodified instead of having the override reapplied to it.
    public static void ClearFsmKey(string sceneName, string fsmKey)
    {
        SceneEdits edits = Load(sceneName);
        int removed = edits.FsmEdits.RemoveAll(e => e.FsmKey == fsmKey);
        if (removed > 0)
        {
            Save(edits);
        }
    }
}
