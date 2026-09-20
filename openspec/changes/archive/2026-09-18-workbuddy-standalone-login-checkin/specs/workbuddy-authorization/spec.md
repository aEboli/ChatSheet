## MODIFIED Requirements

### Requirement: 授权状态来自 WorkBuddy ACP

授权模式 SHALL 通过官方独立 CodeBuddy CLI 或 WorkBuddy CLI 的 ACP 检查当前登录态，SHALL NOT 读取、解密或复制凭据文件。

#### Scenario: 独立组件可用

- **WHEN** 官方 Windows 原生 CLI 已安装
- **THEN** 直接启动原生可执行文件，不依赖 WorkBuddy 桌面端或 Node
- **AND THEN** 通过 ACP 获取授权及模型目录，返回结果不含凭据或原始输出

#### Scenario: 当前账号已登录

- **WHEN** 官方 CLI 的 ACP session/new 成功
- **THEN** 返回 authorized 状态及安全模型字段，不返回凭据或原始输出

#### Scenario: 当前账号未登录

- **WHEN** ACP 返回 auth_required 或等价的未授权错误
- **THEN** 返回 unauthorized 状态，并提示点击浏览器登录

#### Scenario: WorkBuddy 不可用

- **WHEN** 组件不存在、启动失败或账户尚未授权
- **THEN** 面板显示真实状态以及安装组件或浏览器登录入口

## ADDED Requirements

### Requirement: 浏览器登录与完成刷新

ChatSheet SHALL 提供官方独立组件检测、安装和浏览器登录入口，并在授权完成后刷新账户与模型。

#### Scenario: 点击登录

- **WHEN** 用户点击浏览器登录且组件存在
- **THEN** 调用 ACP authenticate 使用官方浏览器授权
- **AND THEN** 成功后刷新模型目录及签到状态，用户可取消等待

#### Scenario: 安装组件

- **WHEN** 用户点击安装独立组件
- **THEN** 使用官方 Windows 安装流程下载并校验组件
- **AND THEN** 安装后重新检测；失败或取消不会显示安装成功

### Requirement: 每个账户每日自动签到

ChatSheet SHALL 按账号和北京时间日期查询签到；只有接口明确表示活动有效且今日未签到时才自动领取，随后重新查询确认。SHALL NOT 伪造签到成功或设备验证。

#### Scenario: 今日已经签到

- **WHEN** 官方接口确认今日已签到
- **THEN** 前端显示今日已签到并复用当天缓存
- **AND THEN** 不提交领取请求

#### Scenario: 今日尚未签到

- **WHEN** 官方接口明确返回活动有效且今日未签到
- **THEN** 在跨进程互斥下记录尝试并提交最多一次领取
- **AND THEN** 回查确认成功后显示今日已签到

#### Scenario: 请求结果不确定

- **WHEN** 查询失败、字段未知、设备校验被拒绝或提交超时
- **THEN** 显示无法确认或未完成状态，允许手动回查
- **AND THEN** 同日不重复提交已尝试的领取请求

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
