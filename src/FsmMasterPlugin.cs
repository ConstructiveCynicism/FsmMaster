using BepInEx;

namespace FsmMaster;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.ace9653.fsmmaster")]
public partial class FsmMasterPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        // Put your initialization logic here
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
    }
}
