using HarmonyLib;
using Modding;
using UnityEngine;

namespace FsmMaster;

public class FsmMasterMod : Mod<FsmMasterSaveSettings, FsmMasterGlobalSettings>, IMod
{
    internal static FsmMasterMod? Instance { get; private set; }

    private static Harmony? _harmony;

    public override void Initialize()
    {
        Instance = this;

        // No OnDestroy/unpatch pairing needed here - the old Modding API has no hot-reload, Initialize()
        // runs exactly once per game process, and the patches stay installed for the process's lifetime.
        _harmony = Harmony.CreateAndPatchAll(typeof(FsmActivatedPatch));
        _harmony.PatchAll(typeof(ForceCursorVisiblePatch));
        _harmony.PatchAll(typeof(FsmStateEnteredPatch));

        // Soft dependency, no-op if DebugMod isn't installed - see DebugModSavestateCompat's own
        // top comment for why this can't be an attribute-driven Harmony patch like the ones above.
        DebugModSavestateCompat.TryHook(_harmony);

        var driverObject = new GameObject("FsmMasterDriver");
        driverObject.AddComponent<FsmMasterDriver>();
        UnityEngine.Object.DontDestroyOnLoad(driverObject);

        Log("FsmMaster initialized.");
    }

    public override string GetVersion() => "0.1.0";

    public override bool IsCurrent() => true;
}
