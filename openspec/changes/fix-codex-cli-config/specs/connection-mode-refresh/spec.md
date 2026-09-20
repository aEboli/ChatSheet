## ADDED Requirements

### Requirement: Codex 探测使用已选服务商配置

Codex CLI 模式 SHALL 读取 `CODEX_HOME`（缺省为用户目录的 `.codex`）中的用户级 `config.toml`，根据 `model_provider` 选择对应配置，读取该服务商的地址、协议和模型。探测和实际连接 SHALL 使用相同解析结果；有效的自定义服务商配置 SHALL NOT 因缺少 `auth.json` 被判定为不存在。

#### Scenario: 只有 TOML 自定义服务商配置

- **WHEN** 用户配置了自定义服务商地址、`wire_api = "responses"`、`experimental_bearer_token` 和模型，但没有 `auth.json`
- **THEN** Codex 来源可用，按已选服务商地址获取模型，并沿用配置模型
- **AND THEN** 不复制或输出令牌，不要求创建认证文件

#### Scenario: 多服务商与协议地址

- **WHEN** TOML 包含多个服务商且当前选择不是第一项
- **THEN** 只使用所选服务商的地址和凭据，Responses 使用对应协议
- **AND THEN** 保留配置的 API 根路径，不擅自添加 `/v1`

#### Scenario: 认证来源与无效配置

- **WHEN** 所选服务商使用环境变量或 OpenAI API key 认证
- **THEN** 从指定环境变量或既有 `auth.json` 的 `OPENAI_API_KEY` 读取，缺失时给出明确原因
- **AND THEN** 不将 ChatGPT 订阅令牌当作普通 API key，不自动执行外部认证命令，不在错误中包含原始配置与密钥
