using System;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// Targets Fsm's own private, parameterless Preprocess() rather than PlayMakerFSM.Awake(): every path
// that initializes an Fsm for the first time funnels through this exact method, which is also the one
// that sets Preprocessed = true and calls action.Init(state) for every action, so a postfix here is
// guaranteed to run exactly once per Fsm, right after its actions' State backrefs become safe to touch.
// Fsm.Owner (hence FsmComponent) is set before either caller reaches this method, so
// __instance.FsmComponent is never null here.
[HarmonyPatch(typeof(Fsm), "Preprocess", new System.Type[] { })]
internal static class FsmActivatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(Fsm __instance)
    {
        PlayMakerFSM? component = __instance.FsmComponent;
        if (component == null)
        {
            return;
        }

        FsmEditManager? editManager = FsmMasterDriver.Instance?.EditManager;
        if (editManager == null)
        {
            return;
        }

        string fsmKey = FsmIdentity.GetFsmKey(component);

        // Must run before GetActiveEditSet/ApplyEditSet below - closes the lifecycle gap where an edit
        // made against an FSM whose owning GameObject was inactive at edit time (so PlayMaker never ran
        // Preprocess on it, leaving every FsmStateAction.State backref null) gets recorded but not
        // physically installed on that instance - see FsmEditManager.InstallSequencer's own comment on
        // this same gap.
        editManager.ReconcileLiveInstance(fsmKey, __instance);

        if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
        {
            editManager.ApplyEditSet(editSet);
        }
    }
}

// HK1 1.2.2.1's InputHandler has no isolated SetCursorVisible(bool) setter to intercept the way
// Silksong's does - InputHandler.OnGUI() sets Cursor.lockState/Cursor.visible with hardcoded literals
// inline, across five branches (title screen, menu, controller-in-menu, paused, default gameplay).
// Decompiled in full (Mono.Cecil, not guessed): the whole method is 37 IL instructions and touches
// nothing but Cursor.lockState/Cursor.visible plus a few read-only scene/pause bools - no other side
// effect to lose by skipping it outright.
//
// A postfix here (forcing Cursor.visible/lockState back after OnGUI runs) was tried first and
// confirmed broken in-game: CursorLockMode.Locked warps the OS cursor to screen center the instant
// it's set, and Unity dispatches OnGUI once per queued input event (several times per rendered frame,
// not just once) - every one of those calls re-locks-and-warps via the gameplay branch, and a postfix
// only ever undoes it a moment too late, producing a visible locked/flickering cursor. A prefix that
// skips the original method entirely while the overlay wants the cursor free avoids the warp
// happening in the first place, rather than reacting to it afterward.
[HarmonyPatch(typeof(InputHandler), "OnGUI")]
internal static class ForceCursorVisiblePatch
{
    // Mirrors FsmGraphOverlay.IsVisible every frame (see FsmMasterDriver.Update) - forced true instead
    // of patched in/out on demand (unlike the Silksong-era version, which toggled a Harmony patch on
    // InputHandler's cursor setter): this prefix is already installed for the process's whole lifetime,
    // same as every other patch on this branch, and the check itself costs nothing extra.
    internal static bool ForceVisible = false;

    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!ForceVisible)
        {
            return true;
        }

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        return false;
    }
}

// This PlayMaker version's Fsm class has no StateChanged hook of its own (confirmed by reflecting
// every field/event on Fsm - there's no such member at all, public or private). EnterState(FsmState) is
// the one place every state entry funnels through - non-public, but Harmony patches by method name
// regardless of visibility - so a postfix here, broadcast as a static event, gives every interested
// listener the same "observe every state entered, in order, even within a single-frame multi-hop chain"
// guarantee a real StateChanged field would have, without needing per-instance subscription management.
[HarmonyPatch(typeof(Fsm), "EnterState", new[] { typeof(FsmState) })]
internal static class FsmStateEnteredPatch
{
    internal static event Action<Fsm, FsmState>? StateEntered;

    [HarmonyPostfix]
    private static void Postfix(Fsm __instance, FsmState state)
    {
        StateEntered?.Invoke(__instance, state);
    }
}
