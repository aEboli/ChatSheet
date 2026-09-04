using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Agent
{
    /// <summary>
    /// 审批卡片上的一格对照。
    ///
    /// 空与读不到是两件事：原值为空、新值为 0 是一次正常写入；
    /// 读不到当前值是探测失败。合成一个字段会让用户把后者看成前者。
    /// </summary>
    internal sealed class PreviewCell
    {
        /// <summary>范围内的行号，从 1 起。不是工作表行号。</summary>
        internal int Row { get; set; }

        /// <summary>范围内的列号，从 1 起。</summary>
        internal int Column { get; set; }

        internal string Before { get; set; }

        internal string After { get; set; }

        internal bool BeforeEmpty { get; set; }

        internal bool AfterEmpty { get; set; }
    }

    /// <summary>
    /// 「将改成什么」的截断预览。
    ///
    /// 只送给面板，不进对话历史、不回传模型：预览矩阵进上下文等于每步批准
    /// 再付一次读取的税，而最近几条消息本来就不参与压缩。
    /// </summary>
    internal sealed class ChangePreview
    {
        /// <summary>探测读不到当前值（超限、地址无法解析）。此时不得渲染空对照表。</summary>
        internal bool CurrentUnreadable { get; set; }

        /// <summary>范围级外观属性逐项都不一致。格式类操作用它说明「当前格式不统一」。</summary>
        internal bool FormattingMixed { get; set; }

        /// <summary>没有列出的单元格数。是格数，不是行数。</summary>
        internal int OmittedCells { get; set; }

        /// <summary>
        /// 这次操作会抹掉多少个有值的单元格。
        ///
        /// 与 <see cref="Cells"/> 里的逐格对照分开：抹除类操作的要害不是
        /// 「改成什么」而是「丢掉什么」，而丢多少个必须是范围内的总数，
        /// 不能只数卡上列出的那几格——列出 8 行、实际丢 300 格时，
        /// 用户按卡上看到的判断就错了一个量级。
        /// </summary>
        internal int DiscardedValues { get; set; }

        /// <summary>对照描述的是哪一类改动。面板据此换表头与说明。</summary>
        internal string Kind { get; set; } = "write";

        internal List<PreviewCell> Cells { get; } = new List<PreviewCell>();
    }

    /// <summary>
    /// 把探测到的当前值与参数里的新值配成对照。
    ///
    /// 上限取在「面板一眼扫得完」，而不是工具层的 5000 格：整表画进审批卡，
    /// 300–480px 的窄栏会变成一张要滚动的电子表，批准按钮被顶出视野。
    /// </summary>
    internal static class ChangePreviewBuilder
    {
        /// <summary>卡上最多画几行。</summary>
        internal const int MaxRows = 8;

        /// <summary>卡上最多画几列。</summary>
        internal const int MaxColumns = 6;

        /// <summary>单格文本上限。工具层的 512 是给模型看的，卡片上用不到那么长。</summary>
        internal const int MaxCellText = 40;

        /// <summary>
        /// 公式的单格上限。
        ///
        /// 比普通值宽得多：`=IFERROR(VLOOKUP($A2,Sheet2!$A:$D,4,FALSE),"未找到")` 是 51 个
        /// 字符，截到 40 恰好把关键部分切掉，而「批准前看得见要改什么」对公式
        /// 反而最要紧——值错了看得出来，公式错了要跑一遍才知道。
        /// 数字与短文本 40 字够用，多给的宽度只花在真正需要的那一类上。
        /// </summary>
        internal const int MaxFormulaText = 120;

        /// <summary>
        /// 为写入类调用生成对照。
        ///
        /// <paramref name="current"/> 来自审批前那一次影响估算的探测，不再单独读一遍：
        /// 审批发生在 UI 线程上，为预览多跑一次 COM 读取会让大范围下的 Excel 假死，
        /// 而那时卡片还没画出来。
        /// </summary>
        internal static ChangePreview Build(JToken current, JToken incoming, int totalCells, bool formulas = false)
        {
            var preview = new ChangePreview { Kind = formulas ? "formula" : "write" };
            var cap = formulas ? MaxFormulaText : MaxCellText;

            var currentRows = current as JArray;
            var incomingRows = incoming as JArray;

            if (incomingRows == null)
            {
                return null;
            }

            if (currentRows == null)
            {
                preview.CurrentUnreadable = true;
            }

            var rows = Math.Min(incomingRows.Count, MaxRows);
            var listed = 0;

            for (var r = 0; r < rows; r++)
            {
                var incomingRow = incomingRows[r] as JArray;
                if (incomingRow == null)
                {
                    continue;
                }

                var currentRow = currentRows != null && r < currentRows.Count
                    ? currentRows[r] as JArray
                    : null;

                var columns = Math.Min(incomingRow.Count, MaxColumns);
                for (var c = 0; c < columns; c++)
                {
                    var after = incomingRow[c];
                    var before = currentRow != null && c < currentRow.Count ? currentRow[c] : null;

                    preview.Cells.Add(new PreviewCell
                    {
                        Row = r + 1,
                        Column = c + 1,
                        Before = Render(before, cap),
                        After = Render(after, cap),
                        BeforeEmpty = IsEmpty(before),
                        AfterEmpty = IsEmpty(after),
                    });
                    listed++;
                }
            }

            // 报剩下的格数而不是行数：用户要判断的是「还有多少没看见」，
            // 而截断同时发生在行和列两个方向上，行数说不全这件事。
            preview.OmittedCells = Math.Max(0, totalCells - listed);
            return preview;
        }

        /// <summary>
        /// 为抹除类操作生成对照：列出会被丢掉的那些值。
        ///
        /// 与写入类的区别在于「将改为」这一侧是确定的——清除后是空，
        /// 合并后只有左上角那一格留得下来。所以真正要给用户看的是
        /// 「现在这里有什么」，以及总共会丢几个有值的格子。
        ///
        /// <paramref name="keepAnchor"/> 为真时（合并）跳过左上角那一格：
        /// 它是唯一保得住的，把它列进「会丢」是错的。
        /// </summary>
        internal static ChangePreview BuildDiscard(JToken current, int totalCells, bool keepAnchor, string kind)
        {
            var rows = current as JArray;
            if (rows == null)
            {
                return new ChangePreview { CurrentUnreadable = true, Kind = kind };
            }

            var preview = new ChangePreview { Kind = kind };
            var listed = 0;
            var discarded = 0;

            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r] as JArray;
                if (row == null)
                {
                    continue;
                }

                for (var c = 0; c < row.Count; c++)
                {
                    // 合并保左上角：它不会丢，既不计数也不上卡。
                    if (keepAnchor && r == 0 && c == 0)
                    {
                        continue;
                    }

                    if (IsEmpty(row[c]))
                    {
                        continue;
                    }

                    // 先把总数数全，再决定列不列——卡上截断不能改变「丢几个」这个结论。
                    discarded++;

                    if (listed < MaxRows * MaxColumns && r < MaxRows && c < MaxColumns)
                    {
                        preview.Cells.Add(new PreviewCell
                        {
                            Row = r + 1,
                            Column = c + 1,
                            Before = Render(row[c], MaxCellText),
                            After = string.Empty,
                            BeforeEmpty = false,
                            AfterEmpty = true,
                        });
                        listed++;
                    }
                }
            }

            preview.DiscardedValues = discarded;
            preview.OmittedCells = Math.Max(0, discarded - listed);
            return preview;
        }

        private static bool IsEmpty(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return true;
            }

            return token.Type == JTokenType.String && token.Value<string>().Length == 0;
        }

        private static string Render(JToken token, int cap = MaxCellText)
        {
            if (IsEmpty(token))
            {
                return string.Empty;
            }

            string text;
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    text = token.Value<bool>() ? "TRUE" : "FALSE";
                    break;

                case JTokenType.Float:
                case JTokenType.Integer:
                    text = Convert.ToString(token.Value<object>(), CultureInfo.InvariantCulture);
                    break;

                default:
                    text = token.Value<string>() ?? token.ToString();
                    break;
            }

            text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > cap ? text.Substring(0, cap) + "…" : text;
        }
    }
}
