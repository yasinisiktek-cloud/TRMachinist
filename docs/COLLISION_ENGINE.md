# Collision infrastructure

TRMachinist includes mesh collision geometry, oriented bounds and collision-policy layers used to reason about tool, stock and machine geometry.

Important source files include:

- `MeshCollisionGeometry.cs`
- `OrientedBounds3.cs`
- `ToolCollisionGeometry.cs`
- `ToolStockCollisionPolicy.cs`
- `MachineCollisionPolicy.cs`
- `CollisionAlertPolicy.cs`

The current public alpha should be treated as collision-analysis infrastructure, not a certified machine collision system. Broadphase and available narrow checks are only as trustworthy as the machine geometry, tool/holder data, transforms and controller state supplied to them.
