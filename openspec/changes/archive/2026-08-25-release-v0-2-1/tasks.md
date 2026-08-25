## 1. Version and public documentation

- [x] 1.1 Set the add-in version to 0.2.1 and update all current download links and asset names.
- [x] 1.2 Add v0.2.1 release notes covering the fit-undo fix, the input queue, the English thinking levels, and the upgrade path.
- [x] 1.3 Remove the interim installation note that directed users to install from source for these fixes.
- [x] 1.4 Keep the v0.2.0 release notes reachable as a historical entry.

## 2. Verification

- [x] 2.1 Run a clean Release build of the solution.
- [x] 2.2 Run the real-Excel tool tests and all Web panel suites.
- [x] 2.3 Run the pane harness in automatic mode.
- [x] 2.4 Run the fit-undo and input-queue end-to-end scripts against real Excel.

## 3. Packaging

- [x] 3.1 Generate the v0.2.1 ZIP and external SHA-256 sidecar.
- [x] 3.2 Verify extracted layout, internal checksum manifest, assembly version, and archive hash.
- [x] 3.3 Install the packaged archive locally and confirm the deployed assembly and web assets match the source.

## 4. GitHub publication

- [x] 4.1 Commit and push the verified source to `origin/main`, then create and push annotated tag `v0.2.1` on the same commit.
- [x] 4.2 Create the public GitHub Release, upload both assets, and verify remote target, names, sizes, and digests against the local artifacts.
