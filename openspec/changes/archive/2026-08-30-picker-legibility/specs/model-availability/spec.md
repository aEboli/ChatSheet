# model-availability Specification

## ADDED Requirements

### Requirement: A verdict is legible at a glance, not only on close inspection

An availability verdict SHALL be rendered on the most prominent element of the model's
row — the model's own name — and SHALL NOT rely solely on a small adjacent indicator.

A list of several dozen models is read by scanning it. A seven-pixel dot beside a name
rendered in the same colour whatever the verdict does not survive scanning: the user sees
one uniform column and has to inspect each row to find the verdict, which is the work the
verdict existed to save.

The unavailable verdict SHALL be the one carried by colour, and that colour SHALL be the
palette's error colour rather than its warning colour. The question the user is asking of
this list is which models do not work; that is the answer worth making unmissable.

The available verdict SHALL NOT be rendered in the same colour as the current selection.
Both are affirmative marks on a row, and one colour for both makes "this one works"
indistinguishable from "this is the one in use".

The same verdict SHALL be rendered in the same colour wherever it appears, including the
collapsed summary the picker shows when closed. One fact shown in two colours is two facts
to learn.

#### Scenario: Scanning a list for models that do not work

- **WHEN** the picker shows models with mixed verdicts
- **THEN** the models recorded unavailable are distinguishable from the others by the
  colour of their names

#### Scenario: The selected model is also recorded available

- **WHEN** the currently selected model is recorded available
- **THEN** the row remains identifiable as the selection

#### Scenario: The summary agrees with the list

- **WHEN** the selected model is recorded unavailable
- **THEN** the closed picker's summary marks it in the same colour the list uses

### Requirement: Secondary explanation is available on demand, the state itself is not

Where a row's state is conveyed by colour or shape, the prose explaining what that state
means SHALL be reachable on demand rather than occupying a line of its own on every row.

Restating each verdict in prose on every row doubles the height of every entry in a list
whose purpose is to hold several dozen of them, and it does not make the verdict easier to
find — it makes the list shorter.

This SHALL NOT extend to a state that has no colour or shape of its own. A confirmation in
flight SHALL keep its explanation on the row, because it is the only means of telling a
slow gateway from a click that did nothing, which is the reason that state exists.

Nor SHALL it extend to an annotation that changes what an option does. A thinking level the
current model does not support SHALL keep its annotation on the row: that is not an
explanation of the option but a statement that choosing it will not take effect, and the
user needs it before choosing, not after.

#### Scenario: A row whose verdict is shown by colour

- **WHEN** a model carries a recorded verdict
- **THEN** the row does not spend a line restating the verdict in prose
- **AND THEN** the explanation is reachable on demand

#### Scenario: A confirmation in flight

- **WHEN** a confirmation has been requested and no outcome has arrived
- **THEN** the row itself says so, without the user having to ask

#### Scenario: A thinking level the model does not support

- **WHEN** the current model does not support a thinking level
- **THEN** that level's row carries the annotation without the user having to ask

### Requirement: An occasional action may be revealed on demand, but never made unreachable

A per-row control for an action taken only occasionally MAY be hidden until the user
indicates interest in that row, so that a list of mostly-unconfirmed models does not become
a column of buttons.

Revealing SHALL be driven by pointer hover and by keyboard focus reaching the row. A control
revealed only by hover is unreachable by keyboard, which removes the action rather than
tidying it.

On input that reports no hover capability, the control SHALL be shown at all times. Hiding
an entry point behind an interaction the device cannot perform is indistinguishable from not
offering it.

Revealing the control SHALL NOT change the row's layout. A control that takes up space when
it appears reflows the row beneath the pointer, and in a list of long identifiers that
reflow moves every row after it.

#### Scenario: Keyboard reaches a hidden control

- **WHEN** the user moves focus into a row whose confirm control is hidden
- **THEN** the control is revealed and can be activated

#### Scenario: A device without hover

- **WHEN** the input device reports no hover capability
- **THEN** the confirm control is shown without requiring hover

#### Scenario: Revealing the control leaves the row where it was

- **WHEN** the confirm control is revealed on a row carrying a long model identifier
- **THEN** the row's other content does not move

### Requirement: The picker's popup fits the panel it opens into

The picker's popup SHALL remain wholly within the panel, in both axes, at every panel size
the host permits.

The popup opens upward, because the control sits at the foot of the panel, and its overflow
is clipped rather than scrolled. Anything outside the panel is therefore not merely awkward
but unreachable, with nothing on screen indicating that it exists.

A minimum width intended to fit long model identifiers SHALL yield when the panel is
narrower than that minimum. A lower bound takes precedence over an upper bound, so a fixed
minimum silently overhangs a narrow panel instead of being capped by the available width.

Where the popup cannot show all of its content, the space SHALL be taken from a section that
scrolls, and every section SHALL retain enough height to remain usable. The section listing
models SHALL keep a floor: reducing it to nothing leaves a popup that opens onto no models,
which is the whole of what the control is for.

#### Scenario: A panel narrower than the popup's preferred width

- **WHEN** the panel is narrower than the popup's minimum width
- **THEN** the popup is no wider than the panel

#### Scenario: A panel too short for the popup's full content

- **WHEN** the panel is too short to show every section at full height
- **THEN** the popup stays within the panel
- **AND THEN** the model list retains usable height and scrolls

#### Scenario: The composer has grown

- **WHEN** the user has typed enough to expand the composer
- **THEN** the popup still opens wholly within the panel

### Requirement: Model and thinking level each get the panel's full width

The picker SHALL NOT divide its popup into side-by-side columns for models and thinking
levels.

At the panel widths this add-in is designed for, splitting the popup leaves neither side
enough room: a model identifier from a gateway runs to several dozen characters and wraps to
three lines in a fraction of a narrow panel, while a thinking level's name together with an
annotation does not fit in the remainder at all. Stacking the two gives each the full width
and one line per entry.

Each thinking level SHALL occupy one row.

#### Scenario: A long model identifier at a narrow panel width

- **WHEN** the panel is at its usual width and the catalogue contains a long identifier
- **THEN** the model list has the popup's full width available for it

#### Scenario: A thinking level with an annotation

- **WHEN** a thinking level carries an annotation
- **THEN** the level's name and its annotation share one row
