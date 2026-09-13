# Contributing to TRMachinist

Thanks for helping improve open CNC simulation tooling.

## Before opening a change

1. Open or find an issue describing the bug, feature or research goal.
2. Keep changes focused and explain controller/machine assumptions.
3. Add or update a smoke/regression test whenever practical.
4. Run the Release build and smoke tests on Windows/.NET 8.

```powershell
dotnet build .\TRMachinist.sln -c Release
dotnet run --project .\src\TRMachinist.SmokeTests\TRMachinist.SmokeTests.csproj -c Release --no-build
```

## Test data and licensing

Only contribute NC, CAD, machine definitions, meshes, screenshots or other artifacts that you have the right to redistribute. Do not submit customer programs, proprietary machine-builder files, commercial postprocessors, Siemens NX binaries or confidential shop data. Synthetic reproductions are preferred.

## Controller behavior

Clearly separate controller-standard behavior from machine-builder/OEM behavior. Do not generalize an OEM M-code, PLC sequence, safety interlock or kinematic parameter from one machine to another.

## Pull requests

Describe:

- what changed,
- why it changed,
- how it was tested,
- any machine/controller assumptions,
- whether numeric output or stock topology changes are expected.

By contributing, you agree that your contribution is licensed under the repository's Apache-2.0 license.
