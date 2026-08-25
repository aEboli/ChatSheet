## Why

The public release still points to `v0.1.0`, while `main` now contains several compatible but user-visible capabilities: provider retries, connection-bound model selection, reusable model catalogs, clearer range labels, text attachments, a direct fit action, and task-pane focus handling. Publishing the source without a matching versioned package and accurate feature notes would leave Windows users downloading stale binaries and reading incomplete limits.

## What Changes

- Advance the add-in and public release line to `v0.2.0`, because the accumulated changes add multiple user-visible capabilities rather than only correcting a patch-level defect.
- Update the README, Windows archive installation guide, and release notes so version links, asset names, feature descriptions, privacy boundaries, and operational limits match the shipped code.
- Correct the direct fit path so a range omitted by the panel is resolved before the undo snapshot, and suppress an undo button when no usable record was created.
- Build, test, package, hash, publish, and remotely verify the Windows release assets and source tag.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `windows-release-distribution`: Require consistent release versions and an accurate user-visible feature summary across project metadata, documentation, tags, and assets.

## Impact

- Affected code: fit execution/undo tracking and its real-Excel regression coverage.
- Affected public documentation: `README.md`, `docs/windows-release-install.md`, `docs/releases/v0.2.0.md`, and two implementation change records.
- Affected version metadata: `src/ChatSheet.AddIn/ChatSheet.AddIn.csproj`.
- Affected external systems: `origin/main`, the annotated `v0.2.0` tag, and a public GitHub Release containing the Windows ZIP and checksum sidecar.
