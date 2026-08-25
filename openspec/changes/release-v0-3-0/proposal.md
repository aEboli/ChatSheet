## Why

Four user-visible changes have landed on `main` since the published `v0.2.1` archive: cell merge and unmerge tools, a double-clickable installer, the panel fit action rendered as an operation card with an explicit origin marker, and a queue strip that no longer grows without bound. Two of them are new capabilities rather than fixes, so the published line is now behind the source in kind, not only in degree. Excel loads the assembly from the install directory, so none of this reaches users until a new archive is published; the installer in particular cannot help the users it was written for while it exists only in the repository.

## What Changes

- Advance the add-in and public release line to `v0.3.0`. A minor bump rather than a patch: `merge_cells`/`unmerge_cells` widen the tool catalog and the installer adds a new entry point, so the version number itself should signal added capability instead of correction.
- Publish a new archive and checksum sidecar rather than replacing the `v0.2.1` assets, preserving the auditable link between each published archive, its checksum, and its source revision.
- Update the README, Windows archive installation guide, and release notes so version links, asset names, the merge tools, the operation-card presentation of fit, and the queue-strip behavior match the shipped code.
- Build, test, package, hash, publish, and remotely verify the Windows release assets and source tag.

## Capabilities

### New Capabilities

- None in this change. `cell-merge-tools` and `panel-operation-cards` are specified separately; this change only publishes them.

### Modified Capabilities

- `windows-release-distribution`: No requirement text changes. This change exercises the existing requirements, including the launcher requirements added after `v0.2.1`, for a new version.

## Impact

- Affected version metadata: `src/ChatSheet.AddIn/ChatSheet.AddIn.csproj`.
- Affected public documentation: `README.md`, `docs/windows-release-install.md`, `docs/releases/v0.3.0.md`.
- Affected external systems: `origin/main`, the annotated `v0.3.0` tag, and a public GitHub Release containing the Windows ZIP and checksum sidecar.
- Not affected: protocol mapping, permission boundaries, approval defaults, and the `v0.2.1` release assets, which remain published and valid for their own source revision.
