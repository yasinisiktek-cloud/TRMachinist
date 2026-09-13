# Architecture

TRMachinist is split into three public projects.

## TRMachinist.Core

The platform-independent simulation core. Major responsibilities include:

- NC parsing and modal controller state
- operation/path construction and path timing
- machine package/job package reading
- coordinate systems and work offsets
- tool and holder geometry
- mesh collision geometry and collision policy
- deterministic simulation playback
- Triple-Dexel stock representation and material removal

## TRMachinist.Simulator

Windows WPF front end with 3D rendering. It connects the core simulation state to the interactive machine scene, path visualization, IPW updates, diagnostics, workpiece offsets, and tool/holder UI.

## TRMachinist.SmokeTests

Executable regression suite for deterministic behaviors. It exercises controller parsing, interval/geometry search, profile quality, mesh collision, stock updates, and related simulation invariants.

## Data boundary

The public repository intentionally excludes real customer NC files, proprietary postprocessors, licensed OEM machine packages, Siemens NX binaries, and local provenance snapshots.
