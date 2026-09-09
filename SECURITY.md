# Security Policy

## Reporting a vulnerability

Report suspected vulnerabilities privately before any public disclosure:

1. Email **keelmatrix@gmail.com**.
2. Open a private GitHub security advisory in this repository.

Do not create a public issue or otherwise publicly disclose sensitive vulnerability details, exploit steps, credentials, customer data, migration source, generated SQL, or private reports.

Please include, when safe:

- affected EfGuard version, package/tool, runtime, operating system, and EF/provider combination;
- safe reproduction steps or a minimized proof of concept;
- security impact and affected trust boundary;
- suggested mitigation or fix, if known.

Reports are investigated best-effort. Security fixes are prioritized for the latest released package line; older versions may receive fixes case-by-case.

## Product security notes

EfGuard does not connect to or modify a database. EF design-time code is executed in a bounded child process from the selected project, so use a least-privilege environment and a design-time factory without production side effects. Extraction failures fail closed. Generated reports are local files and may contain schema identifiers; protect them as you would migration source.
