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
[HarmonyPatch(typeof(Fsm), "Preprocess", new Type[] { })]
internal static class FsmActivatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(Fsm __instance)
    {
        // Wrapped in try/catch so any exception this patch throws is logged explicitly rather than
        // relying on however Harmony's generated trampoline propagates it - this patch runs on every
        // single FSM in the game, so a silent/misattributed exception here would be hard to trace back.
        try
        {
            PlayMakerFSM? component = __instance!.FsmComponent;
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

            // Must run before GetActiveEditSet/ApplyEditSet below - closes the lifecycle gap where an
            // edit made against an FSM whose owning GameObject was inactive at edit time (so PlayMaker
            // never ran Preprocess on it, leaving every FsmStateAction.State backref null) gets recorded
            // but not physically installed on that instance - see FsmEditManager.InstallSequencer's own
            // comment on this same gap.
            editManager.ReconcileLiveInstance(fsmKey, __instance);

            if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
            {
                editManager.ApplyEditSet(editSet);
            }
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FsmActivatedPatch threw: {ex}");
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
// regardless of visibility - so a postfix here, forwarded through Core's FsmStateChangeBridge, gives
// FsmActiveStateTracker the same "observe every state entered, in order, even within a single-frame
// multi-hop chain" guarantee a real StateChanged field would have.
//
// EnterState fires on every single state transition of every live FSM in the game, continuously -
// unlike FsmActivatedPatch (once per FSM lifetime) or ForceCursorVisiblePatch (once per frame, one call
// site), this is a genuine hot path. Live-testing confirmed the hook itself (installed on demand, empty
// postfix body) is safe - the game only broke once this postfix actually called
// FsmStateChangeBridge.RaiseStateEntered with FsmActiveStateTracker subscribed to it. No [HarmonyPatch]
// target attribute here since the patch is applied manually against EnterStateMethod instead, mirroring
// the Silksong loader's own on-demand ForceCursorVisiblePatch pattern.
internal static class FsmStateEnteredPatch
{
    // Wrapped in try/catch, unlike a normal production patch, specifically because this call chain
    // (RaiseStateEntered -> FsmActiveStateTracker's subscribed handler) is the one under active
    // investigation for the confirmed-in-testing game-breaking issue - if an exception in there is
    // escaping through Harmony's trampoline uncaught (and, on this old Mono runtime, corrupting
    // something beyond just this one call), catching and logging it here should surface that instead of
    // it happening silently.
    internal static void Postfix(Fsm __instance, FsmState state)
    {
        try
        {
            FsmStateChangeBridge.RaiseStateEntered(__instance, state);
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FsmStateEnteredPatch threw: Fsm={__instance?.Name}, State={state?.Name}, Exception={ex}");
        }
    }
}
