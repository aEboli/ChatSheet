using System;
using System.Collections.Generic;
using System.Text;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 一个随消息发送的文本文件。
    ///
    /// 只有名字和文本：面板侧已按 UTF-8 解码完毕，加载项既不落盘也不再碰
    /// 原始字节。二进制格式（xlsx、pdf 等）在面板侧就被拒绝，走不到这里——
    /// 能进模型的只有文字，而这里没有解析二进制的能力。
    /// </summary>
    internal sealed class TextAttachment
    {
        internal string Name { get; set; }

        internal string Text { get; set; }

        /// <summary>UTF-8 编码后的字节数，用于校验上限与日志。</summary>
        internal int ByteLength { get; set; }
    }

    internal static class FileSupport
    {
        /// <summary>
        /// 允许的扩展名。
        ///
        /// 白名单而非黑名单：能列全的是「确定读得出文字」的那些，
        /// 而二进制格式的种类无穷。名单之外一律拒绝并说明原因，
        /// 比让用户附上一份乱码、模型再据此胡乱作答要好。
        /// </summary>
        internal static readonly string[] SupportedExtensions =
        {
            ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml",
            ".yaml", ".yml", ".ini", ".conf", ".log", ".sql",
            ".html", ".css", ".js", ".ts", ".py", ".cs", ".java", ".go",
            ".rb", ".sh", ".ps1", ".bat", ".r", ".vba", ".bas",
        };

        /// <summary>单轮最多附带的文件数。</summary>
        internal const int MaxFilesPerTurn = 4;

        /// <summary>
        /// 单个文件的字节上限。
        ///
        /// 64 KiB 的依据是上下文而非传输：文件内容整段进对话历史，按
        /// <see cref="Agent.Conversation"/> 的估算（中文 1.5 字/token）
        /// 全中文的 64 KiB 约 14,500 token，接近一次 5,000 单元格读取的量级。
        /// 再大就会让一个附件独占大半预算，挤掉真正的工作数据。
        /// </summary>
        internal const int MaxBytesPerFile = 64 * 1024;

        /// <summary>
        /// 单轮所有文件的字节上限。
        ///
        /// 不等于「单文件上限 × 文件数」：四个满额文件合计约 58,000 token，
        /// 已是 200,000 预算的三成，且这些内容一条也压缩不掉（它们在用户
        /// 消息里，不是可压缩的工具结果）。取一半，即最坏约 29,000 token。
        /// </summary>
        internal const int MaxTotalBytes = 128 * 1024;

        internal static bool IsSupportedExtension(string fileName)
        {
            var extension = ExtensionOf(fileName);
            if (extension.Length == 0)
            {
                return false;
            }

            foreach (var candidate in SupportedExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取小写扩展名，含点号；没有扩展名时返回空串。</summary>
        internal static string ExtensionOf(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            var dot = fileName.LastIndexOf('.');
            return dot > 0 && dot < fileName.Length - 1
                ? fileName.Substring(dot).ToLowerInvariant()
                : string.Empty;
        }

        /// <summary>
        /// 校验并构造一个文件附件。
        ///
        /// 面板已经校验过一轮，这里仍要再校验一次：面板的校验是为了即时反馈，
        /// 而权威判断必须在加载项侧——上限值本就由这里下发，两处规则不一致时
        /// 该以这里为准。
        /// </summary>
        internal static TextAttachment Create(string name, string text)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ProviderException("FILE_INVALID", "文件缺少名称。");
            }

            if (text == null)
            {
                throw new ProviderException("FILE_EMPTY", $"文件 {name} 没有内容。");
            }

            if (!IsSupportedExtension(name))
            {
                throw new ProviderException(
                    "FILE_UNSUPPORTED",
                    $"不支持的文件类型：{name}。可附带的是文本文件，如 txt、md、csv、json、yaml。");
            }

            // NUL 出现在文本文件里只有一种解释：它其实是二进制。
            // 扩展名可以被改，内容不会。
            if (text.IndexOf('\0') >= 0)
            {
                throw new ProviderException(
                    "FILE_BINARY",
                    $"文件 {name} 含有二进制内容，读不出文字。");
            }

            // 替换字符 U+FFFD 是「编码猜错了」的痕迹。
            //
            // 面板侧已用严格解码逐个试过编码，正常不会漏出替换字符；但编码终究
            // 是猜的，而猜错的后果是文件条显示正常、模型收到一片乱码，没有任何
            // 报错信号。与其让乱码占着上下文预算，不如在这里明确拒绝。
            //
            // 错误码与 FILE_BINARY 分开：前者要用户改编码，后者要用户换文件，
            // 下一步动作完全不同。
            if (text.IndexOf('�') >= 0)
            {
                throw new ProviderException(
                    "FILE_GARBLED",
                    $"文件 {name} 的内容含有乱码，可能不是 UTF-8 编码。请另存为 UTF-8 后再试。");
            }

            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxBytesPerFile)
            {
                throw new ProviderException(
                    "FILE_TOO_LARGE",
                    $"文件 {name} 为 {bytes / 1024.0:F0} KB，超过 {MaxBytesPerFile / 1024} KB 上限。" +
                        "请截取需要的部分再附带。");
            }

            return new TextAttachment
            {
                Name = name,
                Text = text,
                ByteLength = bytes,
            };
        }

        /// <summary>
        /// 把文件内容拼进用户输入。
        ///
        /// 为什么拼成文本而不像图片那样走协议的多模态字段：四种协议都没有
        /// 「文本文件」这一类内容块，各家的文件上传接口又互不相通且要先上传
        /// 再引用。拼成带围栏的代码块是所有协议、所有模型都读得懂的形式，
        /// 也让这份内容天然进入对话历史，后续轮次仍可引用。
        ///
        /// 文件在前、提问在后：附件是背景材料，问题是要回答的东西，
        /// 把问题放在最后一句能让它离生成位置最近。
        /// </summary>
        internal static string Compose(string userInput, IReadOnlyList<TextAttachment> files)
        {
            if (files == null || files.Count == 0)
            {
                return userInput ?? string.Empty;
            }

            var builder = new StringBuilder();

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var fence = Fence(file.Text);

                builder.Append("附件 ").Append(i + 1).Append('/').Append(files.Count)
                    .Append("：").Append(file.Name)
                    .Append("（").Append((file.ByteLength / 1024.0).ToString("F1")).Append(" KB）")
                    .Append('\n');

                builder.Append(fence).Append(LanguageTag(file.Name)).Append('\n');
                builder.Append(file.Text);
                // 内容不以换行结尾时补一个，否则收尾围栏会接在最后一行末尾，
                // 那样围栏不成立，正文会被当作代码块的一部分继续吃下去。
                if (!file.Text.EndsWith("\n", StringComparison.Ordinal))
                {
                    builder.Append('\n');
                }

                builder.Append(fence).Append('\n').Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                builder.Append(userInput);
            }
            else
            {
                // 只附文件不写字时给一句说明，否则模型收到的就是一段没有请求的
                // 材料，多半会自行猜测意图。
                builder.Append("以上是我附带的文件，请先看内容再等我的问题。");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 选一个比内容里最长反引号串更长的围栏。
        ///
        /// 必需：Markdown 文件本身就常含 ``` 代码块，用固定的三反引号包起来，
        /// 收尾围栏会提前出现在文件中间，后半段内容就跑到了代码块外面。
        /// </summary>
        private static string Fence(string text)
        {
            var longest = 0;
            var current = 0;

            foreach (var ch in text ?? string.Empty)
            {
                if (ch == '`')
                {
                    current++;
                    if (current > longest) { longest = current; }
                }
                else
                {
                    current = 0;
                }
            }

            return new string('`', Math.Max(3, longest + 1));
        }

        /// <summary>围栏的语言标注。给不出对应语言时留空，不猜。</summary>
        private static string LanguageTag(string fileName)
        {
            switch (ExtensionOf(fileName))
            {
                case ".json": return "json";
                case ".xml": return "xml";
                case ".html": return "html";
                case ".css": return "css";
                case ".js": return "javascript";
                case ".ts": return "typescript";
                case ".py": return "python";
                case ".cs": return "csharp";
                case ".java": return "java";
                case ".go": return "go";
                case ".rb": return "ruby";
                case ".sh": return "bash";
                case ".ps1": return "powershell";
                case ".sql": return "sql";
                case ".yaml":
                case ".yml": return "yaml";
                case ".md":
                case ".markdown": return "markdown";
                case ".csv": return "csv";
                default: return string.Empty;
            }
        }

        internal static string Describe(IReadOnlyList<TextAttachment> files)
        {
            if (files == null || files.Count == 0)
            {
                return "无文件";
            }

            var total = 0;
            var names = new List<string>();
            foreach (var file in files)
            {
                total += file.ByteLength;
                names.Add(file.Name);
            }

            return $"{files.Count} 个文件，合计 {total / 1024.0:F0} KB（{string.Join("、", names)}）";
        }
    }
}
