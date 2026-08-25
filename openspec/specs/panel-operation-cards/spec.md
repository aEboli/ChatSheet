# panel-operation-cards Specification

## Purpose
Present operations the user triggers directly from the panel using the same structure as
operations the model triggers, while keeping the two distinguishable. Both kinds answer
the same questions — which range changed, how much it affected, whether it can be undone —
so presenting them in two different shapes forces the user to look for the same
information in two places. The difference that does matter is who initiated the work, and
that is what the presentation marks.

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
