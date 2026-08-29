# model-availability Specification

## ADDED Requirements

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

### Requirement: Bulk confirmation covers the list, not the catalogue

The add-in SHALL let the user confirm every listed model in one action, running the
requests one at a time, reporting progress as it goes, and stopping when the user asks it
to. Results already obtained SHALL be kept when the run is stopped part way.

The add-in SHALL NOT offer bulk confirmation over the whole catalogue. A catalogue of
several dozen models would become several dozen billed requests, which is the cost the
user is trying to avoid in the first place.

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
