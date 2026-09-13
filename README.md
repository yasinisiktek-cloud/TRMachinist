# TRMachinist

**Open-source CNC machine simulation and triple-dexel material-removal engine for G-code verification, machine kinematics, collision analysis and in-process workpiece (IPW) simulation.**

TRMachinist is a Windows/.NET 8 research and engineering project focused on transparent, inspectable CNC simulation. The repository contains the simulation core, WPF/DirectX desktop viewer and a self-contained smoke-test suite.

> **Alpha software.** TRMachinist is not a machine-safety system and does not certify NC programs for production. Validate programs with the machine builder's documentation, controller simulation, dry-run/single-block procedures and your shop's safety process.

## Why this project exists

CNC verification often depends on closed tooling. TRMachinist explores an open implementation of the difficult parts that are useful to CAM developers, postprocessor engineers and manufacturing-software researchers:

- triple-dexel stock representation and material removal,
- regional surface reconstruction for IPW display,
- Fanuc- and SINUMERIK-aware G-code parsing,
- 3+2 / rotary machine kinematics and coordinate transforms,
- cutter, shank and holder geometry,
- collision broadphase / mesh geometry infrastructure,
- deterministic playback and timing,
- repeatable regression and geometry tests.

## Repository layout

```text
src/TRMachinist.Core        Simulation, G-code, kinematics, collision and triple-dexel engine
src/TRMachinist.Simulator   WPF/DirectX desktop simulator and IPW rendering
src/TRMachinist.SmokeTests  Self-contained regression and geometry tests
src/TRMachinist.GpuProbe    Experimental GPU rendering probe (not in the main solution)
samples/                     Synthetic, non-production NC examples
docs/                        Architecture, engine notes, benchmarks and roadmap
```

## Current engine highlights

- Three-axis dexel stock representation (X/Y/Z families).
- Swept and static cutter queries with profile-aware material removal.
- Regional surface meshing instead of rebuilding the entire stock surface for every visible update.
- G0/G1 and sampled G2/G3/helix path handling in the parser.
- Fanuc modal state and selected SINUMERIK modal drilling-cycle support.
- Machine/work coordinate transforms and rotary-axis motion infrastructure.
- Parametric cutting-tool, shank and holder geometry.
- Mesh collision geometry and oriented-bounds utilities.
- Deterministic playback and stock generation checks.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/TRIPLE_DEXEL_ENGINE.md](docs/TRIPLE_DEXEL_ENGINE.md).

## Build

Requirements:

- Windows x64
- .NET 8 SDK
- DirectX-capable environment for the desktop viewer

```powershell
dotnet restore .\TRMachinist.sln
dotnet build .\TRMachinist.sln -c Release
dotnet run --project .\src\TRMachinist.SmokeTests\TRMachinist.SmokeTests.csproj -c Release --no-build
```

Run the desktop simulator:

```powershell
dotnet run --project .\src\TRMachinist.Simulator\TRMachinist.Simulator.csproj -c Release
```

GitHub Actions repeats the Windows Release build and self-contained smoke-test run for pull requests and pushes.

## Input formats

The engine can work with TRMachinist machine/job packages and controller NC text. Machine and job packages used during private development are **not** published in this repository. The public samples are synthetic and contain no customer or production data.

Do not assume that a machine package, axis convention or controller command is portable between real machines. OEM M-codes, PLC behavior, kinematic parameters and safety logic are machine-specific.

## Development benchmark

The R33 development snapshot reduced one full internal stock-computation benchmark from **125.974 s to 75.662 s** (about **39.94%**) while preserving the accepted result checks used in that experiment. This is a development measurement, not a universal performance claim; results depend on stock, toolpath, CPU and resolution. See [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

## Project status

The repository starts from the verified R33 source snapshot (`0.1.0-alpha4-test9-gpu-r33`). It is intentionally published as an alpha engineering project. Important remaining work includes broader controller-cycle coverage, generalized simultaneous-5-axis inverse kinematics, stronger narrow-phase collision checks, adaptive stock resolution and broader public test fixtures.

## Contributing

Issues and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/ROADMAP.md](docs/ROADMAP.md). Please use only test data that you have the right to publish.

## License

TRMachinist is licensed under the **Apache License 2.0**. See [LICENSE](LICENSE). Third-party dependencies retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Vendor independence

TRMachinist is an independent open-source project. Siemens, SINUMERIK, Siemens NX, Fanuc and other names referenced by the parser or documentation belong to their respective owners. No proprietary Siemens NX binaries, machine-builder packages, customer NC programs or commercial postprocessor sources are included in this repository.
