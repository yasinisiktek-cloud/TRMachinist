# Security Policy

TRMachinist processes machine definitions, job packages, meshes and NC text. Treat untrusted files as potentially hostile input.

## Reporting a vulnerability

Please do not publish a working exploit in a public issue. Contact the maintainer through the GitHub profile associated with this repository and provide a minimal reproduction that does not contain confidential production data.

## Safety boundary

A successful simulation is **not** proof that a real CNC program is safe. The software does not replace OEM safety systems, machine limits, PLC interlocks, collision options, workholding validation or shop procedures. Never bypass machine safety systems based on simulator output.
