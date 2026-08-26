# panel-operation-cards Specification

## ADDED Requirements

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
