# WPS Writer 白名单增量规范

## Requirement: 安装级白名单登记

安装器 SHALL 将 `OfficeHelper.Word.AddIn` 写入以下 ProgID 值集合，并清理同名子键：

- `HKCU\\Software\\Kingsoft\\Office\\WPS\\AddinsWL`
- `HKLM\\SOFTWARE\\Kingsoft\\Office\\WPS\\AddinsWL`
- `HKLM\\SOFTWARE\\WOW6432Node\\Kingsoft\\Office\\WPS\\AddinsWL`

#### Scenario: 32 位 WPS Writer 重启

- **WHEN** 用户在安装后完全退出并重新打开 WPS Writer
- **THEN** WPS SHALL 有机会实例化 `OfficeHelper.Word.AddIn`
- **AND THEN** 若未实例化，诊断 SHALL 报告未进入 COM 加载阶段，而不是假报 Ribbon 成功
