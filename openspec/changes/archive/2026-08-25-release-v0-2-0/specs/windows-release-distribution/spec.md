## ADDED Requirements

### Requirement: Consistent version and feature documentation

A public release SHALL use one version consistently across project metadata, the source tag, README download links, installation guidance, release notes, archive directory, archive filename, and checksum sidecar. Release notes SHALL summarize the user-visible changes since the previous public release and state the operational, privacy, support, signing, automated-verification, and manual-acceptance boundaries that materially affect those changes.

#### Scenario: Prepare a feature release

- **WHEN** a maintainer prepares a public release containing user-visible capabilities added since the previous version
- **THEN** the project version, tag, documentation links, release asset names, and packaged documents all identify the same version
- **AND THEN** the release notes describe those capabilities and their relevant limits without presenting local tests as clean-machine, configured-provider, or user-workbook acceptance

#### Scenario: Preserve historical release records

- **WHEN** a newer version becomes the current public release
- **THEN** current download guidance points to the newer version
- **AND THEN** historical release notes and previously published tags remain available without being rewritten as current facts
