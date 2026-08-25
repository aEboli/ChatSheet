## Context

The repository already has a version-aware packaging command and a long-term `windows-release-distribution` specification. The existing public `v0.1.0` release must remain immutable, while the current source and new documentation need a separate feature release. The local environment has Git and authenticated GitHub access but no `gh` executable.

## Goals / Non-Goals

**Goals:**

- Publish one source commit as `main`, annotated tag `v0.2.0`, and GitHub Release `v0.2.0`.
- Make the project version, asset names, README links, packaged installation guide, release notes, tag, and Release target agree.
- Verify the archive structure and hashes locally, then verify the remote assets and digests after upload.
- Preserve accurate boundaries around unsigned packaging, UAC, supported hosts, model-provider validation, and manual Excel acceptance.

**Non-Goals:**

- Replace the ZIP with an MSI/EXE, add code signing, add an updater, or introduce CI release automation.
- Modify or replace the historical `v0.1.0` tag, Release, assets, or archived release notes.
- Use a configured real model provider or saved API key as part of release verification.

## Decisions

### Use v0.2.0 rather than v0.1.1

The release includes multiple new capabilities since `v0.1.0`, not only compatible bug fixes. A minor version communicates that distinction while remaining within the project's pre-1.0 compatibility line.

### Resolve the implicit fit range before snapshot capture

The fit tool can derive UsedRange during execution, but undo snapshots are captured before execution. When an undo identifier is present and `range` is omitted, the executor resolves UsedRange once and writes the address back to the same argument object before capturing. The tool then consumes that resolved address. If preprocessing cannot resolve the range, normal tool execution retains the existing structured error behavior.

The panel channel checks the resulting undo store and returns an identifier only when `CanUndo` is true. This keeps a successful fit usable even when a very large range exceeds snapshot limits, while preventing a button that can only return `NOT_FOUND`.

### Publish from the verified release commit

All source and documentation changes are committed before packaging. The package is built from that commit, verified locally, and uploaded without altering its bytes. The annotated tag and Release both target the same commit. Publication may use the authenticated GitHub REST API because the absence of `gh` is a tooling limitation, not a reason to skip remote verification.

## Risks / Trade-offs

- [Snapshot preparation changes a previously implicit argument] -> Limit the mutation to `fit_range` with an undo identifier and cover the empty-range path in a real Excel test.
- [A fit succeeds without undo on an extremely large sheet] -> Do not fail the requested operation; omit the undo identifier so the UI does not promise unavailable recovery.
- [Documentation drifts from asset names] -> Derive the package name from the project version and search all current release documents before packaging.
- [Release upload succeeds only partially] -> Query the GitHub Release after upload and compare target commit, asset names, sizes, and SHA-256 digests with local artifacts.
- [Automated checks are mistaken for user acceptance] -> State that packaging does not prove clean-machine UAC installation, configured-provider behavior, or the user's workbook workflow.

## Migration Plan

1. Correct source behavior and public documentation, then pass strict OpenSpec validation.
2. Run the full local build/test gates and generate the versioned ZIP plus sidecar.
3. Validate the extracted package, commit and push `main`, then create and push the annotated tag.
4. Create the public GitHub Release, upload the exact verified assets, compare remote metadata and digests, then archive this OpenSpec change.
