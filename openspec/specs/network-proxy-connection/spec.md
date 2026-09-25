# network-proxy-connection Specification

## Purpose
为 Excel 与 Word 共用的 Office-helper 面板提供清晰独立的代理设置入口，并保证代理配置在网络请求、凭据保护和目录缓存中的行为一致。

## Requirements

### Requirement: 独立代理页面

面板 SHALL 在应用栏“设置”入口之后、主题切换入口之前提供“代理”入口。该入口 SHALL 打开独立代理页面；通用设置页 SHALL 不再呈现代理配置。

#### Scenario: 打开代理页面

- **WHEN** 用户点击应用栏中的“代理”入口
- **THEN** 面板 SHALL 显示多代理配置、连接测试和代理保存操作
- **AND** 应用栏入口顺序 SHALL 为“设置”、“代理”、主题切换

#### Scenario: 保存代理设置

- **WHEN** 用户保存代理页中的配置
- **THEN** 宿主 SHALL 保存代理配置和代理密码
- **AND** 当前接入模式与已选模型 SHALL 保持不变

### Requirement: 代理配置

独立代理页面 SHALL 提供直连、系统代理、HTTP、HTTPS 和 SOCKS5 五种方式。选择 HTTP、HTTPS 或 SOCKS5 时 SHALL 要求有效主机和 1-65535 端口，并允许填写可选用户名和密码。

独立代理页面 SHALL 支持保存多套带名称的代理配置，允许选择当前配置；HTTP、HTTPS 和 SOCKS5 的地址与端口 SHALL 在同一行显示。页面 SHALL 提供“连接失败时自动切换到其他代理”开关，默认开启。

#### Scenario: 切换代理配置

- **WHEN** 用户选择另一套已保存代理配置
- **THEN** 当前模型目录、测试请求和后续对话 SHALL 使用该配置
- **AND** 配置密码 SHALL 只从该配置对应的 DPAPI 密钥槽读取

#### Scenario: 默认直连

- **WHEN** 用户未配置代理或选择“直连”
- **THEN** 请求 SHALL 禁用代理，不读取系统代理地址

#### Scenario: 系统代理

- **WHEN** 用户选择“系统代理”
- **THEN** 请求 SHALL 使用操作系统默认代理设置

#### Scenario: 代理测试

- **WHEN** 用户点击“测试代理连接”
- **THEN** 宿主 SHALL 通过当前未保存的代理字段执行一次有限时长 HTTPS 测试
- **AND** 页面 SHALL 显示成功或可读的失败原因

#### Scenario: 自动切换

- **WHEN** 当前代理请求在尚未交付任何流式事件前出现网络错误、限流或 5xx 瞬时故障，且自动切换已开启
- **THEN** 客户端 SHALL 按配置列表顺序尝试其他代理配置
- **AND** 已经交付过流式事件的请求 SHALL NOT 因切换而重复发送

### Requirement: 凭据保护

代理密码 SHALL 使用 DPAPI 加密存储，设置 JSON、面板消息回包、日志和模型目录键 SHALL NOT 包含密码原文。

#### Scenario: 代理密码持久化

- **WHEN** 用户保存或清除某套代理配置的密码
- **THEN** 密码 SHALL 存入或从对应的加密存储槽删除
- **AND** 明文密码 SHALL NOT 写入设置文件或日志

### Requirement: 请求一致性

模型列表、对话、模型能力探测和视觉中转 SHALL 使用同一份已解析代理配置。代理类型、地址、端口和用户名变化 SHALL 使模型目录缓存键变化。

#### Scenario: 代理变化刷新模型目录身份

- **WHEN** 代理类型、地址、端口或用户名发生变化
- **THEN** 模型目录 SHALL 使用新的连接身份
- **AND** 旧代理下的目录 SHALL NOT 被当作当前代理的目录复用
