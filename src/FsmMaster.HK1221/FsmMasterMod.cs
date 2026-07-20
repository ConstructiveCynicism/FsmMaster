using HarmonyLib;
using Modding;
using UnityEngine;

namespace FsmMaster;

public class FsmMasterMod : Mod<FsmMasterSaveSettings, FsmMasterGlobalSettings>, IMod
{
    internal static FsmMasterMod? Instance { get; private set; }

    // Exposed so FsmMasterDriver can install/uninstall FsmStateEnteredPatch's postfix on demand (see
    // that patch's own comment for why it isn't attribute-patched here like the others) - only ever
    // called from that one on-demand site, no other patch needs it.
    internal static Harmony? HarmonyInstance { get; private set; }

    public override void Initialize()
    {
        Instance = this;

        // No OnDestroy/unpatch pairing needed here - the old Modding API has no hot-reload, Initialize()
        // runs exactly once per game process, and these patches stay installed for the process's
        // lifetime - unlike FsmStateEnteredPatch, which FsmMasterDriver installs/uninstalls on demand
        // (see FsmMasterDriver.Update).
        HarmonyInstance = Harmony.CreateAndPatchAll(typeof(FsmActivatedPatch));
        HarmonyInstance.PatchAll(typeof(ForceCursorVisiblePatch));
        HarmonyInstance.PatchAll(typeof(FocusOnHoverSuppressionPatch));

        // Soft dependency, no-op if DebugMod isn't installed - see DebugModSavestateCompat's own
        // top comment for why this can't be an attribute-driven Harmony patch like the ones above.
        DebugModSavestateCompat.TryHook(HarmonyInstance);

        var driverObject = new GameObject("FsmMasterDriver");
        driverObject.AddComponent<FsmMasterDriver>();
        Object.DontDestroyOnLoad(driverObject);

        Log("FsmMaster initialized.");
    }

    public override string GetVersion() => "hk1221v0.3.2";

    public override bool IsCurrent() => true;
}
