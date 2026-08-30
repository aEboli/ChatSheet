# model-availability Specification

## ADDED Requirements

### Requirement: Availability is a narrower claim than capability

The add-in SHALL treat a model as available when a request reaches it and it replies.
Availability SHALL NOT depend on whether the model can call tools or accept images.

This boundary is deliberate. A model that cannot call tools is still usable: the add-in
already serves it in advisory mode, answering with formulas and steps. Folding "cannot
call tools" into "unavailable" would remove from the user's view a model the add-in is
specified to keep serving.

Availability SHALL be tracked separately from tool and vision capability, and SHALL NOT
overwrite either. A model recorded as lacking tools or vision SHALL NOT thereby be
recorded as unavailable.

#### Scenario: A model without tool support is still available

- **WHEN** a model replies but cannot call tools
- **THEN** it is reported as available
- **AND THEN** its tool capability record is unchanged

#### Scenario: An unavailable verdict leaves capability records alone

- **WHEN** a model is found unavailable
- **THEN** no tool protocol or vision judgement is recorded for it

### Requirement: Availability is learned from real turns at no extra cost

The add-in SHALL derive availability from conversations the user already runs, and SHALL
NOT send any additional request to establish it.

A turn that reaches the model SHALL record it as available, whatever else goes wrong
afterwards. Reaching the model is the whole of what availability claims.

A turn that fails with a client error naming the model as the thing at fault SHALL record
that model as unavailable.

A recorded verdict SHALL NOT survive evidence against it: a model marked unavailable that
later replies SHALL become available, and a model marked available that later fails naming
itself SHALL become unavailable. A verdict that outlived its evidence is worse than none,
because the user has no reason to doubt it.

A model the user has never sent a turn to SHALL carry no verdict, and SHALL be reported as
unconfirmed rather than as either outcome.

#### Scenario: A successful turn marks the model available

- **WHEN** a turn reaches the model
- **THEN** that model is recorded available without the user confirming it separately

#### Scenario: A turn that fails after reaching the model

- **WHEN** a turn reaches the model and then fails for an unrelated reason
- **THEN** the model is recorded as available

#### Scenario: A failed turn marks the model unavailable

- **WHEN** a turn fails because the model does not exist
- **THEN** that model is marked unavailable

#### Scenario: A successful turn clears an unavailable mark

- **WHEN** a model previously marked unavailable replies to a turn
- **THEN** it is marked available

#### Scenario: An untried model carries no verdict

- **WHEN** the user has never sent a turn to a model
- **THEN** it is reported as unconfirmed

### Requirement: Only failures that identify the model mean unavailable

A failure SHALL be recorded as unavailable only when it is a client error that names the
model as the thing at fault, such as an unknown model, or access refused for that model
specifically.

A failure SHALL be recorded as unknown when it describes the account, the network, or the
service rather than the model: a rejected key, rate limiting, a server error, a timeout, or
a transport failure. Unknown SHALL also be the outcome whenever the failure cannot be
attributed with confidence.

A failure that describes the request rather than the model — a malformed body, a parameter
the add-in itself set wrongly — SHALL be recorded as unknown, because that is a defect on
this side and says nothing about the model.

