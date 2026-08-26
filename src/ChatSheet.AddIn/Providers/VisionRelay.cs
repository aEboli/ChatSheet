using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 视觉中转：用另一个有视觉能力的模型把图片转成文字，交给看不了图的主模型。
    ///
    /// 只换模型名，连接、协议与密钥都沿用当前这一套——要求用户再配一套接入信息
    /// 会把「贴张截图」这件小事变成一次配置作业，而同一个服务商下通常本来就有
    /// 带视觉的型号可用。
    /// </summary>
    internal static class VisionRelay
    {
        /// <summary>
        /// 转述的提示词。
        ///
        /// 刻意限定在表格用途上：泛泛地「描述这张图」会得到「一张电子表格的截图」
        /// 这类无用回答，而主模型需要的是能据以判断问题的具体内容——
        /// 有哪些列、数值是什么、报错原文写了什么。
        /// </summary>
        private const string RelayPrompt =
            "请把这张图片的内容转写成文字，供一个看不到图片的表格助手使用。要求：\n" +
            "1) 若是表格截图，按行列复述可见内容，先写表头，再写各行数据，保留原始数字与单位；" +
            "说明可见的范围地址（如 A1:D20）与是否有合并单元格。\n" +
            "2) 若含报错或提示框，原样抄下完整文案，包括错误代码。\n" +
            "3) 若是图表，说明图表类型、坐标轴含义与大致趋势。\n" +
            "4) 只描述实际看到的内容，看不清就写「看不清」，不要推测或补全。\n" +
            "直接给出转写结果，不要写开场白。";

        /// <summary>单张图的转述上限。描述太长会挤占主模型的上下文。</summary>
        private const int MaxDescriptionChars = 4000;

        /// <summary>
        /// 把一张图转成文字。失败时抛 <see cref="ProviderException"/>，
        /// 由调用方决定退回「去图续跑」。
        /// </summary>
        internal static async Task<string> DescribeAsync(
            ResolvedRelayTarget target,
            ImageAttachment image,
            CancellationToken cancellationToken)
        {
            var request = new ChatRequest
            {
                Protocol = target.Protocol,
                BaseUrl = target.BaseUrl,
                Token = target.Token,
                Model = target.Model,
                // 转述是照抄可见内容，不需要思考档位，也不该带工具。
                Thinking = ThinkingLevel.Off,
                IncludeTools = false,
                MaxOutputTokens = 2048,
            };

            request.Messages.Add(ChatMessage.FromUser(RelayPrompt, new[] { image }));

            var text = new StringBuilder();

            using (var client = new ChatClient())
            {
                await client.StreamAsync(
                    request,
                    chatEvent =>
                    {
                        if (chatEvent.Kind == ChatEventKind.TextDelta)
                        {
                            text.Append(chatEvent.Text);
                        }

                        return Task.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var description = text.ToString().Trim();
            if (description.Length == 0)
            {
                throw new ProviderException("RELAY_EMPTY", $"视觉中转模型 {target.Model} 没有返回任何描述。");
            }

            if (description.Length > MaxDescriptionChars)
            {
                description = description.Substring(0, MaxDescriptionChars) + "…（描述过长已截断）";
            }

            return description;
        }

        /// <summary>
        /// 把转述结果拼成替代图片的文本。
        ///
        /// 必须写明这是转述而非用户原话：主模型据此知道细节可能有损，
        /// 遇到关键数字时会先读一遍表格核对，而不是直接拿描述当事实写进单元格。
        /// </summary>
        internal static string ComposeDescriptions(IReadOnlyList<string> descriptions, int total)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"（系统提示：用户附了 {total} 张图片，而你没有视觉能力。" +
                "以下是另一个模型对这些图片的文字转写，供你参考。转写可能有损，" +
                "涉及具体数值时请调用工具读取表格核对，不要直接采信。）");

            for (var i = 0; i < descriptions.Count; i++)
            {
                builder.AppendLine();
                builder.AppendLine($"图片 {i + 1}/{total} 的转写：");
                builder.AppendLine(descriptions[i]);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>没有中转模型可用时，告知主模型图片的存在。</summary>
        internal static string ComposeUnavailableNotice(int total, string reason)
        {
            var builder = new StringBuilder();
            builder.Append($"（系统提示：用户附了 {total} 张图片，但你没有视觉能力，图片未能送达。");

            if (!string.IsNullOrEmpty(reason))
            {
                builder.Append(reason);
            }

            builder.Append("请不要假装看过图片。若这张图对回答是必要的，" +
                "请说明你看不到图片，并告诉用户可以改用带视觉的模型，" +
                "或让用户把图中的关键内容用文字说明；同时尽量用工具读取表格来自行获取所需事实。）");

            return builder.ToString();
        }
    }

    /// <summary>中转请求的目标：沿用当前连接，只替换模型名。</summary>
    internal sealed class ResolvedRelayTarget
    {
        internal ProtocolKind Protocol { get; set; }

        internal string BaseUrl { get; set; }

        internal string Token { get; set; }

        internal string Model { get; set; }
    }
}
