# Security Policy

## Supported versions

| Version      | Supported |
|--------------|-----------|
| 1.0.0-rc.x   | Yes — active release candidate |
| < 1.0.0-rc.1 | No |

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Report privately by emailing **info@fiavaion.com** with the subject
`[MultiMon Security] <brief description>`. Include:

- a description of the vulnerability and its potential impact;
- steps to reproduce (a minimal proof of concept if possible);
- the MultiMon version and Windows version you tested on.

You'll receive an acknowledgement within 5 business days. We aim to release a fix or mitigation within
30 days of a confirmed report, and will credit reporters in the changelog unless you prefer to remain
anonymous.

## Known accepted limitations (not treated as vulnerabilities)

These are documented design trade-offs for the 1.0.0 local-desktop, single-performer threat model:

- **FFmpeg PATH resolution** — the *Convert to HAP* dialog resolves `ffmpeg.exe` from the system `PATH`
  without filtering world-writable directories. Mitigation: install FFmpeg only from a trusted source
  and keep your `PATH` clean.
- **Project file media paths** — `.mmproj` files are loaded and their media paths (including UNC paths)
  used without additional validation. Mitigation: only open project files you created.

MultiMon does not require a network connection; all decode, render, and audio processing is local.
