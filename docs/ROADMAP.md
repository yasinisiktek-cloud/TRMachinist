# Roadmap

TRMachinist is an early open-source project. Proposed directions:

## Near term

- [ ] Establish reproducible public machine/job fixtures with no proprietary assets.
- [ ] Expand CI regression coverage and publish deterministic benchmark fixtures.
- [ ] Document `.trmac` and `.trjob` schemas that are safe to implement independently.
- [ ] Split controller parsing tests into smaller focused test modules.
- [ ] Improve diagnostics for unsupported controller constructs.

## Simulation engine

- [ ] Broader cutter-profile coverage and regression fixtures.
- [ ] Adaptive/multilevel stock resolution research.
- [ ] More aggressive regional meshing without changing accepted stock topology.
- [ ] Optional compute/GPU research with CPU-reference parity checks.

## Kinematics and controllers

- [ ] Generalized machine-family kinematic definitions.
- [ ] Broader public Fanuc and SINUMERIK examples.
- [ ] Better separation of controller-standard and OEM-specific behavior.
- [ ] Research generalized simultaneous 5-axis inverse kinematics/TCP handling.

## Collision

- [ ] Strengthen narrow-phase mesh collision checks.
- [ ] Public fixture library for tool/holder/fixture/machine collision regressions.
