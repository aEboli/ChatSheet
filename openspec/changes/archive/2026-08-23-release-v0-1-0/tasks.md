## 1. Release packaging implementation

- [x] 1.1 Correct the installation-script documentation and support both source and prebuilt archive layouts.
- [x] 1.2 Add a version-aware PowerShell packaging command that stages the payload and writes ZIP and checksum artifacts.
- [x] 1.3 Ignore generated release artifacts without ignoring source-controlled release documentation.

## 2. Public release guidance

- [x] 2.1 Add Chinese Windows release installation guidance with prerequisites, UAC, checksum, support, and signing boundaries.
- [x] 2.2 Add v0.1.0 release notes and update the README with the real Release download path and archive workflow.

## 3. Verification

- [x] 3.1 Build the Release configuration and run the existing focused tool and web test suites.
- [x] 3.2 Create the ZIP, unpack it, validate its layout and hashes, and run its read-only diagnostic entry point.
- [x] 3.3 Run repository hygiene checks and strict OpenSpec validation.

## 4. Publication

- [x] 4.1 Commit and push the verified release-preparation source changes.
- [x] 4.2 After action-time confirmation, create and push the annotated `v0.1.0` tag, create the public GitHub Release, upload the ZIP and SHA-256 sidecar, and verify the remote assets.
