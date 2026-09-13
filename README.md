# TRMachinist

[![Windows build and smoke tests](https://github.com/yasinisiktek-cloud/TRMachinist/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/yasinisiktek-cloud/TRMachinist/actions/workflows/build.yml)

**Open-source CNC machine simulation and Triple-Dexel material-removal engine for G-code verification, machine kinematics, collision detection, and in-process workpiece (IPW) simulation.**

TRMachinist is an experimental CNC simulation platform written in C#/.NET 8. Its core focuses on deterministic NC parsing, machine-coordinate resolution, tool/holder geometry, collision analysis, and a Triple-Dexel stock representation for material-removal simulation.

> Status: **alpha / active development**. The current public source baseline is `0.1.0-alpha4-test9-gpu-r33`, validated on GitHub Actions with a clean Windows .NET 8 Release build and smoke-test run. This project is not a substitute for machine-builder documentation, a certified postprocessor, controller simulation, or shop-floor prove-out procedures.

## Highlights

- Triple-Dexel stock model and surface meshing
- Swept cutting-volume / frustum processing
- In-process workpiece (IPW) updates
- Fanuc-oriented modal G-code parsing support
- SINUMERIK-oriented modal cycle handling
- Machine-coordinate and work-offset transforms
- Tool and holder geometry generation
- Mesh / tool / stock collision logic
- Deterministic playback and timing helpers
- Windows WPF + DirectX visualization
- Smoke-test suite covering geometry, parsing, collision, and stock behavior

## Repository layout

```text
src/
  TRMachinist.Core/        Core simulation, parsing, kinematics, collision and Triple-Dexel engine
  TRMachinist.Simulator/   Windows WPF/DirectX simulator UI
  TRMachinist.SmokeTests/  Deterministic validation and regression tests
samples/
  fanuc/                   Generic, non-production NC samples
  sinumerik/               Generic, non-production NC samples
docs/                      Architecture and engineering notes
```

## Requirements

- Windows 10/11 for the simulator UI
- .NET 8 SDK
- Visual Studio 2022 or `dotnet` CLI
- x64 recommended for the WPF/DirectX simulator

## Build

```powershell
dotnet restore TRMachinist.sln
dotnet build TRMachinist.sln -c Release
```

Run the smoke tests:

```powershell
dotnet run --project src/TRMachinist.SmokeTests/TRMachinist.SmokeTests.csproj -c Release
```

Run the simulator:

```powershell
dotnet run --project src/TRMachinist.Simulator/TRMachinist.Simulator.csproj -c Release
```

## Triple-Dexel engine

The stock engine represents material through three orthogonal dexel fields. The implementation includes initialization, cutting-profile bounds, frustum sweeps, surface reconstruction, target comparison, and progress tracking. See [`docs/TRIPLE_DEXEL_ENGINE.md`](docs/TRIPLE_DEXEL_ENGINE.md).

## Controller scope

TRMachinist currently contains controller-aware logic for Fanuc-style and SINUMERIK-style NC structures, but it intentionally does **not** assume machine-builder-specific M-codes, PLC behavior, options, or safety logic. A program parsing successfully does not mean it is safe to run on a real machine.

## Samples and privacy

Only synthetic/generic examples belong in this public repository. Real customer NC programs, proprietary postprocessors, licensed machine packages, and confidential production data must not be committed.

## Contributing

Contributions are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) and open an issue before large architectural changes.

## Security and safety

Please read [`SECURITY.md`](SECURITY.md). Never use this project to bypass machine safety systems, interlocks, limits, guards, or OEM protections.

## License

Licensed under the Apache License 2.0. See [`LICENSE`](LICENSE).

Third-party components keep their own licenses. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
