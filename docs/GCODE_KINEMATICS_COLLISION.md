# G-code, kinematics and collision

## NC parsing

`GCodeParser` and controller-specific helpers track modal state and translate supported NC structures into simulation-ready operations. Fanuc and SINUMERIK logic is intentionally conservative: machine-builder M-codes and PLC behavior are not assumed unless explicitly modeled.

## Coordinate resolution

`MachineCoordinateResolver`, `MachineToolCoordinates`, and `CoordinateTransforms` resolve programmed coordinates, offsets, and machine/tool relationships used by the simulator.

## Tooling

`ToolCuttingProfile`, `ParametricToolMeshBuilder`, and `ToolHolderLibrary` describe cutting and non-cutting geometry so stock removal and collision checks can use different regions of the assembly.

## Collision

`MeshCollisionGeometry`, `ToolCollisionGeometry`, `MachineCollisionPolicy`, and `ToolStockCollisionPolicy` provide reusable geometry and policy layers for collision evaluation.

Collision detection in this repository is an engineering aid, not a safety certification. Real machines must still be proven out using approved procedures.
