// SPDX-License-Identifier: EUPL-1.2
using System;
using HarmonyLib;
using InControl;

namespace FsmMaster;

// Suppresses InControlInputModule's "select whatever the mouse is hovering" behavior while any
// CanvasTextField is focused, restoring it immediately afterward. Without this, moving the mouse
// toward another clickable widget (e.g. the Load button) while a field is focused deselects that
// field mid-hover, which can interfere with the click landing on the intended target in the same
// input pass. Same patch as the Silksong loader's own FocusOnHoverSuppressionPatch, targeting
// InControlInputModule instead of HollowKnightInputModule - see CanvasTextField.cs's own comment on
// this naming delta between the two games.
// "ProcessMove" is a string literal, not nameof(InControlInputModule.ProcessMove) - that method is
// protected on this type (unlike Silksong's public HollowKnightInputModule.ProcessMove), so nameof
// can't resolve it from here even though Harmony can still patch it by name regardless of visibility.
//
// ProcessMove is called continuously (every input poll), same risk profile as FsmStateEnteredPatch's
// EnterState hook - both Prefix/Postfix are wrapped in try/catch for the same reason that one needed
// it: an uncaught exception escaping a Harmony postfix/prefix on a hot path caused severe, hard-to-
// trace corruption elsewhere in the game on this old Mono runtime (see FsmStateEnteredPatch's own
// history). __state defaults to the pre-suppression value before the try so Postfix always restores
// something sane even if Prefix's body throws.
[HarmonyPatch(typeof(InControlInputModule), "ProcessMove")]
internal static class FocusOnHoverSuppressionPatch
{
    [HarmonyPrefix]
    private static void Prefix(InControlInputModule __instance, out bool __state)
    {
        __state = __instance.focusOnMouseHover;
        try
        {
            if (CanvasTextField.AnyFieldFocused)
            {
                __instance.focusOnMouseHover = false;
            }
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FocusOnHoverSuppressionPatch.Prefix threw: {ex}");
        }
    }

    [HarmonyPostfix]
    private static void Postfix(InControlInputModule __instance, bool __state)
    {
        try
        {
            __instance.focusOnMouseHover = __state;
        }
        catch (Exception ex)
        {
            FsmMasterMod.Instance?.LogError($"FocusOnHoverSuppressionPatch.Postfix threw: {ex}");
        }
    }
}
