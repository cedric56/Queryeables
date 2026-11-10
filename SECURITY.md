# Security

## Reporting a vulnerability

Please do not open a public issue. Report it privately through
[GitHub's private vulnerability reporting](https://github.com/cedric56/Queryeables/security/advisories/new): describe the
issue, the affected package and version, and how to reproduce it. You will get an answer within a week; a fix is
released before the details are made public.

## Supported versions

Queryeables is in early development: only the latest version receives fixes.

## What the server assumes

The [security guide](docs/guides/security.md) lists what Queryeables enforces (access policies, row filters, limits,
server-owned values, hidden columns, notifications) and what the application must do itself (authentication,
`[Authorize]` on the hubs, TLS, the key of an encrypted offline copy).
