# 2026-08-24：单元格上限调整与「适配」按钮

本次改动包含三件事：把读取上限与上下文预算按 272k 窗口重算、给 `format_range`
补上垂直对齐、新增面板「适配」按钮及其背后的 `fit_range` 工具。

改动均已构建、测试并安装到本机（`%LOCALAPPDATA%\ChatSheet\app`）。

## 一、读取上限 2,000 → 5,000

`src/ChatSheet.AddIn/Tools/ToolLimits.cs`

```csharp
internal const int MaxReadCells = 5_000;   // 原 2_000
```

调整后 `ToolLimits` 里六个范围类常量全为 5,000，读写不再分档。
`MaxAutofitDimensions` 数的是行列数而非单元格数，共用该值只是数值巧合。

### 为什么原来是 2,000

上限不来自 Excel 或模型服务商，是项目自设的闸。校验在碰 COM 之前发生
（`RangeResolver.AssertCellLimit`），超限抛 `RANGE_TOO_LARGE`，作为结构化结果
回传模型，让它自行缩小范围重试，而不是崩溃。

读取与写入卡的是两件不同的事：

- 读取卡上下文。结果整片进对话历史，几轮全量读取就能顶到压缩阈值。
- 写入卡爆炸半径与响应时间。结果只回条数，不占上下文。

### 为什么 5,000 是这个窗口下的落点

按 `Conversation.EstimateTokens` 的同一套算法（中文 1.5 字/token、其他
4 字符/token）实测 200 行 × 10 列紧凑 JSON 的每单元格成本：

| 数据形态 | token/单元格 |
| --- | --- |
| 短整数 | 0.81 |
| 空单元格 | 1.31 |
| 数字（金额/单价） | 2.31 |
| 典型混合表（3 成中文 / 6 成数字 / 1 成空） | 2.33 |
| 中文短文本（4 字） | 3.48 |
| 日期字符串 | 5.56 |
| 中文长文本（12 字） | 8.14 |
| 混合表 + `include_formulas` | 5.05 |

跨度近 10 倍，定上限须按最坏那档（中文密集约 8 token/格）算。

真正的天花板不是预算本身，而是 `Conversation.TrimToBudget` 的保护尾部：
最后 6 条消息永不压缩、永不丢弃，工具调用循环里约装 3 轮工具结果。若这 3 条
本身就超过预算的 70%（压缩目标线），压缩会在边界检查处 break，带着超限的
上下文发出去——上下文管理直接失效。

于是约束是 **3 × 单次读取上限（最坏形态）≤ 预算 × 70%**。按预算 200,000
（目标线 140,000）：

| MaxReadCells | 混合表 | 全中文长文本 | 3 条全中文并存 |
| --- | --- | --- | --- |
| 2,000（原值） | 4.7k | 16.3k | 49k，余量很大 |
| **5,000（现值）** | 11.6k | 40.7k | **122k，贴着 140k** |
| 8,000 | 18.6k | 65.1k | 195k，超过压缩阈值 180k |
| 10,000 | 23.3k | 81.4k | 244k，超窗口 |

8,000 在中文密集表上会把压缩器逼死，故取 5,000。

## 二、上下文预算 100,000 → 200,000

`src/ChatSheet.AddIn/Storage/Settings.cs`（属性默认值与反序列化回退值两处）

```csharp
internal int ContextBudgetTokens { get; set; } = 200_000;
```

取 200,000 而非窗口全额 272,000，留 72k 余量给三件事：

- **估算器不算图片。** `EstimateTotalTokens` 只累加 `Content` 与 `ToolCalls`，
  `ChatMessage.Images` 完全不计。图片在真实窗口里占的量对估算器是隐形的。
- **输出与输入共享窗口。** `MaxOutputTokens` 默认 8192，多数服务商如此。
- **估算器在纯数字上可能偏低。** 数字的实际分词往往比 4 字符/token 更碎。
  它在中文上偏保守，在数字上不一定。

