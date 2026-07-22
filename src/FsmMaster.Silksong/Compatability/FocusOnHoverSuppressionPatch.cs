// SPDX-License-Identifier: EUPL-1.2
using HarmonyLib;
using InControl;

namespace FsmMaster;

// Suppresses HollowKnightInputModule's "select whatever the mouse is hovering" behavior while any
// CanvasTextField is focused, restoring it immediately afterward. Without this, moving the mouse
// toward another clickable widget (e.g. the Load button) while a field is focused deselects that
// field mid-hover, which can interfere with the click landing on the intended target in the same
// input pass.
[HarmonyPatch(typeof(HollowKnightInputModule), nameof(HollowKnightInputModule.ProcessMove))]
internal static class FocusOnHoverSuppressionPatch
{
    [HarmonyPrefix]
    private static void Prefix(HollowKnightInputModule __instance, out bool __state)
    {
        __state = __instance.focusOnMouseHover;
        if (CanvasTextField.AnyFieldFocused)
        {
            __instance.focusOnMouseHover = false;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(HollowKnightInputModule __instance, bool __state)
    {
        __instance.focusOnMouseHover = __state;
    }
}
