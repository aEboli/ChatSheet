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

### Requirement: An approval card shows what will change, not only where

When a write requires approval, the approval card SHALL present a preview of the change
the user is being asked to allow, in addition to the affected sheet, address and cell
count.

For `write_values` and `write_formulas`, the preview SHALL be a truncated cell-by-cell
comparison of current values against the values in the tool arguments. The current values
SHALL come from the impact probe already performed to size the range; the add-in SHALL NOT
read the range a second time for the preview. Formula writes SHALL compare formula text,
not calculated values.

The on-card table SHALL contain at most 8 rows and 6 columns. Cell text on the card SHALL
be truncated to 40 characters with a visible ellipsis. When the range is larger, the card
SHALL state how many cells were omitted, as a count of remaining cells, not of remaining
rows. Truncation SHALL be stated in text, not by colour alone.

An empty cell and a missing reading SHALL be distinguishable. An empty cell SHALL be
labelled as empty. When the probe cannot read current values, the card SHALL say so and
SHALL NOT present an empty table as if it were a complete preview.

For format, number-format and fit operations, the card SHALL keep showing the arguments
that will be applied. When the probe shows that range-level formatting is mixed, the card
SHALL say that current formatting is mixed, and SHALL NOT dump a format matrix into the
card.

When `fit_range` omits `range`, the add-in SHALL resolve the sheet's used range before
building the card, so the card names an address rather than leaving the impact row blank.

The preview SHALL be sent only on the approval request. It SHALL NOT be added to the
conversation sent to the model, and SHALL NOT be echoed in the tool result.

#### Scenario: Approving a small write of values

- **WHEN** the model asks to write a 3×2 block of values and the current cells are readable
- **THEN** the approval card shows those six cells as current value against new value
- **AND THEN** the card still states the sheet, the address as a position, and the cell count

#### Scenario: Approving a write larger than the preview

- **WHEN** the model asks to write more than 8 rows or more than 6 columns
- **THEN** the card shows at most 8×6 cells
- **AND THEN** it states how many cells were omitted

#### Scenario: Approving a formula write

- **WHEN** the model asks to write formulas
- **THEN** the preview compares formula text, not calculated values

#### Scenario: The probe cannot read the range

- **WHEN** the impact probe fails because the range exceeds the read limit or cannot be parsed
- **THEN** the approval card still appears
- **AND THEN** it states that current values could not be read
- **AND THEN** it does not present an empty comparison table

#### Scenario: Fit without an explicit range

- **WHEN** the model calls `fit_range` without `range`
- **THEN** the approval card names the used-range address that will be fitted
- **AND THEN** the impact row is not blank

#### Scenario: Preview stays off the model transcript

- **WHEN** the user allows or rejects the operation
- **THEN** the comparison is not present in the messages later sent to the model

### Requirement: A range label on a card is a way to land there in Excel

Where a card or a collected-turn summary already presents a worksheet address as a
position, that presentation SHALL be activatable and SHALL ask the host to activate the
sheet and go to that address. The control SHALL remain usable when collapsed summaries
are expanded. Colour SHALL NOT be the only indication that the label can be activated.

If the sheet or address cannot be resolved, the host SHALL report the existing range or
sheet error to the panel, and SHALL NOT select a different sheet or address. The hover
description SHALL state that activating the label replaces the current Excel selection.

#### Scenario: Jump from an approval card

- **WHEN** the user activates the range presentation on an approval card
- **THEN** Excel activates that sheet and goes to that address

#### Scenario: Jump from an operation card

- **WHEN** a completed operation card states a range and the user activates it
- **THEN** Excel goes to that range

#### Scenario: The address no longer resolves

- **WHEN** the user activates a range whose sheet has been renamed or whose address is invalid
- **THEN** the panel reports the failure
- **AND THEN** Excel's selection is not silently moved to a different range

### Requirement: An undo control is offered only when that undo can succeed

An operation card SHALL offer undo only when the host has a record that can actually
reverse the operation. A record that will fail, or that will restore nothing, SHALL NOT
produce an undo control. Where undo is withheld, the card SHALL state why, using the same
kind of note already used when a panel fit cannot keep a snapshot.

