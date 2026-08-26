# 任务

## 能力档案

- [x] `Providers/ModelCapabilities.cs`：`ToolProtocolMode`（Native/Text/None）、
      `ToolProtocolPreference`（Auto + 三个手动档）、按「连接键 + 模型名」缓存的档案
- [x] 错误分类：`LooksLikeToolUnsupported` 与 `LooksLikeVisionUnsupported`，
      两者互不重叠；只认 4xx，5xx 交给既有重试
- [x] `LooksLikeToolRefusal`：识别「无法访问/看不到/没有权限」等推辞，
      且必须同时谈到表格——只说「我不能」可能是合理拒绝
- [x] 启发式每个连接+模型只试一次，用 `ToolRefusalProbed` 记住已试过

## 文本指令协议

- [x] `Providers/TextToolProtocol.cs`：紧凑签名清单（名称、参数、一行说明）
- [x] 指令块格式 ` ```chatsheet:tool ` + JSON（`tool`、`args`）；宽松接受漏写信息串
      但同时含 `tool` 与 `args` 且工具名真实存在的块
- [x] 解析：一次回复可含多个块，按出现顺序执行
- [x] `Providers/TextToolGate.cs` 流式闸门：识别到围栏开头即攥住，确认是工具块则
      吞掉，否则原样放行；流结束时未闭合的块要收束，不能一直攥着
- [x] 末尾半个围栏要留到下一段再判断（三个反引号会被拆到不同增量里）

## Agent 循环

- [x] `BuildRequest` 按当前模式决定是否带工具声明
- [x] 400 指向工具字段 → 记档、换 Text、重跑该步且不计步数
- [x] 首步零调用且正文是推辞 → 记档、换 Text、重跑（推辞不进上下文）
- [x] Text 模式下连续两步无可用指令块 → 记档为 None、提示用户、改用顾问提示
- [x] 两条能力判据都排在截断判定之后（截断的形态与「不会用工具」一样）
- [x] 文本协议的工具结果以 user 消息回灌，并标记为工具结果供压缩识别
- [x] 400 指向图片字段 → 记档；有中转模型则转述，无则去图重发并告知模型
- [x] 转述结果按图缓存，多步不重复请求
- [x] 已知无视觉时轮首直接回退，不必先撞一次 400
- [x] 指令块没写出工具名时报「块写坏了」而非「未知工具」，截断的报 ARGS_TRUNCATED

## 系统提示

- [x] `SystemPrompt.Build` 接受工具模式：Native 保持逐字不变
- [x] Text：追加工具清单与指令块格式说明
- [x] None：改写为顾问口吻，去掉「你已经连上工作簿」，回答风格里也去掉
      「说明改了哪些范围」

## 视觉中转

- [x] `Providers/VisionRelay.cs`：同连接换模型名，单图单请求，不带工具
- [x] 提示词按表格用途要求描述结构、表头、数值、报错文案
- [x] 描述注入时注明是转写、涉及数值要读表核对
- [x] 中转失败 → 退回去图并说明，不让整轮失败
- [x] `Settings.ResolveVisionRelay`：与主模型同名时不中转

## 设置与桥接

- [x] `Settings`：`ToolProtocol`（枚举）与 `VisionRelayModel`（字符串），含读写与收敛
- [x] `AgentChannels`：`settings.get` 下发两项与可选清单，`settings.save` 接收
- [x] 改动工具形态即作废探测结果
- [x] `settings.js`：高级参数里加下拉与输入框，只用既有的 `.input`/`.field-hint`

## 面板

- [x] `chat.js`：`tool-fallback` 与 `vision-fallback` 两个事件落成通知
- [x] 文本协议下的操作卡片与原生一致（同一条 tool-start/tool-result 路径）

## 测试

- [x] `tests/ChatSheet.ToolTests/CapabilityTests.cs`：57 项——错误分类（含 401/503
      与图工具互斥的反例）、推辞识别（含合理拒绝与正常作答的反例）、清单文本、
      指令块解析（多块、未知工具、坏 JSON、宽松匹配、普通 JSON 反例）、
      闸门（一次性、逐字、普通代码块、未闭合、行内反引号、多块）、档案缓存
- [x] `tests/web/capability-fallback.test.mjs`：13 项，两条提示都落成胶囊且不影响收尾
- [x] `tests/mock-provider/server.mjs`：`notool` 与 `novision` 两个场景，
      中转模型名作为 novision 的例外
- [x] `scripts/verify-chat-e2e.ps1`：两个场景的判定与 `-VisionRelayModel`
- [x] 变异验证：闸门改成放行原文 → 2 条如期失败；面板不落通知 → 5 条如期失败

## 文档

- [x] `docs/changes/2026-08-26-model-capability-fallback.md`
- [x] README：核心能力表新增一行、已知限制新增两行、配置页新增「模型能力不足时」
      一节、验证命令补三条

## 验证结果

- 工具层与接入层：`ChatSheet.ToolTests.exe` 通过 427，失败 0（新增 57 项）
- 面板单测 18 个文件合计 453 项通过，失败 0（新增 13 项）
- 端到端（真实 Excel + mock）：
  - `notool`：带 17 个工具被 400 拒 → 降级 Text → 指令块执行 →
    A1=名称 B1=数量 真的写进单元格 → 正常收尾；带 tools 的请求只发 1 次
  - `novision -WithImage`：图片被拒 → 去图重发 → 模型收到说明 → 正常收尾
  - `novision -WithImage -VisionRelayModel mock-vision`：经中转转写后主模型据此作答
- 设置页：控件 9 个，200px 与 455px 下均无横向溢出；新控件的颜色全走调色板变量
- Release 构建 0 警告 0 错误
