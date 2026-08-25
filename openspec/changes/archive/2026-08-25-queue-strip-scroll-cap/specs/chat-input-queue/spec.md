# chat-input-queue Specification

## Purpose
Let a user keep describing work while the add-in is already running a turn, without
losing input, reordering it, or letting two turns write to the same workbook at once.
The add-in executes one turn at a time; this capability moves the resulting wait from
a disabled composer into a visible, cancellable queue in the panel, kept next to the
composer where the user is still working rather than mixed into the record of what has
already happened.

## Requirements

### Requirement: Composer stays usable while a turn is running

The composer SHALL remain enabled while a turn is in progress. Content submitted during
a turn SHALL be accepted into a first-in-first-out queue rather than rejected, discarded,
or sent concurrently. The add-in SHALL continue to run at most one turn at a time, and
queued content SHALL NOT produce a busy rejection.

#### Scenario: Submit twice while a turn is running

- **WHEN** a user submits content while a turn is in progress, then submits more content
- **THEN** both submissions are accepted and retained in submission order
- **AND THEN** no additional turn is started while the current turn is still running

#### Scenario: Queue drains after the running turn ends

- **WHEN** the running turn finishes and the queue is non-empty
- **THEN** the panel starts the next queued submission without further user action
- **AND THEN** it continues until the queue is empty, running one turn at a time

### Requirement: Queued content is visible next to the composer, not in the transcript

Queued submissions SHALL be visible from the moment they are accepted, in a dedicated
pending area adjacent to the panel's per-turn usage readout, and SHALL NOT be placed in
the conversation transcript before they start. Each queued submission SHALL show its
position in the queue, and positions SHALL be renumbered whenever the queue changes.
The pending area SHALL be present only while the queue is non-empty, and SHALL order
submissions so that the one to run next is nearest the composer, with earlier-queued
entries extending away from it.

A submission SHALL move into the conversation transcript when its turn starts, so that
each submission is shown in exactly one place at a time.

#### Scenario: Submit while a turn is running

- **WHEN** a user submits content while a turn is in progress
- **THEN** the submission appears in the pending area with its queue position
- **AND THEN** it does not appear in the conversation transcript
- **WHEN** its turn later starts
- **THEN** it appears in the conversation transcript and leaves the pending area

#### Scenario: Cancel one queued submission

Each queued submission SHALL offer a cancel action that removes only that submission.
A cancelled submission SHALL leave no trace in the conversation transcript: it never
ran, and the transcript records only what happened.

- **WHEN** a user cancels a queued submission that has not started
- **THEN** it is removed from the queue and is never sent
- **AND THEN** it does not appear in the conversation transcript
- **AND THEN** the positions shown for the remaining queued submissions are updated

### Requirement: The pending area is capped in height and scrolls

The pending area SHALL occupy no more vertical space than three queued submissions,
however many are queued, so that a long queue cannot displace the conversation. When
more are queued than fit, the area SHALL be scrollable along its long axis, and SHALL
NOT introduce horizontal scrolling for the panel. Whenever the pending area is
redrawn, the visible portion SHALL be the end of the queue nearest the composer, so
the submission that runs next is always in view without scrolling.

#### Scenario: Queue grows past what fits

- **WHEN** more submissions are queued than the pending area can show at once
- **THEN** the pending area shows the three nearest the composer and does not grow further
- **AND THEN** the remaining queued submissions are reachable by scrolling within it
- **AND THEN** the panel gains no horizontal scrolling

#### Scenario: Pending area is redrawn while scrolled away

- **WHEN** the queue changes while the pending area is scrolled away from the composer end
- **THEN** the redrawn pending area again shows the submission that runs next

### Requirement: One control, three meanings

The primary submit control SHALL never be disabled. Its meaning SHALL be determined by
whether a turn is running and whether the composer has content:

| Turn running | Composer has content | Meaning |
| --- | --- | --- |
| no | — | send |
| yes | yes | add to queue |
| yes | no | stop |

The control's accessible name, hover description, and displayed graphic SHALL all reflect
the current meaning, and SHALL update as the composer's content changes. Attachments alone
SHALL count as content.

#### Scenario: Meaning follows composer content during a turn

- **WHEN** a turn is running and the composer is empty
- **THEN** the control means stop, and says so
- **WHEN** the user then types content or adds an attachment
- **THEN** the control means add to queue, and its name, description, and graphic change accordingly

### Requirement: Stopping and starting a new session clear the queue

Stopping SHALL cancel the queue in addition to interrupting the running turn, so that no
queued submission starts after the user asks to stop. Starting a new session SHALL also
clear the queue. Stopping SHALL report how many queued submissions it cancelled; as with
cancelling one, the cancelled submissions themselves SHALL NOT be added to the transcript.

#### Scenario: Stop with submissions queued

- **WHEN** a user stops while submissions are queued
- **THEN** the running turn is asked to stop and every queued submission is cancelled
- **AND THEN** no queued submission is started afterwards
- **AND THEN** the user is told how many queued submissions were cancelled

### Requirement: Attachment ownership is fixed at submission time

Attachments SHALL be bound to a submission at the moment it is submitted. Attachments
added after a submission is queued SHALL belong to a later submission and SHALL NOT be
sent with the already-queued one.

#### Scenario: Add an attachment after queueing

- **WHEN** a user submits content with an attachment while a turn is running, then adds
  another attachment and submits again
- **THEN** the first queued submission is sent with only the attachment present when it
  was submitted
- **AND THEN** the attachment added afterwards is sent only with the later submission
