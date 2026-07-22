// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Generic;
using HarmonyLib;
using Modding;
using UnityEngine;

namespace FsmMaster;

public class FsmMaster : Mod<FsmMasterSaveSettings, FsmMasterGlobalSettings>, IMod
{
    internal static FsmMaster? Instance { get; private set; }

    // Exposed so FsmMasterDriver can install/uninstall FsmStateEnteredPatch's postfix on demand (see
    // that patch's own comment for why it isn't attribute-patched here like the others) - only ever
    // called from that one on-demand site, no other patch needs it.
    internal static Harmony? HarmonyInstance { get; private set; }

#region API
    // =========================================================================================
    // Public API for other mods
    // =========================================================================================

    /// <summary>
    /// Returns every FSM edit currently in effect this session as a JSON string, in the same format as files/savestate data
    /// </summary>
    /// <returns>A JSON string describing every active edit set. Empty/no edits still returns valid JSON.</returns>
    public static string GetActiveEdits()
    {
        List<FsmEditSet> activeEditSets = FsmMasterDriver.Instance?.EditManager?.GetAllActiveEditSets()
            ?? new List<FsmEditSet>();
        return FsmSaveDataStore.SerializeEditSets(activeEditSets);
    }

    /// <summary>
    /// Shows or hides the small "Fsm Edits Active" indicator the graph overlay draws in the
    /// bottom-right corner whenever at least one FSM has an edit in effect. On by default.
    /// </summary>
    public static bool ShowEditIndicator
    {
        get => FsmGraphOverlay.ShowEditIndicator;
        set => FsmGraphOverlay.ShowEditIndicator = value;
    }

    /// <summary>
    /// Whether FsmMaster should show UI and hint the toggle hotkey
    /// </summary>
    public static bool FirstRunComplete
    {
        get => Instance?.GlobalSettings.FirstRunComplete ?? false;
        set
        {
            if (Instance != null)
            {
                Instance.GlobalSettings.FirstRunComplete = value;
            }
        }
    }
#endregion

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

    // Generated at build time from this project's target prefix and $(Version), so a version bump in
    // Directory.Build.props renames the release zip and this string together.
    public override string GetVersion() => BuildInfo.ReleaseName;

    public override bool IsCurrent() => true;
}
