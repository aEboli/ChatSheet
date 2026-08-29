# 2026-08-29：按需确认一个模型能不能用

没有判定的模型行末多了个「试一下」，点一次花一条最小请求就知道结论。名单可以一次
确认完，带进度、可中断、已得结果保留。

## 问题

一期把可用性判定从真实对话里白拿了下来，但它只覆盖用户已经发过一轮的模型。
从没用过的一律显示「未确认」——而「我想知道这个没用过的能不能用」恰恰是最初的问题。

补上这个按钮看起来只是加个入口，实际不是。审计在这条通路上找出四个会让**绿灯骗人**
的前提缺陷，以及三样缺失的基础设施。本次先修它们，再加按钮。

## 探测请求必须是真实请求的真子集

只准去掉字段，不准换值。换了值就有两种误判，而其中一种是主动骗人。

### 「关掉思考」不是「不传思考参数」

`ChatRequest.Thinking = Off` 会实际发出一个关闭值：OpenAI 系发
`reasoning_effort: "none"`（`Thinking.cs:85` → `RequestBuilder.cs:112`），
Gemini 发 `thinkingConfig.thinkingBudget = 0`，Anthropic 发 `thinking.type = "disabled"`。
而真实对话默认走 High（`Settings.cs:69`）。于是：

- 对 `"high"` 报 400 的模型，探测里是**绿的**，真实对话每次都失败
- 只认 low/medium/high 的网关对 `"none"` 报 400，判未知，而且是**永久未知**

新增 `ChatRequest.SuppressThinking`：四个协议都整段跳过思考参数，不是写一个关闭值。
测试里有一条专门的反例锁住这个区别——`Thinking = Off` 时 `reasoning_effort` 仍然
在请求体里，所以 Off 不能拿来当「不传」。

现有的自动改档重试只护 Anthropic（`ChatClient.cs:70` 的 when 子句第一条就是协议判断），
另两条协议的思考相关 400 完全不进那条重试。整段不传绕开了这个不对称。

### 输出上限的字段名对推理模型是错的

`RequestBuilder.cs:99` 只写 `max_tokens`，全仓没有 `max_completion_tokens`。
OpenAI 的推理模型对 `max_tokens` 直接回 400。「输出上限压到最小」必然要设这个字段，
于是最该便宜确认的一批模型恒定确认不出来。

这是既有缺陷——真实对话同样发 `max_tokens`，探测只是让它变成恒定可见的症状。
因此修在 `AgentRunner` 的回退链里，真实对话一并受益：错误点名这个字段就换另一个
重跑该步，并按「连接 + 模型」记档。

判别刻意**不**靠模型名。`ModelCapabilities.cs` 的注释早就写明「模型名与能力没有
可靠对应关系，按名字猜会在换网关后立刻错」。测试里有一条反例：模型名叫 `o3-mini`
也不改变字段名，只有被拒过才改。

### 探测发什么消息

`BuildAnthropic` 把 System 抽到顶层 `body["system"]`，只有非 System 消息进 `messages`。
一个「只带系统提示」的极简探测在 Anthropic 上会产出 `messages: []` 并被 400 拒绝；
Gemini 的 `contents` 同理。写死一条极短的 user 消息（`"hi"`），不带 system。

测试里同时验了反面：只带 system 时 `messages` 真的会空——否则那条断言是白测的。

### 不新写非流式通路

原设想是加非流式发送与读取，再「判定以 HTTP 状态为准，不解析响应体」。
这会主动扔掉三条现成的体内错误识别（Responses、Anthropic、Gemini 的解析器都有）。
而 OpenAI Chat Completions 解析器**没有** error 分支——只读 usage 与 choices，
缺 choices 就 `yield break`，且它是默认协议。

改为复用 `StreamAsync`，并给 Chat Completions 补上 error 分支。
「200 但一个事件都没收到」明确判未知，不留成隐式的「可用」。

## 三样基础设施

- **不走退避**：`StreamAsync` 新增 `maxRetries` 参数，探测传 0。
  `RetryPolicy.TotalBackoff` 是 23 秒，与「点一下就知道」直接矛盾。
- **超时**：`HttpClient.Timeout` 是 `InfiniteTimeSpan`，不给截止时间等于没有超时。
  探测用 15 秒，linked CTS 区分「我方超时」与「用户取消」——两者此前都抛裸的
  `OperationCanceledException`，没有码，分不开。我方超时包装成带
  `PROBE_TIMEOUT` 码的异常以便判未知；用户取消一个字都不记。
- **并发**：探测之间单飞排队；对话在飞时拒绝探测。`SendAsync` 的 `BUSY` 守卫只在
  它自己内部，新通道继承不到。几条请求压在一个账号上会招限流，而限流判未知等于
  花了钱没答案，还可能把用户那一轮带着上下文的请求限掉。

## 批量的停止不与对话的停止混用

批量用独立于 `_currentRun` 的取消源与独立的 `models.probe.stop` 通道。
`Stop()` Cancel 的是 `_currentRun` 这一个对话槽位，复用它会让「停止」在批量与对话
之间产生歧义——正是「发消息不再误停当前任务」已经付过代价的故障。
面板测试里有一条断言专盯这件事：点停批量绝不发 `chat.stop`。

## 只走已保存的连接

`ListModelsAsync` 刻意接受未保存配置让设置页能试连，但探测不照抄：判定的键取自
已保存的连接，拿候选网关探出来的结论会盖到用户当前正在用的连接上。
所以设置页不提供探测入口，只保留「获取模型列表」。

## 验证

- C# `ProbeTests.cs` 28 条：四协议的 `SuppressThinking` 双向断言、
  `Thinking = Off` 仍会写值的反例、消息非空及其反证、输出上限字段的三条
  （默认、被拒后、不按名字猜）、字段名判据的交叉反例。整套 524 条通过。
- 面板 `model-probe.test.mjs` 26 条：「试一下」只对未确认的行出现、正在确认是可见的
  独立态、结论到达后替换、批量禁用与进度、**停批量不发 `chat.stop`**。
  末尾带变异自检。
- `scripts/verify-picker.ps1` 37 条通过（改动前 18），真实 Excel 宿主里六种模型
  各自确认出预期结论。
- **端到端抓到一个单测全绿时漏掉的真 bug**：体内错误抛的是 `STREAM_ERROR` 而不是
  `HTTP_4xx`，而 `Classify` 先用 `IsClientError` 卡了一道，于是「别名模型」这种最
  典型的不可用情形恒判未知。已修，并补上单测——`mock-aliasbroken` 那条场景就是为它
  留的。
- 变异验证：去掉 Chat Completions 的 `SuppressThinking` 判断后，对应断言立即转红。
- `tests/mock-provider/server.mjs` 新增按模型名分流的七种情形（404 点名模型、
  403 点名模型、403 只说密钥、429、200 体内含错误、200 零事件、只认
  `max_completion_tokens`、只认 low/medium/high）。
- 两套主题：新规则用到的 10 个调色板变量在浅色与深色下都有定义，无硬编码颜色
  （静态核对）。本环境无交互桌面，未做目视截图确认。
