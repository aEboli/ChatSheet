# 验证与交付记录

## 实际交付

- 本机安装更新到 0.10.3.14，安装 DLL 与 Release 构建 SHA256 相同。
- 本机设置已读回 approval=Automatic、maxSteps=0；原设置保存在 settings.json.before-open-operations.bak。
- 安装器 x86/x64 COM 注册及宿主登记自检通过。独立 Excel COM 实例确认加载项 Connect=True，日志出现 OnConnection 和 CTPFactoryAvailable。
- 原 17 个专用工具保留；新增 excel_object 结构化对象遍历、属性读取/写入、方法调用、对象参数引用、二维数组及缺省参数。
- 默认自动执行实测无需审批回调。可选审批仍按原规则；工作簿切换、取消及撤销绑定回归通过。
- 读取分页和宽范围尾页验证通过；写入分块末格和第二块取消后的部分进度验证通过。
- 大范围合并结果为单个区域时不再逐格查同一合并区域；大范围丢值计数交给宿主 CountA。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| dotnet build ChatSheet.sln -c Release --nologo | 0 警告、0 错误 |
| 完整 ChatSheet.ToolTests | 765 通过、0 失败（其后新增审查项单独运行） |
| --project-audit-tests | 11 通过、0 失败，含通用入口自动执行及八次相同失败终止 |
| --open-operations-tests，Excel | 34 通过、0 失败，复制项目验证的是失败如实反馈 |
| --open-operations-tests --wps | 34 通过、0 失败，复制读回成功 |
| Web 测试 | 全部文件通过；版本更新后 version.test.mjs 定向复测 7/7 |
| OpenSpec | 提案严格校验通过，归档前再次校验 |

结果日志保存在 artifacts/open-operations-0.10.3.14。没有向配置的真实模型发送收费测试对话；不将 COM 和协议回归描述为真实模型端到端验收。

## 已执行能力样本及限制

Excel/WPS 临时工作簿覆盖：二维写入、读取、分页、分块中止、边框、筛选、数据验证、条件格式、命名区域、单元格插入、行隐藏、多区域字体、页面方向、冻结窗格、透视缓存/透视表及字段配置。原有新增/重命名工作表、建表、建图、排序、清空、合并和撤销在完整回归中验证。

本机 Excel 16.0 Range.Copy 返回宿主错误；单独使用 Excel COM、显示窗口后再次调用也复现。WPS 同场景复制成功。该差异仍存在，不用成功断言掩盖失败。未枚举所有 COM 成员、所有图表类型、打印机或外部数据连接；通用工具开放入口并不表示这些能力逐项验收完成。

原有专用矩阵工具仍拒绝非连续区域，模型可按连续区域调用；支持并集的格式等操作通过 excel_object 调用宿主原生方法。通用对象调用无自动撤销，失败可能有部分改动，结果会要求读回核实。读取页大小按单元格数控制，超长文本密集表仍受模型上下文预算限制。宿主不可中断的单个 COM 方法必须等待返回，用户停止会阻止之后的调用和写入块。

## 官方资料

- https://learn.microsoft.com/en-us/office/vba/api/overview/excel/object-model
- https://learn.microsoft.com/en-us/office/vba/api/excel.range.autofilter
- https://learn.microsoft.com/en-us/office/vba/api/excel.validation.add
- https://learn.microsoft.com/en-us/office/vba/api/excel.range.copy
- https://learn.microsoft.com/en-us/office/vba/api/excel.pivotcaches.create
