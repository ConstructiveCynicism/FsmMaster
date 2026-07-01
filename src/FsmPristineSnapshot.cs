using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Everything needed to put one FsmKey's live instances back exactly as they were, captured lazily the first
// time any edit actually touches that key in this session (not proactively for every untouched FSM).
// OriginalValues reuses FsmEditSet's shape for the parts that round-trip as plain data
// (variables/action-fields/transitions), resolved back onto a live Fsm by name at restore time. The action
// lists below instead hold direct object references - FsmStateAction exposes its own owning .State/.Fsm
// back-references (HutongGames.PlayMaker.FsmStateAction), so restoring them doesn't need to re-match them
// against a particular live instance by position; each action already knows where it lives.
internal sealed class FsmPristineSnapshot
{
    public FsmEditSet OriginalValues { get; } = new();

    // stateName -> indices of actions FsmMaster disabled while neutering that state (duplicate FSM instances
    // are assumed structurally identical, so one shared index list applies to every instance's own actions).
    public Dictionary<string, List<int>> NeuteredActionIndices { get; } = new();

    // The synthetic exit action injected into each neutered state instance - one entry per live instance
    // touched, since every duplicate gets its own action object.
    public List<FsmStateAction> InjectedExitActions { get; } = new();

    // (originalAction, sequencerAction) for each sequencer installed - one entry per live instance touched.
    public List<(FsmStateAction Original, FsmStateAction Sequencer)> InstalledSequencers { get; } = new();
}
