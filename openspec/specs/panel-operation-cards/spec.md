# panel-operation-cards Specification

## Purpose
Present operations the user triggers directly from the panel using the same structure as
operations the model triggers, while keeping the two distinguishable. Both kinds answer
the same questions — which range changed, how much it affected, whether it can be undone —
so presenting them in two different shapes forces the user to look for the same
information in two places. The difference that does matter is who initiated the work, and
that is what the presentation marks.

Beyond a single operation, the record has to stay legible as a conversation accumulates
turns. Operations are therefore organised by the turn they belong to: the turn in progress
shows its operations as they happen, while earlier turns keep theirs behind one summary
line that states what that turn did. This answers the question a user actually asks when
checking results — "that instruction, which places did it change?" — without making them
count cards between message bubbles.

## Requirements

### Requirement: Panel-initiated operations appear as operation cards

An operation the user triggers directly from the panel SHALL be presented in the
conversation transcript using the same operation-card structure as a model-initiated tool
call: a collapsible summary row carrying the operation's name and outcome, an undo control
when the operation can be undone, and a collapsed body holding the parameters and result.

The card SHALL appear when the operation starts, showing an in-progress state, and SHALL
be filled in place when the operation finishes. Panel-initiated operations SHALL NOT be
added to the conversation sent to the model; their presentation in the transcript is for
the user only.

#### Scenario: User triggers a panel operation

- **WHEN** the user triggers an operation from a panel control
- **THEN** an operation card appears in the transcript showing an in-progress state
- **AND THEN** the same card is updated in place when the operation finishes
- **AND THEN** no additional card is created for the result

#### Scenario: Long-running panel operation

- **WHEN** a panel operation takes a long time to complete
- **THEN** its card remains visible in the in-progress state until the result arrives

### Requirement: Origin is distinguishable by more than colour

An operation card for a panel-initiated operation SHALL be visually distinguished from a
model-initiated one. The distinction SHALL NOT rely on colour alone: the card SHALL carry
a textual origin marker in its summary row, readable whether the card is collapsed or
expanded, and that marker SHALL have a hover description stating what the origin means.

Origin marking SHALL NOT displace state marking. When a card is in an error or undone
state, the presentation for that state SHALL take precedence over the origin colour,
while the textual origin marker remains.

#### Scenario: Panel and model operations side by side

- **WHEN** the transcript holds both a panel-initiated and a model-initiated operation card
- **THEN** both use the same card structure
- **AND THEN** the panel-initiated one carries a textual origin marker the other does not

#### Scenario: Panel operation is undone

- **WHEN** a panel-initiated operation is undone
- **THEN** the card takes on the undone presentation used for model-initiated operations
- **AND THEN** its origin marker is still present

### Requirement: Undo on a panel operation card uses the host's record identifier

A panel-initiated operation's undo record identifier is only known once the host has
executed the operation, whereas its card is created beforehand. The undo control SHALL act
on the identifier the host reported, not on any identifier used to place the card, so that
undoing cannot fail with a missing-record error.

Where the host reports that no undo record was registered, the card SHALL NOT offer an
undo control, and SHALL state why it is absent. A missing control with no explanation
reads as a malfunction, when it is in fact a deliberate refusal to promise an undo that
could not be honoured.

#### Scenario: Undo a completed panel operation

- **WHEN** the user activates the undo control on a panel-initiated operation card
- **THEN** the request identifies the operation by the record identifier the host reported
- **AND THEN** the operation is undone rather than failing to be found

#### Scenario: Host registered no undo record

- **WHEN** a panel operation succeeds but the host reports no undo record
- **THEN** the card offers no undo control
- **AND THEN** the card states why undo is unavailable

### Requirement: Failures land on the same card

When a panel-initiated operation fails, whether the host rejected it or the call itself
could not complete, the failure SHALL be reported on the card that was already on screen,
using the same error presentation as a failed model-initiated operation. No card SHALL be
left in the in-progress state after its operation has settled.

#### Scenario: Host rejects a panel operation

- **WHEN** a panel operation is rejected by the host
- **THEN** the existing card shows the failure and is marked as an error
- **AND THEN** the card is expanded so the reason is visible without further action
- **AND THEN** no card remains in the in-progress state

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

### Requirement: A normally finished turn is marked as such

When a turn ends normally, the transcript SHALL carry a completion marker for that turn,
presented the same way as the messages reporting abnormal endings — a centred system
message in the transcript, not a message bubble — and visually distinct from them.

The marker SHALL be added only for a turn that ended normally. A turn that was stopped,
that hit a step limit, that was abandoned after repeated truncation, or that failed SHALL
NOT receive one, so that a completion marker and an abnormal-ending message can never
both describe the same turn. Two contradictory endings are worse than none: the user
cannot tell which to believe.

The marker's purpose is served largely by its absence, so its hover description SHALL
state that absence means the turn was interrupted.

#### Scenario: Turn completes normally

- **WHEN** a turn ends normally
- **THEN** a completion marker is added for that turn
- **AND THEN** it is presented as a centred system message, like the abnormal-ending messages

#### Scenario: Turn is stopped

- **WHEN** a turn is stopped before it finishes
- **THEN** no completion marker is added for that turn
- **AND THEN** the message reporting the stop is still present

#### Scenario: Turn fails or hits a limit

- **WHEN** a turn ends by failure, by reaching a step limit, or by being abandoned after
  repeated truncation
- **THEN** no completion marker is added for that turn
- **AND THEN** the message describing that ending is still present

### Requirement: The completion marker stays the turn's closing line

Where a turn has a completion marker, that marker SHALL remain the last item belonging to
that turn, including after the turn's operations are collected into a group. A turn whose
content continues past its own completion marker cannot be scanned for whether it
finished.

Where the ordering used to restore a group is derived from a recorded position, that
record SHALL be kept consistent with the marker's final placement, so that restoring a
group does not reverse the two.

#### Scenario: Operations collected after a turn completed normally

- **WHEN** a turn that completed normally has its operations collected into a group
- **THEN** the group is placed before that turn's completion marker
- **AND THEN** the marker is still the last item belonging to that turn

#### Scenario: Group restored after collection

- **WHEN** such a group is restored to the transcript order
- **THEN** the completion marker still follows the operations of the turn it belongs to
