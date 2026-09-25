using System;
using System.Collections.Generic;
using System.Linq;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>工具的风险级别，决定是否需要用户审批。</summary>
    internal enum ToolRisk
    {
        /// <summary>只读，自动执行。</summary>
        Read = 0,

        /// <summary>会修改工作簿内容，需要审批。</summary>
        Write = 1,

        /// <summary>会改变工作簿结构（增删工作表等），需要审批。</summary>
        Structure = 2,
    }

    internal sealed class ToolDefinition
    {
        internal ToolDefinition(string name, string description, ToolRisk risk, object parameters)
        {
            Name = name;
            Description = description;
            Risk = risk;
            Parameters = parameters;
        }

        internal string Name { get; }

        internal string Description { get; }

        internal ToolRisk Risk { get; }

        /// <summary>JSON Schema 形态的参数定义，直接作为函数声明发给模型。</summary>
        internal object Parameters { get; }

        internal bool RequiresApproval => Risk != ToolRisk.Read;
    }

    /// <summary>
    /// 工具清单。
    ///
    /// 边界是刻意收紧的：只暴露表格操作，不提供文件系统、shell 或网络访问。
    /// 这既是安全约束，也让模型的选择空间足够小，从而更可靠。
    /// </summary>
    internal static class ToolCatalog
    {
        private static object Obj(object properties, params string[] required)
        {
            return new
            {
                type = "object",
                properties,
                required,
                additionalProperties = false,
            };
        }

        private static object Str(string description)
        {
            return new { type = "string", description };
        }

        private static object OptStr(string description)
        {
            return new { type = new[] { "string", "null" }, description };
        }

        private static object Bool(string description)
        {
            return new { type = "boolean", description };
        }

        private static readonly object SheetProp = OptStr("工作表名称。省略则使用当前活动工作表。");

        internal static readonly IReadOnlyList<ToolDefinition> All = new List<ToolDefinition>
        {
            new ToolDefinition("excel_object",
                "操作当前工作簿的 Excel COM 对象模型：行列增删、复制、筛选、边框、条件格式、验证、命名区域、图表、透视表、冻结窗格、页面设置。" +
                "root 为 workbook、worksheet、window；path 按序取属性或调用返回对象的方法（kind=get/call，member，args数组）。" +
                "最后 action=get/set/call 对 member 操作；args 用官方 COM 位置参数和枚举数值，省略参数用 {missing:true}。" +
                "对象参数用 {ref:{root,sheet,path}}；二维数据用 {matrix:[[...]]}。" +
                "例如隐藏行：root=worksheet,path=[{member:Range,args:[\"2:4\"]},{member:EntireRow}],action=set,member=Hidden,args=[true]。" +
                "修改后必须读回验证；无自动撤销。数据读取优先 read_range 分页。只支持当前工作簿表格对象，不提供文件、宏或应用程序入口。",
                ToolRisk.Structure,
                Obj(new {
                    root = Str("workbook、worksheet 或 window。"), sheet = SheetProp,
                    path = new { type = "array", items = new { type = "object", properties = new {
                        kind = OptStr("get（默认）或 call。"), member = Str("官方对象模型成员名称。"),
                        args = new { type = "array", items = new { } } } } },
                    action = Str("get、set 或 call。"), member = Str("最终成员名称。"),
                    args = new { type = "array", items = new { } }
                }, "root", "path", "action", "member")),
            new ToolDefinition(
                "get_workbook_info",
                "获取当前工作簿的结构摘要：文件名、工作表清单、各表已用范围与行列数。开始任何任务前应先调用它了解全局。",
                ToolRisk.Read,
                Obj(new { })),

            new ToolDefinition(
                "get_selection",
                "获取用户当前选中的范围地址与尺寸。用户说“这里”“这一列”时用它确定目标。",
                ToolRisk.Read,
                Obj(new { })),

            new ToolDefinition(
                "read_range",
                "读取指定范围，每页最多 5000 格。返回 next_offset 时用原范围和该 offset 继续，直到 null。",
                ToolRisk.Read,
                Obj(
                    new
                    {
                        range = Str("范围地址，例如 A1:D20。"),
                        sheet = SheetProp,
                        include_formulas = Bool("为真时同时返回公式文本，而不只是计算结果。"),
                        offset = new { type = "integer", description = "按行遍历的单元格偏移，默认 0，续页使用 next_offset。" },
                    },
                    "range")),

            new ToolDefinition(
                "write_values",
                "向范围写入字面值。values 的行列数必须与范围尺寸一致。大范围自动分块写入；输入仍需适合模型的单次输出长度。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址，例如 A1:C3。"),
                        sheet = SheetProp,
                        values = new
                        {
                            type = "array",
                            description = "二维数组，外层为行、内层为列。元素可为字符串、数字、布尔或 null。",
                            items = new { type = "array", items = new { type = new[] { "string", "number", "boolean", "null" } } },
                        },
                    },
                    "range", "values")),

            new ToolDefinition(
                "write_formulas",
                "向范围写入公式。每个元素需以 = 开头。大范围自动分块写入；输入仍需适合模型的单次输出长度。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址。"),
                        sheet = SheetProp,
                        formulas = new
                        {
                            type = "array",
                            description = "二维公式数组，外层为行、内层为列，元素为以 = 开头的公式文本。",
                            items = new { type = "array", items = new { type = "string" } },
                        },
                    },
                    "range", "formulas")),

            new ToolDefinition(
                "format_range",
                "设置范围的字体与填充样式。只需提供要改变的属性，未提供的保持原样。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址。"),
                        sheet = SheetProp,
                        bold = new { type = new[] { "boolean", "null" }, description = "加粗。" },
                        italic = new { type = new[] { "boolean", "null" }, description = "倾斜。" },
                        font_size = new { type = new[] { "number", "null" }, description = "字号，单位磅。" },
                        font_color = OptStr("字体颜色，十六进制如 #FF0000。"),
                        fill_color = OptStr("填充色，十六进制如 #FFFF00。"),
                        horizontal_alignment = OptStr("水平对齐：left、center、right。"),
                        vertical_alignment = OptStr("垂直对齐：top、center、bottom。"),
                        wrap_text = new { type = new[] { "boolean", "null" }, description = "自动换行。" },
                    },
                    "range")),

            new ToolDefinition(
                "set_number_format",
                "设置范围的数字格式代码，例如 0.00、#,##0、yyyy-mm-dd、0.0%。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址。"),
                        sheet = SheetProp,
                        format_code = Str("Excel 数字格式代码。"),
                    },
                    "range", "format_code")),

            new ToolDefinition(
                "autofit_range",
                "按内容自动调整列宽或行高。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址，例如 A:D 或 A1:D20。"),
                        sheet = SheetProp,
                        target = Str("调整对象：columns 调列宽，rows 调行高。"),
                    },
                    "range", "target")),

            new ToolDefinition(
                "fit_range",
                "一次完成适配：设置水平与垂直对齐，按内容调整列宽后将超过 100 的列限制为 100，启用自动换行并调整行高。" +
                "用户说「适配」「排版整理一下」这类要求时优先用它，比分别调用 format_range 与 autofit_range 更省步数。" +
                "省略 range 表示适配整张表的已用范围，这也是面板「适配」按钮的行为。不受单元格数量限制。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = OptStr("目标范围地址。省略则取该表的已用范围。"),
                        sheet = SheetProp,
                        horizontal_alignment =
                            OptStr("水平对齐：left、center、right。省略为 center。垂直方向固定居中。"),
                    })),

            new ToolDefinition(
                "merge_cells",
                "把范围合并成一个单元格。用户说「合并单元格」「跨列居中」「把标题横过来」时用它。" +
                "只有左上角单元格的内容会保留，其余内容会被丢弃，因此合并前应先读一遍范围确认没有要保住的值。" +
                "大范围也可执行，但超出快照预算时不提供自动撤销。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("要合并的范围地址，例如 A1:D1。必须多于一个单元格。"),
                        sheet = SheetProp,
                        across = Bool(
                            "为真时逐行分别合并（每行合成一格，行与行不相连），为假或省略时整片合成一格。"),
                        horizontal_alignment =
                            OptStr("同时设置水平对齐：left、center、right。省略则保持原有对齐。"),
                        vertical_alignment =
                            OptStr("同时设置垂直对齐：top、center、bottom。省略则保持原有对齐。"),
                    },
                    "range")),

            new ToolDefinition(
                "unmerge_cells",
                "取消范围内的单元格合并，把合并区域拆回独立单元格。" +
                "原合并区域的内容留在左上角单元格，其余单元格为空。" +
                "大范围也可执行，但超出快照预算时不提供自动撤销。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("范围地址。范围内与之相交的合并区域都会被拆开。"),
                        sheet = SheetProp,
                    },
                    "range")),

            new ToolDefinition(
                "clear_range",
                "清除范围内容、格式或两者。这是破坏性操作。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("目标范围地址。"),
                        sheet = SheetProp,
                        scope = Str("清除范围：contents 仅内容，formats 仅格式，all 全部。"),
                    },
                    "range", "scope")),

            new ToolDefinition(
                "add_worksheet",
                "新增一张工作表。",
                ToolRisk.Structure,
                Obj(
                    new
                    {
                        name = Str($"新工作表名称，最长 {ToolLimits.MaxSheetNameLength} 个字符。"),
                        after_sheet = OptStr("插入到该工作表之后。省略则置于末尾。"),
                    },
                    "name")),

            new ToolDefinition(
                "rename_worksheet",
                "重命名工作表。",
                ToolRisk.Structure,
                Obj(
                    new
                    {
                        old_name = Str("现有工作表名称。"),
                        new_name = Str($"新名称，最长 {ToolLimits.MaxSheetNameLength} 个字符。"),
                    },
                    "old_name", "new_name")),

            new ToolDefinition(
                "sort_range",
                "按指定列排序范围。",
                ToolRisk.Write,
                Obj(
                    new
                    {
                        range = Str("待排序范围地址，应包含数据区域。"),
                        sheet = SheetProp,
                        key_column = Str("排序依据的列字母，例如 B。必须落在 range 之内。"),
                        ascending = Bool("为真升序，为假降序。"),
                        has_header = Bool("范围首行是否为标题行。"),
                    },
                    "range", "key_column")),

            new ToolDefinition(
                "create_table",
                "把范围转换为表格（带筛选与样式）。",
                ToolRisk.Structure,
                Obj(
                    new
                    {
                        range = Str("表格范围地址，应包含标题行。"),
                        sheet = SheetProp,
                        name = OptStr("表格名称。省略则由宿主自动命名。"),
                        has_header = Bool("首行是否为标题行。"),
                    },
                    "range")),

            new ToolDefinition(
                "create_chart",
                "基于数据范围创建图表。",
                ToolRisk.Structure,
                Obj(
                    new
                    {
                        range = Str("数据范围地址。"),
                        sheet = SheetProp,
                        chart_type = Str("图表类型：column、bar、line、pie、scatter、area。"),
                        title = OptStr("图表标题。"),
                    },
                    "range", "chart_type")),
        };

        internal static ToolDefinition Find(string name)
        {
            return All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        }
    }
}
