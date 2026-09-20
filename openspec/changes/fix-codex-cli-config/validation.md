# 验证记录

日期：2026-09-20。修复版本：`0.10.3.10`。

## 原因与官方依据

本机没有 `~/.codex/auth.json`，实际使用 `config.toml` 中 `model_provider = "custom"` 的服务商，协议为 `responses`，地址为 `http://localhost:8080`，模型为 `gpt-6-astra`，直接 bearer token 已配置。原实现先强制读取认证文件，导致在模型请求前就失败；地址读取还只取第一处 `base_url`，协议固定为 Chat Completions，模型没有读取。

- [Codex Authentication](https://learn.chatgpt.com/docs/auth)：用户级认证可以存在于文件、系统凭据库或内存；自定义服务商可以选择单独的认证来源。
- [Custom model providers](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers)：服务商由 `model_provider` 选择，连接地址与认证在对应表中定义。
- [Configuration Reference](https://learn.chatgpt.com/docs/config-file/config-reference#configtoml)：直接 token 为 `experimental_bearer_token`，环境变量为 `env_key`，当前官方默认协议为 `responses`。
- [Tomlyn 0.20.0](https://github.com/xoofx/Tomlyn/tree/0.20.0)：TOML 1.0 解析 API `Toml.ToModel`；官方 NuGet 元数据显示兼容 .NET Standard 2.0，且没有传递依赖，适用于本项目 .NET Framework 4.8。

只检查必要的配置结构，未输出密钥，未修改 Codex 配置或登录状态。未执行 profile、额外认证命令或请求头；这些当前不支持的配置会明确报错。

## 复现与测试

新增配置回归 16 项，旧实现 13 项失败；修复后 16 项全通过，覆盖无认证文件、已选服务商隔离、TOML 引号/注释/内联表、环境变量、既有 API key、订阅认证边界、无认证服务商、协议和路径及错误脱敏。

| 检查 | 结果 |
| --- | --- |
| `dotnet build ChatSheet.sln -c Release --nologo` | 通过，0 警告、0 错误，版本 0.10.3.10 |
| `ChatSheet.ToolTests.exe --codex-config-tests` | 16 项通过 |
| `ChatSheet.ToolTests.exe --codex-config-live` | 实际取得 9 个模型，包含配置模型，探测与连接结果一致 |
| x86 PowerShell 加载相同程序集及 Tomlyn 后调用配置回归与实机获取 | 通过，16 项回归与 9 个模型读取正常 |
| `node tests/web/settings-switch.test.mjs` | 30 项通过 |
| `node tests/web/version.test.mjs` | 7 项通过 |
| 修改文件 UTF-8 严格解码、异常字符检查、中文人工复查 | 通过 |
| `git diff --check`（保留 Windows CRLF） | 相关文件通过 |
| `openspec validate fix-codex-cli-config --strict --no-interactive` | 通过 |

实机仅请求现有服务的模型目录，没有发送模型对话。安装前后将核对 ChatSheet 设置和 Codex 配置的 SHA256；临时验证记录位于 `artifacts/codex-config-0.10.3.10/`。

## 安装状态

构建产物已就绪，包括新增 `Tomlyn.dll`。WPS 正占用当前 0.10.3.9 DLL，待用户保存并退出后安装。
