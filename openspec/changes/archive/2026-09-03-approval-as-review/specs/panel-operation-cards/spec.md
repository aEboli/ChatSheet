# panel-operation-cards Specification

## ADDED Requirements

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
Fill pattern and fill colour SHALL be treated as a pair: where the pattern was not
captured as a uniform value, the colour SHALL NOT be written either.

This is stricter than skipping empty values. For a range whose cells differ in fill, the
host reports no uniform pattern but still reports a colour of zero, which is its way of
saying the colour is not uniform rather than a statement that the fill is black. Writing
that zero back paints the whole range black and turns a cell that had no fill into a
filled one. An undo that leaves the workbook in a state the user never had is worse than
an undo that restores less, because restoring is the entire reason the control was
activated.

#### Scenario: Undo after formatting a range with mixed fill

- **WHEN** a range where some cells have a fill and others have none is formatted, and the
  user then undoes that operation
- **THEN** no cell in the range is given a black fill
- **AND THEN** a cell that had no fill still has no fill

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

A card whose undo record has been dropped because the store exceeded its cap SHALL lose
its undo control and SHALL state that the record is no longer kept.

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
