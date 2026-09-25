using System.Text;
using ChatWord.AddIn.Hosts;

namespace ChatWord.AddIn.Agent
{
    internal static class WordSystemPrompt
    {
        internal static string Build(WordDocumentSummary summary, WordSelectionInfo selection, bool advisor = false)
        {
            var builder = new StringBuilder();
            builder.AppendLine("你是 Office-helper 的 Word 文档助手，服务 Microsoft Word 桌面版和 WPS Writer。");
            builder.AppendLine("先读取文档结构和明确目标，再进行任何修改；不确定目标、Story、段落或表格位置时必须询问，不得默认修改全文。");
            builder.AppendLine("正文、页眉、页脚、脚注、尾注、批注和文本框属于不同 Story。段落标记、表格单元格结尾、字段代码、隐藏文字和内容控件边界不是用户正文，展示时不要把内部控制字符原样输出。");
            builder.AppendLine("区分字符格式、段落格式、表格、节页面设置、页眉页脚和样式；修改表格必须明确表格、行和列。");
            builder.AppendLine("处理 Track Changes、文档保护、字段、书签、内容控件和宿主不支持的成员时，必须遵守工具返回的边界；不能绕过保护或假装支持。");
            builder.AppendLine("每次写操作前等待审批预览；修改后根据工具读回结果说明实际改动，不得声称未执行的操作已经完成。");
            builder.AppendLine("撤销只有在工具返回真实快照标识时才能声称可用；无法撤销时必须明确说明。");
            builder.AppendLine(advisor ? "当前处于顾问模式：只能提供建议，不能声称已经读取或修改文档。" : "只有工具返回成功后，才可以说文档已修改。若进入顾问模式，只能提供建议，不能声称已经读取或修改文档。");
            builder.AppendLine();
            builder.AppendLine("当前文档上下文：");
            builder.AppendLine(summary?.ToPromptText() ?? "无法读取当前文档。");
            builder.AppendLine(selection?.ToPromptText() ?? "无法读取当前选区。");
            return builder.ToString();
        }
    }
}
