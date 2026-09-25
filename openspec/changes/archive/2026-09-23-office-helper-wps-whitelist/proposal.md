# Office-helper WPS Writer 安装级白名单

## 背景

WPS Writer 12.8 的 32 位进程在当前机器上读取安装级 `AddinsWL` 白名单；仅写入当前用户白名单时，Word COM 加载项不会进入 `OnConnection`，因此功能区不会出现 Office-helper。

## 方案

安装器保留当前用户登记，并同步写入 WPS 安装级 x64 与 x86 白名单值集合。登记使用 `OfficeHelper.Word.AddIn` 值而不是同名子键，诊断和卸载覆盖三处登记。

## 边界

不改变 Excel/WPS 表格 ProgID、CLSID、工具或 UI；不强制结束正在运行的 WPS 进程。
