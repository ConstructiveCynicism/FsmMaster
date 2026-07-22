// SPDX-License-Identifier: EUPL-1.2
#if NET35 || NET472
using System;
using HutongGames.PlayMaker;

namespace FsmMaster;

// net35's PlayMaker build has no Fsm.StateChanged field of its own, and net472's Core.dll is shared by
// both hk1432 (same PlayMaker build as hk1221, no StateChanged either) and hk1578 (which does have it) -
// see FsmActiveStateTracker's class-level comment for why using the field on net472 at all isn't safe.
// Each net35/net472 loader instead installs its own EnterState hook (a Harmony postfix on hk1221, a
// MonoMod On.Fsm.EnterState hook on hk1432/hk1578 - confirmed the only place every state entry funnels
// through, public or not) and forwards it here, so FsmActiveStateTracker gets the same "every state
// entered, in order, even within a single-frame multi-hop chain" guarantee a real StateChanged field
// would have provided, without Core itself depending on Harmony or MonoMod directly.
public static class FsmStateChangeBridge
{
    public static event Action<Fsm, FsmState>? StateEntered;

    public static void RaiseStateEntered(Fsm instance, FsmState state) => StateEntered?.Invoke(instance, state);
}
#endif
