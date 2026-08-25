## Why

The published `v0.2.0` archive contains a defect that makes the panel's fit action advertise an undo button that always fails, and it predates the input-queue capability now on `main`. Users who install the published archive therefore see a broken undo affordance and a composer that locks while a turn runs. The fixes exist in source but Excel loads the assembly from the install directory, so they do not reach users until a new archive is published.

## What Changes

- Advance the add-in and public release line to `v0.2.1`. The maintainer chose a patch-level version even though the input queue is a new user-visible capability; the release notes therefore describe the queue explicitly so the version number alone does not have to carry that information.
- Publish a new archive and checksum sidecar rather than replacing the `v0.2.0` assets, preserving the auditable link between each published archive, its checksum, and its source revision.
- Update the README, Windows archive installation guide, and release notes so version links, asset names, the new queue behavior, and the thinking-level naming match the shipped code, and remove the interim note that told users to install from source to obtain these fixes.
- Build, test, package, hash, publish, and remotely verify the Windows release assets and source tag.

## Capabilities

### New Capabilities

- None in this change. The input-queue capability is specified separately in `chat-input-queue`; this change only publishes it.

### Modified Capabilities

- `windows-release-distribution`: No requirement text changes. This change exercises the existing requirements for a new version.

## Impact

- Affected version metadata: `src/ChatSheet.AddIn/ChatSheet.AddIn.csproj`.
- Affected public documentation: `README.md`, `docs/windows-release-install.md`, `docs/releases/v0.2.1.md`.
- Affected external systems: `origin/main`, the annotated `v0.2.1` tag, and a public GitHub Release containing the Windows ZIP and checksum sidecar.
- Not affected: tool catalog, protocol mapping, permission boundaries, and the `v0.2.0` release assets, which remain published and valid for their own source revision.
