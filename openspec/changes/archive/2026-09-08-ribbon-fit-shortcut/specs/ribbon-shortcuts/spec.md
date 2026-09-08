## Purpose

为 ChatSheet 在宿主功能区中提供可逐步扩展的常用操作快捷区，让用户无需先在右侧面板中寻找入口，也能直接触发已有的确定性表格动作，同时保持面板中的结果反馈和撤销能力一致。

## ADDED Requirements

### Requirement: Fit current sheet is available after settings and diagnostics

The ChatSheet ribbon tab SHALL expose a shortcut labelled “适配当前表” in the same group as the existing panel, settings and diagnostics controls. The shortcut SHALL appear after “设置” and “诊断”, establishing that trailing area as the ribbon's shortcut region. The existing fit control inside the panel SHALL remain available.

#### Scenario: User opens the ChatSheet ribbon tab

- **WHEN** the host renders the ChatSheet ribbon tab
- **THEN** it shows “设置”, followed by “诊断”, followed by “适配当前表”
- **AND THEN** the panel's existing fit control is still available

### Requirement: The ribbon shortcut uses the existing fit workflow

Activating “适配当前表” from the ribbon SHALL show or keep open the ChatSheet panel, navigate it to the conversation view, and trigger the same fit action as the panel's fit control. The action SHALL retain the panel's current horizontal alignment choice, defaulting to centre when no choice has been made. Its progress, result, failure and undo availability SHALL use the existing manual-operation card workflow rather than a separate execution or notification path.

#### Scenario: User activates the shortcut while the panel is ready

- **WHEN** the user activates “适配当前表” and the panel is ready
- **THEN** the panel is visible on the conversation view
- **AND THEN** one fit action starts with the panel's current alignment choice
- **AND THEN** the transcript shows its existing manual-operation card

#### Scenario: Fit cannot be performed

- **WHEN** the shortcut-triggered fit fails
- **THEN** the failure is shown on the same manual-operation card used by the panel fit control
- **AND THEN** no separate ribbon-only result path is introduced

### Requirement: A cold-start shortcut activation is not lost

If the panel or its embedded page is still loading when the ribbon shortcut is activated, ChatSheet SHALL retain the activation until the page has completed loading and then dispatch one fit action. It SHALL NOT silently discard the activation or execute it more than once.

#### Scenario: User activates the shortcut before the panel page loads

- **WHEN** the user activates “适配当前表” before the panel page has completed loading
- **THEN** ChatSheet opens the panel and waits for the page to become ready
- **AND THEN** exactly one fit action starts after the page is ready

