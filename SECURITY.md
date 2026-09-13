# Security and machine-safety policy

TRMachinist is engineering software under active development and is **not safety-certified**.

## Reporting a security issue

Please do not publish exploitable security details in a public issue. Contact the repository maintainer privately through the GitHub account associated with this repository and provide enough information to reproduce the issue.

## CNC safety boundary

TRMachinist must not be treated as the sole authority for machine-safe motion. Always validate programs with the machine builder's documentation, approved postprocessor, controller/machine simulation when available, dry-run/single-block procedures, safe clearances, correct work offsets, and correct tool data.

Contributions must not implement instructions for bypassing guards, door interlocks, E-stops, safety PLCs, travel limits, spindle safety, or other protective systems.
