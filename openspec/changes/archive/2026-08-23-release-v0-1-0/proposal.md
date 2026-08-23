## Why

ChatSheet is publicly available from source, but a fresh clone requires a .NET SDK before it can be installed. The first public version needs a repeatable, integrity-checkable Windows distribution that is honest about its prerequisites and installation permissions.

## What Changes

- Add a reproducible PowerShell packaging command for a prebuilt Windows ZIP named from the add-in version.
- Allow the existing installer to detect the ZIP package layout and install its prebuilt payload without a .NET SDK, while preserving the source-build workflow.
- Add Chinese installation and v0.1.0 release documentation, including prerequisites, UAC/COM registration, checksum verification, and current limitations.
- Publish the verified artifacts as a public GitHub `v0.1.0` release whose tag identifies the exact source commit.

## Capabilities

### New Capabilities

- `windows-release-distribution`: Build, package, install, verify, document, and publish a versioned Windows ZIP distribution for ChatSheet.

### Modified Capabilities

- None.

## Impact

- Affected scripts: `scripts/install.ps1`, `scripts/ChatSheet.Registration.psm1`, and a new release-packaging script.
- Affected public documentation: `README.md` plus new release installation and release-notes documents.
- Affected repository hygiene: generated release artifacts remain ignored by Git.
- Affected external systems: a signed Git tag and a public GitHub Release with the ZIP and its SHA-256 sidecar.
