## 1. Version and public documentation

- [x] 1.1 Set the add-in version to 0.3.0 and update all current download links and asset names.
- [x] 1.2 Add v0.3.0 release notes covering the merge tools, the double-click installer, the operation-card fit action, the queue-strip change, and the upgrade path.
- [x] 1.3 Keep the v0.2.1 and earlier release notes reachable as historical entries.

## 2. Verification

- [x] 2.1 Run a clean Release build of the solution.
- [x] 2.2 Run the real-Excel tool tests and all Web panel suites.
- [x] 2.3 Run the pane harness in automatic mode.

## 3. Packaging

- [ ] 3.1 Generate the v0.3.0 ZIP and external SHA-256 sidecar.
- [ ] 3.2 Verify extracted layout, internal checksum manifest, assembly version, and archive hash.

## 4. GitHub publication

- [ ] 4.1 Commit and push the verified source to `origin/main`, then create and push annotated tag `v0.3.0` on the same commit.
- [ ] 4.2 Create the public GitHub Release, upload both assets, and verify remote target, names, sizes, and digests against the local artifacts.
