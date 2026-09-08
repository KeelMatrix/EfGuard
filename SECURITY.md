# Security

Please report suspected vulnerabilities privately to the maintainers before public disclosure. Do not include credentials, connection strings, customer data, migration source, or generated SQL in an issue.

EfGuard does not connect to or modify a database. EF design-time code is executed in a bounded child process from the selected project, so use a least-privilege environment and a design-time factory without production side effects. Extraction failures fail closed. Generated reports are local files and may contain schema identifiers; protect them as you would migration source.
