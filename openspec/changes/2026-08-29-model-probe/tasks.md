# 任务：按需确认一个模型能不能用（二期）

前提缺陷的证据与取舍见 `proposal.md`；一期已完成的部分见
`openspec/changes/2026-08-29-model-availability/`。

## 前提一：思考参数可整段不传

- [x] `Providers/ChatTypes.cs`：`ChatRequest` 加 `SuppressThinking`（默认 false）
- [x] `Providers/RequestBuilder.cs` 四个协议都跳过思考段：Chat Completions 的
      `reasoning_effort`（`:107-113`）、Responses 的 `reasoning`（`:236-240`）、
      Anthropic 的 `thinking`（`:423-426`）、Gemini 的 `thinkingConfig`（`:568-580`）
- [x] 不是设关闭值而是不写这个键。`Thinking.OpenAiEffort(Off)` 返回的是字符串
      `"none"`（`Thinking.cs:85`），那仍然是一个值，只认 low/medium/high 的网关会 400
- [x] 断言：`SuppressThinking` 时四个协议的请求体里都不出现思考相关键；
      不设时逐字与今天一致（照 `ThinkingTests` 的写法）

## 前提二：输出上限的字段名按证据选

- [x] `Providers/ModelCapabilities.cs`：`ModelCapability` 加一项记住该模型要用
      `max_tokens` 还是 `max_completion_tokens`（三态：未知/前者/后者）
- [x] `Providers/RequestBuilder.cs`：Chat Completions 按档写字段名（`:99` 现在写死
      `max_tokens`，全仓无 `max_completion_tokens`）
- [x] `Providers/CapabilitySignals.cs`：认「错误在说输出上限字段不对」
      （`max_tokens`、`max_completion_tokens`、`unsupported parameter` 的组合）
- [x] `Agent/AgentRunner.cs` 的 catch 链加一条回退：换字段名重跑该步且不计步数，
      与既有的工具/视觉回退同形状；同样排在「点名模型」判据之后
- [x] 判别不许靠模型名。`ModelCapabilities.cs:32-37` 的注释已明确反对按名字猜——
      换网关后立刻错
- [x] 反例断言：从未被拒过的模型用哪个字段与模型名无关

## 前提三：探测请求的形态

- [x] `Providers/ModelProbe.cs`：一条固定的极短 user 消息，不带 system
- [x] 不带工具（`IncludeTools = false`）、不带图片、`SuppressThinking = true`、
      输出上限压到最小但留够
- [x] 断言：Anthropic 与 Gemini 的探测请求体里 `messages`/`contents` 非空。
      `BuildAnthropic` 把 System 抽到顶层（`RequestBuilder.cs:280-287`、`:330-338`），
      只带系统提示会产出空数组并被 400

## 前提四：复用流式通路

- [x] `Providers/StreamParsers.cs` 的 `OpenAiChatStreamParser` 补一条 error 分支：
      它现在只读 usage 与 choices，缺 choices 就 `yield break`（`:102-104`），
      而 Chat Completions 是默认协议（`Protocols.cs:75`）
- [x] 探测走 `StreamAsync`，不新写非流式读取——三个协议的体内错误识别已经现成
      （`StreamParsers.cs:262`、`StreamParsers.Anthropic.cs:141`、`:165`）
- [x] 「200 但一个事件都没收到」判未知，不判可用
- [x] 断言：体内错误点名模型时判不可用；零事件时判未知

## 基础设施：重试、超时、取消

- [x] `ChatClient.StreamAsync` 加 `maxRetries` 参数（现在硬编码
      `RetryPolicy.MaxRetries`，`:123`），探测传 0。`TotalBackoff` 是 23 秒，
      每个模型等 23 秒与「秒级确认」矛盾
- [x] 探测独立的短截止时间（15 秒，不含退避）。`HttpClient.Timeout` 是
      `Timeout.InfiniteTimeSpan`（`:39-44`），不给截止时间等于没有超时
- [x] linked CTS 区分「我方超时」与「用户取消」：两者现在都抛裸的
      `OperationCanceledException`（`:195-198`），没有 Code。我方超时包装成带 Code 的
      `ProviderException` 以便判未知；用户取消不记任何判定
- [x] 断言：超时判未知；取消不写判定

## 基础设施：并发与互斥

- [x] 探测单飞：进行中再点则排队，不并发
- [x] 对话在飞时拒绝探测并说明原因。`SendAsync` 的 `BUSY` 守卫只在它自己内部
      （`AgentChannels.cs:587-590`），新通道继承不到
- [x] 断言：第二次探测在第一次结束后才发；对话在飞时探测被拒且没有请求发出

## 批量确认

