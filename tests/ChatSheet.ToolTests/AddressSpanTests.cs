using System;
using ChatSheet.AddIn.Tools;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 地址区间解析与相交判断。
    ///
    /// 这层是「撤销会不会静默盖掉之后那次写入」的唯一判据，而它刻意不碰宿主：
    /// 撤销发生在操作之后，期间工作簿可能已被改动，用 COM 代理去比对是
    /// 静默出错的做法。判据既然纯，就必须在这里把边界钉死——
    /// 尤其「解析不出来时按不相交处理」这条：漏报退回今天的行为，
    /// 而编一个错区间会让用户在没有冲突时被反复拦下。
    /// </summary>
    internal static class AddressSpanTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestParsesCommonForms(report);
            TestRejectsUnparsable(report);
            TestIntersection(report);
            TestWholeColumnAndRow(report);
        }

        private static void TestParsesCommonForms(Action<string, bool, string> report)
        {
            // 单格：四个边界都落在自己身上。
            report(
                "单格地址解析成 1×1 区间",
                AddressSpan.TryParse("B2", out var single)
                    && single.FirstRow == 2 && single.LastRow == 2
                    && single.FirstColumn == 2 && single.LastColumn == 2,
                Describe("B2", AddressSpan.TryParse("B2", out var s1), s1));

            // 绝对引用：$ 不改变区间。快照里存的地址正是这种形态。
            report(
                "绝对引用与相对引用等价",
                AddressSpan.TryParse("$B$2:$D$10", out var abs)
                    && abs.FirstRow == 2 && abs.LastRow == 10
                    && abs.FirstColumn == 2 && abs.LastColumn == 4,
                Describe("$B$2:$D$10", true, abs));

            // 工作表前缀：相交另有表名参与判断，这里只认感叹号之后那段。
            report(
                "带工作表前缀只取地址段",
                AddressSpan.TryParse("Sheet1!B2:C3", out var prefixed)
                    && prefixed.FirstRow == 2 && prefixed.LastColumn == 3,
                Describe("Sheet1!B2:C3", true, prefixed));

            // 倒序写法要摆正，否则 D10:B2 的区间会算成空。
            report(
                "倒序范围被摆正",
                AddressSpan.TryParse("D10:B2", out var reversed)
                    && reversed.FirstRow == 2 && reversed.LastRow == 10
                    && reversed.FirstColumn == 2 && reversed.LastColumn == 4,
                Describe("D10:B2", true, reversed));

            // 三字母列：AA 之后的列号靠 26 进制累加，算错会让整块区间偏移。
            report(
                "三字母列按 26 进制换算",
                AddressSpan.TryParse("AA1", out var wide) && wide.FirstColumn == 27,
                Describe("AA1", true, wide));
        }

        private static void TestRejectsUnparsable(Action<string, bool, string> report)
        {
            // 多区域并集：RangeResolver 目前按第一块处理，这里跟着放弃判断。
            // 猜一个区间会在没有冲突时拦住用户，比漏报更糟。
            report(
                "多区域地址不参与相交判断",
                !AddressSpan.TryParse("B:B,D:D", out _),
                "B:B,D:D 应解析失败");

            report(
                "分号分隔的多区域同样放弃",
                !AddressSpan.TryParse("A1:B2;C3:D4", out _),
                "A1:B2;C3:D4 应解析失败");

            // 混合形态含义不确定，交回失败比猜一个方向诚实。
            report(
                "B2:D 这类混合写法解析失败",
                !AddressSpan.TryParse("B2:D", out _),
                "B2:D 应解析失败");

            report(
                "空地址解析失败",
                !AddressSpan.TryParse("   ", out _),
                "空白应解析失败");

            report(
                "三段冒号解析失败",
                !AddressSpan.TryParse("A1:B2:C3", out _),
                "A1:B2:C3 应解析失败");
        }

        private static void TestIntersection(Action<string, bool, string> report)
        {
            AddressSpan.TryParse("B2:D10", out var target);

            // 完全套住、部分搭边、只共一角，都算相交：撤销会覆盖其中任一情形。
            AddressSpan.TryParse("C5:C8", out var inside);
            report("被完全包含算相交", target.Intersects(inside), "B2:D10 vs C5:C8");

            AddressSpan.TryParse("D10:F12", out var corner);
            report("只共一角也算相交", target.Intersects(corner), "B2:D10 vs D10:F12");

            AddressSpan.TryParse("A1:Z100", out var outer);
            report("反向包含也算相交", target.Intersects(outer), "B2:D10 vs A1:Z100");

            // 行重叠但列不重叠，不算相交——乱序撤销在这种情形下是刻意允许的。
            AddressSpan.TryParse("F2:H10", out var sameRows);
            report("行重叠列不重叠不算相交", !target.Intersects(sameRows), "B2:D10 vs F2:H10");

            // 列重叠但行不重叠，同理。
            AddressSpan.TryParse("B20:D30", out var sameColumns);
            report("列重叠行不重叠不算相交", !target.Intersects(sameColumns), "B2:D10 vs B20:D30");

            // 相交必须对称，否则先撤哪一条会得到不同结论。
            report("相交判断是对称的", inside.Intersects(target), "C5:C8 vs B2:D10");
        }

        private static void TestWholeColumnAndRow(Action<string, bool, string> report)
        {
            // 整列覆盖所有行：模型写 A:D 是常见形态，不能算成只有第一行。
            report(
                "整列覆盖全部行",
                AddressSpan.TryParse("A:D", out var columns)
                    && columns.FirstRow == 1 && columns.LastRow > 1000000
                    && columns.FirstColumn == 1 && columns.LastColumn == 4,
                Describe("A:D", true, columns));

            report(
                "整行覆盖全部列",
                AddressSpan.TryParse("2:5", out var rows)
                    && rows.FirstRow == 2 && rows.LastRow == 5
                    && rows.FirstColumn == 1 && rows.LastColumn > 16000,
                Describe("2:5", true, rows));

            // 整列与其中一格必然相交：清整列之后撤销上一次写入会盖掉它。
            AddressSpan.TryParse("A:D", out var wholeColumns);
            AddressSpan.TryParse("B5", out var cell);
            report("整列与其中一格相交", wholeColumns.Intersects(cell), "A:D vs B5");

            // 整列与范围外的列不相交。
            AddressSpan.TryParse("F5", out var outside);
            report("整列与范围外的列不相交", !wholeColumns.Intersects(outside), "A:D vs F5");
        }

        private static string Describe(string address, bool parsed, AddressSpan span)
        {
            return parsed
                ? $"{address} → 行 {span.FirstRow}-{span.LastRow}，列 {span.FirstColumn}-{span.LastColumn}"
                : $"{address} 解析失败";
        }
    }
}
