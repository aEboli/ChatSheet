# model-availability Specification

## Purpose
Tell the user which of a gateway's models actually work, and let them keep the few they use
within reach. A gateway commonly returns dozens of model IDs of which most are unusable, and
`GET /models` gives only names. This capability derives a verdict — available, unavailable, or
unconfirmed — from turns the user already runs, lets them confirm an untried model on demand
for the cost of one minimal request, and keeps a per-connection list of the models they mean
to use. Verdicts annotate and never hide: the add-in's judgement is heuristic, so a wrong one
must cost no more than a label the user can overrule by trying again.

## Requirements

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

### Requirement: The user can confirm one model on demand

The add-in SHALL let the user confirm a single model by sending one minimal request over
the current connection, and SHALL record the outcome under the same three states and the
same attribution rules that apply to a real turn.

Confirmation SHALL NOT be triggered automatically by opening the picker or by loading the
catalogue. Opening a list must never turn into a charge the user did not ask for.

While a confirmation is in flight, the model's row SHALL show that it is being confirmed,
distinctly from the three outcomes. Without this the user cannot tell a slow gateway from a
click that did nothing.

#### Scenario: User confirms one model

- **WHEN** the user asks to confirm a model
- **THEN** exactly one request is sent for that model
- **AND THEN** the outcome is shown on that model's row

#### Scenario: Opening the picker sends nothing

- **WHEN** the user opens the model picker
- **THEN** no confirmation request is sent

#### Scenario: A confirmation in flight is visible

- **WHEN** a confirmation has been requested and no outcome has arrived
- **THEN** that model's row shows it is being confirmed

### Requirement: The confirmation request is a subset of a real request

The minimal request SHALL differ from a real conversation request only by removing things:
no tool declarations, no images, no thinking parameters, and a smaller output limit. It
SHALL NOT substitute a different value for any field a real request would send.

Requesting no thinking SHALL mean omitting the thinking parameters entirely, not sending a
value that means "off". A value that means off is still a value, and a gateway that rejects
it reports a failure that describes the add-in's own request rather than the model — which
is recorded as unknown, permanently, no matter how often the user asks. The reverse is
worse: a model that rejects the thinking level a real conversation would send confirms as
available and then fails every real turn, so the outcome actively misleads.

The request SHALL carry one short user message. A request carrying only a system prompt is
not universally valid: for protocols that lift the system role out of the message list, it
produces an empty message list, which is rejected.

#### Scenario: No thinking parameters are sent

- **WHEN** a confirmation request is built
- **THEN** it contains no thinking or reasoning parameter for any protocol

#### Scenario: The message list is never empty

- **WHEN** a confirmation request is built for a protocol that lifts the system role out of the message list
- **THEN** the message list still contains one user message

#### Scenario: A model that rejects the conversation's thinking level

- **WHEN** a model would reject the thinking level a real conversation sends
- **THEN** confirmation does not report it as available

### Requirement: The output limit field is chosen by evidence, not by model name

Where a protocol accepts more than one field name for the output limit, the add-in SHALL
determine which one a model accepts from the service's own rejection, and SHALL remember
that determination for that connection and model.

The choice SHALL NOT be made by matching the model's name. Model names have no reliable
correspondence to behaviour, and a guess encoded in a name pattern breaks as soon as a
gateway renames or aliases a model.

A rejection that names the output limit field SHALL cause one retry with the other field
name, in the same shape as the add-in's other capability fallbacks. This applies to real
conversations as well as confirmations, because the same rejection occurs there.

#### Scenario: A model rejecting the first field name

- **WHEN** a request fails with a client error naming the output limit field
- **THEN** the request is retried once with the other field name
- **AND THEN** that choice is remembered for that connection and model

#### Scenario: The choice is not guessed from the name

- **WHEN** a model has never been observed to reject either field name
- **THEN** the field name used does not depend on the model's name

### Requirement: Confirmation reuses the streaming path

The add-in SHALL send the confirmation over the same request path a real conversation uses,
rather than a separate non-streaming path.

The streaming path already recognises errors returned inside a successful response for
several protocols. A separate path judging only the transport status would discard that
recognition and report a model as available when the service plainly said otherwise.

