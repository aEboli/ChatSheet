# 增量规范：WPS Writer 加载项禁用清理

## MODIFIED Requirements

### Requirement: WPS Writer 加载项登记覆盖实际白名单

安装器 SHALL 将 Word 加载项 ProgID 写入当前用户的 WPS Writer `AddinsWL` 白名单值集合，并在目标 WPS 安装使用安装级白名单时同步写入 64 位和 32 位 `HKLM` 视图。登记 SHALL 使用 ProgID 值而不是同名子键；登记前 SHALL 从用户级和安装级 x86/x64 `AddinsCL`/`AddinsBL` 清单移除 `OfficeHelper.Word.AddIn` 及兼容的 `.1` 值；诊断 SHALL 分别报告白名单和禁用清单状态，卸载 SHALL 清理上述登记。

#### Scenario: 32 位 WPS Writer 使用安装级白名单

- **WHEN** WPS Writer 进程为 32 位并读取 `HKLM\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsWL`
- **THEN** 安装器 SHALL 在该键写入 `OfficeHelper.Word.AddIn` 值
- **AND THEN** 重新启动 WPS Writer 后加载项 SHALL 能进入 COM 加载阶段

#### Scenario: WPS 残留禁用项

- **WHEN** `OfficeHelper.Word.AddIn` 存在于用户级或安装级 `AddinsCL`/`AddinsBL`
- **THEN** 安装器 SHALL 只移除该 ProgID 及其 `.1` 兼容值
- **AND THEN** 其他加载项的禁用值 SHALL 保持不变

#### Scenario: 双击打开文档

- **WHEN** 用户完整退出 WPS 后通过文件关联打开 `.docx`
- **THEN** WPS SHALL 进入 `OfficeHelper.Word.AddIn` 的 COM `OnConnection`
- **AND THEN** 日志 SHALL 出现 Ribbon 加载和侧边栏创建记录

#### Scenario: 诊断阻止状态

- **WHEN** 用户运行安装器诊断
- **THEN** 输出 SHALL 分别显示用户级和 x86/x64 安装级 WPS 禁用清单是否仍包含 Office-helper
