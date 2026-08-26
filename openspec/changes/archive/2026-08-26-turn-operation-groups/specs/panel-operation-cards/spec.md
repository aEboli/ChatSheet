# panel-operation-cards Specification

## ADDED Requirements

### Requirement: Operations are grouped by the turn they belong to

Operation cards produced while a turn runs SHALL be presented individually, in the order
they occur, so that in-progress state, failure detail and undo controls are visible as
they happen. Once a later turn begins, the operations belonging to the earlier turn SHALL
be collected into a single collapsible group placed after that turn's content, collapsed
by default.

Collection SHALL happen when the next turn begins, not when a turn ends: immediately
after a turn completes the user is most likely to be reading its results or undoing them.

A group SHALL stay with the turn it belongs to rather than being relocated to the end of
the transcript, so that reviewing a turn's operations is an expansion in place.

Operations the user triggers from the panel SHALL be collected into the same group as the
operations of the turn they occurred within, retaining their origin marker. Where no turn
has run before them, their group SHALL be labelled as panel-initiated rather than as a
turn.

#### Scenario: A second turn begins

- **WHEN** a turn has produced operation cards and a later turn begins
- **THEN** the earlier turn's operation cards are collected into one collapsed group
- **AND THEN** the group is positioned after that turn's content, not at the end of the transcript
- **AND THEN** the new turn's operations are again shown individually as they occur

#### Scenario: A turn finishes with no further turn

- **WHEN** a turn finishes and no later turn has begun
- **THEN** its operation cards remain shown individually

#### Scenario: Panel-initiated operation during a turn's span

- **WHEN** the user triggers a panel operation, and a later turn begins
- **THEN** that operation is collected into the same group as the operations around it
- **AND THEN** its origin marker is still readable once the group is expanded

### Requirement: A group's summary states what the turn did

A group's summary row SHALL state how many operations it holds and how many of those
changed the workbook versus only read from it, derived from the risk classification the
host reports for each operation rather than from a separate classification in the panel.

Where a group holds a failed operation, the summary SHALL say so and SHALL be marked as
an error, because a failed operation's own presentation is not visible while the group is
collapsed. Where a group holds an undone operation, the summary SHALL say so, and SHALL
be kept accurate when an operation inside it is undone or redone after collection.

#### Scenario: Group holding both reads and writes

- **WHEN** a group is formed from operations that both read and changed the workbook
- **THEN** its summary states the total count and the split between the two

#### Scenario: Group holding a failure

- **WHEN** a group is formed from operations of which one failed
- **THEN** its summary reports the failure and the group is marked as an error

#### Scenario: Operation undone after its group was formed

- **WHEN** the user undoes an operation inside a collected group
- **THEN** the group's summary reflects that an operation is undone

### Requirement: A group can be restored to the transcript order

A group SHALL offer a control that dissolves it, returning its operation cards to the
positions they held in the transcript before collection, interleaved with the messages
they occurred between. Restoring SHALL be available whether or not the group is expanded,
and SHALL NOT require the group to be expanded first.

Restoration SHALL reconstruct the original order from a record of each item's own place in
the transcript, not from its adjacency to another item, since a neighbouring item may
itself have been collected into a different group.

After a group is restored, its cards SHALL remain in place rather than being collected
again by a subsequent turn.

#### Scenario: Restore a collected group

- **WHEN** the user activates the restore control on a group
- **THEN** the group is dissolved
- **AND THEN** its cards appear at the positions they held before collection, between the
  messages they originally occurred between

#### Scenario: Restore a group formed before another group

- **WHEN** an earlier group is restored after a later group has been formed
- **THEN** the restored cards land in their original positions
- **AND THEN** the later group stays intact

#### Scenario: A later turn begins after a restore

- **WHEN** a group has been restored and a later turn begins
- **THEN** the restored cards are not collected again
