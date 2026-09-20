## MODIFIED Requirements

### Requirement: 浏览器登录与完成刷新

ChatSheet SHALL 提供官方独立组件检测、安装和浏览器登录入口，并在授权完成后刷新账户与模型。登录入口 SHALL 只在登录或安装操作进行期间禁用；ACP 路径尚未探测成功或一次探测失败不得使登录动作不可操作。登录、模型探测和对话 SHALL 为已分类的国内或国际 CLI 使用对应的产品配置目录，且不得混用两个连接模式的账号上下文。WPS 文件视图无法访问官方安装目录时，安装流程 SHALL 提供位于 ChatSheet 本地目录、仍指向同一已验证官方文件的桥接入口。

#### Scenario: 点击登录

- **WHEN** 用户点击浏览器登录且组件存在
- **THEN** 先通过当前模式的 ACP 检查并复用已有授权；只有跨进程独占锁内同一来源明确未授权且 `getUserInfo` 无用户时，才调用 `authenticate` 使用官方浏览器授权
- **AND THEN** 成功后刷新当前模式的账户、模型目录及适用的签到状态，用户可取消等待
- **AND THEN** 登录期间按钮禁用，操作结束后恢复可操作状态

#### Scenario: 显式切换账号

- **WHEN** 用户在已授权状态点击“切换账号”
- **THEN** 打开与当前授权来源对应的官方客户端，由用户在官方客户端管理账号；ChatSheet SHALL NOT 为切换账号调用 `authenticate`
- **AND THEN** 普通登录、模型刷新和接入模式切换不得主动登出已登录账号

#### Scenario: 国际版桌面端已有登录态

- **WHEN** 本机安装 WorkBuddyAI 国际版且其 ACP 已授权
- **THEN** 国际版优先复用该桌面端及 `.workbuddy-ai` 配置目录
- **AND THEN** 国内版仅使用国内产品目录；独立国际 CLI 仍使用 `.codebuddy` 作为后备
- **AND THEN** 面板或宿主重新启动后重新通过同一产品 ACP 读取官方组件保留的授权

#### Scenario: 切回授权接入模式

- **WHEN** 用户切换到国内或国际授权模式
- **THEN** 自动读取该模式的授权状态和模型目录，无需再次点击登录
- **AND THEN** 旧模式或账号操作前发出的迟到响应不得覆盖当前状态

#### Scenario: 授权页打开反馈与重试

- **WHEN** ACP 发送当前模式的 `_codebuddy.ai/authUrl` 通知
- **THEN** 后端只接受当前模式允许的官方 HTTPS 域名，首次开窗由官方 CLI 负责，ChatSheet SHALL NOT 收到通知后再自动打开一次
- **AND THEN** 用户手动重开时若 WPS 拒绝直接启动浏览器，通过 WMI 进程服务在宿主外启动 Explorer
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
- **AND THEN** 账号管理打开该 ACP 所属官方客户端，不要求先安装独立组件；仅有独立 CLI 时明确提示到当前来源管理账号
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

#### Scenario: 登录成功但 authenticate 尚未返回

- **WHEN** 浏览器授权完成，而当前 authenticate 请求仍在等待
- **THEN** ChatSheet SHALL 定期通过同一 ACP 来源只读确认授权，确认成功后立即返回该来源的模型目录并解除登录忙碌状态
- **AND THEN** 从官方客户端返回面板时强制刷新当前模式的授权与模型；失败或取消不改动先前授权和模型

#### Scenario: 授权操作结束或收到迟到通知

- **WHEN** 用户取消、操作超时或登录完成
- **THEN** 授权链接立即失效，旧 operationId 的通知、重开与取消不得影响新操作
- **AND THEN** 重开只使用后端当前操作保存的官方链接，不接受前端传入的任意地址
