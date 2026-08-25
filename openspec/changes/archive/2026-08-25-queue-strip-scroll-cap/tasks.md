# 任务

## 面板

- [x] `app.css`：`.queue-strip` 加高度上限（三条 + 两道间隙，由 CSS 变量派生），
      `overflow-y: auto`、`overflow-x: hidden`
- [x] `app.css`：滚动条收窄。实测后只留 `::-webkit-scrollbar`（6px），
      删掉 `scrollbar-width: thin`——两条同写时以后者为准，伪元素成死规则
- [x] `app.css`：`.queue-chip` 的 `min-height` 改用 `--queue-chip-height`，
      与上限算法同源
- [x] `app.css`：删掉不再产生的 `.msg-cancelled` / `.msg-cancelled-tag` 三条规则
- [x] `chat.js`：`renderQueueStrip` 重画后 `scrollTop = 0` 归到队首那一端
- [x] `chat.js`：删掉 `mountCancelledBubble`；`cancelQueued` 只出队并重画
- [x] `chat.js`：`clearQueue` 去掉 `keepText` 形参（两个调用点同步），
      停止时仍报「取消了几条」
- [x] `chat.js`：`mountEntryBubble` 去掉只服务于取消气泡的 `entry.wrapper/body` 赋值

## 自动化钩子与验证

- [x] `TaskPaneControl.ReadQueueState`：删掉「已取消 / 已取消内容」两个字段，
      `已发送` 不再排除 `.msg-cancelled`；新增「排队条可滑动」
- [x] `AddInAutomation.ReadQueueForTest` 注释里的返回值样例同步
- [x] `verify-chat-queue.ps1`：取消后的断言改为「原文不再出现在对话流里」，
      两条时断言不可滑动、四条时断言可滑动，停止取消的四条同样不留痕
- [x] `send-stop.test.mjs`：「被取消的落进对话流」两条断言翻面；
      删掉已成恒真的 `.msg-cancelled` 计数断言，改断言对话流正文
- [x] `queue.test.mjs`：新增第三节，覆盖取消不留痕与重画后归位

## 规格与文档

- [x] `openspec/specs/chat-input-queue/spec.md`：新增「排队区限高并可滑动」一条要求，
      取消一条与停止清空两处改为不在对话流留痕
- [x] `docs/changes/2026-08-25-queue-strip-scroll-cap.md`
- [x] 归档到 `openspec/changes/archive/`

## 验证结果

- 面板单测 11 个文件合计 **268 项通过、0 失败**（`queue.test.mjs` 43 项、
  `send-stop.test.mjs` 34 项；改动前为 254 项）
- 工具单测 **342 项通过、0 失败**；Release 构建 0 警告 0 错误
- 真实 `index.html` 上用 WebView2 同源的 headless Chromium 量过布局，
  栏宽 320 / 360 / 420px 结果一致：3 条起排队条恒为 60px（改动前 6 条 123px、
  12 条 249px），可见位次恒为 1、2、3，超三条时可滑动，全程无横向溢出；
  滚动条为占位而非覆盖，取消按钮命中测试通过
- 三个变异逐条验证了新断言有效（明细见改动记录）
- 量具用后即删，未留在仓库里
- 未跑：`verify-chat-queue.ps1` 要启动真实 Excel，本次未在本机执行；
  脚本已按新字段与新行为改好，其中「可滑动」与「取消不留痕」两组断言是新加的