For any protocol where an error inside a successful response is not recognised, the add-in
SHALL treat a response that delivers no events at all as unknown rather than available.
Reaching the service is not the same as reaching the model.

#### Scenario: An error inside a successful response

- **WHEN** the service returns a success status whose body contains an error naming the model
- **THEN** the model is reported unavailable rather than available

#### Scenario: A successful response with no content

- **WHEN** the service returns a success status and delivers no events
- **THEN** the outcome is unknown rather than available

### Requirement: Confirmation does not wait out the retry ladder

A confirmation SHALL NOT run the full retry-and-backoff sequence a real conversation uses.
That sequence is tens of seconds long, which contradicts the purpose of a confirmation the
user is waiting on. Retrying SHALL be left to the user asking again.

A confirmation SHALL have its own deadline, short and not including any backoff. Without
one it inherits no deadline at all: the request path has no timeout of its own, so a gateway
that accepts the connection and never answers would leave the row showing "being confirmed"
indefinitely and block everything queued behind it.

A deadline the add-in imposed SHALL be distinguishable from the user cancelling, and SHALL
be reported as unknown. The two arrive as the same kind of failure otherwise, and a
cancellation must not be recorded as a fact about the model.

#### Scenario: A gateway that never answers

- **WHEN** a confirmation exceeds its deadline
- **THEN** the outcome is unknown
- **AND THEN** anything queued behind it proceeds

#### Scenario: The user cancels rather than the deadline expiring

- **WHEN** the user cancels a confirmation
- **THEN** no outcome is recorded for that model

### Requirement: Only one confirmation is in flight, and never during a turn

A confirmation requested while another is running SHALL be queued rather than sent
concurrently. Concurrent probes invite rate limiting, and rate limiting is reported as
unknown — turning a cost the user paid into no answer.

A confirmation SHALL NOT be sent while a conversation turn is in flight. It SHALL be
refused with a reason the user can act on. The existing guard against concurrent turns
protects only the turn channel, so a confirmation channel inherits nothing from it. Probes
fired alongside a turn put several request streams on one account and can rate-limit the
turn that carries the user's whole context.

#### Scenario: Two confirmations requested at once

- **WHEN** the user asks to confirm a second model while the first is running
- **THEN** the second is sent only after the first finishes

#### Scenario: Confirmation requested during a turn

- **WHEN** a conversation turn is in flight and the user asks to confirm a model
- **THEN** no request is sent and the user is told why

### Requirement: Bulk confirmation covers the list and, separately, the catalogue

The add-in SHALL let the user confirm every listed model in one action, running the
requests one at a time, reporting progress as it goes, and stopping when the user asks it
to. Results already obtained SHALL be kept when the run is stopped part way.

The add-in SHALL also offer confirmation over the whole catalogue, as an action distinct
from the one covering the list. A catalogue of several dozen models becomes several dozen
billed requests, so the control offering it SHALL state, before it is used, how many
requests this will send.

Catalogue-wide confirmation MAY run several requests at once, where confirming several
dozen models one at a time would take too long to wait on. Doing so raises the chance of
rate limiting, and rate limiting is recorded as unknown — a cost paid for no answer — so
the control SHALL say that too. The requests it runs concurrently SHALL be bounded, and
individually requested confirmations SHALL NOT become concurrent as a side effect: a batch
holds the single-flight guard for its whole duration and manages its own concurrency
within it, so that the number in flight is always a known quantity.

An outcome SHALL be shown on each model's row as it arrives, rather than only when the
whole batch finishes. Confirming several dozen models takes long enough that a list which
changes nothing until the end is indistinguishable from one that has stalled.

Stopping a bulk run SHALL be a separate action from stopping a conversation, and SHALL NOT
cancel a conversation. Conversely, stopping a conversation SHALL NOT cancel a bulk run.
One control that stops either depending on hidden state is the failure this project has
already paid to fix once.

#### Scenario: Bulk run over a short list

- **WHEN** the user confirms a list of five models
- **THEN** five requests are sent one after another with visible progress

#### Scenario: Bulk run stopped part way

