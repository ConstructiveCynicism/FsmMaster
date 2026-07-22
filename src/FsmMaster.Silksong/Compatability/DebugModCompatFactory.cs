// SPDX-License-Identifier: EUPL-1.2
using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace FsmMaster;

// Pure C# surface FsmMasterPlugin's own field is typed against - no DebugMod type appears anywhere in
// this interface's signature, so an IDebugModCompat? field on FsmMasterPlugin never forces DebugModCompat
// (and its DebugMod-typed fields, e.g. SaveState) to load.
internal interface IDebugModCompat
{
    void Unhook();
    void PollPendingReload();
}

// The half of FsmMaster's DebugMod integration that has to stay safe to call even when DebugMod isn't
// installed. Deliberately holds no field, parameter, or return type from DebugMod anywhere in its own
// declaration - Mono resolves a type's full field layout (including DebugModCompat's own SaveState
// field) the moment any static member of that type is invoked, even along a branch that's never actually
// taken at runtime, which is what made the previous single-method DebugModCompat.TryCreate crash
// FsmMasterPlugin.Awake outright whenever DebugMod wasn't present. Splitting the "is DebugMod even here"
// check into this separate class, and the actual `new DebugModCompat(...)` call into its own method
// below, keeps that resolution from ever happening unless the check has already passed.
internal static class DebugModCompatFactory
{
    public static IDebugModCompat? TryCreate(FsmEditManager editManager, Action rescanLiveFsms, ManualLogSource logger)
    {
        if (!Chainloader.PluginInfos.ContainsKey(DebugMod.DebugMod.Id))
        {
            return null;
        }

        return CreateAndHook(editManager, rescanLiveFsms, logger);
    }

    // NoInlining is load-bearing here, not a perf hint. The JIT resolves every type an instruction
    // references while compiling the method that contains it, before any of that method's own branches
    // run - so if this were inlined into TryCreate, the `new DebugModCompat(...)` below would become
    // part of TryCreate's own compiled body and force DebugModCompat's field layout to resolve on every
    // call, guard or no guard. Keeping it a distinct method means it's only ever JIT-compiled once
    // TryCreate's Chainloader check above has already confirmed DebugMod is actually loaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDebugModCompat CreateAndHook(FsmEditManager editManager, Action rescanLiveFsms, ManualLogSource logger)
    {
        var compat = new DebugModCompat(editManager, rescanLiveFsms, logger);
        compat.Hook();
        return compat;
    }
}
