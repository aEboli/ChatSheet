using System;
using System.Globalization;
using System.Text;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.AddIn.Agent
{
    /// <summary>
    /// 系统提示构造。
    ///
    /// 刻意写明边界与失败处理方式：模型看不到宿主界面，
    /// 只能通过工具结果理解现状，因此必须明确告知
    /// 「先读后写」「尺寸必须匹配」「超限要分批」这些约束，
    /// 否则它会反复触发同类错误。
    ///
    /// 三种工具形态各要一套说法。最要紧的是顾问模式必须收回「你已经连上工作簿」
    /// 这句话：一个既不会调用工具、又被反复告知自己有读写权限的模型，
    /// 会直接编造出「我已经填好了」这种回答。
    /// </summary>
    internal static class SystemPrompt
    {
        /// <summary>
        /// 星期名写死，不走 CultureInfo：这台机器的区域设置未必是中文，
        /// 而提示的其余部分都是中文，混出「Friday」只会让模型跟着换语言。
        /// </summary>
        private static readonly string[] WeekdayNames =
        {
            "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六",
        };

        /// <param name="now">
        /// 当前时间，默认取本机时间。留出参数只为可测：测试要能钉住一个固定时刻，
        /// 否则断言得跟着真实时钟走。
        /// </param>
        internal static string Build(
            WorkbookSummary summary,
            SelectionInfo selection,
            bool includeSelection,
            ToolProtocolMode toolMode = ToolProtocolMode.Native,
            DateTimeOffset? now = null)
        {
            var builder = new StringBuilder();

            if (toolMode == ToolProtocolMode.None)
            {
                AppendAdvisorHeader(builder);
            }
            else
            {
                AppendOperatorHeader(builder, toolMode);
            }

            AppendCurrentTime(builder, now ?? DateTimeOffset.Now);

            builder.AppendLine("## 当前工作簿");
            builder.AppendLine(summary == null ? "（尚未取得工作簿信息）" : summary.ToPromptText());

            if (includeSelection && selection != null)
            {
                builder.AppendLine();
                builder.AppendLine(selection.ToPromptText());
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 当前时间。三种工具形态都要，因此放在共用段落里。
        ///
        /// 模型自身没有时钟，「今天」只能来自训练数据的截止时间，
        /// 于是「日期写今天」会得到一个看着像真日期、实际差了大半年的值
        /// （实测：2026-09-04 当天写出 2026-01-19）。这类错误不报错、
        /// 形态又完全合法，用户不逐格核对就发现不了，所以必须直接给出事实。
        ///
        /// 放在工作簿信息之前、指令段落之后：这两段都是每轮重采的事实，
        /// 归在一起；而结尾位置也最不容易在长上下文里被冲掉。
        /// </summary>
        private static void AppendCurrentTime(StringBuilder builder, DateTimeOffset now)
        {
            builder.AppendLine("## 当前时间");

            // 精确到分。秒级没有用处，还会让每轮系统提示都不一样。
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- 现在是 {0} {1} {2}（用户本地时间，时区 {3}）。",
                now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                WeekdayNames[(int)now.DayOfWeek],
                now.ToString("HH:mm", CultureInfo.InvariantCulture),
                FormatOffset(now.Offset)));
            builder.AppendLine("- 这是唯一可信的时间来源。凡是「今天」「现在」「本月」「上个季度」" +
                "「三天后」这类相对说法，一律以此为基准推算，不要凭记忆或训练数据里的日期作答。");
            builder.AppendLine("- 写日期进单元格时：要固定值就写出上面推算出的具体日期；" +
                "要「每次打开都显示当天」才用 =TODAY() 或 =NOW()。没说清楚时按固定值写。");
            builder.AppendLine();
        }

        /// <summary>时区偏移写成 UTC+08:00 这种形态，不依赖区域设置。</summary>
        private static string FormatOffset(TimeSpan offset)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "UTC{0}{1:00}:{2:00}",
                offset < TimeSpan.Zero ? "-" : "+",
                Math.Abs(offset.Hours),
                Math.Abs(offset.Minutes));
        }

        /// <summary>能动手时的提示。原生与文本协议共用，只在「怎么发起调用」上分岔。</summary>
        private static void AppendOperatorHeader(StringBuilder builder, ToolProtocolMode toolMode)
        {
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

            if (toolMode == ToolProtocolMode.Text)
            {
                builder.AppendLine(TextToolProtocol.PromptSection());
                builder.AppendLine();
            }

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

            AppendStyle(builder);
        }

        /// <summary>
        /// 顾问模式的提示。
        ///
        /// 这一版刻意不提「你已经连上工作簿」，也不列工具：模型已经被证明
        /// 既发不出原生调用、也写不对指令块，再告诉它有读写权限只会得到
        /// 「我已经帮你填好了」这类凭空捏造的回答。
        /// </summary>
        private static void AppendAdvisorHeader(StringBuilder builder)
        {
            builder.AppendLine("你是 ChatSheet，嵌入在 Microsoft Excel 右侧面板中的表格助手。");
            builder.AppendLine();

            builder.AppendLine("## 最重要的前提");
            builder.AppendLine("- 本次对话下你**不能**读取或修改用户的工作簿，没有任何可用的操作通道。");
            builder.AppendLine("- 绝不可以声称自己已经读过、写过或改过用户的表格，也不要说「我已经完成」——那是错的。");
            builder.AppendLine("- 你的职责是给出方案：公式、步骤、注意事项，让用户自己在 Excel 里执行。");
            builder.AppendLine("- 需要表格里的具体数据才能回答时，直接请用户把相关内容贴给你。");
            builder.AppendLine();

            builder.AppendLine("## 能力边界");
            builder.AppendLine("- 你可以解释 Excel 的功能与公式、设计表格结构、写出可直接粘贴的公式与操作步骤。");
            builder.AppendLine("- 你没有文件系统、命令行或网络访问能力。");
            builder.AppendLine("- 用户可以直接附带文件：消息中出现「附件 N/M：文件名」加代码块时，那就是文件的完整内容，可直接使用。");
            builder.AppendLine("- 下面给出的工作簿信息由加载项采集，可以引用；但除此之外的任何单元格内容你都无从得知。");
            builder.AppendLine();

            builder.AppendLine("## 工作方式");
            builder.AppendLine("- 给公式时写清楚放在哪个单元格、需不需要向下填充。");
            builder.AppendLine("- 涉及多步操作时按步骤列出，每步说明在哪里点什么。");
            builder.AppendLine("- 若用户以为你能直接动手，说明这个模型不支持工具调用，可以在设置页换一个支持的模型。");
            builder.AppendLine();

            AppendStyle(builder, canOperate: false);
        }

        private static void AppendStyle(StringBuilder builder, bool canOperate = true)
        {
            builder.AppendLine("## 回答风格");
            builder.AppendLine("- 用简体中文，简洁直接。");

            if (canOperate)
            {
                builder.AppendLine("- 完成后说明改了哪些范围、影响多少单元格，不要复述工具的原始返回值。");
            }

            builder.AppendLine("- 需要用户决策时明确提出问题，不要自行假设。");
            builder.AppendLine();
        }
    }
}