The status code alone SHALL NOT decide this. A refusal can describe the account ("this key
is not valid") or one model ("this key has no access to that model") under the same code,
and only the latter is a fact about the model. A not-found response can describe a wrong
address as easily as a wrong model. Where the two cannot be told apart, the outcome SHALL be
unknown, because a rejected key marking every model unavailable is the exact failure this
rule exists to prevent.

#### Scenario: Unknown model recorded as unavailable

- **WHEN** a turn fails because the model does not exist
- **THEN** the model is recorded unavailable

#### Scenario: Rate limiting recorded as unknown

- **WHEN** a turn fails with rate limiting
- **THEN** the model is recorded unknown rather than unavailable

#### Scenario: Invalid key recorded as unknown

- **WHEN** a turn fails because the key is rejected
- **THEN** every model's outcome is unknown rather than unavailable

#### Scenario: Access refused for one named model

- **WHEN** a turn fails because the account may not use that particular model
- **THEN** the model is recorded unavailable

#### Scenario: A not-found response that names nothing

- **WHEN** a turn fails as not found without the service naming the model
- **THEN** the model is recorded unknown

### Requirement: Attribution reads the service's own words, never the add-in's

The judgement of whether a failure names the model SHALL be made from the text the service
returned, and SHALL NOT be made from any text the add-in composed.

The add-in appends its own guidance to error messages, and the guidance for a not-found
response mentions the model name. A judgement that read the composed message would find
the model named in every not-found response, including one caused by a wrong address, and
would then condemn every model on the list in turn.

The service's original error text SHALL therefore be carried alongside the composed message
rather than folded into it. Where the original text could not be recovered, the outcome
SHALL be unknown.

#### Scenario: The add-in's own guidance is not evidence

- **WHEN** a turn fails as not found and the service says nothing about the model
- **AND WHEN** the add-in's composed message mentions the model name as guidance
- **THEN** the model is recorded unknown

#### Scenario: The original error text is unavailable

- **WHEN** a turn fails and the service's own error text could not be recovered
- **THEN** the model is recorded unknown

### Requirement: An error that names the model is not a capability signal

When a failure names the model as the thing at fault, the add-in SHALL NOT treat it as
evidence about tool or vision support, and SHALL NOT begin either fallback on account of it.

Without this, a model whose name happens to contain a word the vision heuristic matches is
recorded as unable to accept images when it is in fact absent: the add-in then spends a
relay request describing the picture, strips the images, retries the same absent model, and
tells the user the model cannot see. One error produces two records, one of them false, and
the false one is what the user is shown.

#### Scenario: An absent model whose name resembles a capability word

- **WHEN** a turn carrying an image fails because the model does not exist
- **AND WHEN** the model's name contains a word the vision heuristic matches
- **THEN** no vision limitation is recorded for it
- **AND THEN** no relay request is spent

### Requirement: Verdicts live in memory for as long as the add-in is loaded

Availability verdicts SHALL be held in memory and SHALL NOT be written to disk, so that a
gateway restoring quota or access is picked up without the user clearing anything.

The lifetime SHALL be stated as the add-in's own, not the panel's. Closing the panel does
not discard verdicts, because closing the panel does not destroy the panel: the add-in
keeps the control and reuses it when the panel is shown again.

Verdicts SHALL be discarded for a connection when that connection changes, and whenever a
key is written for it. The key of a connection does not include the API key, so a new key on
the same address SHALL be treated as grounds to discard: the set of models an account can
reach follows the key.

Discarding SHALL be driven by the act of writing a key, not by comparing it to the previous
one. Comparing requires reading back the stored secret to no purpose, and discarding a
verdict that the next turn re-establishes is the cheaper mistake.

Discarding SHALL affect only the connection concerned, leaving other connections' verdicts
in place.

#### Scenario: Verdicts survive the panel being closed and reopened

- **WHEN** the user closes the panel and shows it again
- **THEN** verdicts obtained earlier are still in effect

#### Scenario: A new key discards verdicts

- **WHEN** the user saves a different API key for the same address
- **THEN** the availability verdicts for that connection are discarded
- **AND THEN** other connections' verdicts are unaffected

### Requirement: The user keeps a per-connection list of models they actually use

The add-in SHALL let the user mark individual models as ones they use, and SHALL remember
those marks across sessions. The list SHALL be keyed by connection as well as model name,
because the same model name served through a different gateway is a different offering.
The key SHALL NOT include the API key.

The list SHALL be stored outside the settings file. The settings file is rebuilt in full
from a fixed set of keys whenever any of several independent writers saves it, so an entry
that is not part of that set would be erased by an unrelated save.

Writing the list SHALL replace the previous file in one step and SHALL keep the previous
contents recoverable. A write that removes the old file before putting the new one in place
can lose both, and unlike settings — which fall back to defaults — a lost list is the user's
own work with nothing to fall back to.

A model the user types by hand SHALL be added to the list, because entering an ID by hand
is itself the statement that this is a model they intend to use.

Model names SHALL be compared without regard to case, matching how the catalogue itself
de-duplicates names, so that a model marked in one casing is recognised in another.

Where the list holds entries for several connections, only the entry for the current
connection SHALL be validated. Entries belonging to other connections SHALL be left exactly
as they are, because in a per-connection list a mismatched owner is the normal state of
every group except the current one, not an error to be cleaned up.

For a connection identified by which local CLI it uses, the list SHALL be grouped by the CLI
that was actually resolved rather than by the setting the user chose. Otherwise pinning the
setting to the CLI already in use — the natural thing to do once the user knows which one it
is — changes the identity while the credentials stay identical, and the marks they built up
become unreachable.

If the stored list cannot be read, the add-in SHALL behave as though no model is marked and
SHALL keep the unreadable file for inspection.

#### Scenario: Marks survive a restart

- **WHEN** the user marks a model and later restarts Excel
- **THEN** the mark is still in effect for that connection

#### Scenario: The same model name on two connections

- **WHEN** a model is marked on one connection
- **THEN** the other connection's list is unaffected

#### Scenario: A hand-entered model joins the list

- **WHEN** the user types a model ID that the catalogue does not offer
- **THEN** that model is added to the list

#### Scenario: A mark made in different casing

- **WHEN** a model is marked as `GPT-4O` and the catalogue offers `gpt-4o`
- **THEN** the catalogue's row shows the mark

#### Scenario: Pinning the CLI keeps the list

- **WHEN** the user pins the CLI setting to the CLI that was already being resolved
- **THEN** the list built up before remains in effect

#### Scenario: An unreadable list

- **WHEN** the stored list cannot be parsed
- **THEN** no model is treated as marked
- **AND THEN** the file is left in place

### Requirement: Listed models are ordered first, always

The model picker SHALL show listed models before the rest of the catalogue, with no setting
required to enable it. The remainder SHALL keep the order the catalogue supplied.

Ordering rather than hiding is what makes a long catalogue usable without any risk of
withholding something the user needs. It is also the disposition this control already takes:
thinking levels a model does not support are annotated, not hidden.

A verdict arriving SHALL NOT change a row's position. A mark that appears and simultaneously
makes the row jump is harder to use than no mark at all.

#### Scenario: Listed models come first

- **WHEN** the picker renders a catalogue containing listed and unlisted models
- **THEN** the listed models appear before the others

#### Scenario: A verdict does not reorder rows

- **WHEN** a verdict is recorded for a model while the picker is open
- **THEN** that model's position is unchanged

### Requirement: One switch narrows the picker to that list

The add-in SHALL provide a single switch controlling whether the model picker shows only
listed models. The switch SHALL default to off, so a user who upgrades sees the picker
behave as before.

When the switch is on and no listed model appears in the current catalogue, the picker SHALL
show the full catalogue. This covers an empty list and an entirely stale one under one rule:
a list whose every entry has gone — a repointed gateway, withdrawn models, an ID mistyped
months ago — is not empty, and narrowing to it would leave the picker holding nothing but
the selected model while withholding the models that do work.

The currently selected model SHALL remain visible whether or not it is listed, and whether
or not the switch is on.

Marking a model while the switch is on SHALL NOT immediately narrow the picker. The user's
action says "remember this one"; narrowing on the spot makes its effect "hide the other
fifty-nine", with nothing in view connecting the two. The picker SHALL instead continue
showing the full catalogue and offer narrowing as an explicit next action.

When models are being withheld, the picker SHALL say how many and offer a way to show them
all again.

The switch SHALL be persisted through the channel belonging to the control that carries it,
and SHALL NOT be round-tripped through the settings page's form. That form holds a snapshot
taken when the panel was opened, so sending it back would overwrite a switch the user
changed in the picker since.

#### Scenario: Switch off leaves the catalogue intact

- **WHEN** the switch is off
- **THEN** every model returned by the catalogue is shown

#### Scenario: Switch on with an empty list

- **WHEN** the switch is on and no model has been marked
- **THEN** the full catalogue is shown

#### Scenario: Switch on with an entirely stale list

- **WHEN** the switch is on and no listed model appears in the catalogue
- **THEN** the full catalogue is shown

#### Scenario: The selected model is never hidden

- **WHEN** the switch is on and the selected model is not listed
- **THEN** that model is still shown and still selected

#### Scenario: The first mark does not narrow the view

- **WHEN** the switch is on, the list is empty, and the user marks one model
- **THEN** the full catalogue is still shown

#### Scenario: Saving the settings page keeps the switch

- **WHEN** the user changes the switch in the picker and then saves the settings page
- **THEN** the switch keeps the value the user set

### Requirement: Verdicts annotate and never hide

An availability verdict SHALL NOT remove a model from the picker. Models SHALL be withheld
only by the user's own switch and list.

This holds the cost of a wrong verdict to a label the user can overrule by trying again.
The add-in's judgement of availability is heuristic, because no protocol offers a way to
ask; a heuristic that hides things would eventually hide something the user needs, and
leave them no way to see that it happened.

A model recorded unavailable SHALL remain selectable, so a user who believes the verdict is
wrong can act on that belief.

The absence of a verdict SHALL be rendered as its own state rather than as blank space, so
that a model never tried is distinguishable from one whose mark failed to appear.

The user's own mark and the add-in's verdict SHALL be distinguishable by more than colour.
They are different kinds of claim with different lifetimes — one is the user's intent and
persists, the other is the add-in's observation and does not.

#### Scenario: An unavailable model stays in the list

- **WHEN** a model is recorded unavailable and the switch is off
- **THEN** the model is still shown, marked as unavailable

#### Scenario: An unavailable model can still be chosen

- **WHEN** the user selects a model marked unavailable
- **THEN** the selection takes effect

#### Scenario: A model with no verdict

- **WHEN** a model has never been sent a turn
- **THEN** its row shows an unconfirmed state rather than nothing