`Settings.Normalize` 的硬上限是 2,000,000，200k 不会被截。

> 已存在的设置文件不会自动迁移到新默认值。只有全新安装或在设置页手动改过
> 才会用上 200,000；旧文件里存的 100,000 会照旧读出来。

## 三、垂直对齐

`format_range` 原本只有水平对齐。新增 `vertical_alignment`，取值
`top`、`center`、`bottom`。

需要独立的解析函数而不能复用水平那套：`xlCenter`（-4108）两个方向共用，
但顶部（`xlTop`，-4160）与底部（`xlBottom`，-4107）各有专属值。

撤销快照同步补上这一项（`FormatSnapshot.VerticalAlignment`，采集与还原两处）。
漏掉的话，撤销会把垂直居中留在表上。

## 四、面板「适配」按钮

`src/web/index.html` 控制栏第四个按钮，排在「图片」之后。该行原本已有
`flex-wrap: wrap`，窄栏挤不下时按钮换行而非溢出，无需改布局。

### 行为

鼠标悬停或点击按钮展开浮层，三选一：**靠左 / 居中（默认）/ 靠右**。选中哪一项
就立刻按那一项对**当前工作表的已用范围**做四件事：

1. 水平对齐（按所选）
2. 垂直居中
3. 自动调整列宽
4. 自动调整行高

不必先选区——按钮的意思就是「把这页排好」。

顺序不能调换。对齐会改变文本排布，进而影响自动调整算出的行高；列宽必须先于
行高，因为列变窄后换行的文本需要更高的行才放得下。

### 为什么水平给选项、垂直不给

水平对齐是排版偏好：表头常居中，文字列常靠左，数字列常靠右，没有普适答案。

垂直方向不同。适配要解决的问题本身就是「行变高后文字贴顶」，居中是唯一合理
答案，给它开选项只会多一个没人会改的旋钮。

### 为什么做成浮层而不是先做后改

对齐必须在动手前定。若先按默认适配、再让用户改对齐，就变成两次操作、两条撤销
记录——正是合成 `fit_range` 要避免的事。

悬停与点击都能展开：悬停顺手，点击照顾键盘与触摸。选中即执行并收起浮层，
当前选项在浮层里高亮，所以「现在是哪个」一眼可见。

选择记在会话内（`fitAlignment`，默认 `center`），连续排版不必每次重选；没有落盘
成持久设置——适配是即时动作，为它写一条设置项不值得。

浮层与按钮之间那 4px 空隙用一块透明补丁（`.fit-pop::after`）接上。不接的话，
鼠标从按钮移向选项的途中会先离开容器，浮层随即收起，选项根本点不到。

### 为什么合成一个工具

合成 `fit_range` 而不是让调用方连发三次（`format_range` + 两次
`autofit_range`），是为了撤销的原子性：三次调用会留下三条撤销记录，用户点
一次却要撤三次才回到原状。

### 为什么不经过模型

面板按钮走 `sheet.fit` 通道直接调工具。这是确定性的排版动作，点按钮已经表达
了意图，再让模型转述一遍只会增加延迟、token 开销和被误解的可能。

`fit_range` 同时也在工具目录里，所以模型仍可在用户用文字提出「适配一下」时
自行调用。省略 `range` 即表示整表已用范围，与按钮行为一致。

### 撤销

操作完成后在对话流插入一条带「撤销」按钮的提示胶囊
（`addUndoableNotice`）。面板直接发起的操作没有对应的工具卡片，撤销按钮无处
可挂，故挂在提示上。

这一步是必需的：加载项经 COM 的写入会清空 Excel 自身的撤销栈，Ctrl+Z 拿不
回原来的排版。

### 适配不受单元格上限约束

`fit_range` 刻意不调用 `AssertCellLimit`。上限的两个理由在它身上都不成立：

