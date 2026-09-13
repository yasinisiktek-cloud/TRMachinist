# R33 public source import

This snapshot imports the three projects in the supplied R33 solution: Core,
Simulator and SmokeTests. The product version remains
`0.1.0-alpha4-test9-gpu-r33`; this is not a claim that the software is production-ready.

## Publication boundary

Included: C# sources, project files, WPF XAML/themes, the solution and version.
Excluded: production NC programs, customer jobs, proprietary machine/CAD assets,
NXOpen integration probes, vendor binaries, local-path/provenance reports,
research folders, build outputs and the separate experimental GPU display probe.
The simulator does not acquire NX or ModuleWorks binaries from this repository.

Line endings are normalized to LF and UTF-8 BOMs are removed. Changes to the
product code are limited to explanatory comments and a documentation tool-name
example; the material-removal algorithms are not replaced or simplified.

In the smoke tests, machine labels and tool names are made generic. A high-tilt
CYCLE800 regression previously tied to a production program is replaced with
synthetic angles (30/20 degrees and -110/2 degrees) and a synthetic point
(12, 8, 4 mm). Reference constants were independently computed by numerical
matrix normal-alignment in Python, rather than copied from the C# solver. The
quadrant-crossing regression and its numerical tolerances remain present.

## Validation scope

The source author's supplied record reports 70 successful smoke tests for the
original R33 package. That historical record is not a test of this edited import.
The GitHub Windows build and smoke-test workflow is the validation gate for this
public snapshot. Read the run's result; workflow presence alone is not a pass.
No GPU rendering, real-machine safety, production-program compatibility, or
performance benchmark certification is claimed by a console smoke-test pass.

The default smoke-test run requires no private machine/job/NC files. Optional
command-line audits still accept user-owned external datasets, but those data
are intentionally not published here.

The files listed in `R33_SOURCE_MANIFEST.json` are checked using SHA-256 of their
canonical UTF-8/LF bytes. This manifest contains no source-machine paths.
