# 设计：为什么一期不做「试一下」

原方案的核心交互是「对着某个模型点一下，花一条最小请求得出可用/不可用/未知」。
审计（10 个 agent，5 个维度，每个维度经一轮对抗复核）发现这条通路与真实对话的
形状不一致，会两头误判。本文记下待修前提，实现留到二期。

## 探测的绿灯会骗人，而且往两个方向

`ChatRequest.Thinking = Off` 不是「不传思考参数」，而是传一个关闭值：

- `Thinking.OpenAiEffort(Off)` 返回字符串 `"none"`（`Thinking.cs:85`），
  写进 `reasoning_effort`（`RequestBuilder.cs:112`）与 `reasoning.effort`（`:236-240`）
- Gemini 落到 else 分支写 `thinkingConfig.thinkingBudget = 0`（`:568-580`）
- Anthropic 写 `thinking.type = "disabled"`（`:423-426`）

而真实对话默认走 High（`Settings.cs:69`）。`ChatRequest` 没有「整段不传」的开关，
只有 `IncludeTools` 与 `AnthropicStyleOverride`（`ChatTypes.cs:114`、`:120`）。于是：

- 对 `"high"` 报 400 的模型，探测里是**绿的**，真实对话每次都失败——绿灯主动骗人
- 只认 low/medium/high 的网关对 `"none"` 报 400，按判据判「未知」，而且是**永久未知**，
  用户点多少次都一样

现有的自动改档重试只护 Anthropic：`IsThinkingStyleMismatch` 的 when 子句第一条就是
`request.Protocol == ProtocolKind.AnthropicMessages`（`ChatClient.cs:70`），
OpenAI 与 Gemini 的思考相关 400 完全不进这条重试。

**二期待修**：给 `ChatRequest` 一个「整段不传思考参数」的标记，让探测请求可证明是
真实请求的子集；或把 `ChatClient.cs:70` 的协议门槛一并放开。

## 输出上限的字段名对推理模型是错的

`RequestBuilder.cs:99` 在 Chat Completions 上只写 `max_tokens`，全仓没有
`max_completion_tokens`。OpenAI 的 o 系与 gpt-5 系推理模型对 `max_tokens` 直接回 400。

「输出上限压到最小」必然要设 `MaxOutputTokens`，于是同一个探测请求对推理模型同时踩
两个会被拒的参数——最该便宜确认的一批模型恒定确认不出来。

这是既有缺陷（真实对话同样发 `max_tokens`），探测只会让它变成一个恒定可见的症状。

**二期待修**：按协议与模型选字段名，并补四协议请求体断言。

## 探测发什么消息，规范里没写

`BuildAnthropic` 把 System 角色抽到顶层 `body["system"]`（`RequestBuilder.cs:280-287`、
`:350-353`），只有非 System 消息进 `messages`（`:330-338`）。一个「只带系统提示」的
极简探测在 Anthropic 上会产出 `messages: []`，被服务端以 400 拒绝；Gemini 的 `contents`
同理。`VisionRelay` 没这个问题，因为它必然带一条用户消息（`VisionRelay.cs:58`）。

**二期待定**：写死一条固定的极短 user 消息，不带 system，并覆盖「messages/contents 非空」。

## 不要新写非流式通路

原任务要求给 `ChatClient` 加非流式发送与读取，再「判定以 HTTP 状态为准，
不解析响应体」。这会主动扔掉三条现成的体内错误识别：

- Responses：`case "error"`（`StreamParsers.cs:262`）
- Anthropic：`case "error"`（`StreamParsers.Anthropic.cs:141`）
- Gemini：响应体里查 error 对象（`StreamParsers.Anthropic.cs:165`）

现成先例就是 `VisionRelay.DescribeAsync`：同形态的最小请求走 `StreamAsync`，
只累加 TextDelta，空结果算失败（`VisionRelay.cs:46-90`）。

代价是要给 `StreamAsync` 一个 `maxRetries` 参数、或把 `StreamOnceAsync` 提为 internal：
重试次数硬编码在 `ChatClient.cs:123`，`RetryPolicy.TotalBackoff` 是 23 秒
（1+2+4+8+8，`RetryPolicy.cs:100-112`），每个模型等 23 秒与「秒级确认」直接矛盾。

**另注**：OpenAI Chat Completions 解析器**没有** error 分支——只读 usage 与 choices，
缺 choices 就 `yield break`（`StreamParsers.cs:102-104`），而它是默认协议
（`Protocols.cs:75`）。二期要么补一条 error 分支，要么在规范里写死「200 但一个事件
都没收到」怎么判，别把它留成隐式的「可用」。

## 探测还缺三样基础设施

- **超时**：`HttpClient.Timeout` 是 `Timeout.InfiniteTimeSpan`（`ChatClient.cs:39-44`）。
  全仓唯一带时限的 CTS 在 `AgentChannels.cs:524-527`，而且包含那 23 秒退避。
  挂住的网关会把行永久停在「正在确认」，并按「同时只允许一个在飞」把后续排队全堵死。
  超时与用户取消都抛裸的 `OperationCanceledException`（`ChatClient.cs:195-198`），
  没有 Code，分不开。
- **并发**：`SendAsync` 的 `BUSY` 守卫只在它自己内部（`AgentChannels.cs:587-590`），
  新注册的 `models.probe` 一点都继承不到，正如今天的 `models.list` 也继承不到。
  一轮对话在飞时放五个探测出去，六条请求压在同一个账号上，而限流判未知——
  花了钱没拿到答案，还可能把用户那一轮真正带着上下文的请求给限掉。
- **批量的进度与中断**：`_pushRaw` 现在只有两种 kind（`models-retry`、
  `approval-request`），中断只有 `Stop()`，而它 Cancel 的是 `_currentRun` 这一个对话槽位
  （`AgentChannels.cs:554-571`）。复用它会让「停止」在批量与对话之间产生歧义——
  正是「发消息不再误停当前任务」那个已经付过代价的故障。

## 探测的判定要按请求实际发往的连接记

`ListModelsAsync` 刻意接受未保存的 mode/protocol/baseUrl/token，让设置页能先试连
（`AgentChannels.cs:481-520`，注释在 `:497`）。若 `models.probe` 照这个形状做，
而判定的键取自**已保存**的 `Settings.ConnectionKey()`，那么在设置页对一个还没保存的
候选网关点探测，结论会盖到用户当前正在用的那个连接上。

面板侧对目录已经修过同一类问题：`putModelCatalog` 用 revision 守卫
（`model-catalog.js:58-69`），`loadModels` 落库前比对 `state.catalogKey === key`
（`picker.js:338`）。

一期不触发这个陷阱——判定来自真实对话，走的必然是已保存设置。二期要按「请求实际
发往哪」记，并让通道回传那个键，供面板丢弃已经不属于当前视图的结论。

## 一期跑过之后再决定二期做多少

一期上线后有两个数能直接看到：

1. 用户真正切换过的模型有几个。如果是三五个，那些从没发过一轮的 ID 本来就不需要
   按需确认，排序加标注就够了。
2. 「未知」里有多少是 401/429 这类账号问题。如果占大头，二期的探测同样拿不到答案。

先量再做。
