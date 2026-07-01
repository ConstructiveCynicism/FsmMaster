using System.Collections.Generic;
using System.Linq;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FsmMaster;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.ace9653.fsmmaster")]
[BepInDependency("org.silksong-modding.fsmutil")]
public partial class FsmMasterPlugin : BaseUnityPlugin
{
    private FsmEditManager? _editManager;
    private FsmVariableTracker? _variableTracker;

    internal FsmVariableTracker? VariableTracker => _variableTracker;

    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");

        _editManager = new FsmEditManager(Logger);
        _variableTracker = new FsmVariableTracker(fsmKey => _editManager.GetLiveInstances(fsmKey));

        string sceneName = SceneManager.GetActiveScene().name;
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);
        FsmSnapshot snapshot = FsmDataCollector.CollectSnapshot(sceneName, fsms);

        //new FsmConsoleLogger(Logger).LogSnapshot(snapshot);

        ApplyPersistedEditsForScene(sceneName, fsms);

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;

        _editManager?.RevertAllForUnload();
        _editManager = null;
        _variableTracker = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        PlayMakerFSM[] fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);
        ApplyPersistedEditsForScene(scene.name, fsms);
    }

    // Groups the freshly discovered live FSMs by FsmKey (see FsmIdentity, which also covers scene-authored
    // duplicate objects sharing an FSM), registers them with the edit manager, and reapplies whatever this
    // scene's save file has recorded for any key that's actually present.
    private void ApplyPersistedEditsForScene(string sceneName, PlayMakerFSM[] components)
    {
        if (_editManager == null)
        {
            return;
        }

        Dictionary<string, List<PlayMakerFSM>> groups = FsmIdentity.DiscoverFsmGroups(components);
        foreach (KeyValuePair<string, List<PlayMakerFSM>> group in groups)
        {
            _editManager.RegisterLiveInstances(group.Key, group.Value.Select(c => c.Fsm));
        }

        foreach (FsmEditSet editSet in FsmSaveDataStore.LoadAllForScene(sceneName))
        {
            if (groups.ContainsKey(editSet.FsmKey))
            {
                _editManager.ApplyEditSet(editSet);
            }
        }
    }

    // Reverts a live FSM to its pristine values and strips its persisted overrides, so this scene coming up
    // again later leaves it unmodified too.
    public void ResetFsm(string sceneName, string fsmKey)
    {
        _editManager?.ResetFsm(fsmKey);
        FsmSaveDataStore.ClearFsmKey(sceneName, fsmKey);
    }
}
