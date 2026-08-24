using System;
using System.Collections.Generic;
using System.Linq;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 文本文件附件验证。
    ///
    /// 这条路径与图片不同：文件不走协议的多模态字段，而是拼进用户消息的
    /// 文本里。拼错的后果不像格式错误那样会被服务端明确拒绝，而是模型收到
    /// 一段结构错乱的内容却照样作答——最难发现的一类失败。因此重点验证
    /// 围栏的正确性与上限的拦截。
    /// </summary>
    internal static class FileTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestCreate(report);
            TestCompose(report);
            TestFencing(report);
            TestProtocolShape(report);
        }

        private static void TestCreate(Action<string, bool, string> report)
        {
            var file = FileSupport.Create("data.csv", "名称,数量\n铅笔,10\n");
            report("接受 csv", file.Name == "data.csv", file.Name);
            // 中文按 UTF-8 是 3 字节，字节数必然大于字符数。
            report("按 UTF-8 计字节数", file.ByteLength > "名称,数量\n铅笔,10\n".Length, $"实际 {file.ByteLength}");

            foreach (var good in new[] { "a.txt", "b.md", "c.json", "d.yaml", "e.sql", "f.py", "g.tsv" })
            {
                try
                {
                    FileSupport.Create(good, "x");
                    report($"接受 {good}", true, "");
                }
                catch (ProviderException ex)
                {
                    report($"接受 {good}", false, ex.Message);
                }
            }

            // 二进制格式必须拒绝：这边没有解析它们的能力，
            // 硬拼进消息只会让模型收到乱码。
            foreach (var bad in new[] { "book.xlsx", "doc.docx", "scan.pdf", "pack.zip", "pic.png" })
            {
                try
                {
                    FileSupport.Create(bad, "x");
                    report($"拒绝 {bad}", false, "未抛异常");
                }
                catch (ProviderException ex)
                {
                    report($"拒绝 {bad}", ex.Code == "FILE_UNSUPPORTED", ex.Code);
                }
            }

            // 没有扩展名无从判断，一律拒绝。
            try
            {
                FileSupport.Create("README", "x");
                report("拒绝无扩展名", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("拒绝无扩展名", ex.Code == "FILE_UNSUPPORTED", ex.Code);
            }

            // 扩展名可以被改，内容不会：NUL 出现即判定为二进制。
            try
            {
                FileSupport.Create("fake.txt", "abc\0def");
                report("按内容识破改了扩展名的二进制文件", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("按内容识破改了扩展名的二进制文件", ex.Code == "FILE_BINARY", ex.Code);
            }

            // 替换字符是「面板那边编码猜错了」的痕迹。这里是权威兜底：
            // 与其让乱码占着上下文预算，不如明确拒绝并让用户改编码。
            try
            {
                // 这串正是把 GBK 的「名称,数量」按 UTF-8 宽松解码得到的结果。
                FileSupport.Create("garbled.csv", "����,����");
                report("拒绝含替换字符的乱码内容", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("拒绝含替换字符的乱码内容", ex.Code == "FILE_GARBLED", ex.Code);
            }

            // 错误码必须与二进制区分：一个要用户改编码，一个要用户换文件。
            var binaryCode = CodeOf(() => FileSupport.Create("b.txt", "a\0b"));
            var garbledCode = CodeOf(() => FileSupport.Create("g.txt", "a�b"));
            report(
                "乱码与二进制用不同错误码",
                binaryCode == "FILE_BINARY" && garbledCode == "FILE_GARBLED",
                $"二进制={binaryCode} 乱码={garbledCode}");

            // 正常内容不能被这两条误伤。
            try
            {
                var clean = FileSupport.Create("clean.csv", "名称,数量\n铅笔,10");
                report("正常中文内容不被误判", clean.Text.Contains("铅笔"), clean.Text);
            }
            catch (ProviderException ex)
            {
                report("正常中文内容不被误判", false, ex.Code + " " + ex.Message);
            }

            // 单文件上限。刚好等于上限要放行，超一个字节就拦。
            var atLimit = new string('a', FileSupport.MaxBytesPerFile);
            try
            {
                var ok = FileSupport.Create("limit.txt", atLimit);
                report("正好等于上限放行", ok.ByteLength == FileSupport.MaxBytesPerFile, $"实际 {ok.ByteLength}");
            }
            catch (ProviderException ex)
            {
                report("正好等于上限放行", false, ex.Message);
            }

            try
            {
                FileSupport.Create("over.txt", atLimit + "a");
                report("超过上限一个字节即拦截", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("超过上限一个字节即拦截", ex.Code == "FILE_TOO_LARGE", ex.Code);
            }

            // 中文按字节而非字符计：3 万个汉字约 90 KB，超过 64 KB 上限。
            try
            {
                FileSupport.Create("cn.txt", new string('汉', 30000));
                report("中文按字节计上限", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("中文按字节计上限", ex.Code == "FILE_TOO_LARGE", ex.Code);
            }

            foreach (var pair in new[]
            {
                new { Name = (string)null, Text = "x", Code = "FILE_INVALID" },
                new { Name = "  ", Text = "x", Code = "FILE_INVALID" },
                new { Name = "a.txt", Text = (string)null, Code = "FILE_EMPTY" },
            })
            {
                try
                {
                    FileSupport.Create(pair.Name, pair.Text);
                    report($"拒绝缺字段（{pair.Code}）", false, "未抛异常");
                }
                catch (ProviderException ex)
                {
                    report($"拒绝缺字段（{pair.Code}）", ex.Code == pair.Code, $"实际 {ex.Code}");
                }
            }
        }

        /// <summary>取调用抛出的错误码，没抛则返回空串。用于对比两条规则的分工。</summary>
        private static string CodeOf(Func<TextAttachment> action)
        {
            try
            {
                action();
                return string.Empty;
            }
            catch (ProviderException ex)
            {
                return ex.Code;
            }
        }

        private static void TestCompose(Action<string, bool, string> report)
        {
            var files = new List<TextAttachment>
            {
                FileSupport.Create("a.csv", "名称,数量\n铅笔,10"),
                FileSupport.Create("b.json", "{\"k\":1}"),
            };

            var composed = FileSupport.Compose("按 a.csv 的格式排一下", files);

            report("含第一个文件名", composed.Contains("附件 1/2：a.csv"), "");
            report("含第二个文件名", composed.Contains("附件 2/2：b.json"), "");
            report("含文件内容", composed.Contains("铅笔,10") && composed.Contains("{\"k\":1}"), "");
            report("含围栏语言标注", composed.Contains("```csv") && composed.Contains("```json"), "");

            // 提问必须在最后：附件是背景材料，问题离生成位置越近越好。
            report(
                "提问排在附件之后",
                composed.TrimEnd().EndsWith("按 a.csv 的格式排一下", StringComparison.Ordinal),
                composed.Substring(Math.Max(0, composed.Length - 40)));

            // 内容不以换行结尾时必须补一个，否则收尾围栏贴在最后一行末尾，
            // 围栏不成立，后面的正文会被当成代码块继续吃下去。
            var lines = composed.Split('\n');
            var fenceLines = lines.Count(l => l.Trim() == "```");
            report("每个文件各有一条独立的收尾围栏", fenceLines == 2, $"实际 {fenceLines} 条");

            // 无附件时原样返回，不加任何包装。
            report("无附件时原样返回", FileSupport.Compose("只是问句", null) == "只是问句", "");
            report(
                "无附件且空输入时返回空串",
                FileSupport.Compose(null, new List<TextAttachment>()) == string.Empty,
                "");

            // 只附文件不写字：必须补一句说明，否则模型收到的是一段没有请求的材料。
            var silent = FileSupport.Compose("   ", files);
            report("只附文件不写字时补一句说明", silent.Contains("以上是我附带的文件"), "");
            report("补的说明在附件之后", silent.IndexOf("以上是我附带的") > silent.IndexOf("b.json"), "");
        }

        private static void TestFencing(Action<string, bool, string> report)
        {
            // Markdown 文件本身就含 ``` 代码块。用固定三反引号会让收尾围栏
            // 提前出现在文件中间，后半段内容跑到代码块外面被当成指令读。
            var markdown = "# 标题\n\n```js\nconsole.log(1)\n```\n\n结尾";
            var composed = FileSupport.Compose("看看这个", new List<TextAttachment>
            {
                FileSupport.Create("doc.md", markdown),
            });

            report("含 ``` 的内容改用四反引号围栏", composed.Contains("````markdown"), "");
            report("文件内部的三反引号原样保留", composed.Contains("```js"), "");

            // 更长的反引号串也要能包住。
            var nested = "前\n````\n里面\n````\n后";
            var deeper = FileSupport.Compose("x", new List<TextAttachment>
            {
                FileSupport.Create("deep.md", nested),
            });
            report("含四反引号时用五个", deeper.Contains("`````markdown"), "");

            // 围栏必须严格长于内容里最长的那串，否则包不住。
            var longest = 0;
            var current = 0;
            foreach (var ch in nested)
            {
                current = ch == '`' ? current + 1 : 0;
                if (current > longest) { longest = current; }
            }

            var opening = deeper.Split('\n').First(l => l.StartsWith("`", StringComparison.Ordinal));
            var fenceLength = opening.TakeWhile(c => c == '`').Count();
            report("围栏长于内容里最长的反引号串", fenceLength > longest, $"围栏 {fenceLength}，内容 {longest}");
        }

        /// <summary>
        /// 文件拼进文本后，请求体里应当只有普通文本，不产生任何多模态结构。
        /// 这一点必须验证：若误走了图片那条路，四种协议都会报格式错误。
        /// </summary>
        private static void TestProtocolShape(Action<string, bool, string> report)
        {
            var composed = FileSupport.Compose("排一下", new List<TextAttachment>
            {
                FileSupport.Create("a.csv", "名称,数量"),
            });

            var request = new ChatRequest
            {
                Protocol = ProtocolKind.OpenAiChatCompletions,
                BaseUrl = "https://example.com/v1",
                Model = "m",
                IncludeTools = false,
            };
            request.Messages.Add(ChatMessage.FromUser(composed));

            var body = RequestBuilder.Build(request, stream: true);
            var user = (body["messages"] as JArray)?.LastOrDefault() as JObject;

            report(
                "无图片时 content 仍是字符串",
                user?["content"]?.Type == JTokenType.String,
                user?["content"]?.Type.ToString());
            report(
                "文件内容进了消息正文",
                user?.Value<string>("content")?.Contains("附件 1/1：a.csv") == true,
                "");
        }
    }
}
