# Public data policy

This repository is intended for source code and synthetic test data only.

Do not commit:

- customer drawings, NC programs or job packages
- proprietary/licensed postprocessor source
- machine-builder confidential PLC/M-code documentation
- Siemens NX installation binaries or proprietary NX content
- commercial machine models unless redistribution rights are explicit
- local absolute paths, credentials, tokens or personally identifying production metadata

When a regression requires production-derived input, minimize and anonymize it into a synthetic fixture that reproduces only the technical behavior under test.
