## Context

See [proposal.md](proposal.md) for the release motivation. The existing installer builds from `src/ChatSheet.AddIn/bin/Release`, copies the resulting payload to `%LOCALAPPDATA%\ChatSheet\app`, and then performs two privileged managed-COM registrations in HKLM plus a current-user Excel add-in registration. There is no installer project, code-signing setup, CI release job, or committed build output.

## Goals / Non-Goals

**Goals:**

- Keep source installation behavior intact while enabling the same installer to consume a prebuilt `app` directory in a release archive.
- Make the packaging procedure deterministic enough to recreate, inspect, and hash a release asset from the tagged source.
- Make publicly visible prerequisites and limitations match the actual installation code.

**Non-Goals:**

- Create an MSI, EXE bootstrapper, certificate, code-signing workflow, auto-updater, or CI automation.
- Claim WPS support, silent installation, clean-machine acceptance, or support for a platform not exercised by the existing project.
- Change the COM registration mechanism or the add-in's runtime behavior.

## Decisions

### Use a prebuilt ZIP instead of a native installer

The release asset will be a versioned ZIP with a PowerShell entry point. It reuses the established installation path and has no new installer toolchain or unsigned executable behavior to validate. An MSI/EXE was considered, but the project has no WiX/Inno/NSIS configuration and no signing process; generating one only for the release would add an untested distribution surface.

### Make the installer layout-aware

`install.ps1` will treat a sibling `app/ChatSheet.AddIn.dll` as an extracted release payload. In that layout it will skip the source build and source timestamp check, while all deployment, COM registration, UAC elevation, diagnostics, and uninstall behavior remain shared. In a source layout it will retain the original build-first path. This avoids maintaining separate registration scripts that could drift.

### Stage before compressing and generate two checksum surfaces

The packaging script will build Release, copy the entire output directory to `artifacts/release/ChatSheet-v<version>-win/app`, then copy the installer scripts and canonical Markdown documents. It will write a packaged `SHA256SUMS.txt` for the staged files and a sidecar checksum for the finished ZIP. `artifacts/` will stay ignored. A top-level package directory prevents extracted files from mixing with users' other downloads.

### Publish only verified assets at an action-time confirmation point

The source release-preparation commit will be built, tested, packaged, unpacked, and checked locally before the tag and public GitHub Release are created. The public ZIP and its checksum will be uploaded only after the user confirms the concrete public files and unsigned-package status; the public release will use an annotated `v0.1.0` tag on the verified commit.

## Risks / Trade-offs

- [Windows flags an unsigned package or the UAC prompt is unexpected] → State that the ZIP is not code-signed and explain why installation requires elevation before download/installation.
- [ZIP script drift from source installer] → Package the same source-controlled installer scripts and verify an extracted archive can run the read-only diagnostic entry point.
- [Stale or incomplete build output is packaged] → Build immediately before staging and assert the assembly and WebView2 panel entry file exist before compression.
- [Checksum only proves archive bytes, not successful Excel loading] → Publish both archive and payload checksums, and explicitly distinguish integrity verification from real Excel or clean-machine acceptance.
- [Release metadata and tag diverge] → Verify local and remote tag references and compare the downloaded release asset SHA-256 to the generated sidecar after publication.

## Migration Plan

1. Add the layout-aware installer, packaging command, documentation, and ignored artifact directory.
2. Build and run the existing focused test suites, then package and validate an extracted archive.
3. Commit the release preparation, create and push the annotated version tag, and publish the verified ZIP plus sidecar.
4. If release publication must be rolled back, delete or mark the Release as draft and remove the remote tag only with explicit follow-up authorization; local source and generated artifacts remain available for diagnosis.