Creating a chart SHALL report the name Excel assigned to the shape. Without that name,
no structure undo record SHALL be registered for the chart. After a chart is undone by
deleting it, the card SHALL NOT offer redo: restoring a deleted chart is unsupported, and
a redo control that cannot succeed is the same defect as an undo control that cannot
succeed.

When a format snapshot cannot represent the range because every range-level property it
would restore is mixed, and no complete cell-wise appearance snapshot exists, no format
undo SHALL be registered. Where only some properties are mixed, undo MAY be registered and
the card SHALL state that formatting will be restored only in part. Clearing a range MAY
still register content undo; the card SHALL then state that values can be restored and
formatting cannot be restored completely.

#### Scenario: Only some appearance properties are mixed

- **WHEN** a format operation runs on a range where some appearance properties are uniform
  and others are mixed
- **THEN** the card offers undo
- **AND THEN** the card states that formatting will be restored only in part

### Requirement: Restoring formatting SHALL NOT invent a value the range never had

Restoring a snapshot SHALL skip any property whose captured value does not describe the
range, and SHALL NOT write a value the host returned as a placeholder for "not uniform".
Fill colour SHALL be written only when Interior.ColorIndex was captured as a uniform
value that is not "no fill". Interior.Color returns zero both for a genuinely black fill
and for a non-uniform one, so colour itself cannot be the gate. When ColorIndex is
xlNone, colour SHALL NOT be written: writing a colour turns a range that had no fill
into a solid fill.

#### Scenario: Undo after formatting a range with mixed fill

- **WHEN** a range where some cells have a fill and others have none is formatted, and the
  user then undoes that operation
- **THEN** no cell in the range is given a black fill
- **AND THEN** a cell that had no fill still has no fill

#### Scenario: Undo after formatting a uniformly unfilled range

- **WHEN** a range with no fill is formatted and the user then undoes that operation
- **THEN** the range still has no fill

#### Scenario: Creating a chart reports a name and can be undone

- **WHEN** a chart is created and Excel assigns the shape a name
- **THEN** the result includes that name
- **AND THEN** the operation card offers undo
- **AND THEN** undo deletes that chart

#### Scenario: A chart without a name has no undo control

- **WHEN** a chart is created but no shape name is available
- **THEN** the card offers no undo control
- **AND THEN** the card states why undo is unavailable

#### Scenario: Undoing a chart does not offer a doomed restore

- **WHEN** a created chart has been undone
- **THEN** the card does not offer restore
- **AND THEN** the card states that the chart cannot be recreated automatically

#### Scenario: Mixed formatting is not pretend-undoable

- **WHEN** `format_range` runs on a range whose formatting is mixed at range level
- **THEN** the card offers no format undo
- **AND THEN** the card states that a complete format snapshot could not be kept

### Requirement: Overlapping undo is confirmed, not silent

Independent undo of non-overlapping operations remains allowed. Before restoring a
record, the host SHALL look for a later, not-yet-undone record on the same sheet whose
address intersects the record being undone. On the first request, the host SHALL refuse
with a distinct warning that the later write would be overwritten, and the card SHALL
offer an explicit control to proceed. Only a second, explicit request SHALL restore.

Intersection SHALL be decided from sheet name plus the A1 row/column span of each
record's stored address. Multi-area addresses remain resolved as they are today; this
requirement does not expand union ranges into their areas.

A card whose undo record has been dropped because the store exceeded its cap SHALL, on
the first attempt to undo it, lose its undo control and SHALL state that the record is
no longer kept. The control is withdrawn when it is used after eviction, not by a
separate notification at eviction time.

#### Scenario: Undo of an earlier write that overlaps a later one

- **WHEN** the user undoes an earlier write whose range intersects a later, still-active write
- **THEN** the first request does not restore
- **AND THEN** the card explains the overlap and offers a control to proceed anyway

#### Scenario: Proceeding after the overlap warning

- **WHEN** the user confirms undo after that warning
- **THEN** the earlier snapshot is restored

#### Scenario: Non-overlapping writes undo independently

- **WHEN** two writes on the same sheet do not intersect
- **THEN** undoing the earlier one succeeds on the first request
