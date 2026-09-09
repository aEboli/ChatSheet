# model-availability Specification Delta

## MODIFIED Requirements

### Requirement: Bulk confirmation covers the list and, separately, the catalogue

The add-in SHALL let the user confirm every listed model in one action, running the
requests one at a time, reporting progress as it goes, and stopping when the user asks it
to. Results already obtained SHALL be kept when the run is stopped part way.

The add-in SHALL also offer confirmation over the whole catalogue, as an action distinct
from the one covering the list. A catalogue of several dozen models becomes several dozen
billed requests, so the control offering it SHALL state, before it is used, what the run
will cost.

Catalogue-wide confirmation SHALL be offered at more than one scope, so that a user who
only needs to find a working model does not have to pay for the whole catalogue to learn
it. The scopes SHALL be presented before any request is sent, and choosing one SHALL be
what starts the run. A single control that both offers the choice and starts the widest
run is what this replaces: the widest run was the only thing one click could express, and
it is the most expensive one.

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

Where a bulk run cannot be started, the control SHALL say why. The reasons SHALL include a
conversation being in flight: the add-in refuses a confirmation during a turn, and a
refusal that reaches the user only as a host log entry is indistinguishable from a control
that did nothing.

#### Scenario: Bulk run over a short list

- **WHEN** the user confirms a list of five models
- **THEN** five requests are sent one after another with visible progress

#### Scenario: Bulk run stopped part way

- **WHEN** the user stops a bulk run after two models
- **THEN** those two outcomes are kept and no further requests are sent

#### Scenario: Stopping a bulk run leaves a conversation alone

- **WHEN** a bulk run is stopped
- **THEN** any conversation in flight is unaffected

#### Scenario: The scopes are offered before anything is sent

- **WHEN** the user activates the catalogue-wide control
- **THEN** the available scopes are shown
- **AND THEN** no request has been sent
- **AND THEN** choosing one scope starts that run

#### Scenario: Outcomes appear during a catalogue-wide run

- **WHEN** a catalogue-wide run has confirmed some models but not all
- **THEN** those models' rows already show their outcomes

#### Scenario: An individual confirmation during a batch

- **WHEN** a batch is running and a single confirmation is requested
- **THEN** it waits for the batch rather than adding to the requests in flight

#### Scenario: A run that cannot start during a conversation

- **WHEN** a conversation turn is in flight
- **THEN** the catalogue-wide control reports that it is unavailable and why

## ADDED Requirements

### Requirement: A run may stop once enough models are found to work

A catalogue-wide run MAY carry a target number of working models. Once that many models in
the run have been recorded available, the run SHALL stop sending further requests.

Only the available verdict SHALL count towards the target. Rate limiting and the add-in's
own deadline are both recorded as unknown, and unknown means the request was paid for and
produced no answer; counting it as progress towards "found a model that works" would
declare success on exactly the outcome the rest of this capability exists to avoid.

Requests already sent when the target is reached SHALL be allowed to finish, and their
verdicts SHALL be recorded. They have already been paid for, and discarding their answers
costs the user money for nothing. Stopping SHALL therefore mean stopping dispatch, and
SHALL NOT be implemented by cancelling the run: cancellation discards those verdicts,
releases the single-flight guard while requests are still in flight, and leaves the rows
concerned showing neither a verdict nor that anything is still happening.

A target the run cannot reach SHALL NOT change what the run covers. Where fewer models
work than the target asks for, every candidate SHALL be tried, and the run SHALL end as a
completed catalogue-wide run.

#### Scenario: The target is reached early

- **WHEN** a run with a target of one finds a working model
- **THEN** no further requests are dispatched
- **AND THEN** the requests already sent are completed and their verdicts recorded

#### Scenario: An unknown outcome does not count

- **WHEN** a run with a target of one receives a rate-limited response
- **THEN** the run continues

#### Scenario: Fewer working models than the target

- **WHEN** a run with a target of two finds only one working model in the whole catalogue
- **THEN** every model in the catalogue has been tried

### Requirement: Each scope states its bound, and what would end it early

Every scope offered SHALL state the greatest number of requests it can send. That number
SHALL be the number of candidate models for every scope, including those carrying a target:
a target that no model satisfies sends the whole catalogue, so a scope whose stated bound
is smaller than the catalogue is stating something that will sometimes be false.

Because the bound is therefore the same figure for every scope, the bound alone SHALL NOT
be what distinguishes them. Each scope SHALL additionally state what would make it end
early, and the least number of requests it can send. Where the run may dispatch several
requests at once, that least number SHALL account for the requests already in flight when a
target is met — a run bounded by concurrency cannot send fewer requests than its
concurrency, and a scope reading as though it sends one is understating its cost.

#### Scenario: A scope carrying a target

- **WHEN** the user reads a scope that stops after finding one working model
- **THEN** it states the same upper bound as the scope covering the whole catalogue
- **AND THEN** it states that finding a working model ends it
- **AND THEN** it states the least number of requests it can send

#### Scenario: The widest scope

- **WHEN** the user reads the scope covering the whole catalogue
- **THEN** it states how many requests that sends
- **AND THEN** nothing is claimed about it ending early

### Requirement: How a run ended is reported, and is never inferred from a cancellation

A finished run SHALL report which of three things ended it: the user stopping it, its
target being met, or every candidate having been tried. Where more than one applies, the
user stopping it SHALL take precedence, because only that outcome means verdicts already
paid for were discarded.

The outcome SHALL be determined by what the run did, and SHALL NOT be inferred from whether
a cancellation was raised. A run that stops on its target and then waits for the requests
still in flight can be cancelled by the user during that wait; a rule that reads the
cancellation would report the user's action as the target being met, or the reverse,
depending only on timing.

A run whose target was not met SHALL say so, rather than reporting only that it finished.
The user chose the cheaper scope and was charged for the widest one; a run that reports
nothing leaves them believing the opposite.

A run reported as ended SHALL stay ended. Progress arriving after a run has finished SHALL
NOT restore it: a control that returns to showing a run in progress, for a run the add-in
is no longer performing, offers to stop something that cannot be stopped.

#### Scenario: The target is met

- **WHEN** a run with a target of two records its second available model with candidates left over
- **THEN** the run reports that its target was met, and how many candidates were not tried

#### Scenario: The user stops the run while it finishes the requests in flight

- **WHEN** a run has met its target and the user stops it before the requests in flight return
- **THEN** the run reports that the user stopped it

#### Scenario: The target was not met

- **WHEN** a run with a target of two tries every candidate and finds one available model
- **THEN** the run says the target was not met and that the whole catalogue was tried

#### Scenario: Progress arrives after the run ended

- **WHEN** a run has reported that it ended and a further outcome arrives for one of its models
- **THEN** the control does not return to showing a run in progress

### Requirement: A running scope is measured against what it is waiting for

While a run with a target is in progress, the progress the control reports SHALL be
measured against that target rather than against the number of candidates.

A run that stops after one working model commonly sends a handful of requests out of a
catalogue of dozens. Reporting it against the catalogue shows a figure that will never be
reached, and a run that then stops reads as one that broke.

#### Scenario: A run with a target reports progress

- **WHEN** a run with a target of two has found one working model
- **THEN** the control reports one of two, not one of the catalogue's count
