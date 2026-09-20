# 修复 Codex CLI 配置无法读取

## Why

用户在 0.10.3.9 选择 Codex CLI 时提示缺少 `auth.json`。本机实际通过 `config.toml` 的已选自定义服务商配置 Responses 地址、模型与直接 bearer token，官方允许这种配置不依赖 `auth.json`。现有实现提前要求认证文件，还会读取第一个 `base_url`、固定使用 Chat Completions 且忽略配置模型。

## What Changes

- 修正既有本机 CLI 读取：先解析 Codex 用户级配置中的已选服务商，再读取其认证来源。
- 读取对应地址、协议和模型，支持配置中的直接 bearer token、指定环境变量，以及既有 `auth.json` API key；尊重 `CODEX_HOME`。
- 配置错误和订阅认证不可用时显示明确原因，不输出凭据、不执行凭据命令。
- 复现用户配置，验证实际模型获取，递增修复版本并更新本地安装。

## Capabilities

### Modified Capabilities

- `connection-mode-refresh`：Codex 配置探测与模型获取读取同一有效连接。

## Impact

仅涉及本机 Codex 配置读取、定向回归、依赖和版本文档。不修改 Codex 本机配置或登录状态。采用支持 .NET Framework 4.8 的 Tomlyn 0.20.0 解析 TOML，避免多服务商与引号被简单正则误配。
