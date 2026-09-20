# workbuddy-authorization Specification

## Purpose
让 ChatSheet 通过 WorkBuddy 自己维护的当前登录态获取模型、发送对话和测试模型，
同时保持授权凭据留在 WorkBuddy 内，表格工具继续经过 ChatSheet 的审批与执行链路。

## Requirements

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

### Requirement: 模型目录使用 ACP 返回值

授权模式的 `models.list` SHALL 返回当前账号 ACP 会话中的模型目录，不得硬编码模型名单。
每个条目 SHALL 使用 `modelId` 作为稳定选择值，并可携带非敏感的名称与能力元数据。

#### Scenario: 读取多个可用模型

- **WHEN** ACP 返回 `models.availableModels`
- **THEN** 面板获得每个模型的 ID 和显示名称
- **AND THEN** 选择模型时保存 ID 而不是显示名称

#### Scenario: 模型目录为空

- **WHEN** 授权成功但 ACP 返回空模型目录
- **THEN** 通道返回授权成功状态和空目录
- **AND THEN** 面板允许重试并明确显示没有可选择模型

### Requirement: 授权模式不进入普通 API 对话链路

授权模式 SHALL 使用 WorkBuddy CLI ACP 完成对话和模型探测，而 SHALL NOT 将空 API 地址或 WorkBuddy 登录态交给普通 HTTP `ChatClient`。

#### Scenario: 用户尝试发送授权模式对话

- **WHEN** 当前模式为 `Authorized` 且用户发送消息
- **THEN** 通过 ACP 初始化并创建会话，选择用户保存的模型后发送 prompt
- **AND THEN** 将流式正文、思考和完成状态返回面板
- **AND THEN** 不再返回 `AUTHORIZED_CHAT_NOT_SUPPORTED`

#### Scenario: 表格工具续传

- **WHEN** 模型输出 ChatSheet 文本工具调用
- **THEN** 现有工具审批、执行和撤销链路处理调用
- **AND THEN** 工具结果发送到同一 ACP 会话并继续接收回答
- **AND THEN** 不重复发送先前助手输出

#### Scenario: 用户取消或请求超时

- **WHEN** 用户停止对话或 ACP 请求达到截止时间
- **THEN** 结束当前请求并释放本次创建的 CLI 进程
- **AND THEN** 用户取消和超时可区分，超时返回可操作的中文提示

#### Scenario: 授权模型探测

- **WHEN** 用户测试授权模式的模型
- **THEN** 使用 ACP 发送小型探测请求并根据流式结果判定可用性
- **AND THEN** 不创建普通 HTTP 请求

#### Scenario: 服务端请求额外工具权限

- **WHEN** ACP 服务端发出权限请求
- **THEN** 返回取消结果且不执行 WorkBuddy 工具
- **AND THEN** 不因字符串请求 ID 导致协议解析失败

### Requirement: 授权凭据不进入 ChatSheet 存储

授权模式 SHALL 不使用 ChatSheet 的自定义 API `SecretStore` 键保存 WorkBuddy token，
且明文 `settings.json` SHALL 不新增 WorkBuddy token 字段。

#### Scenario: 保存授权设置

- **WHEN** 用户保存授权模式与已选模型
- **THEN** 设置中只保存模式、模型 ID 和普通行为配置
- **AND THEN** WorkBuddy token 不出现在保存 payload、`settings.json` 或 ChatSheet secret store

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

### Requirement: 国际版 WorkBuddy 授权

系统 SHALL 提供 `AuthorizedInternational` 模式。该模式 SHALL 使用 ACP `authenticate` 的 `external` 方法，并只打开官方 `codebuddy.ai` 或 `workbuddy.ai` HTTPS 授权地址。国际版模型目录、收藏和普通授权模式 SHALL 按连接键隔离。

#### Scenario: 国际版登录

- **WHEN** 用户选择国际版模式并点击浏览器登录
- **THEN** 使用 Google/GitHub 的 `external` 授权方式
- **AND THEN** 授权完成后刷新国际版模型目录

#### Scenario: 国际版不调用国内签到

- **WHEN** 当前模式为 `AuthorizedInternational`
- **THEN** 不请求 `copilot.tencent.com` 的国内签到接口
- **AND THEN** 面板显示国际版暂不支持该签到服务

### Requirement: 每个账户每日自动签到

ChatSheet SHALL 按账号和北京时间日期查询签到；只有接口明确表示活动有效且今日未签到时才自动领取，随后重新查询确认。SHALL NOT 伪造签到成功或设备验证。只有已签到或活动关闭的确定结果 SHALL 作为同日完成缓存；面板打开期间，未知、未完成、未登录或请求暂时失败 SHALL 在五分钟后自动重新检查。

