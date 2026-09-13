# G-code, controller state and kinematics

TRMachinist intentionally treats controller interpretation and machine kinematics as separate layers.

## Parsing

The source includes:

- generic G-code token/motion handling,
- Fanuc modal-state helpers,
- selected SINUMERIK modal-cycle interpretation,
- path timing and operation grouping.

Supported syntax is not equivalent to a complete controller emulator. OEM macros, PLC behavior and many advanced cycles are outside the current scope.

## Coordinate transforms

`CoordinateTransforms`, `MachineCoordinateResolver`, `MachineToolCoordinates`, `DeterministicStockMotion` and `SimulationPlayer` carry the main coordinate/motion responsibilities. Rotary axes require an explicit machine model; the presence of B/C or A/C axes alone does not prove simultaneous 5-axis/TCP capability.

## Safety and portability

Never copy a machine-specific M-code, parameter or kinematic pivot from a sample into a different real machine. The public NC samples exist to exercise parser/motion paths, not to drive production equipment.
