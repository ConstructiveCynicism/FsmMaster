using System;
using InControl;

namespace FsmMaster;

// Suppresses HollowKnightInputModule's "select whatever the mouse is hovering" behavior while any
// CanvasTextField is focused, restoring it immediately afterward. Without this, moving the mouse
// toward another clickable widget (e.g. the Load button) while a field is focused deselects that
// field mid-hover, which can interfere with the click landing on the intended target in the same
// input pass. This loader's actual active EventSystem module is InControl.HollowKnightInputModule,
// not InControl.InControlInputModule - decompiling Assembly-CSharp showed UIManager.inputModule is
// typed HollowKnightInputModule, a direct subclass of UnityEngine.EventSystems.StandaloneInputModule
// with its own ProcessMove override and its own focusOnMouseHover field, unrelated to
// InControlInputModule entirely. This loader generation's MonoMod HookGen wraps ProcessMove despite
// it being protected (confirmed against the real MMHOOK_Assembly-CSharp.dll), so no reflection-based
// detour is needed the way an inaccessible-type hook would otherwise require.
//
// ProcessMove is called continuously (every input poll), same risk profile as
// FsmActivationPatches.OnFsmEnterState - wrapped in try/catch for the same reason that hook needed
// it on the old Modding API: an uncaught exception escaping a patch on a hot path caused severe,
// hard-to-trace corruption elsewhere in the game on that older Mono runtime. This loader generation's
// MonoMod-detoured methods haven't independently reproduced that failure, but the same defensive
// wrapping costs nothing and keeps the two loader generations' hot-path hooks consistent.
internal static class FocusOnHoverSuppressionPatch
{
    internal static void OnProcessMove(On.InControl.HollowKnightInputModule.orig_ProcessMove orig, HollowKnightInputModule self, UnityEngine.EventSystems.PointerEventData pointerEvent)
    {
        bool previous = self.focusOnMouseHover;
        try
        {
            if (CanvasTextField.AnyFieldFocused)
            {
                self.focusOnMouseHover = false;
            }
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FocusOnHoverSuppressionPatch.OnProcessMove (pre) threw: {ex}");
        }

        orig(self, pointerEvent);

        try
        {
            self.focusOnMouseHover = previous;
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FocusOnHoverSuppressionPatch.OnProcessMove (post) threw: {ex}");
        }
    }
}
