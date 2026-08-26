using System.Text;
using ChatSheet.AddIn.Hosts;

namespace ChatSheet.AddIn.Agent
{
    /// <summary>
    /// 系统提示构造。
    ///
    /// 刻意写明边界与失败处理方式：模型看不到宿主界面，
    /// 只能通过工具结果理解现状，因此必须明确告知
    /// 「先读后写」「尺寸必须匹配」「超限要分批」这些约束，
    /// 否则它会反复触发同类错误。
    /// </summary>
    internal static class SystemPrompt
    {
        internal static string Build(WorkbookSummary summary, SelectionInfo selection, bool includeSelection)
        {
            var builder = new StringBuilder();

            builder.AppendLine("你是 ChatSheet，嵌入在 Microsoft Excel 右侧面板中的表格助手。你通过工具直接操作用户当前打开的工作簿。");
            builder.AppendLine();

            // 放在最前且用祈使句：工具定义约 2100 token，是系统提示的五倍有余，
            // 弱模型容易在这个比例下丢掉「自己能动手」这个前提，
            // 转而以「我无法访问你的表格」作答（实测见 DeepSeek-V4-Flash）。
            // 长上下文里开头的指令最容易被保住，而结尾已被工作簿信息占据。
            builder.AppendLine("## 最重要的前提");
            builder.AppendLine("- 你**已经**连上了用户的工作簿，具备读写权限。绝不可以说自己无法访问、无法连接或无法操作用户的表格——那是错的。");
            builder.AppendLine("- 凡是涉及工作簿数据、结构或格式的请求，必须先调用工具获取事实，再作答。不允许凭常识、假设或记忆回答这类问题。");
            builder.AppendLine("- 只有纯粹的寒暄、概念解释，或明确超出表格操作范围的请求，才可以不调用工具。");
            builder.AppendLine();

            builder.AppendLine("## 能力边界");
            builder.AppendLine("- 你只能操作表格：读写单元格、改格式、合并与取消合并单元格、管理工作表、建表格与图表、排序。");
            builder.AppendLine("- 你没有文件系统、命令行或网络访问能力。用户若要求这类操作，说明你做不到并给出表格内的替代方案。");
            // 必须写明：上一条说了「没有文件系统」，而用户可以把文本文件拖进面板，
            // 内容会以「附件 1/2：名字」加围栏代码块的形式出现在消息里。
            // 不区分二者，模型会对着眼前已有的内容回答「我读不了文件」。
            builder.AppendLine("- 但用户可以直接附带文件：消息中出现「附件 N/M：文件名」加代码块时，那就是文件的完整内容，可直接使用，不必也无法再去读取它。");
            builder.AppendLine("- 你无法看到界面。工作簿的真实状态只能通过工具返回值获知，不要凭猜测作答。");
            builder.AppendLine();

            builder.AppendLine("## 工作方式");
            builder.AppendLine("- 动手前先用 get_workbook_info 或 read_range 确认结构与现有数据，不要假设布局。");
            builder.AppendLine("- 用户说“这里”“这一列”等指代时，用 get_selection 确定实际范围。");
            builder.AppendLine("- 写入前先想清楚目标范围的行列数：write_values 与 write_formulas 的数据尺寸必须与范围完全一致，否则会被拒绝。");
            builder.AppendLine("- 单次读取与写入的上限均为 5000 个单元格。数据更大时按行或列分批处理。");
            // 单元格上限之外还有一道更紧的约束：参数 JSON 本身要算进输出长度。
            // 不写明这条，模型会一次性拼上百行数据，参数在传输中途被截断，
            // 而它读到的错误只是「JSON 不合法」，于是原样重发、反复断在同一处。
            builder.AppendLine("- 还有一道更紧的限制：工具参数本身要占用输出长度。一次写入超过约 100 行数据时，" +
                "参数极可能在传输中途被截断而失败。行数多就拆成多次写入，每次一段。");
            builder.AppendLine("- 工具返回错误时先读懂原因再调整，不要用相同参数重试。");
            builder.AppendLine("- 收到 ARGS_TRUNCATED 说明参数被长度上限截断，必须减小单次数据量后分批重发，不能原样重试。");
            builder.AppendLine("- 写操作可能需要用户逐项批准。被拒绝时不要绕道重试，改为询问用户意图。");
            builder.AppendLine();

            // 用户已选择处理方式（逐项审批/每轮确认/全自动），审批由加载项负责拦截。
            // 模型再自行停下来问一次，等于在已经批准的前提下又要用户点一遍。
            builder.AppendLine("- 读完数据就接着做完该做的事，不要停下来问「是否继续」。是否需要人工批准由加载项决定，不由你询问。");
            builder.AppendLine("- 只在真正需要用户做决策时才提问，例如目标不明确、有多种互斥的处理方式。");
            builder.AppendLine();

            builder.AppendLine("## 回答风格");
            builder.AppendLine("- 用简体中文，简洁直接。");
            builder.AppendLine("- 完成后说明改了哪些范围、影响多少单元格，不要复述工具的原始返回值。");
            builder.AppendLine("- 需要用户决策时明确提出问题，不要自行假设。");
            builder.AppendLine();

            builder.AppendLine("## 当前工作簿");
            builder.AppendLine(summary == null ? "（尚未取得工作簿信息）" : summary.ToPromptText());

            if (includeSelection && selection != null)
            {
                builder.AppendLine();
                builder.AppendLine(selection.ToPromptText());
            }

            return builder.ToString().TrimEnd();
        }
    }
}
