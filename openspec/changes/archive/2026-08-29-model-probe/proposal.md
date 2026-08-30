# 提案：按需确认一个模型能不能用

## 问题

一期把可用性判定从真实对话里白拿了下来，但它只覆盖用户已经发过一轮的模型。
从没用过的一律显示「未确认」——而「我想知道这个没用过的能不能用」恰恰是最初的问题。

补上「点一次就知道」看起来只是加个按钮，实际不是。审计（10 个 agent，5 个维度，
每个维度经一轮对抗复核）在这条通路上找出四个会让**绿灯骗人**的前提缺陷，
以及三样缺失的基础设施。本期先修它们，再加按钮。

## 前提：探测请求必须是真实请求的子集

如果探测请求与真实对话在任何一个字段上不同，而那个字段恰好是会被拒的，
探测就会两头误判。四条都真实存在：

### 1. 「关掉思考」不是「不传思考参数」

`ChatRequest.Thinking = Off` 会实际发出一个关闭值：
`Thinking.OpenAiEffort(Off)` 返回字符串 `"none"`（`Thinking.cs:85`），
写进 `reasoning_effort`（`RequestBuilder.cs:112`）与 `reasoning.effort`（`:236-240`）；
Gemini 落到 else 分支写 `thinkingConfig.thinkingBudget = 0`（`:568-580`）；
Anthropic 写 `thinking.type = "disabled"`（`:423-426`）。

而真实对话默认走 High（`Settings.cs:69`）。于是：

- 对 `"high"` 报 400 的模型，探测里是**绿的**，真实对话每次都失败
- 只认 low/medium/high 的网关对 `"none"` 报 400，判未知，而且是**永久未知**

现有的自动改档重试只护 Anthropic：`IsThinkingStyleMismatch` 的 when 子句第一条就是
`request.Protocol == ProtocolKind.AnthropicMessages`（`ChatClient.cs:70`）。

**改**：给 `ChatRequest` 一个「整段不传思考参数」的开关，四个协议都跳过该段。
不是设一个关闭值，是根本不写这个字段——这样探测请求可证明是真实请求的真子集。

### 2. 输出上限的字段名对推理模型是错的

`RequestBuilder.cs:99` 在 Chat Completions 上只写 `max_tokens`，全仓没有
`max_completion_tokens`。OpenAI 的 o 系与 gpt-5 系推理模型对 `max_tokens` 直接回 400。

「输出上限压到最小」必然要设 `MaxOutputTokens`，于是同一个探测请求对推理模型同时踩
两个会被拒的参数——最该便宜确认的一批模型恒定确认不出来。

这是既有缺陷，真实对话同样发 `max_tokens`，探测只是让它变成恒定可见的症状。

**改**：Chat Completions 上按模型判别字段名。判别不能靠猜模型名——那正是
`ModelCapabilities` 注释里明确反对的做法（「模型名与能力没有可靠对应关系」）。
改为：先按 `max_tokens` 发，收到明确指向该字段的 400 就换 `max_completion_tokens`
重发一次并记档，与既有的工具/视觉回退同一个形状。

### 3. 探测发什么消息，此前没有规定

`BuildAnthropic` 把 System 角色抽到顶层 `body["system"]`（`RequestBuilder.cs:280-287`），
只有非 System 消息进 `messages`（`:330-338`）。一个「只带系统提示」的极简探测在
Anthropic 上会产出 `messages: []`，被服务端以 400 拒绝；Gemini 的 `contents` 同理。
`VisionRelay` 没这个问题，因为它必然带一条用户消息（`VisionRelay.cs:58`）。

**改**：写死一条固定的极短 user 消息，不带 system。

### 4. 不新写非流式通路

原任务要求加非流式发送与读取，再「判定以 HTTP 状态为准，不解析响应体」。
这会主动扔掉三条现成的体内错误识别：Responses（`StreamParsers.cs:262`）、
Anthropic（`StreamParsers.Anthropic.cs:141`）、Gemini（`:165`）。

