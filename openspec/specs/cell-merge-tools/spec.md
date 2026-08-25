# cell-merge-tools Specification

## Purpose
Let the model merge and unmerge worksheet cells, which the tool catalog previously did
not expose at all. Merging is the only write operation that silently destroys data: the
host keeps the value of the top-left cell and discards the rest, leaving no trace. This
capability therefore treats disclosure of what will be lost, and recoverability of what
was lost, as part of the operation rather than as extras.

## Requirements

### Requirement: Merging and unmerging are available as approvable write tools

The tool catalog SHALL expose a tool that merges a cell range into a single cell, and a
tool that unmerges the merged areas intersecting a range. Both SHALL be classified at the
same risk level as other content-modifying tools, so that they are subject to the user's
approval policy and appear in an approval card showing the affected range.

The merge tool SHALL support merging a range into one cell, and merging each row of the
range separately. It SHALL accept optional horizontal and vertical alignment, applied as
part of the same operation so that "merge and centre" is a single undoable action; when
alignment is not requested, existing alignment SHALL be left unchanged.

The add-in's stated capability boundary SHALL include merging and unmerging, so the model
does not describe merging as something it cannot do.

#### Scenario: User asks to merge a range

- **WHEN** a user asks to merge cells
- **THEN** the model can call the merge tool rather than reporting that it has no such capability
- **AND THEN** the call is subject to the active approval policy before it executes

#### Scenario: Merge with centring in one action

- **WHEN** a merge is requested together with horizontal alignment
- **THEN** the range is merged and that alignment is applied
- **AND THEN** the result is recorded as a single undoable operation

#### Scenario: Merge without alignment leaves alignment alone

- **WHEN** a merge is requested with no alignment specified
- **THEN** the range is merged and the alignment already in effect is preserved

### Requirement: A merge discloses how many values it discards

Before merging, the add-in SHALL determine how many non-empty cells will lose their
content, counting every cell in the range except the cells the host preserves, and SHALL
report that count in the tool result. The tool's description SHALL state that content
outside the preserved cell is discarded and that the range should be read first.

Reporting SHALL be accurate rather than optimistic: the result SHALL also report the
merged areas actually in effect after the operation, so a merge that absorbed
pre-existing merged areas outside the requested range is not reported as if the layout
matched the request.

#### Scenario: Merge a range holding several values

- **WHEN** a range containing three non-empty cells is merged into one cell
- **THEN** the result reports that two values were discarded
- **AND THEN** the result reports the merged areas now in effect

### Requirement: Merges are reversible, or refused

A merge SHALL be undoable such that undoing it both splits the merged area back into
individual cells and restores the content that was discarded, along with any alignment
the same call changed. Redoing SHALL re-apply the merge.

Because restoring discarded content requires per-cell state, the merge tool SHALL reject
ranges larger than the per-cell snapshot limit and report the limit, rather than
performing a merge whose lost values could not be recovered.

Unmerging SHALL be undoable by restoring the merged areas that existed before the call.
Unmerging does not discard content, so it SHALL NOT require content to be recorded.

#### Scenario: Undo a merge that discarded values

- **WHEN** a merge that discarded values is undone
- **THEN** the affected cells are no longer merged
- **AND THEN** the discarded values are present again in their original cells
- **AND THEN** alignment changed by the same call is back to its previous value

#### Scenario: Merge a range too large to snapshot

- **WHEN** a merge is requested for a range exceeding the per-cell snapshot limit
- **THEN** the merge is refused with the limit and the range's size stated
- **AND THEN** no cell content is lost

#### Scenario: Undo an unmerge

- **WHEN** an unmerge is undone
- **THEN** the merged areas that existed before the unmerge are in effect again

### Requirement: Unmerging a range with nothing merged is reported as such

When the target range contains no merged cells, the unmerge tool SHALL report that as a
failure identifying the range, rather than reporting success. A no-op success would tell
the model the layout had been changed, and would leave an undo entry that reverses
nothing.

#### Scenario: Unmerge a range with no merged cells

- **WHEN** an unmerge is requested for a range containing no merged cells
- **THEN** the call fails, naming the range
- **AND THEN** no undo entry is recorded

### Requirement: Host confirmation prompts never block the panel

While merging or restoring a merge, the add-in SHALL suppress host confirmation dialogs
and SHALL restore the host's previous setting afterwards. The add-in runs on the host's
UI thread, so such a dialog would freeze the host together with the panel, and the user's
permission has already been obtained through the panel's approval card.

#### Scenario: Merge a range holding several values

- **WHEN** a range with more than one non-empty cell is merged
- **THEN** the operation completes without the host presenting a confirmation dialog
- **AND THEN** the host's dialog setting is left as it was before the call

### Requirement: Restoring a snapshot re-establishes merge state in a writable order

When a snapshot records merge state, restoring it SHALL unmerge the range before writing
cell content and SHALL re-apply the recorded merged areas afterwards. Only the preserved
cell of a merged area accepts writes, so restoring content into a still-merged range
would be rejected by the host.

Recorded merged areas SHALL be captured whole even where they extend beyond the range
being snapshotted, so that restoring cannot re-create part of a merged area. If one
recorded area cannot be re-applied, the remaining areas SHALL still be restored.

#### Scenario: Undo a merge over a range that already contained a merged area

- **WHEN** a merge over a range that already contained a merged area is undone
- **THEN** the pre-existing merged area is in effect again
- **AND THEN** the cell content recorded before the merge is restored
