# FsmMaster for Hollow Knight and Silksong

A live PlayMaker FSM inspector and editor for Hollow Knight: Silksong. View any
FSM in the game as an interactive graph, edit its states, variables, and
transitions while the game runs, and save your edits to reapply them later.

Press `F3` to toggle the overlay. You can bind a key for a monitor only mode. Both are rebindable in-game via BepInEx's Configuration Manager.

For any questions or bug reports, please join the [Modding Discord](https://discord.gg/F6Y5TeFQ8j) or [Speedrunning Discord](https://discord.gg/3JtHPsBjHD).

---
- [FsmMaster for Hollow Knight and Silksong](#fsmmaster-for-hollow-knight-and-silksong)
  - [Features](#features)
  - [Coming Soon](#coming-soon)
  - [Controls](#controls)
    - [Panel Controls](#panel-controls)
    - [Graph Controls](#graph-controls)
    - [State Panel](#state-panel)
    - [Sequencer](#sequencer)
  - [Installation](#installation)
  - [Credits](#credits)
---

## Features
- Graphic, customizable UI with performance toggles for lower-end machines.
- FSM editing and viewing with live information - watch states, variables, and
  transitions update in real time as the FSM actually runs.
- Rebindable hotkeys via BepInEx's Configuration Manager (Silksong)
- Savestate support with [DebugMod](https://github.com/hk-speedrunning/Silksong.DebugMod) - active FSM edits are saved into and
  restored from savestates automatically.
- Custom RNG sequencing - replace an FSM's random event picks with a fixed,
  ordered sequence you control.
- Hidden variable editing, including variables PlayMaker doesn't normally expose in its own editor.
- Monitoring panel compatible with multiple simultaneous FSMs.
- Interactable graph - click and drag your way through an FSM's states,
  transitions, and events instead of reading raw data.
- Custom config saving/loading, with optional auto-load on scene change.

## Coming Soon
- Adaptive RNG, to make practicing attacks faster based on what you're struggling with
- Timer Mod integration, starting and stopping the timer based on Fsm values
- Hollow Knight backport
- Sequencers for more actions

## Controls

### Panel Controls
- **Open** - choose an FSM to display.
- **Save** - save edits to file.
- **Load** - load edits from file.
- **Undo** - undo last edit.
- **Reset** - reset FSM to default.
- **Hide** - hide graph.
- **Auto** - load last saved file automatically upon scene change.

### Graph Controls
- **Scroll Wheel** - zoom.
- **Drag** - pan.
- **Click** - select a state, event, or transition.
- **Double Click** - change states.
- **Right Click** - disable a state or transition.
- **Click and Drag from a transition** - retarget it to a different state.

### State Panel
- Edit most basic variable types directly.
- Click the blue dot next to a variable to add it to the monitor panel.

### Sequencer
- Drag events to build a custom sequence, and rearrange them to replace an
  FSM's random event selection with a fixed pattern.

## Installation

> [!IMPORTANT]
> For moderation reasons, the Silksong version requires [Silksong.ModList](https://github.com/silksong-modding/Silksong.ModList) to be installed. This mod is included in the [release download](<https://github.com/ConstructiveCynicism/FsmMaster/releases/latest>).
If Silksong.ModList is not installed, FsmMaster will silently fail to load.

FsmMaster can be installed via [Cogfly](https://github.com/Nix-main/Cogfly/releases/latest), [Thunderstore](https://thunderstore.io/c/hollow-knight-silksong/p/ConstructiveCynicism/FsmMaster), [Lumafly](https://themulhima.github.io/Lumafly/) or manually installed with the latest [release download](<https://github.com/ConstructiveCynicism/Silksong.FsmMaster/releases/latest>).

> [!TIP]
> FsmMaster works standalone, but installing [DebugMod](https://github.com/hk-speedrunning/Silksong.DebugMod) alongside it gives you
> savestate support - active FSM edits are automatically captured and restored
> when you save or load a savestate. I recommend installing DebugMod for the
> best experience.

## Credits

- Core development - ConstructiveCynicism
- Graph layout and FSM visualization conventions inspired by [FSMExpress](https://github.com/nesrak1/FSMExpress) - nesrak1
- Built on [Silksong.DebugMod](https://github.com/hk-speedrunning/Silksong.DebugMod)'s savestate system

## License

[MIT](LICENSE)
