## MODIFIED Requirements

### Requirement: 授权状态来自 WorkBuddy ACP

授权模式 SHALL 通过官方独立 CodeBuddy CLI 或 WorkBuddy CLI 的 ACP 检查当前登录态，SHALL NOT 读取、解密或复制凭据文件。宿主拒绝直接创建 CLI 子进程时，ChatSheet SHALL 通过本机 WMI 进程服务代理启动，并继续使用受限的本机管道传输 ACP。

#### Scenario: 独立组件可用

- **WHEN** 官方 Windows 原生 CLI 已安装
- **THEN** 直接启动原生可执行文件，不依赖 WorkBuddy 桌面端或 Node
- **AND THEN** 通过 ACP 获取授权及模型目录，返回结果不含凭据或原始输出

#### Scenario: 安装官方 headless 组件

- **WHEN** 用户在设置页安装独立组件
- **THEN** 下载并校验 `codebuddy-code-headless_Windows_x86_64.zip`
- **AND THEN** 解压 `codebuddy-headless.exe` 并执行官方安装命令
- **AND THEN** 只有检测到版本目录中的 `codebuddy.exe` 时才报告安装成功

#### Scenario: 当前账号已登录

- **WHEN** 官方 CLI 的 ACP session/new 成功
- **THEN** 返回 authorized 状态及安全模型字段，不返回凭据或原始输出

#### Scenario: 当前账号未登录

- **WHEN** ACP 返回 auth_required 或等价的未授权错误
- **THEN** 返回 unauthorized 状态，并提示点击浏览器登录

#### Scenario: WorkBuddy 不可用

- **WHEN** 组件不存在、启动失败或账户尚未授权
- **THEN** 面板显示真实状态以及安装组件或浏览器登录入口
- **AND THEN** 浏览器登录入口不得仅因一次 ACP 探测失败而禁用；点击后 SHALL 重新探测并返回可操作的启动或安装提示

#### Scenario: WPS 拒绝直接创建子进程

- **WHEN** 已验证的官方 CLI 存在，但 WPS 对直接 `CreateProcess` 返回拒绝访问
- **THEN** ChatSheet 通过 WMI 进程服务在宿主外启动同一 CLI
- **AND THEN** 模型探测、浏览器登录和对话继续使用随机的本机命名管道传输
- **AND THEN** 不创建常驻服务，不把凭据或 ACP 原始正文写入日志

#### Scenario: 允许直接创建子进程

- **WHEN** Excel 或测试宿主可正常启动 CLI
- **THEN** 继续使用直接重定向标准流的现有路径
- **AND THEN** 不额外启动 WMI 代理

### Requirement: 浏览器登录与完成刷新

ChatSheet SHALL 提供官方独立组件检测、安装和浏览器登录入口，并在授权完成后刷新账户与模型。登录入口 SHALL 只在登录或安装操作进行期间禁用；ACP 路径尚未探测成功或一次探测失败不得使登录动作不可操作。登录、模型探测和对话 SHALL 为已分类的国内或国际 CLI 使用对应的产品配置目录，且不得混用两个连接模式的账号上下文。WPS 文件视图无法访问官方安装目录时，安装流程 SHALL 提供位于 ChatSheet 本地目录、仍指向同一已验证官方文件的桥接入口。

#### Scenario: 点击登录

- **WHEN** 用户点击浏览器登录且组件存在
- **THEN** 调用当前模式对应的 ACP `authenticate` 使用官方浏览器授权
- **AND THEN** 成功后刷新当前模式的账户、模型目录及适用的签到状态，用户可取消等待
- **AND THEN** 登录期间按钮禁用，操作结束后恢复可操作状态

#### Scenario: 授权页打开反馈与重试

- **WHEN** ACP 发送当前模式的 `_codebuddy.ai/authUrl` 通知
- **THEN** 后端只接受当前模式允许的官方 HTTPS 域名，并尝试交给系统默认浏览器
- **AND THEN** WPS 拒绝直接启动浏览器时通过 WMI 进程服务在宿主外启动 Explorer
- **AND THEN** 面板显示授权页已打开的状态，并提供经过同样安全校验的“重新打开登录页”入口
- **AND THEN** 浏览器启动失败时仍保留该入口和可操作错误，不把登录表现为无响应
- **AND THEN** ChatSheet 不读取、保存或回传 Google/GitHub 密码、验证码或令牌

#### Scenario: 安装组件

- **WHEN** 用户点击安装独立组件
- **THEN** 使用官方 Windows 安装流程下载并校验组件
- **AND THEN** 安装后重新检测；失败或取消不会显示安装成功

#### Scenario: 已有桌面端 ACP 时切换账号

- **WHEN** ACP 路径可用但独立 headless 组件尚未安装
- **THEN** “切换账号”仍保持可点击
- **AND THEN** 登录使用可用的 WorkBuddy CLI 路径，不要求先安装独立组件
- **AND THEN** 启动的 ACP 进程继承该路径对应的产品配置目录

#### Scenario: ACP 探测暂时失败

- **WHEN** 设置页尚未拿到 ACP 路径，或本次探测暂时失败
- **THEN** 登录按钮仍保持可操作（仅在登录/安装操作进行中禁用）
- **AND THEN** 后端 SHALL 在登录动作中重新探测当前模式，而不是静默忽略点击
- **AND THEN** 若重新探测仍失败，返回安装、启动或重试提示，而不是无响应

#### Scenario: 国内与国际配置目录隔离

- **WHEN** 用户分别探测国内版和国际版 WorkBuddy ACP
- **THEN** 每个进程只接收其产品对应的配置目录环境
- **AND THEN** 国内版不得读取国际版目录，国际版不得读取国内版目录
- **AND THEN** 两种模式的模型目录和授权状态保持独立

#### Scenario: WPS 使用组件桥接入口

- **WHEN** 安装器检测到官方版本目录中的 `codebuddy.exe`
- **THEN** 在 ChatSheet 组件目录创建指向该文件的硬链接并验证 PE 头
- **AND THEN** WPS 只通过该入口定位文件，实际授权配置仍使用国际版 `.codebuddy` 目录
- **AND THEN** 删除或更新 ChatSheet 不修改官方版本文件
