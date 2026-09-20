# 增量规范：WorkBuddy 独立组件安装与国际版

## Requirement: 独立组件使用官方 headless 包

安装 SHALL 使用官方版本清单和 SHA256 校验，并下载 `codebuddy-code-headless_Windows_x86_64.zip`；压缩包入口 SHALL 为 `codebuddy-headless.exe`，安装后 SHALL 通过官方版本目录中的 `codebuddy.exe` 重新检测。

### Scenario: 当前官方包可安装

- **WHEN** 用户点击安装且系统为 64 位 Windows
- **THEN** 下载并校验 headless 包
- **AND THEN** 执行 `codebuddy-headless.exe install <version>`
- **AND THEN** 只有重新检测到真实版本入口时才报告安装成功

## Requirement: ACP 可用即可登录

设置页 SHALL 按 ACP 路径返回的 `canLogin` 判断浏览器登录/切换账号按钮；不能因为 `standalone` 为 false 而禁用按钮。登录 SHALL 使用所有可用 ACP 路径中的首选路径。

### Scenario: 桌面端 ACP 已存在

- **WHEN** `available=true` 且 `standalone=false`
- **THEN** 登录/切换账号按钮可点击
- **AND THEN** 登录请求不会要求先安装独立组件

## Requirement: 国际版授权

系统 SHALL 提供 `AuthorizedInternational` 模式。该模式 SHALL 使用 ACP `authenticate` 的 `external` 方法，并允许官方 `codebuddy.ai`/`workbuddy.ai` HTTPS 授权地址；模型目录和收藏 SHALL 与国内授权模式隔离。

### Scenario: 国际版浏览器授权

- **WHEN** 当前模式为 `AuthorizedInternational` 且用户点击登录
- **THEN** 打开 ACP 返回的 Google/GitHub 授权地址
- **AND THEN** 授权完成后刷新国际版模型目录

### Scenario: 国际版签到

- **WHEN** 当前模式为 `AuthorizedInternational`
- **THEN** 不调用国内 `copilot.tencent.com` 每日签到接口
- **AND THEN** 面板显示当前账号暂不支持该签到功能或隐藏签到操作
