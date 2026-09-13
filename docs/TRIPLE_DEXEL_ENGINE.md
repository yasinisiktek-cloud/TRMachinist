# Triple-Dexel material-removal engine

TRMachinist uses a Triple-Dexel representation for in-process stock simulation. Three orthogonal dexel fields provide directional coverage while keeping stock updates more compact than a uniformly dense volumetric mesh.

Public source areas include:

- `TripleDexelStock.cs` — stock state, cutting updates, interval queries and core storage
- `TripleDexelInitialization.cs` — initial stock construction
- `TripleDexelFrustum.cs` — cutting-frustum representation
- `TripleDexelFrustumSweep.cs` — swept-volume/frustum processing along tool motion
- `TripleDexelProfileBounds.cs` and `TripleDexelProfileRows.cs` — cutting profile acceleration structures
- `TripleDexelSurfaceMesher.cs` — reconstruction of a renderable stock surface
- `TripleDexelTargetComparison.cs` — stock/target comparison utilities
- `TripleDexelCycleProgress.cs` — progress tracking for stock updates

## Design goals

1. Deterministic results for the same stock, tool geometry and motion path.
2. Incremental IPW updates suitable for playback rather than end-of-program-only visualization.
3. Geometry routines that remain testable independently from the WPF renderer.
4. Clear separation between cutting geometry and machine/controller semantics.
5. Regression-friendly metrics for performance and geometry quality.

The engine is under active optimization; API and storage formats may change during alpha development.
