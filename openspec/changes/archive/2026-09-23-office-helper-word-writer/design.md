# 设计

## 入口与边界

保留 `ChatSheet.AddIn` 作为 Excel/WPS 表格入口，新增独立的 `ChatWord.AddIn` 入口。两个入口可以部署到同一安装目录并共享 WebView2 静态资源、模型提供器、设置和会话协议，但不共享 Workbook/Word 宿主对象或工具执行器。

新的入口实现 `IDTExtensibility2`、`ICustomTaskPaneConsumer` 和 `IRibbonExtensibility`，沿用仓库已验证的手写 Office 扩展性接口和 `IReflect` Ribbon 分派。若 WPS Writer 不提供 CTP 工厂，入口必须返回诊断错误并保留可用的 Ribbon/顾问模式，不能假报面板已打开。

## 共享层

- `Providers`、`Storage`、`Conversation`、模型协议、WebView2 导航安全、消息 envelope、审批响应等待、操作卡生命周期和基础日志可共享。
- `TaskPaneControl`、`TaskPaneController`、宽度/主题/焦点处理可抽成宿主无关核心；宿主绑定的 Application accessor、标题、能力和路由由 Word/Excel 入口注入。
- `ChangePreview` 的卡片生命周期和截断规则共享；Word 预览数据使用 `targetLabel`、前后文本、段落/表格位置，不复用 `sheet`/`address` 字段。
- 将来可以抽出 `IHostContext`、`IHostTargetResolver`、`IHostToolExecutor` 和 `IHostUndoStore` 接口，但本变更不先重写稳定的 Excel 实现。

## Word 宿主层

- `WordHostProbe`：按当前进程名和 ProgID 识别 Microsoft Word/`Word.Application` 与 WPS Writer/`kwps.Application`；`Application.Name` 只作为日志，不作为唯一识别依据。
- `WordDocumentContext`：在一次 UI/STA 调度内采集当前文档、选区、故事、段落、表格和能力摘要。未保存文档的空 `Path` 不能被当成当前目录。
- `WordTarget` 与 `WordTargetResolver`：只接受显式 Story + Start/End、段落索引、表格索引/行/列、Bookmark 或 ContentControl；目标解析后保存文档实例身份、story type、Start/End 和必要的 locator。
- `WordToolExecutor`：专用白名单工具逐条执行，所有可选成员先探测；失败返回 `UNSUPPORTED_MEMBER`、`TARGET_NOT_FOUND`、`PROTECTED_DOCUMENT`、`TRACK_CHANGES_REQUIRED` 等具体错误。
- `WordSnapshotCapture`：保存正文/Story 范围文本、字符/段落格式、表格结构及可验证的目标身份。无法完整保存的操作不登记撤销；Word/WPS 的 `Document.Undo`/`Redo` 只能作为宿主行为探测，不能作为带 ID 的唯一撤销实现。

## Story 与内部标记

Word 的 Range 是某个 Story 内的字符区间，不是 Excel 单元格。读取结果分为显示文本和结构元数据：正文/段落结尾 `Chr(13)`、表格单元格结尾 `Chr(13) + Chr(7)`、字段代码、内容控件边界和隐藏文本不直接展示给用户；需要编辑时保留内部 Start/End 和原始标记。StoryRanges 枚举不完整时，必须按已知 story 类型单独探测并报告不可读的 Story。

## UI 与安装

Word Ribbon 使用独立 XML 和回调，按钮采用“文档助手面板”“设置”“诊断”“撤销”等文案，不放置表格专属快捷动作。产品层显示 Office-helper；旧 ChatSheet Excel 注册保持不变。

COM 类注册继续写入 HKLM 的 32 位与 64 位 Classes 视图。Word 宿主登记使用 `HKCU\\Software\\Microsoft\\Office\\Word\\Addins\\<ProgID>`。WPS Writer 登记路径必须由目标版本探针或官方资料确认，不能照抄 ET 路径；安装器应在无法确认时跳过该路径并在诊断中说明原因。

## 线程、释放与验证

Word/WPS Application、Document、Range、Story、Paragraph、Table、Cell、Find、Style、Font、ParagraphFormat、PageSetup 等 COM 引用只在 UI/STA 委托内使用，离开前按反向顺序释放。WebView2 推送沿用现有 `SynchronizationContext` 出口。每次写入在审批后重新确认文档实例和目标，执行后读回实际文本/格式/数量；读回失败时返回不确定状态而非成功。
