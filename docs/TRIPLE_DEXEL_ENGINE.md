# Triple-dexel material-removal engine

TRMachinist represents stock with dexel interval families aligned to X, Y and Z. Using three directions preserves side-wall and undercut information that a single Z height field cannot represent.

## Main components

- `TripleDexelStock` — stock interval storage and material-removal entry point.
- `TripleDexelFrustum` / `TripleDexelFrustumSweep` — cutter/profile intersection queries for static and swept motion.
- `TripleDexelProfileRows` / `TripleDexelProfileBounds` — profile sampling and bounded query helpers.
- `TripleDexelSurfaceMesher` — reconstructs a visible surface from the dexel boundary field.
- `TripleDexelTargetComparison` / `ZDexelTarget` — target/remaining-stock comparison infrastructure.
- `TripleDexelCycleProgress` / `CycleStockProgress` — progress publication and stock-update state.

## Regional remeshing

The rendering pipeline groups stock into regions so a local cut does not force a full visible-stock remesh. Only affected regions and required boundary neighborhoods are rebuilt. This is intended to keep high-resolution stock practical during interactive playback.

## Determinism

Optimization work is accepted only when result checks remain stable. The R33 development work rejected faster variants that changed interval/volume or final-surface hashes. This repository keeps the accepted source path, not those experimental variants.

## Accuracy

A development configuration used 0.15 mm stock spacing, but the engine architecture must not treat that number as a machine-independent guarantee. Appropriate resolution depends on the part, feature size, cutter, available memory and required verification confidence.