- [x] `models.probe.bulk`：只对名单，串行，逐个推进度
- [x] 新增一种 push kind 用于批量进度（现在只有 `models-retry` 与
      `approval-request`，`AgentChannels.cs:537`、`:758`）
- [x] 独立的取消源与 `models.probe.stop` 通道。**不得**复用 `Stop()`——
      它 Cancel 的是 `_currentRun` 这一个对话槽位（`:554-571`）
- [x] 中断后已得结果保留
- [x] 断言：停批量不取消对话；停对话不取消批量（双向反例）
- [x] 不对完整目录提供批量

## 连接归属

- [x] 探测只走已保存设置，不接受未保存配置。`ListModelsAsync` 刻意接受未保存值
      让设置页试连（`AgentChannels.cs:481-520`），照抄会把候选网关的结论盖到
      当前连接上
- [x] 设置页不提供探测入口，只保留「获取模型列表」

## 面板

- [x] 行内「试一下」：只对没有判定的模型显示，避免每行都挂一个按钮
- [x] 「正在确认」是独立显示态，与三态都不同
- [x] 按钮不能是 button 套 button——`.picker-item` 是 `<button>`。放 `.picker-row`
      容器里作兄弟节点，与一期的星标同一处
- [x] 名单区的「全部确认」入口与进度、停止按钮
- [x] 对话在飞时按钮禁用并说明原因，不是点了没反应
- [x] `describePicker` 报出正在确认的模型与批量进度
- [x] 「正在确认」的样式两套主题一起加，只走调色板变量

## 测试

- [x] `tests/ChatSheet.ToolTests/ProbeTests.cs`，并在 `Program.cs` 加一行 Run
- [x] 四协议请求体断言：`SuppressThinking` 生效、消息非空、不带工具
- [x] 输出上限字段回退的双向断言
- [x] 超时与取消的区分
- [x] 单飞与互斥
- [x] 批量的中断与结果保留
- [x] `tests/web/model-probe.test.mjs`：正在确认态、按钮只对未确认的模型出现、
      对话在飞时禁用、批量进度
- [x] 假 DOM 照 `capability-fallback.test.mjs`，并先变异一次确认断言会红
- [x] `tests/mock-provider/server.mjs` 加按模型名分流的场景：某些 404、某些 403
      点名模型、某些 403 只说密钥、某些 429、某些 200 但体内含错误、某些挂住不答
- [x] `scripts/verify-chat-e2e.ps1` 的 ValidateSet 与 settings 字典跟上
- [x] `scripts/verify-picker.ps1`：真实宿主里点一次「试一下」并看到结论

## 文档

- [x] README 的「找出哪几个模型真能用」那一节补上按需确认
- [x] README「可用性判定的边界」改口径：不再是「从没发过一轮的一律未确认」
- [x] `docs/changes/2026-08-29-model-probe.md`
- [ ] 归档两个变更目录（一期与本期）

## 验证结果

- 构建 Debug 与 Release 均 0 错误。
- C# `ProbeTests.cs` 28 条通过；整套 524 条通过（一期结束时 494 条）。
- 面板 `model-probe.test.mjs` 26 条通过；`tests/web` 20 个文件全通过。
- `scripts/verify-picker.ps1` 37 条通过（一期结束时 18 条），真实 Excel 宿主。
  六种模型各自确认出预期结论：正常→可用、404 点名模型→不可用、
  200 体内含错误→不可用、429→未确认、403 只说密钥→未确认、200 零事件→未确认。
- 两套主题：新规则用到的 10 个调色板变量浅色深色都有定义，无硬编码颜色（静态核对）。
  本环境无交互桌面，**未做目视截图确认**。

### 变异验证

1. **端到端抓到一个单测全绿时漏掉的真 bug**：体内错误抛的是 `STREAM_ERROR`，
   不是 `HTTP_4xx`，而 `Classify` 先用 `IsClientError` 卡了一道，于是「别名模型」
   这种最典型的不可用情形恒判未知。单测当时全绿，是 `verify-picker` 的
   `mock-aliasbroken` 场景把它照出来的。已修并补上两条单测。
2. **去掉 Chat Completions 的 SuppressThinking 判断** → 对应断言立即转红，恢复后回绿。
3. **面板测试的假 DOM 一开始回复形状写错了**（用了 `payload` 而不是 `data`，
   缺 `kind: 'response'`），于是 `request()` 永远拿不到内容、收尾不跑，
   5 条断言转红。这反过来说明那 5 条确实在验真东西。顺带发现一期的
   `model-favorites.test.mjs` 有同样的harness 缺陷——修正后 27 条依然全绿，
   说明那些断言不依赖回复到达。
4. 两个面板测试文件都常驻一段「清空模型列后断言必须转红」的自检。
