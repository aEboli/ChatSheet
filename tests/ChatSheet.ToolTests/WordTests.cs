using System;
using System.Collections.Generic;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Tools;
using ChatWord.AddIn.Agent;
using ChatWord.AddIn.Hosts;
using ChatWord.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    internal static class WordTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestCatalog(report);
            TestPrompt(report);
            TestTargetAndRead(report);
            TestHostIdentification(report);
            TestRibbonTerminology(report);
            TestApprovalContract(report);
        }

        private static void TestCatalog(Action<string, bool, string> report)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in WordToolCatalog.All)
            {
                names.Add(tool.Name);
                report("Word 工具参数有 Schema：" + tool.Name, tool.Parameters != null, "缺少参数 Schema");
                report("Word 工具不暴露 Excel 对象：" + tool.Name,
                    !tool.Description.Contains("Workbook") && !tool.Description.Contains("Worksheet") && !tool.Description.Contains("A1"),
                    "描述包含 Excel 术语");
            }
            report("包含文档信息工具", names.Contains("get_document_info"), "缺少 get_document_info");
            report("包含明确目标工具", names.Contains("read_range") && names.Contains("replace_text"), "缺少读写工具");
        }

        private static void TestPrompt(Action<string, bool, string> report)
        {
            var prompt = WordSystemPrompt.Build(
                new WordDocumentSummary { HasDocument = true, Name = "测试文档.docx", Host = "Microsoft Word", Version = "16.0", Pages = 2 },
                new WordSelectionInfo { HasSelection = true, Story = "main_text", Start = 4, End = 8, Text = "摘要" });
            report("提示词使用 Word 身份", prompt.Contains("Word 文档助手"), "缺少身份");
            report("提示词要求先读后写", prompt.Contains("先读取文档结构"), "缺少先读后写边界");
            report("提示词说明顾问模式", prompt.Contains("顾问模式"), "缺少顾问模式边界");
            report("提示词没有工作簿术语", !prompt.Contains("工作簿") && !prompt.Contains("工作表"), "混入 Excel 术语");
        }

        private static void TestTargetAndRead(Action<string, bool, string> report)
        {
            var document = new FakeDocument();
            var target = WordTargetResolver.Resolve(document, new JObject
            {
                ["target"] = new JObject { ["story"] = "main_text", ["start"] = 2, ["end"] = 7 },
            });
            report("Story Start/End 目标解析", target.Start == 2 && target.End == 7 && target.Story == "main_text", "目标位置不正确");
            target.Dispose();

            var app = new FakeApplication { Name = "Microsoft Word", Version = "16.0", ActiveDocument = document };
            var executor = new WordToolExecutor(() => app, "Word.Application");
            var result = executor.Execute("read_range", new JObject
            {
                ["target"] = new JObject { ["story"] = "main_text", ["start"] = 0, ["end"] = 20 },
            });
            report("读取范围去除段落/单元格标记", result.Ok && result.Data != null && !result.Data.ToString().Contains("\a"), result.Error ?? "读取失败");

            var defaultedLocators = WordTargetResolver.Resolve(document, new JObject
            {
                ["target"] = new JObject
                {
                    ["story"] = "main_text", ["start"] = 0, ["end"] = 20,
                    ["paragraph_index"] = 0, ["table_index"] = 0, ["row"] = 0, ["column"] = 0,
                },
            });
            report("忽略模型补出的零值定位字段", defaultedLocators.Start == 0 && defaultedLocators.End > 0, "零值定位字段导致目标歧义");
            defaultedLocators.Dispose();

            var paragraphs = executor.Execute("read_paragraphs", new JObject { ["story"] = "main_text", ["limit"] = 10 });
            report("按 Story 读取段落", paragraphs.Ok, paragraphs.Error ?? "读取失败");

            var table = executor.Execute("read_table", new JObject { ["story"] = "main_text", ["table_index"] = 1, ["offset"] = 1, ["limit"] = 1 });
            var tableData = table.Ok ? JObject.FromObject(table.Data) : new JObject();
            report("读取表格尺寸与行分页", table.Ok && tableData.Value<int?>("rows") == 3 && tableData.Value<int?>("columns") == 2 &&
                tableData.Value<int?>("next_offset") == 2 && (tableData["data"] as JArray)?.Count == 1,
                table.Error ?? "尺寸或分页结果不正确");

            var preview = JObject.FromObject(executor.BuildPreview("replace_text", new JObject
            {
                ["target"] = new JObject { ["story"] = "main_text", ["start"] = 2, ["end"] = 7 },
                ["find"] = "目标", ["replace"] = "预览",
            }));
            report("Word 审批预览只读计算前后文本", preview.Value<bool?>("supported") == true &&
                preview.Value<string>("before") == "目标文本" && preview.Value<string>("after") == "预览文本", "预览不正确");
        }

        private static void TestHostIdentification(Action<string, bool, string> report)
        {
            var word = new FakeApplication { Name = "Microsoft Word", Version = "16.0" };
            var wps = new FakeApplication { Name = "Microsoft Word", Version = "12.0" };
            report("Word ProgID 识别", WordHostProbe.Detect(word, "Word.Application") == WordHostKind.MicrosoftWord, "错误识别 Word");
            report("WPS ProgID 优先于 Name", WordHostProbe.Detect(wps, "kwps.Application") == WordHostKind.WpsWriter, "WPS 被误识别为 Word");
        }

        private static void TestRibbonTerminology(Action<string, bool, string> report)
        {
            var assembly = typeof(ChatWord.AddIn.WordComAddIn).Assembly;
            using (var stream = assembly.GetManifestResourceStream("ChatWord.AddIn.Resources.Ribbon.xml"))
            using (var reader = new System.IO.StreamReader(stream))
            {
                var ribbon = reader.ReadToEnd();
                report("Word Ribbon 使用文档术语", ribbon.Contains("文档") && ribbon.Contains("Word/Writer"), "缺少 Word/Writer 文案");
                report("Word Ribbon 不含 Excel 专属按钮", !ribbon.Contains("工作簿") && !ribbon.Contains("工作表") && !ribbon.Contains("适配当前表"), "混入 Excel 专属术语");
            }
        }

        private static void TestApprovalContract(Action<string, bool, string> report)
        {
            var writes = 0;
            var reads = 0;
            var safe = true;
            foreach (var tool in WordToolCatalog.All)
            {
                if (tool.Risk == ToolRisk.Read)
                {
                    reads++;
                    safe &= !tool.RequiresApproval;
                }
                else
                {
                    writes++;
                    safe &= tool.RequiresApproval;
                }
            }
            report("Word 读工具不弹审批、写工具必须审批", safe && writes > 0 && reads > 0, "审批风险分类不完整");
        }

        private sealed class FakeApplication
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public FakeDocument ActiveDocument { get; set; }
        }

        private sealed class FakeDocument
        {
            public FakeDocument()
            {
                var table = new FakeTable(new[,] { { "类别", "具体内容" }, { "消费品", "商品" }, { "服务", "乐园" } });
                StoryRanges = new FakeCollection(new Dictionary<int, object>
                {
                    [1] = new FakeRange("第一段\r", 0, 20, new FakeCollection(new Dictionary<int, object> { [1] = table })),
                });
            }

            public string Name => "测试文档.docx";
            public string FullName => "";
            public bool Saved => true;
            public bool ReadOnly => false;
            public int ProtectionType => 0;
            public bool TrackRevisions => false;
            public FakeCollection StoryRanges { get; }
            public FakeCollection Paragraphs { get; } = new FakeCollection();
            public FakeCollection Tables { get; } = new FakeCollection();
            public FakeCollection Sections { get; } = new FakeCollection();
            public FakeCollection Bookmarks { get; } = new FakeCollection();
            public FakeCollection ContentControls { get; } = new FakeCollection();
            public FakeCollection Fields { get; } = new FakeCollection();
            public FakeCollection Comments { get; } = new FakeCollection();
            public FakeCollection Footnotes { get; } = new FakeCollection();
            public FakeCollection Endnotes { get; } = new FakeCollection();
            public int ComputeStatistics(int statistic) => 1;
            public FakeRange Range(int start, int end) => new FakeRange("目标文本", start, end);
        }

        private sealed class FakeCollection
        {
            private readonly Dictionary<int, object> _items;
            public FakeCollection() : this(new Dictionary<int, object>()) { }
            public FakeCollection(int count)
            {
                _items = new Dictionary<int, object>();
                for (var i = 1; i <= count; i++) { _items[i] = new object(); }
            }
            public FakeCollection(Dictionary<int, object> items) { _items = items; }
            public int Count => _items.Count;
            public object Item(object index) { return _items.TryGetValue(Convert.ToInt32(index), out var item) ? item : null; }
        }

        private sealed class FakeRange
        {
            private string _text;
            private readonly FakeCollection _tables;
            public FakeRange(string text, int start, int end, FakeCollection tables = null) { _text = text; Start = start; End = end; _tables = tables ?? new FakeCollection(); }
            public int Start { get; private set; }
            public int End { get; private set; }
            public string Text { get { return _text; } set { _text = value; End = Start + (value ?? string.Empty).Length; } }
            public FakeRange Duplicate() { return new FakeRange(_text, Start, End); }
            public void SetRange(int start, int end) { Start = start; End = end; }
            public int StoryType => 1;
            public FakeCollection Paragraphs => new FakeCollection();
            public FakeCollection Tables => _tables;
            public bool Hidden => false;
            public FakeCollection Fields => new FakeCollection();
        }

        private sealed class FakeTable
        {
            private readonly string[,] _cells;
            public FakeTable(string[,] cells) { _cells = cells; }
            public FakeCollection Rows => new FakeCollection(_cells.GetLength(0));
            public FakeCollection Columns => new FakeCollection(_cells.GetLength(1));
            public FakeCell Cell(int row, int column) => new FakeCell(_cells[row - 1, column - 1]);
        }

        private sealed class FakeCell
        {
            public FakeCell(string text) { Range = new FakeRange(text + "\a", 0, text.Length + 1); }
            public FakeRange Range { get; }
        }
    }
}
