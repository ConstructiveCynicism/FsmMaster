using BepInEx;
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

        string sceneName = SceneManager.GetActiveScene().name;
        PlayMakerFSM[] fsms = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None);
        FsmSnapshot snapshot = FsmDataCollector.CollectSnapshot(sceneName, fsms);

        new FsmConsoleLogger(Logger).LogSnapshot(snapshot);
    }
}
