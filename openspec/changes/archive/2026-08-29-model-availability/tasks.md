# 任务：常用模型名单与真实对话中学到的可用性（一期）

二期（按需「试一下」与批量确认）的待修前提见 `design.md`，本期不实现。

## 服务端原文

- [x] `Providers/Protocols.cs`：`ProviderException` 增加只读 `Detail`，只装服务端原文，
      不含任何本地拼装的提示
- [x] `ChatClient.BuildHttpErrorAsync`（`:395-421`）把 `ExtractErrorMessage` 的结果原样
      填进 `Detail`；`Message` 的拼装一字不改——它给用户和日志看，两个用途不共用字段
- [x] `Detail` 读不出来（响应体解析失败）时保持为空，判定见下：空即判未知，
      绝不退回读 `Message`

## 可用性判定

- [x] `Providers/ModelAvailability.cs`：三态（Available / Unavailable / Unknown）与按
      「连接 + 模型」的内存缓存；字典用 `StringComparer.OrdinalIgnoreCase`
- [x] 判据只读 `Detail`，认字段名与固定措辞（`model_not_found`、`does not exist`、
      `no access to`）。Gemini 与 OpenAI 兼容网关的错误形态差别很大，判据不能只对一家有效
- [x] 裸 404 判未知：只有 `Detail` 点名模型才判不可用。`HintFor(404)` 返回的
      「请检查接口地址与模型名是否正确」（`ChatClient.cs:491-492`）含「模型名」二字，
      这条是整套判据成立的前提
- [x] 403 双向：点名模型判不可用，只说密钥判未知，分不清判未知
- [x] 401、429、超时、网络故障、无错误码、任意 5xx 一律判未知。
      `RetryPolicy.IsTransientCode`（`:40-68`）只作为「肯定是未知」的加速判据，
      不作为唯一分流依据——它枚举的是六个具体 5xx，501/505/520/524 都不在表内，
      401 与超时也不在
- [x] 判定不写入 `ModelCapability`：可用性与工具/视觉是三个独立维度，互不改写
- [x] 按连接作废的操作（`ModelCapabilities` 只有全清的 `Reset()`，`:87-90`）
- [x] 注释写「Excel 进程存续期间」而不是「本次面板会话」：`EnsurePane` 是
      `if (_pane != null) return;`（`ComAddIn.cs:225-230`），关面板只改 `IsVisible`
      （`:186`），控件不销毁、`AgentChannels` 不重建。既有 `ModelCapabilities.cs:61-66`
      的注释说「重开面板重探一次」，代码从来没兑现过——照抄那句话会写出一条做不到的规范

## 能力判据的互斥

- [x] `CapabilitySignals` 的 `IsClientError`（`:20`）与 `Mentions`（`:27`）由 private
      提为 internal，类注释标明这是本期唯一改动
- [x] 新增「错误点名了模型」判据，`LooksLikeToolUnsupported` 与
      `LooksLikeVisionUnsupported` 都先排除它（既有的反方向守卫在 `:60-65`，这是第三条）
- [x] 顺路修既有缺陷：`LooksLikeVisionUnsupported` 匹配裸子串 `"image"`（`:96`），
      于是 `gpt-image-1` 的 404 被记成不支持图片、白花一次中转请求、剥图重试同一个
      不存在的模型，最后告诉用户「没有视觉能力」（`AgentRunner.cs:472-482`、`:724-790`）

## 真实对话回写

- [x] `AgentRunner.StreamStepAsync`（`:504` 的事件回调）收到任何 `ChatEvent` 即记可用。
      不用 `ChatClient` 的 `delivered`——那是重试循环内的局部变量（`ChatClient.cs:102`），
      只说明「这一次尝试」交付过
- [x] `RunStepAsync` 的 catch 链（`:459-482`）：失败且 `Detail` 点名模型即记不可用，
      且这条判据要排在两条能力回退之前
- [x] 判定不得比证据活得久：判过不可用的模型后来答了话就改回可用，反之同理
- [x] 换连接与写入密钥时作废该连接的判定。落点 `AgentChannels.cs:439-451`：
      按「写了密钥」触发，不去比对新旧（比对要把已存的密钥读回来，无谓地多碰一次密钥）
- [x] 本机 CLI 模式的密钥在 CLI 自己的配置里、不经 `SecretStore`
      （`LocalCliConfig.cs:166`、`:199`），所以那条路上没有这个触发点。
      写进注释，否则看起来像漏了

## 名单落盘

