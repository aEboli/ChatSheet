using System;
using System.Globalization;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// A1 地址覆盖的行列区间。
    ///
    /// 只为一件事存在：判断两条撤销记录动过的范围有没有相交。
    ///
    /// 刻意不用宿主的 <c>Application.Intersect</c>：撤销发生在操作之后，
    /// 期间工作簿可能已被改动，而每次解析都会拿到新的 COM 代理——
    /// Office 对象不能比标识，拿代理去比对是静默出错的做法。
    /// 地址字符串是记录里已经存好的事实，用它算区间不必再碰宿主。
    /// </summary>
    internal struct AddressSpan
    {
        private AddressSpan(int firstRow, int lastRow, int firstColumn, int lastColumn)
        {
            FirstRow = firstRow;
            LastRow = lastRow;
            FirstColumn = firstColumn;
            LastColumn = lastColumn;
        }

        internal int FirstRow { get; }

        internal int LastRow { get; }

        internal int FirstColumn { get; }

        internal int LastColumn { get; }

        /// <summary>
        /// 解析单块 A1 地址。
        ///
        /// 支持 B2、B2:D10、A:D、2:5 四种形态，`$` 一律忽略。
        /// 整列写法的行区间取满，整行写法的列区间取满——它们确实覆盖那一整条。
        ///
        /// 多区域地址（B:B,D:D）返回 false：<see cref="RangeResolver"/> 目前把并集
        /// 按第一块处理，这里跟着放弃判断，而不是猜一个可能漏报的区间。
        /// 漏报的后果是退回今天的行为（不警告），比编一个错区间安全。
        /// </summary>
        internal static bool TryParse(string address, out AddressSpan span)
        {
            span = default(AddressSpan);
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var text = address.Replace("$", string.Empty).Trim();
            if (text.Length == 0 || text.IndexOf(',') >= 0 || text.IndexOf(';') >= 0)
            {
                return false;
            }

            // 地址可能带工作表前缀（Sheet1!B2:D10）。相交判断另有工作表名参与，
            // 这里只取感叹号之后那一段。
            var bang = text.LastIndexOf('!');
            if (bang >= 0)
            {
                text = text.Substring(bang + 1);
            }

            var parts = text.Split(':');
            if (parts.Length > 2)
            {
                return false;
            }

            if (!TryParseEnd(parts[0], out var startRow, out var startColumn))
            {
                return false;
            }

            var endText = parts.Length == 2 ? parts[1] : parts[0];
            if (!TryParseEnd(endText, out var endRow, out var endColumn))
            {
                return false;
            }

            // 两端形态必须一致：B2:D 这类混合写法含义不确定，交回 false。
            if (startRow.HasValue != endRow.HasValue || startColumn.HasValue != endColumn.HasValue)
            {
                return false;
            }

            var firstRow = startRow ?? 1;
            var lastRow = endRow ?? MaxRows;
            var firstColumn = startColumn ?? 1;
            var lastColumn = endColumn ?? MaxColumns;

            span = new AddressSpan(
                Math.Min(firstRow, lastRow),
                Math.Max(firstRow, lastRow),
                Math.Min(firstColumn, lastColumn),
                Math.Max(firstColumn, lastColumn));
            return true;
        }

        /// <summary>两个区间是否相交。行与列都重叠才算。</summary>
        internal bool Intersects(AddressSpan other)
        {
            return FirstRow <= other.LastRow
                && other.FirstRow <= LastRow
                && FirstColumn <= other.LastColumn
                && other.FirstColumn <= LastColumn;
        }

        private const int MaxRows = 1048576;
        private const int MaxColumns = 16384;

        private static bool TryParseEnd(string part, out int? row, out int? column)
        {
            row = null;
            column = null;

            var text = part.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            var index = 0;
            while (index < text.Length && IsLetter(text[index]))
            {
                index++;
            }

            var letters = text.Substring(0, index);
            var digits = text.Substring(index);

            if (letters.Length > 3)
            {
                return false;
            }

            if (letters.Length > 0)
            {
                column = ColumnIndex(letters);
            }

            if (digits.Length > 0)
            {
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 1
                    || parsed > MaxRows)
                {
                    return false;
                }

                row = parsed;
            }

            // 既没有字母也没有数字，或数字里混着别的字符。
            return letters.Length > 0 || digits.Length > 0;
        }

        private static bool IsLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }

        private static int ColumnIndex(string letters)
        {
            var index = 0;
            foreach (var ch in letters.ToUpperInvariant())
            {
                index = (index * 26) + (ch - 'A' + 1);
            }

            return index;
        }
    }
}
