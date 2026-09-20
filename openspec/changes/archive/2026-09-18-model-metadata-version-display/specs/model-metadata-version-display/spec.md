# WorkBuddy 模型显示与版本标识

## ADDED Requirements

### Requirement: Display a safe WorkBuddy multiplier

The WorkBuddy provider SHALL accept only a finite non-negative decimal value with an `x` prefix
from `_meta.credits`, format it as a two-decimal `0.00x` value, and expose it as `multiplier` in
the authorization model payload. Invalid or absent values SHALL be omitted from the displayed
model option.

#### Scenario: A valid credit value is displayed

- **WHEN** WorkBuddy returns `_meta.credits` equal to `x0.29`
- **THEN** the model option displays `0.29x` after the model name
- **AND** the payload still uses the model's `modelId` as its selection value

#### Scenario: Invalid credit metadata is ignored

- **WHEN** WorkBuddy returns a missing, non-numeric, signed, or non-finite credit value
- **THEN** the UI displays no multiplier for that model
- **AND** the raw metadata is not forwarded to the WebView

### Requirement: Distinguish duplicate model names

The settings page SHALL append the model ID to options whose display names are duplicated, while
keeping the model ID as the option value.

#### Scenario: Two models share a display name

- **WHEN** two distinct model IDs have the same name
- **THEN** both options include their IDs after the name and multiplier
- **AND** selecting either option saves only that option's model ID

### Requirement: Do not retain stale connection status

The settings page SHALL clear old ready-state text when the user changes connection mode, and
Authorized mode SHALL not render a ready banner from another connection mode.

#### Scenario: Switch from a local CLI to WorkBuddy

- **WHEN** the user changes the mode to Authorized
- **THEN** the old CLI URL and model are absent from the top status area
- **AND** the top status identifies the WorkBuddy authorization boundary

### Requirement: Show the installed version in the title bar

The title bar SHALL show the current three-part version using the uppercase Chinese digits
`零壹贰叁肆伍陆柒捌玖`, separated by `点`, and SHALL use KaiTi with a serif fallback.

#### Scenario: Display the current patch version

- **WHEN** the installed assembly reports `0.10.1` or `0.10.1.0`
- **THEN** the title bar shows `版本 零点壹零点壹`
- **AND** the version text uses the configured KaiTi font stack

### Requirement: Follow the documented version increments

The project documentation SHALL state that ordinary updates increment `+0.0.1`, major updates
increment `+0.1`, and super-major updates increment `+1`.

#### Scenario: Document the three version increments

- **WHEN** a maintainer prepares a future version update
- **THEN** the documentation distinguishes ordinary, major, and super-major increments as
  `+0.0.1`, `+0.1`, and `+1`