- [x] `Storage/FavoriteModels.cs`：按连接分组的名单，独立文件在
      `%LOCALAPPDATA%\ChatSheet\` 下。路径写法照 `Settings.cs:226-229`——明文 JSON
      不能进 `SecretStore` 的 `secrets` 子目录（`SecretStore.cs:8-15` 的不变量）
- [x] 原子写用 `File.Replace`（顺带留备份），**不**照抄 `Settings.cs:317-321` 的
      Delete + Move：那两步之间崩溃会同时失去新旧两份。目标文件还不存在时退回 `File.Move`
- [x] 损坏时退回空名单并保留原文件
- [x] 只校验当前连接那一组，其余组按字节原样保留。**不**照搬
      `DropModelFromOtherConnection`（`Settings.cs:141-160`）的处置——那是为「只存一个
      模型」写的，直接照搬会在读盘时删掉其他所有连接的分组，与「名单按连接隔离」相反
- [x] 模型 ID 比较忽略大小写，与 `ChatClient.cs:368` 的去重一致
- [x] 本机 CLI 按**解析后**的 CliKind 归组，不按配置的 `CliSource`：
      `ConnectionKey()` 是 `Mode|CliSource`（`Settings.cs:114-119`），把下拉从「自动」
      钉成「Claude」会让键变化而 `LocalCliConfig.Resolve` 返回的凭据一模一样，
      名单原地失联且旧分组还在盘上够不着
- [x] 不做地址戳记：`TryReadCodexBaseUrl` 吞异常返回 null，「读不出来」与「就是官方
      地址」分不开（`LocalCliConfig.cs:213-214`、`:232-264`）；而 token 缺失时
      `ReadClaude` 在算地址之前就抛了（`:166-174`）。失效交给面板侧的展示期阀门
- [x] 文件路径可注入，照 `LocalCliConfig.ClaudeSettingsPath(string homeDir = null)`
      （`:45-51`）的写法——否则测不了（`Settings.FilePath` 是 private static，
      测试里零覆盖）

## 开关

- [x] `Storage/Settings.cs` 加一个布尔，默认关；Load 与 Save 成对出现
      （白名单重建，只读不写等于被下一次任意写入方删掉）
- [x] 走 `session.update`（`AgentChannels.cs:89-131`）新增一个可空布尔并回传，
      不走 `settings.save`
- [x] **要**把它加进 `settings.js` 的只读字段删除名单（`:606-611`）：`current` 是
      面板启动时的快照（`initSettings` 由 `app.js:48-51` 的 `settingsLoaded` 一次性守卫
      保护，整个面板生命周期只跑一次），不删就会在设置页保存时把刚拨的开关写回旧值

## 桥接

- [x] 名单的读写通道
- [x] `GetSettingsPayload`（`:263-330`）下发名单、三态与开关，由后端给权威判断
      （`:273-274` 已确立这条分工）

## 面板

- [x] `scripts/model-favorites.js`：名单与三态的面板侧投影。一律以后端下发的 payload
      为权威，本地键只用于「这份投影属于谁」的比对，照 `model-catalog.js:58-69` 的
      revision 守卫
- [x] 模型行另起 `buildModelRow`：`buildRow`（`picker.js:217-227`）是模型列与思考等级列
      共用的（`:187`、`:193`、`:213` 三处调用），就地加节点会让思考档位行也长出星标
- [x] `.picker-item` 保持 `<button>`、class 与点击语义不动，新增 `.picker-row` 容器，
      星标作兄弟节点。`.picker-item` 本身是 `<button>`（`:218-219`），HTML 禁止嵌套
      交互元素；而 `TaskPaneControl.cs:354-360` 按 `.picker-item-name` 的 textContent
      全等匹配后 `row.click()`，改成 div 会让端到端驱动失效
- [x] `.picker-item-name` 的 textContent 必须仍是纯模型 ID（同上）
- [x] 状态点在模型名之前：`.picker-item` 是 `flex-direction: column`
      （`app.css:1929-1933`），要给名字外包一层横向 flex（`min-width: 0`）
- [x] 状态文字进 `.picker-item-hint`，并定好与「当前使用」（`picker.js:187`）、
      「当前模型会降级」（`:212-213`）同现时的优先级
- [x] `picker.js:187` 那条空目录分支的行 onClick 是 `() => {}`，明确它是否要能标星
- [x] 排序：名单优先，其余保持后端字母序；判定到达不改变行序
- [x] 列头开关放 `.picker-col-head`（`index.html:137-141`，现装着「模型」与「刷新」），
      用新 class 限定在模型列头，别改两列共用的 `.picker-col-head`
- [x] 阀门：开关开且名单里没有一个出现在当前目录时显示完整目录；当前模型永远可见；
      名单刚从空变成一项时不收起
- [x] 被收起的数量与「显示全部」放模型列底部、`.picker-manual` 之上，复用
      `.picker-empty`（`app.css:1983-1987`）的整行说明形状。列头放不下一份清单，
      而 `.picker-pop` 是 `overflow: hidden`（`:1858-1875`），超宽会被静默裁掉、
      现有自检一条都不会报
- [x] 筛选后为空时不得走 `.picker-empty` 的现有文案：`picker.js:181-183` 那句
      「接口未返回模型列表」会把用户自己开的开关说成网关掉了模型
- [x] 手填入列挂在 `applyManualModel`（`picker.js:246-252`）上——它本身就是
      「不在目录里也要可见」的第三处特例，本次加的是第四处
- [x] 面板侧比较模型 ID 一律折叠大小写：`picker.js:250` 与 `:64` 的 `includes`
      现在区分大小写，`GPT-4O` 与 `gpt-4o` 会并成两行
- [x] `renderTrigger`（`:150-176`）当前模型已知不可用时在摘要行提示。**新增**
      `.picker-model.is-unavailable` 一条规则，颜色照 `.picker-thinking.is-downgraded`
      （`app.css:1853`）走 `--warn-fg`——现有规则按 `.picker-thinking` 限定作用域，
      只把类名搬到模型 span 上会没有样式且不报错
- [x] `syncModelCatalog`（`:95-112`）**仅当键发生变化**时清理本连接的状态视图：
      它无条件覆盖 `state.catalogKey`，而 syncPicker 在「回到对话页」「点对话页签」
      「新会话」都会跑（`app.js:56` → `chat.js:1858` → `:2034` → `:2042` → `:1903`），
      点「刷新」也会（`picker.js:381-385`）。挂无条件清理等于每次切页都清空判定
- [x] `describePicker`（`:429-433`）报出名单、三态与筛选状态
- [x] 三态标记与星标的颜色两套主题一起加，只走调色板变量；纯图形的琥珀用现成的
      `--warn-graphic`（`app.css:59`、`:126` 两套都有）

## 测试

- [x] `tests/ChatSheet.ToolTests/AvailabilityTests.cs`，并在 `Program.cs` 加一行 Run
      （不加只编译不执行）
- [x] 错误分类反例：401、429、503 不得判为不可用，照 `CapabilityTests.cs:52-64`。
      429 在既有测试里没有先例（全文无一处），本次新增
- [x] 裸 404 判未知，`Detail` 点名模型的 404 判不可用
- [x] **关键反例**：`Detail` 为空、而 `Message` 含「模型」二字时必须判未知。
      这条直接锁住「判据不许读 `Message`」
- [x] 403 双向：点名模型判不可用，只说密钥判未知
- [x] 交叉反例：可用性判定不改写工具与视觉档案
- [x] `gpt-image-1` 的 404 不得设 `VisionUnsupported`、不得走视觉回退、不得花中转请求
- [x] 大小写：`GPT-4O` 与 `gpt-4o` 归一后命中同一条判定
- [x] 反射断言：`ModelCapability` 上不出现可用性字段（结构不变量，照 `:367-379`）
- [x] 按连接作废只清本连接
- [x] `FavoriteModels` 读写往返、按连接隔离、损坏退回空名单、
      `File.Replace` 后原文件仍可从备份取回
- [x] `tests/web/model-favorites.test.mjs`：名单按连接隔离、开关关时不过滤、
      名单空时不过滤、名单全部不在目录时不过滤、当前模型不被过滤掉、
      名单优先排序、判定不改变行序
- [x] 假 DOM 必须让 append 摘走原父节点（照 `capability-fallback.test.mjs:36-135`），
      并先变异一次确认断言会红
- [x] `picker-manual-model.test.mjs` 的 `activeRow()` 是
      `row?.children[0]?.textContent`（`:111-114`），假 DOM 的 textContent 不从后代聚合，
      行结构一改就打红。改成按 `.picker-item-name` 找
- [x] `scripts/verify-picker.ps1`：筛选生效后仍能反复切换模型

## 文档

- [x] README 核心能力表、模型选择那一节、「能力探测的边界」
- [x] `docs/changes/2026-08-29-model-availability.md`
- [ ] 归档变更目录

## 验证结果

- 构建：Debug 与 Release 均 0 错误。
- C# `AvailabilityTests.cs` 48 条通过；整套 494 条通过（改动前 476 条）。
- 面板 `model-favorites.test.mjs` 27 条通过；`tests/web` 19 个文件全通过。
- `scripts/verify-picker.ps1` 18 条通过（改动前 15 条），真实 Excel 宿主，
  实测 `拨动后：星标=1/1 开关=true 收起说明=已按名单收起 1 个模型`。
- 两套主题：新规则用到的 13 个调色板变量在浅色与深色下都有定义，且新规则里
  没有硬编码颜色（静态核对）。本环境无交互桌面，`CopyFromScreen` 取不到句柄，
  **未做目视截图确认**。

### 变异验证

三次，其中两次抓到真问题：

1. **删掉展示期阀门** → 5 条断言转红，正是三种锁死场景（名单为空、名单全部失效、
   刚标第一颗星），恢复后回绿。断言确实在守这条规则。
2. **实现过程中真实打红**：早先写过一条「原文出现过模型名 + 提到 model」的兜底判据，
   被「请求体回显」那条反例抓住——网关报参数错误时会把 `{"model":"gpt-4o"}` 回显在
   原文里，两个条件同时满足，于是每条参数错误都被读成点名了模型。据此删掉该兜底，
   只认固定措辞。
3. **清空模型列的自检**（写进测试文件常驻）：确认断言不是对着空节点通过。

另外 `verify-picker.ps1` 第一次跑出假绿：它此前不备份 `favorite-models.json`，
上一次跑剩的星标让本次的标星变成取消标星，筛选于是什么都没收起，而断言仍然通过。
已补上备份与开跑前清空——这条隔离缺失本身就是被这次改动照出来的。
