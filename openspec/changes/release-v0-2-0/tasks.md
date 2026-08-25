## 1. Version and public documentation

- [x] 1.1 Set the add-in version to 0.2.0 and update all current download links and asset names.
- [x] 1.2 Add complete v0.2.0 feature notes and correct stale attachment, fit-limit, privacy, and approval descriptions.

## 2. Release-blocking behavior

- [x] 2.1 Add a real Excel regression that reproduces missing undo tracking when fit omits `range`.
- [x] 2.2 Resolve the implicit UsedRange before snapshot capture and return an undo identifier only for a usable record.

## 3. Verification and packaging

- [x] 3.1 Run the Release build, real Excel tool tests, all Web tests, repository hygiene checks, and strict OpenSpec validation.
- [ ] 3.2 Generate the v0.2.0 ZIP and sidecar, then verify extracted layout, internal checksums, assembly version, and archive hash.

## 4. GitHub publication

- [ ] 4.1 Commit and push the verified source to `origin/main`, then create and push annotated tag `v0.2.0` on the same commit.
- [ ] 4.2 Create the public GitHub Release, upload both assets, and verify remote target, names, sizes, and digests against the local artifacts.
