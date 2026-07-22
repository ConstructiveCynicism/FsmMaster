# FsmMaster for Hollow Knight and Silksong

A live PlayMaker FSM inspector and editor for Hollow Knight and Hollow Knight:
Silksong. View any FSM in the game as an interactive graph, edit its states,
variables, and transitions while the game runs, and save your edits to
reapply them later.

Press `F3` to toggle the overlay. You can bind a key for a monitor only mode.
Both are rebindable in-game via BepInEx's Configuration Manager (Silksong) or
the Modding API's own mod settings menu (Hollow Knight 1.3.1.5/1.4.3.2/1.5.78);
on Hollow Knight 1.2.2.1, whose Modding API predates in-game settings menus,
rebind by editing the mod's saved settings file directly.

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
- Rebindable hotkeys (see above for how, per platform).
- Savestate support with DebugMod - active FSM edits are saved into and
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

| Game | Manager | Manual download | Install to |
|---|---|---|---|
| Silksong | [Thunderstore](https://thunderstore.io/c/hollow-knight-silksong/p/ConstructiveCynicism/FsmMaster) / [Cogfly](https://github.com/Nix-main/Cogfly/releases/latest) | [FsmMaster-silksong.zip](https://github.com/ConstructiveCynicism/FsmMaster/releases/latest/download/FsmMaster-silksong.zip) | `BepInEx/plugins/` |
| Hollow Knight 1.5.78 | [Lumafly](https://themulhima.github.io/Lumafly/) / [Scarab](https://github.com/fifty-six/Scarab) | [FsmMaster-hk1578.zip](https://github.com/ConstructiveCynicism/FsmMaster/releases/latest/download/FsmMaster-hk1578.zip) | `hollow_knight_Data/Managed/Mods/` |
| Hollow Knight 1.4.3.2 | — (manual only) | [FsmMaster-hk1432.zip](https://github.com/ConstructiveCynicism/FsmMaster/releases/latest/download/FsmMaster-hk1432.zip) | `hollow_knight_Data/Managed/Mods/` |
| Hollow Knight 1.3.1.5 | — (manual only) | [FsmMaster-hk1315.zip](https://github.com/ConstructiveCynicism/FsmMaster/releases/latest/download/FsmMaster-hk1315.zip) | `hollow_knight_Data/Managed/Mods/` |
| Hollow Knight 1.2.2.1 | — (manual only) | [FsmMaster-hk1221.zip](https://github.com/ConstructiveCynicism/FsmMaster/releases/latest/download/FsmMaster-hk1221.zip) | `hollow_knight_Data/Managed/Mods/` |

Manual installs: unzip and drop the contents at the "Install to" path (each zip
is already laid out to match - for hk1221 that's a single `FsmMaster.dll` at
the zip root, the rest ship a `FsmMaster/` folder). All five HK targets need
their platform's [Modding API](https://github.com/hk-modding/api) installed
first, same as any other mod for that build.

> [!IMPORTANT]
> For moderation reasons, the Silksong version requires [Silksong.ModList](https://github.com/silksong-modding/Silksong.ModList) to be installed. This mod is included in the Silksong release download.
If Silksong.ModList is not installed, FsmMaster will silently fail to load.

> [!TIP]
> FsmMaster works standalone, but installing [DebugMod](https://github.com/hk-speedrunning/Silksong.DebugMod) (Silksong) or [DebugMod](https://github.com/TheMulhima/HollowKnight.DebugMod) (Hollow Knight) alongside it gives you
> savestate support - active FSM edits are automatically captured and restored
> when you save or load a savestate. I recommend installing DebugMod for the
> best experience.

## Credits

- Core development - ConstructiveCynicism
- FSM edit helpers derived from [Silksong.FsmUtil](https://github.com/silksong-modding/Silksong.FsmUtil) - silksong-modding (EUPL-1.2)
- Graph layout and FSM visualization conventions inspired by [FSMExpress](https://github.com/nesrak1/FSMExpress) - nesrak1
- Built on DebugMod's savestate system - [Silksong.DebugMod](https://github.com/hk-speedrunning/Silksong.DebugMod) / [HollowKnight.DebugMod](https://github.com/TheMulhima/HollowKnight.DebugMod)

## License

FsmMaster contains code derived from [Silksong.FsmUtil](https://github.com/silksong-modding/Silksong.FsmUtil)
(© silksong-modding, EUPL-1.2) and is licensed as a whole under the
**[EUPL-1.2](LICENSE)**. See `NOTICE` and per-file SPDX headers
for attribution details.
