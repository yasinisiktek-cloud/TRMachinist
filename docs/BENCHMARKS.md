# Benchmarks and validation notes

Performance results in this project are engineering measurements, not product guarantees.

## R33 internal full-stock benchmark

One internal development case changed from:

- baseline: **125.974 s**
- accepted R33 source: **75.662 s**
- reduction: **39.94%**

The accepted optimization retained the result checks used in that experiment. Faster experimental variants that changed dexel volume/interval or final-surface hashes were rejected rather than published as the product path.

The R33 validation work also used large independent geometric query sets and deterministic stock/surface ordering checks. The exact private production fixtures are intentionally not public; the long-term goal is to replace them with redistributable public fixtures that exercise the same classes of behavior.

## Reproducing performance

A benchmark should record at minimum:

- TRMachinist commit,
- CPU and memory,
- stock dimensions/resolution,
- tool geometry,
- number and type of toolpath segments,
- whether rendering is included,
- final stock/result hashes or equivalent correctness checks.

Do not compare elapsed times if the result resolution or accepted geometry changes.
