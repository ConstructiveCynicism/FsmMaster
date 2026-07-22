# Changelog

## Unreleased

- Licensing correction: `PlayMakerFsmOps.cs` in FsmMaster.Core is a trimmed-down
  port of [Silksong.FsmUtil](https://github.com/silksong-modding/Silksong.FsmUtil)
  (EUPL-1.2, © silksong-modding); prior releases shipped it under MIT without the
  required EUPL notices. FsmMaster is now licensed as a whole under the EUPL-1.2,
  with attribution restored in `NOTICE`, per-file SPDX headers, and the README.

## v0.3.5

- Added support for Hollow Knight 1.2.2.1, 1.4.3.2, and 1.5.78, alongside the
  existing Silksong release.
- Performance overhaul for the graph overlay.

## v0.3.2

- Fixed dependency issues with DebugMod and FsmUtil.

## v0.3.0

- Fixed a bug where same-room savestates didn't load FSM edits.
- Reset button now clears Sequencers.
- Selected state transitions highlight correctly in thin line mode.
- UI adjustments and bugfixes.
- Adjusted background processes for Architect integration.
