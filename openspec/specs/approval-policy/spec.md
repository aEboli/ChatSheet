# approval-policy Specification

## Purpose
Make the three approval policies on the panel describe what the executor actually does.
Read tools still run without asking. Write and structure tools ask unless a live, visible
grant covers them. The grant is the smallest thing that stops a twelve-card format cleanup
from becoming twelve identical prompts, without also blessing a new worksheet.

## Requirements

### Requirement: The three policies are distinct in the executor

The add-in SHALL honour the stored approval policy as follows.

Under per-write approval, every mutating tool waits for a decision unless a grant created
earlier in the same turn already covers it.

Under per-turn approval, the first mutating tool of the turn waits for a decision. Once
that decision is an allow that records a grant, later mutating tools in the same turn that
the grant covers SHALL run without another card. Structure tools are not covered by a
format, write or destructive grant.

Under automatic approval, mutating tools SHALL run without asking. Automatic SHALL remain
the only policy that asks nothing at the start of a turn.

A policy whose label is per-turn SHALL NOT take the per-write path in the executor. The
shield icon, the settings copy and the hover hint SHALL describe the executor's behaviour,
not a mode the executor does not implement.

#### Scenario: Per-turn asks once for the same class on the same sheet

- **WHEN** approval is per-turn and the model formats three ranges on the same sheet
- **THEN** the first format produces an approval card
- **AND THEN** allowing it runs the other two format calls without further cards

#### Scenario: Per-write still asks without a grant

- **WHEN** approval is per-write and the model formats two ranges on the same sheet
- **THEN** each call produces an approval card unless the user has already granted that
  class on that sheet for the rest of the turn

#### Scenario: Automatic asks nothing

- **WHEN** approval is automatic
- **THEN** mutating tools run without an approval card

### Requirement: A grant is a sheet plus a risk class, not a boolean

A grant SHALL name the resolved sheet and one of four classes:

- format: `format_range`, `set_number_format`, `autofit_range`, `fit_range`
- write: `write_values`, `write_formulas`
- destructive: `clear_range`, `merge_cells`, `unmerge_cells`, `sort_range`
- structure: `add_worksheet`, `rename_worksheet`, `create_table`, `create_chart`

Each grant SHALL cover only its own class. In particular a write grant SHALL NOT cover a
destructive call, and no grant other than a structure grant SHALL cover a structure call.

Erasing and reordering are separated from writing deliberately. Writing overwrites values
that a snapshot can restore; clearing removes contents and formatting, merging silently
discards every value outside the anchor cell, and sorting moves whole rows. Approving
"write these three values" must not authorise "clear this sheet" for the rest of the turn:
what the user inspected and what the grant would then wave through are not the same order
of consequence.

#### Scenario: A write grant does not cover clearing

- **WHEN** the user allows a value write for the rest of that class on a sheet
- **AND WHEN** the model then clears a range on the same sheet
- **THEN** clearing still produces an approval card

#### Scenario: A write grant does not cover merging

- **WHEN** a write grant is live on a sheet
- **AND WHEN** the model then merges cells on that sheet
- **THEN** merging still produces an approval card

The control that used to mean "allow the rest of this turn" SHALL instead mean "allow the
rest of this class on this sheet". A separate control, labelled as including structure,
SHALL be the only way to grant structure for the rest of the turn.

The live grant SHALL be visible next to the approval shield for the rest of the turn, as
text naming the sheet and the class. The shield SHALL NOT be the only indication that a
grant is in force.

That indication SHALL also be the way to withdraw it. Activating it SHALL discard every
grant held for the turn, so that subsequent mutating calls ask again. Withdrawing SHALL
NOT stop the turn and SHALL NOT affect the call already executing: a user who changes
their mind wants to stop automatic approval, not to abandon the task. A grant that can be
seen but not withdrawn leaves stopping the whole turn as the only recourse.

#### Scenario: Withdrawing a live grant

- **WHEN** a grant is in force and the user activates the grant indication
- **THEN** the grant is discarded
- **AND THEN** the next mutating call in the same turn produces an approval card
- **AND THEN** the turn continues running

A grant SHALL live only for the current turn in the current runner. It SHALL NOT be
written to settings or to disk. Starting a new turn SHALL clear it. Switching policy
mid-turn SHALL take effect on the next mutating call.

#### Scenario: Allowing format does not allow a new sheet

- **WHEN** the user allows a format call for the rest of that class on that sheet
- **THEN** later format calls on that sheet run without asking
- **AND THEN** creating a worksheet still produces an approval card

#### Scenario: Write and format are separate grants

- **WHEN** the user has granted format on Sheet1
- **THEN** a `write_values` on Sheet1 still produces an approval card

#### Scenario: The grant is visible

- **WHEN** a grant is in force
- **THEN** the composer shows the sheet and class of that grant next to the shield

#### Scenario: A new turn starts with no grant

- **WHEN** a later turn begins
- **THEN** no grant from the previous turn remains
- **AND THEN** per-turn approval asks again on the first mutating call

### Requirement: Structure cannot ride on "allow the rest"

The add-in SHALL NOT treat a single boolean as permission for every remaining
`RequiresApproval` tool of the turn. In particular, after the user allows a write or a
format, a later `add_worksheet`, `rename_worksheet`, `create_table` or `create_chart` in
the same turn SHALL still require approval unless the user has explicitly granted
structure.

#### Scenario: Allow rest of class, then a chart

- **WHEN** the user allows the rest of the write class on the current sheet
- **AND WHEN** the model then creates a chart
- **THEN** creating the chart produces an approval card