而 OpenAI Chat Completions 解析器**没有** error 分支——只读 usage 与 choices，
缺 choices 就 `yield break`（`StreamParsers.cs:102-104`），且它是默认协议
（`Protocols.cs:75`）。

**改**：复用 `StreamAsync`，并给 Chat Completions 补一条 error 分支。
「200 但一个事件都没收到」明确判未知，而不是留成隐式的「可用」。

## 缺失的基础设施

### 超时

`HttpClient.Timeout` 是 `Timeout.InfiniteTimeSpan`（`ChatClient.cs:39-44`）。
全仓唯一带时限的 CTS 在 `AgentChannels.cs:524-527`，而且包含 23 秒退避。
挂住的网关会把行永久停在「正在确认」，并把后续排队的探测全堵死。

超时与用户取消都抛裸的 `OperationCanceledException`（`ChatClient.cs:195-198`），
没有 Code，分不开。

**改**：探测用独立的短截止时间（15 秒，不含退避），linked CTS 区分「我方超时」
与「用户取消」，我方超时包装成带 Code 的 `ProviderException` 以便判未知。

### 不走完整退避

重试次数硬编码在 `ChatClient.cs:123`，`RetryPolicy.TotalBackoff` 是 23 秒。
每个模型等 23 秒与「秒级确认」直接矛盾。

**改**：给 `StreamAsync` 一个 `maxRetries` 参数，探测传 0。重试留给用户再点一次。

### 并发

`SendAsync` 的 `BUSY` 守卫只在它自己内部（`AgentChannels.cs:587-590`），
新注册的 `models.probe` 一点都继承不到，正如今天的 `models.list` 也继承不到。

**改**：探测之间单飞排队；对话在飞时拒绝探测并说明原因。这条要明写，
因为「就一个请求，直接发」是更省事也更错的实现。

### 批量的进度与中断

`_pushRaw` 现在只有两种 kind（`models-retry`、`approval-request`），
中断只有 `Stop()`，而它 Cancel 的是 `_currentRun` 这一个对话槽位
（`AgentChannels.cs:554-571`）。复用它会让「停止」在批量与对话之间产生歧义——
正是「发消息不再误停当前任务」那个已经付过代价的故障。

**改**：新增一种 push kind 用于批量进度；批量用独立于 `_currentRun` 的取消源，
独立的停止通道。

## 判定按请求实际发往的连接记

`ListModelsAsync` 刻意接受未保存的配置让设置页能试连（`AgentChannels.cs:481-520`）。
若探测照这个形状做，而判定的键取自**已保存**的 `Settings.ConnectionKey()`，
那么在设置页对一个还没保存的候选网关点探测，结论会盖到用户当前正在用的连接上。

面板侧对目录已经修过同一类问题：`putModelCatalog` 的 revision 守卫
（`model-catalog.js:58-69`）。

**改**：本期探测只从对话页发起，走已保存设置，不接受未保存配置——设置页的试连
仍然只有「获取模型列表」。这样就没有张冠李戴的可能，也不必新造一套键的回传。
如果以后要在设置页支持探测，再按 revision 守卫的形状补。

## 影响范围

新增 `Providers/ModelProbe.cs`（最小请求、单飞队列、超时、判定折算）。

改 `Providers/ChatTypes.cs`（`SuppressThinking` 与 `MaxOutputTokensField`）、
`Providers/RequestBuilder.cs`（跳过思考段、按档选输出上限字段名）、
`Providers/ChatClient.cs`（`maxRetries` 参数、探测超时、Chat Completions 的 error 分支）、
`Providers/ModelCapabilities.cs`（记住该模型要用哪个输出上限字段）、
`Providers/StreamParsers.cs`（error 分支）、
`Bridge/AgentChannels.cs`（`models.probe`、`models.probe.bulk`、`models.probe.stop`、
与对话互斥、新 push kind）、
面板 `scripts/picker.js`（行内「试一下」、正在确认态）、
`scripts/model-favorites.js`（正在确认与进度的投影）、
`styles/app.css`（正在确认态，两套主题）。

三态的含义、「标注永不隐藏」、名单与开关的行为都不变。