- 读取受限是怕结果撑爆上下文，而适配不回传数据；
- 写入受限是怕误伤范围过大难恢复，而适配只动对齐与行列尺寸，且留有撤销记录。

剩下的唯一成本是 COM 耗时，由面板侧放宽超时承担（见下）。

### 去掉上限带来的连带问题：快照成本

`TryCapture` 原本对超过 5,000 单元格的范围直接返回 null，即不登记撤销。若不动
它，整表适配会「成功但没有撤销按钮」——最难察觉的一类失败。

原因在 `SnapshotDetail.Format` 会附带读一整片 `NumberFormatLocal`，这是
O(单元格) 的开销，也是整个快照里唯一随单元格数增长的部分。而适配根本不改数字
格式，这片读取纯属浪费。

于是新增 `SnapshotDetail.Alignment`：只采范围级的对齐与字体填充，不读数字格式
矩阵。`fit_range` 用 `Alignment | Size`，快照成本降到 O(行+列)——几万行的表也
只是几千个 double。

`Restore` 无需改动：它本就是按「哪些字段有值」驱动的，新的 detail 组合自动生效。

`TryCapture` 的上限随之改为按维度而非单元格：

```csharp
var cellwise = (detail & (SnapshotDetail.Content | SnapshotDetail.Format)) != 0;
if (cellwise)
{
    if (range.CellCount > ToolLimits.MaxWriteCells) { return null; }
}
else if (range.Rows + range.Columns > ToolLimits.MaxSnapshotDimensions)
{
    return null;
}
```

`MaxSnapshotDimensions = 50_000`（行数加列数）。这类快照每行每列各一次 COM
调用，单次约 0.1 毫秒，取 5 万意味着最坏约 5 秒——比失去撤销更可接受。
超过则不登记撤销，但**适配本身照常执行**。

### 面板超时 30 秒 → 5 分钟

整表适配在超大表上可能跑上一两分钟。默认 30 秒会误报超时，而宿主那边其实还在
正常执行——这种失败最难排查。

```javascript
const result = await request('sheet.fit', {}, { timeout: 300000 });
```

## 已知边界

- **`UsedRange` 的定义偏宽。** 它是 Excel 自己对「数据在哪」的判断，包含曾有
  数据或格式、现在已空的单元格。远处一个残留格式的单元格会让适配范围比预期更大。
  空表时 `UsedRange` 仍返回 A1，故 `UsedRangeAddress` 额外读一次 `CountLarge`
  与值来区分「一格数据」和「完全空表」，后者报 `NO_DATA`。
- **撤销的对齐是范围级的。** `HorizontalAlignment`/`VerticalAlignment` 在范围内
  不一致时宿主返回 null，快照如实记录 null，还原时跳过。所以若适配前整片对齐
  本就参差，撤销会把它还原成统一值而不是原来的参差状态。行高列宽是逐行逐列
  记录的，还原精确。
- **`autofit_range` 与 `format_range` 仍受 5,000 约束。** 只有 `fit_range` 是
  不经模型即可触达的路径，故只给它解开。
- **适配范围过大时会明显卡顿。** 撤销快照的采集与还原各需每行每列一次 COM
  调用，没有批量接口。20 万行的表意味着约 20 万次往返，采集与还原各约 20 秒，
  且 COM 是单线程的，期间界面无响应。若这成为瓶颈，正确的解法是把
  `ReadColumnWidths`/`ReadRowHeights` 改成一次数组读取，属于独立改动。

## 改动文件

后端：

