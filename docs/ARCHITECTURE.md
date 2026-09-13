# Architecture

TRMachinist separates controller/program interpretation, machine/job data, physical motion, stock state and rendering.

## `TRMachinist.Core`

The core is UI-independent and contains:

- G-code parsing and modal state,
- operation/path construction,
- coordinate and machine-tool transforms,
- deterministic playback,
- package readers,
- tool/holder geometry,
- collision geometry and policies,
- triple-dexel stock and surface reconstruction.

The most important design rule is that parser/controller semantics and physical machine kinematics are not silently conflated. Machine-builder behavior must be supplied by data or explicit code rather than guessed from a controller family.

## `TRMachinist.Simulator`

The Windows WPF application is responsible for scene composition, DirectX-backed IPW rendering, user interaction and diagnostics. Stock computation is performed outside the UI thread and published to the visible scene through a controlled pipeline.

## `TRMachinist.SmokeTests`

The smoke-test executable provides deterministic tests without requiring published customer machine/job packages. It exercises parser behavior, geometry, stock intervals, surface masks, collision primitives and controller-specific regression cases.

## Packages and data

Private development used machine (`.trmac`) and job (`.trjob`) packages. Those packages are deliberately excluded from the public repository because real machine, CAD and production data may have separate rights or confidentiality constraints. Public test fixtures should be synthetic or explicitly redistributable.