#### Scenario: 今日已经签到

- **WHEN** 官方接口确认今日已签到
- **THEN** 前端显示今日已签到并复用当天缓存
- **AND THEN** 不提交领取请求，不再自动进行同日重查

#### Scenario: 今日尚未签到

- **WHEN** 官方接口明确返回活动有效且今日未签到
- **THEN** 在跨进程互斥下记录尝试并提交最多一次领取
- **AND THEN** 回查确认成功后显示今日已签到

#### Scenario: 请求结果不确定

- **WHEN** 查询失败、字段未知、设备校验被拒绝或提交超时
- **THEN** 显示无法确认或未完成状态，允许手动回查，并在面板打开期间五分钟后自动重查
- **AND THEN** 同日不重复提交已尝试的领取请求

#### Scenario: 首次查询失败后恢复

- **WHEN** 当天首次请求失败且未提交领取，稍后官方服务或账号恢复可用
- **THEN** 普通自动刷新不得直接返回旧的未知缓存
- **AND THEN** 活动有效且尚未签到时完成领取与回查，多个面板恢复时仍只领取一次
- **AND THEN** 首次消息桥请求失败且尚无显示状态时，仍安排五分钟后的自动重查

#### Scenario: 重查与模式生命周期

- **WHEN** 自动重查到期或等待中的重查返回
- **THEN** 使用原请求的国内模式，不叠加尚未结束的请求
- **AND THEN** 切出国内模式或清空状态后，旧响应不得恢复状态或重新安排重查
- **AND THEN** 活动关闭、不支持或非国内模式的空结果不安排同日重查

#### Scenario: 日期或账号变化

- **WHEN** 北京时间跨日或登录了其他账号
- **THEN** 按新账号及日期重新检查，不复用旧账号或前一天状态
- **AND THEN** 面板关闭时不创建常驻后台签到任务

### Requirement: 签到身份只用于官方服务

签到 SHALL 仅在后端内存中使用官方 ACP 返回的身份访问固定的官方 HTTPS 服务，且 SHALL NOT 将凭据存入缓存、日志或面板。

#### Scenario: 保存每日状态

- **WHEN** 签到查询完成
- **THEN** 缓存仅包含账号指纹、北京时间日期、尝试标识和安全状态
- **AND THEN** 不保存 accessToken 或原始账号响应

### Requirement: 模式切换复用官方登录并保留暂时失败前的模型

国内版与国际版切换 SHALL 通过对应官方 ACP 读取已有登录；成功授权可短时复用，失败或未授权结果 SHALL NOT 作为成功缓存复用。暂时不可用 SHALL NOT 清除已属于当前连接的模型；实际发送仍 SHALL 通过当前官方授权校验。

#### Scenario: 本机两种 WorkBuddy 均已登录

- **WHEN** 用户切换国内版和国际版
- **THEN** 分别取得对应账号的模型目录，无需点击获取或重新登录
- **AND THEN** 模式切换不调用 authenticate，不保存 WorkBuddy 令牌

#### Scenario: 短暂不可用后恢复

- **WHEN** 一次 ACP 探测不可用，随后用户切回该模式或刷新
- **THEN** 重新读取真实状态，不直接沿用失败缓存
- **AND THEN** 暂时不可用期间保留该连接的已选模型，明确未授权时清除

### Requirement: 国际桌面 ACP 继承产品模型上下文

检测到 WorkBuddyAI 桌面端时，国际授权模式 SHALL 优先使用其 CLI。当该产品配置目录具有会话产品快照时，模型查询和对话 SHALL 通过官方 `CODEBUDDY_HOST=workbuddy-desktop` 与 `ACC_PRODUCT_CONFIG_PATH` 传递最新快照；SHALL 继续使用 ACP 返回的模型目录与倍率，不硬编码名单。

#### Scenario: 桌面端有完整模型目录

- **WHEN** WorkBuddyAI 存在会话产品快照
- **THEN** 直接启动和 WPS 代理启动均传递最新快照路径及桌面宿主标识
- **AND THEN** 查询与对话使用相同产品上下文，保留 ACP 返回的零倍率模型

#### Scenario: 快照目录不可用

- **WHEN** 桌面快照目录不存在或无法读取
- **THEN** 保留原有 ACP 启动和真实目录结果，不伪造完整名单

#### Scenario: 独立国际组件

- **WHEN** 当前来源是独立 CodeBuddy CLI
- **THEN** 不为它注入 WorkBuddyAI 会话快照
