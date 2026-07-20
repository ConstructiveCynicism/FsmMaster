#if NET35
using System;
using HutongGames.PlayMaker;

namespace FsmMaster;

// net35's PlayMaker build has no Fsm.StateChanged field of its own (see FsmActiveStateTracker's
// class-level comment) - the HK1221 loader instead installs a Harmony postfix on Fsm.EnterState
// (confirmed the only place every state entry funnels through, public or not) and forwards it here, so
// FsmActiveStateTracker gets the same "every state entered, in order, even within a single-frame
// multi-hop chain" guarantee a real StateChanged field would have provided, without Core itself
// depending on Harmony (which isn't available on every loader - see the hk1578 target's own hooking
// approach in platform-inventory.md).
public static class FsmStateChangeBridge
{
    public static event Action<Fsm, FsmState>? StateEntered;

    public static void RaiseStateEntered(Fsm instance, FsmState state) => StateEntered?.Invoke(instance, state);
}
#endif