| 文件 | 改动 |
| --- | --- |
| `Tools/ToolLimits.cs` | `MaxReadCells` 5,000；新增 `MaxSnapshotDimensions` |
| `Tools/ToolExecutor.Structure.cs` | 新增 `FitRange`（含 `horizontal_alignment`）、`UsedRangeAddress` |
| `Tools/ToolExecutor.Write.cs` | `vertical_alignment` 与 `ParseVerticalAlignment` |
| `Tools/ToolExecutor.Read.cs` | 分派与撤销说明加 `fit_range`；`TryCapture` 改按维度设限 |
| `Tools/ToolCatalog.cs` | 新增 `fit_range` 定义；`format_range` 加 `vertical_alignment` |
| `Tools/UndoStore.cs` | `fit_range` → `Alignment \| Size` |
| `Tools/SnapshotCapture.cs` | 新增 `Alignment` 维度；垂直对齐的采集与还原 |
| `Tools/UndoTypes.cs` | `FormatSnapshot.VerticalAlignment` |
| `Bridge/AgentChannels.cs` | 新增 `sheet.fit` 通道，转发对齐选择 |
| `Storage/Settings.cs` | 预算默认值 200,000（两处） |
| `Agent/SystemPrompt.cs` | 上限文案改为读写均 5,000 |

面板：

| 文件 | 改动 |
| --- | --- |
| `web/index.html` | 「适配」按钮与对齐浮层（三选一） |
| `web/scripts/chat.js` | `sheet.fit` 调用、`addUndoableNotice`、5 分钟超时 |
| `web/styles/app.css` | `.notice-undo`、`.fit` 系列浮层样式 |

文档与测试：`README.md` 限制表、`tests/ChatSheet.ToolTests/Program.cs`、
`tests/mock-provider/server.mjs` 注释。

`docs/releases/v0.1.0.md` 未改——那是已发行版本的存档记录，其中的 2,000 是当时
的事实。`scripts/verify-*.ps1` 里的 `contextBudgetTokens = 100000` 也未改，那是
刻意调小的测试夹具，bulk 场景要靠它在 12 轮内堆到 90% 阈值来验证压缩路径。

## 验证记录

- `dotnet build ChatSheet.sln --configuration Release`：0 警告、0 错误
- `ChatSheet.ToolTests.exe`：通过 274、失败 0（较改动前 265 增 9 条）
- 7 个 web 测试套件：18 / 24 / 5 / 15 / 13 / 5 / 27 全通过
- `install.ps1 -Action install`：LoadBehavior=3，x86/x64 两个视图的类注册与程序集
  键均在位；已部署 DLL 与构建产物字节一致，且含 `fit_range`、`sheet.fit`、
  `MaxSnapshotDimensions`、`horizontal_alignment` 四个新符号；面板三个选项与
  `initFit` 均已部署

> 覆盖安装要替换正被 Excel 加载的 DLL，因此安装前必须完全退出 Excel。脚本会
> 实测文件占用并在被占用时中止，不会强杀进程，也不会留下半新半旧的产物。

新增 11 条工具测试用例，其中 `fit_range {"range":"A:A"}` 替换了一条原先断言
「适配超限被拦」的用例（那条随上限解除而失效），故总数净增 9：

| 用例 | 断言 |
| --- | --- |
| `read_range {"range":"A1:E1000"}` | 正好 5,000 格放行 |
| `read_range {"range":"A1:E1001"}` | 5,001 格拦截 |
| `format_range` + `vertical_alignment:"center"` | 生效并回报该项 |
| `format_range` + `vertical_alignment:"middle"` | 拒绝，`ARG_INVALID` |
| `fit_range {"range":"A1:C2"}` | 回报四项改动 |
| `fit_range {"range":"A:A"}` | 整列约百万格放行（与 `read_range` 的 `A:A` 被拦形成对照） |
| `fit_range {}` | 自行取已用范围 |
| `fit_range` 不带 `horizontal_alignment` | 回报 `center`，即默认值 |
| `fit_range` + `horizontal_alignment:"left"` | 回报 `left` |
| `fit_range` + `horizontal_alignment:"right"` | 回报 `right` |
| `fit_range` + `horizontal_alignment:"justify"` | 拒绝，`ARG_INVALID` |

读取上限的两条边界用例锁住临界点：将来改动 `MaxReadCells` 会立刻失败，提示
重新核算预算。
