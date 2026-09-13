# Contributing to TRMachinist

Thanks for helping improve TRMachinist.

## Before opening a pull request

1. Open or reference an issue for non-trivial changes.
2. Keep production/customer data out of the repository.
3. Do not add proprietary postprocessors, OEM manuals, licensed machine models, or confidential NC programs.
4. Add deterministic tests for parser, geometry, stock, kinematics, or collision changes where practical.
5. Run the Release build and smoke tests.

```powershell
dotnet restore TRMachinist.sln
dotnet build TRMachinist.sln -c Release
dotnet run --project src/TRMachinist.SmokeTests/TRMachinist.SmokeTests.csproj -c Release
```

## Engineering principles

- Prefer deterministic behavior over hidden heuristics.
- Separate controller-standard behavior from machine-builder-specific behavior.
- Never invent OEM M-codes or PLC semantics.
- Keep coordinate transforms and kinematics explicit and testable.
- Preserve numerical tolerances in regression tests.
- Avoid weakening collision or safety checks merely to make a sample pass.

## Licensing contributions

Unless stated otherwise, contributions submitted to this repository are provided under the Apache License 2.0, consistent with the repository license.
