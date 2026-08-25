# 任务

## 一、适配的撤销与恢复

- [x] 执行器在有撤销标识时，先把 `fit_range` 的隐式范围解析回参数（`ToolExecutor.Read.cs`）
- [x] `sheet.fit` 仅在确实登记成记录时回传撤销标识（`AgentChannels.cs`）
- [x] 不可撤销时回传原因，面板并入同一条提示（`AgentChannels.cs`、`chat.js`）
- [x] 原因文案只说事实加最常见成因，不枚举三种采集失败情形
- [x] 工具层补恢复方向的断言：撤销后能恢复、恢复后重新居中（`UndoTests.cs`）
- [x] 新增 `scripts\verify-fit-undo.ps1`：混合对齐下点真实按钮撤销与恢复，
      读回单元格确认对齐与列宽真的变回去、标题居中没被抹平、日志不再出现 `NOT_FOUND`
- [x] 重新部署到 `%LOCALAPPDATA%\ChatSheet\app`（现场跑的是更早的构建）

## 二、输入排队

- [x] 面板侧 FIFO 队列，单实例轮转（`pumping` 闸门），队列条目立即上屏
- [x] 排队气泡显示位次与取消按钮；取消保留原文并标为未发送
- [x] 输入框不再禁用；发送按钮改为三态，图形随含义变化
- [x] 停止与新会话清空队列，并告知取消了几条
- [x] 附件在入队时取快照，之后加的属于下一条
- [x] 附件模块增加变化通知，让粘贴/拖入也能刷新按钮含义（`attachments.js`）
- [x] 后端 `BUSY` 文案与排队行为对齐（`AgentChannels.cs`）
- [x] 布局日志带上队列长度与排队气泡数，供交叉对账
- [x] mock 新增 `slow` 场景：慢回话并原样念回输入，使发送顺序可断言
- [x] 新增五个自动化钩子：读队列状态、取消排队项、点发送按钮、点适配、读最后一条提示
- [x] 新增 `scripts\verify-chat-queue.ps1`：真实 Excel 下验证入队、按序排空、
      不撞 `BUSY`、取消、停止清空队列，且内部队列与 DOM 始终一致
- [x] 改写 `tests\web\send-stop.test.mjs` 为三态语义
- [x] 新增 `tests\web\queue.test.mjs`：轮转闸门（在途峰值为 1、顺序、不重复）
      与附件归属；两者均以变异验证断言会响

## 三、英文思考档位

- [x] `Thinking.Options` 标签改为英文原名，说明文字保留中文（`Thinking.cs`）
- [x] 选择器兜底表由「ID → 中文」改为只存说明；标签缺失时直接用 ID（`picker.js`）
- [x] `verify-picker.ps1` 的点击目标改为英文档位名
- [x] `index.html` 里关于「由脚本填入中文标签」的注释更新

## 四、文档

- [x] `docs\changes\2026-08-25-input-queue-and-fit-undo.md` 记录三件事的成因与取舍
- [x] README：能力表增加「输入排队」行、档位说明改为英文、验证命令补两个脚本
      与面板单测的全量跑法
- [x] `docs\windows-release-install.md` 说明当前 v0.2.0 压缩包早于本次修复
- [x] 新增规格 `openspec\specs\chat-input-queue\spec.md`

## 验证结果

- `ChatSheet.ToolTests`：320 项通过
- 面板单测 10 个文件：合计 209 项通过
- `verify-fit-undo.ps1`、`verify-chat-queue.ps1`、`verify-picker.ps1`、
  `verify-chat-e2e.ps1 -Approval PerWrite`、`verify-pane-focus.ps1`：均通过
- Release 构建 0 警告 0 错误；安装目录 DLL 与 web 资源与源码一致

## 未做（有意保留）

- 队列中某轮失败后，后续轮次仍继续执行，各自报一次错。判断哪些错误属于「终止性」
  需要额外的错误分类，而每条排队输入本身是独立请求。若要改成首轮失败即中止整个队列，
  另开一次改动。
