// SPDX-License-Identifier: EUPL-1.2
using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FsmMaster;

// MonoMod On.* hook equivalents of the old Modding API's Harmony patches (see the hk1221 loader's own
// FsmActivationPatches.cs) - this loader generation has no HarmonyX, hooks are installed/removed as
// plain C# event subscriptions against MonoMod's HookGen-generated On.<Type> classes instead.
internal static class FsmActivationPatches
{
    // Targets Fsm's own private, parameterless Preprocess() rather than PlayMakerFSM.Awake(): every
    // path that initializes an Fsm for the first time funnels through this exact method, which is also
    // the one that sets Preprocessed = true and calls action.Init(state) for every action, so a hook
    // here is guaranteed to run exactly once per Fsm, right after its actions' State backrefs become
    // safe to touch. Fsm.Owner (hence FsmComponent) is set before either caller reaches this method, so
    // self.FsmComponent is never null here. Installed for the process's whole lifetime in
    // FsmMasterMod.Initialize - no unhook needed, this loader generation has no hot-reload.
    internal static void OnFsmPreprocess(On.HutongGames.PlayMaker.Fsm.orig_Preprocess orig, Fsm self)
    {
        orig(self);

        // Wrapped in try/catch so any exception this hook throws is logged explicitly rather than
        // relying on however MonoMod's generated trampoline propagates it - this hook runs on every
        // single FSM in the game, so a silent/misattributed exception here would be hard to trace back.
        try
        {
            PlayMakerFSM? component = self.FsmComponent;
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
            // never ran Preprocess on it, leaving every FsmStateAction.State backref null) gets
            // recorded but not physically installed on that instance - see FsmEditManager's own
            // InstallSequencer comment on this same gap.
            editManager.ReconcileLiveInstance(fsmKey, self);

            if (editManager.GetActiveEditSet(fsmKey) is { } editSet)
            {
                editManager.ApplyEditSet(editSet);
            }
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"OnFsmPreprocess threw: {ex}");
        }
    }

    // InputHandler.SetCursorVisible(bool) is private and unused - decompiling Assembly-CSharp.dll turned
    // up no call site for it at all, so a hook there (the loader's original approach) never fired and
    // the cursor stayed hidden during gameplay regardless. The actual per-frame driver is InputHandler's
    // own OnGUI (same as hk1221's), which for the gameplay branch defers to Modding.ModHooks.OnCursor:
    // that method sets Cursor.lockState = None unconditionally, then either invokes ModHooks.CursorHook
    // if something has subscribed to it, or falls back to Cursor.visible = gm.isPaused. CursorHook is a
    // public event built for exactly this - subscribing here replaces the paused/unpaused fallback
    // entirely, so it must reproduce that fallback itself for the case the overlay isn't forcing the
    // cursor on.
    internal static void OnCursorHook()
    {
        if (ForceCursorVisiblePatch.ForceVisible)
        {
            Cursor.visible = true;
            return;
        }

        Cursor.visible = GameManager.instance != null && GameManager.instance.isPaused;
    }

    // EnterState(FsmState) is the one place every state entry funnels through - this loader generation
    // has no Fsm.StateChanged field on hk1432 (same PlayMaker build as hk1221) and Core's net472 build
    // is shared between hk1432 and hk1578, so both route through FsmStateChangeBridge uniformly instead
    // of branching on hk1578's own StateChanged field - see FsmStateChangeBridge.cs's own comment.
    // Installed/removed on demand by FsmMasterDriver (mirrors hk1221's own on-demand FsmStateEnteredPatch)
    // rather than for the process's whole lifetime, since FsmActiveStateTracker only wants this feed
    // while at least one graph tab is open.
    internal static void OnFsmEnterState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm self, FsmState state)
    {
        orig(self, state);

        try
        {
            FsmStateChangeBridge.RaiseStateEntered(self, state);
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"OnFsmEnterState threw: Fsm={self?.Name}, State={state?.Name}, Exception={ex}");
        }
    }
}

// Mirrors FsmGraphOverlay.IsVisible every frame (see FsmMasterDriver.Update) - forced true instead of
// patched in/out on demand: this hook is already installed for the process's whole lifetime, same as
// every other hook on this loader, and the check itself costs nothing extra.
internal static class ForceCursorVisiblePatch
{
    internal static bool ForceVisible;
}