- **WHEN** the user stops a bulk run after two models
- **THEN** those two outcomes are kept and no further requests are sent

#### Scenario: Stopping a bulk run leaves a conversation alone

- **WHEN** a bulk run is stopped
- **THEN** any conversation in flight is unaffected

#### Scenario: The cost of a catalogue-wide run is stated up front

- **WHEN** the control for confirming the whole catalogue is available
- **THEN** it states how many requests the run will send before it is used

#### Scenario: Outcomes appear during a catalogue-wide run

- **WHEN** a catalogue-wide run has confirmed some models but not all
- **THEN** those models' rows already show their outcomes

#### Scenario: An individual confirmation during a batch

- **WHEN** a batch is running and a single confirmation is requested
- **THEN** it waits for the batch rather than adding to the requests in flight

### Requirement: Confirmation uses the saved connection only

A confirmation SHALL be sent over the connection currently saved, and SHALL NOT accept an
unsaved candidate configuration.

The model-list request deliberately accepts unsaved values so the settings page can try a
connection before saving. A confirmation doing the same would record its verdict against
whichever connection is saved at that moment, so trying a candidate gateway would mark
models on the connection the user is still working with.

#### Scenario: Confirmation is not offered for an unsaved configuration

- **WHEN** the user is editing connection settings that have not been saved
- **THEN** confirmation is not available for that candidate configuration

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

### Requirement: The popup is sized to its content, not to the panel

The picker's popup SHALL take a width fixed to what its content needs, and SHALL NOT expand
to fill the width of the panel it opens into.

A popup that grows with the panel buys a few more characters of model identifier at the cost
of covering most of the conversation behind it. The identifier is what needs the horizontal
room, so the space SHALL be divided by giving the thinking levels only the width their own
text requires and the model list everything remaining.

The width SHALL be expressed so that the panel's available width can override it. A lower
bound takes precedence over an upper bound, so a minimum width silently overhangs a panel
narrower than itself, and the overhanging part is clipped without a scrollbar.

The width given to the thinking levels SHALL be derived from their own content rather than
written as a fixed measurement. A measurement computed from the current font and wording
stops being right when either changes, failing in one of two directions: too little room
wraps the rows, too much leaves a band of empty space.

Each thinking level SHALL occupy one row, and SHALL be prevented from wrapping rather than
merely given enough room not to. Wrapping fails silently: nothing reports it, the rows just
quietly become two lines each.

A model identifier SHALL occupy one row, and SHALL be truncated rather than wrapped when it
does not fit. Wrapping turns a list of several dozen models into one of well over a hundred
lines, which makes finding one harder rather than easier. The complete identifier SHALL
remain available on demand, so that identifiers differing only in their tail can still be
told apart.

The controls in a column's header SHALL occupy one row. A header that wraps pushes the list
below it down. Where the labels do not fit, they SHALL be shortened and their full wording
moved to where it is available on demand, rather than the header being allowed to wrap.
Any state a shortened label can no longer express SHALL be carried by some other visible
means, not dropped.

#### Scenario: A panel much wider than the popup needs

- **WHEN** the panel is considerably wider than the popup's content requires
- **THEN** the popup does not widen to match the panel

#### Scenario: A panel narrower than the popup's fixed width

- **WHEN** the panel is narrower than the popup's fixed width
- **THEN** the popup is no wider than the panel

#### Scenario: A thinking level with an annotation

- **WHEN** a thinking level carries an annotation
- **THEN** the level's name and its annotation share one row

#### Scenario: An annotation too long for the column

- **WHEN** a thinking level's text cannot fit the column's width
- **THEN** the row still occupies one line

#### Scenario: A model identifier longer than its column

- **WHEN** the catalogue contains an identifier too long for the model column
- **THEN** its row still occupies one line
- **AND THEN** the complete identifier is available on demand

#### Scenario: A header whose controls do not fit

- **WHEN** a column header's controls cannot all fit on one row at their full labels
- **THEN** the header still occupies one row

#### Scenario: A state that a shortened label cannot express

- **WHEN** the list-only switch is on but is not currently withholding anything
- **THEN** that state is still visible without lengthening the label
