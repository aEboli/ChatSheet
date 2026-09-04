using System;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 授权类别的分档。
    ///
    /// 这一层要守的是一句话：**用户看过的那一次，和这笔授权接下来会放过的那一次，
    /// 破坏性必须在同一个量级。**
    ///
    /// 最初把写入与清除、合并、排序放在同一个 content 类，于是「往这几格写个值」
    /// 的一次批准，顺带放行了「清空整张表」——而合并还会静默丢值。
    /// 分档不是按「都改单元格」这种实现上的相似性，是按后果。
    /// </summary>
    internal static class ApprovalClassTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestEveryToolIsClassified(report);
            TestWriteIsSeparateFromDestructive(report);
            TestStructureStandsAlone(report);
            TestResolvedTargetIsPinned(report);
            TestSheetlessStructureGetsAGrantKey(report);
        }

        /// <summary>
        /// 解析出的目标要钉进真正执行的那份参数。
        ///
        /// 估算影响与执行是两次独立的 UI 线程往返，中间夹着等用户点按钮那段时间。
        /// 省略 sheet 时两侧各自去取「当前活动表」——用户批准期间切了表，
        /// 卡上说的是一张表，写入落到另一张。原先只有授权的键用了解析结果，
        /// 执行仍拿模型给的原始参数。
        /// </summary>
        private static void TestResolvedTargetIsPinned(Action<string, bool, string> report)
        {
            var write = ToolCatalog.Find("write_values");
            var args = JObject.Parse(@"{""range"":""B2:C3"",""values"":[[1,2],[3,4]]}");
            var impact = new ImpactEstimate { SheetName = "报表", Address = "$B$2:$C$3", CellCount = 4 };

            AgentRunner.PinResolvedTargetForTest(write, args, impact);

            report(
                "省略 sheet 时把解析出的表名钉进参数",
                args.Value<string>("sheet") == "报表",
                $"sheet={args.Value<string>("sheet") ?? "(空)"}");

            // 模型自己写了 sheet 就不要覆盖它——那是它明确的意图。
            var explicitArgs = JObject.Parse(@"{""range"":""B2"",""sheet"":""明细"",""values"":[[1]]}");
            AgentRunner.PinResolvedTargetForTest(write, explicitArgs, impact);
            report(
                "模型指定过 sheet 时不覆盖",
                explicitArgs.Value<string>("sheet") == "明细",
                $"sheet={explicitArgs.Value<string>("sheet")}");

            // fit_range 省略 range 时，执行侧会再算一次已用范围，期间可能又多了几行。
            var fit = ToolCatalog.Find("fit_range");
            var fitArgs = new JObject();
            AgentRunner.PinResolvedTargetForTest(
                fit, fitArgs, new ImpactEstimate { SheetName = "Sheet1", Address = "$A$1:$D$9" });
            report(
                "适配省略范围时把已用范围也钉进参数",
                fitArgs.Value<string>("range") == "$A$1:$D$9",
                $"range={fitArgs.Value<string>("range") ?? "(空)"}");

            // 别的工具省略 range 不该被塞一个范围——那会改变它的语义。
            var clearArgs = JObject.Parse(@"{""scope"":""contents""}");
            AgentRunner.PinResolvedTargetForTest(
                ToolCatalog.Find("clear_range"), clearArgs,
                new ImpactEstimate { SheetName = "Sheet1", Address = "$A$1:$D$9" });
            report(
                "非适配工具不被塞入范围",
                clearArgs.Value<string>("range") == null,
                $"range={clearArgs.Value<string>("range") ?? "(空)"}");
        }

        /// <summary>
        /// 没有工作表名的结构调用也要能被授权。
        ///
        /// add_worksheet 与 rename_worksheet 压根没有 range 参数，影响估算拿不到
        /// 表名。原先的后果是授权写不进去也查不出来：用户点了「含结构允许」，
        /// 芯片是空的，下一张建表卡照样弹——而按钮的悬停说明承诺了相反的事。
        /// 结构操作本来作用于整个工作簿，按表记没有意义。
        /// </summary>
        private static void TestSheetlessStructureGetsAGrantKey(Action<string, bool, string> report)
        {
            var blank = new ImpactEstimate { Text = "将改变工作簿结构" };

            var structureKey = AgentRunner.GrantKeyForTest(ApprovalClass.Structure, blank, new JObject());
            report(
                "结构调用拿不到表名时仍有授权键",
                !string.IsNullOrWhiteSpace(structureKey),
                $"key={structureKey ?? "(空)"}");

            // 范围类拿不到表名说明估算失败，此时宁可每次都问，
            // 不要凭一个兜底键把整个工作簿授权出去。
            foreach (var cls in new[] { ApprovalClass.Format, ApprovalClass.Write, ApprovalClass.Destructive })
            {
                report(
                    $"{cls} 拿不到表名时不给兜底授权",
                    AgentRunner.GrantKeyForTest(cls, blank, new JObject()) == null,
                    AgentRunner.GrantKeyForTest(cls, blank, new JObject()) ?? "(空)");
            }

            // 有表名时一律用表名，不走兜底。
            var named = new ImpactEstimate { SheetName = "Sheet1" };
            report(
                "有表名时用表名作键",
                AgentRunner.GrantKeyForTest(ApprovalClass.Structure, named, new JObject()) == "Sheet1",
                AgentRunner.GrantKeyForTest(ApprovalClass.Structure, named, new JObject()));

            // 兜底键不能与任何合法表名相撞：Excel 的表名不允许含控制字符。
            var hasControlChar = structureKey != null
                && structureKey.IndexOf('\u0000') >= 0;
            report(
                "兜底键含控制字符，与合法表名不会相撞",
                hasControlChar,
                $"key={(structureKey ?? "(空)").Replace("\u0000", "<NUL>")}");
        }

        private static void TestEveryToolIsClassified(Action<string, bool, string> report)
        {
            var expected = new (string Tool, ApprovalClass Class)[]
            {
                ("format_range", ApprovalClass.Format),
                ("set_number_format", ApprovalClass.Format),
                ("autofit_range", ApprovalClass.Format),
                ("fit_range", ApprovalClass.Format),

                ("write_values", ApprovalClass.Write),
                ("write_formulas", ApprovalClass.Write),

                ("clear_range", ApprovalClass.Destructive),
                ("merge_cells", ApprovalClass.Destructive),
                ("unmerge_cells", ApprovalClass.Destructive),
                ("sort_range", ApprovalClass.Destructive),

                ("add_worksheet", ApprovalClass.Structure),
                ("rename_worksheet", ApprovalClass.Structure),
                ("create_table", ApprovalClass.Structure),
                ("create_chart", ApprovalClass.Structure),
            };

            var wrong = 0;
            foreach (var (tool, want) in expected)
            {
                var got = AgentRunner.ClassOfToolForTest(tool);
                if (got != want)
                {
                    wrong++;
                    report($"{tool} 归入 {want}", false, $"实际 {got}");
                }
            }

            report(
                "全部会改工作簿的工具都有分类且分对",
                wrong == 0,
                wrong == 0 ? $"核对 {expected.Length} 个" : $"{wrong} 个分错");
        }

        private static void TestWriteIsSeparateFromDestructive(Action<string, bool, string> report)
        {
            // 这两条是本次分档的全部理由：写入能由快照完整还原，
            // 清除抹掉内容与格式，合并静默丢值。授权不能互相覆盖。
            report(
                "写入与清除不同类",
                AgentRunner.ClassOfToolForTest("write_values")
                    != AgentRunner.ClassOfToolForTest("clear_range"),
                $"write_values={AgentRunner.ClassOfToolForTest("write_values")}，"
                    + $"clear_range={AgentRunner.ClassOfToolForTest("clear_range")}");

            report(
                "写入与合并不同类",
                AgentRunner.ClassOfToolForTest("write_values")
                    != AgentRunner.ClassOfToolForTest("merge_cells"),
                $"merge_cells={AgentRunner.ClassOfToolForTest("merge_cells")}");

            report(
                "写入与排序不同类",
                AgentRunner.ClassOfToolForTest("write_values")
                    != AgentRunner.ClassOfToolForTest("sort_range"),
                $"sort_range={AgentRunner.ClassOfToolForTest("sort_range")}");

            // 格式与写入本来就该分开，顺带守住。
            report(
                "格式与写入不同类",
                AgentRunner.ClassOfToolForTest("format_range")
                    != AgentRunner.ClassOfToolForTest("write_values"),
                $"format_range={AgentRunner.ClassOfToolForTest("format_range")}");
        }

        private static void TestStructureStandsAlone(Action<string, bool, string> report)
        {
            var structure = AgentRunner.ClassOfToolForTest("add_worksheet");
            var others = new[] { "format_range", "write_values", "clear_range" };

            var leaked = 0;
            foreach (var tool in others)
            {
                if (AgentRunner.ClassOfToolForTest(tool) == structure) { leaked++; }
            }

            report(
                "结构不与任何其他类同档",
                leaked == 0 && structure == ApprovalClass.Structure,
                $"structure={structure}，与之同档的其他工具 {leaked} 个");
        }
    }
}
