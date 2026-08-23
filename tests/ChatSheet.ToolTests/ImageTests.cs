using System;
using System.Linq;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 图片输入验证。
    ///
    /// 四种协议的多模态格式互不相同，且都是刚按官方文档实现的：
    ///   Chat Completions：content 数组 + image_url 对象内嵌 data URL
    ///   Responses：      content 数组 + input_image，image_url 为字符串
    ///   Anthropic：      内容块 + source.type=base64 + media_type
    ///   Gemini：         parts + inlineData.mimeType
    /// 写错任何一处都会被服务端拒绝，因此逐个协议核对结构。
    /// </summary>
    internal static class ImageTests
    {
        // 1×1 像素的合法 PNG，来自官方文档示例。
        private const string TinyPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

        private static string TinyPngDataUrl => "data:image/png;base64," + TinyPngBase64;

        internal static void Run(Action<string, bool, string> report)
        {
            TestParsing(report);
            TestOpenAiChatFormat(report);
            TestResponsesFormat(report);
            TestAnthropicFormat(report);
            TestGeminiFormat(report);
            TestImageOrdering(report);
        }

        private static ChatRequest RequestWithImage(ProtocolKind protocol, string text = "这是什么？")
        {
            var request = new ChatRequest
            {
                Protocol = protocol,
                BaseUrl = "https://example.com/v1",
                Token = "t",
                Model = "test-model",
                MaxOutputTokens = 4096,
                IncludeTools = false,
            };

            var image = ImageSupport.ParseDataUrl(TinyPngDataUrl, "tiny.png");
            request.Messages.Add(ChatMessage.FromUser(text, new[] { image }));
            return request;
        }

        private static void TestParsing(Action<string, bool, string> report)
        {
            var image = ImageSupport.ParseDataUrl(TinyPngDataUrl, "tiny.png");
            report("解析 data URL 得到媒体类型", image.MediaType == "image/png", image.MediaType);
            report("解析后 base64 不含前缀", image.Base64 == TinyPngBase64, "");
            report("记录字节长度", image.ByteLength > 0, $"实际 {image.ByteLength}");
            report("回拼 data URL 一致", image.ToDataUrl() == TinyPngDataUrl, "");

            // 不支持的格式必须明确拒绝，而不是发出去让服务端报错。
            foreach (var bad in new[] { "image/bmp", "image/tiff", "image/svg+xml" })
            {
                try
                {
                    ImageSupport.ParseDataUrl($"data:{bad};base64,{TinyPngBase64}", "x");
                    report($"拒绝 {bad}", false, "未抛异常");
                }
                catch (ProviderException ex)
                {
                    report($"拒绝 {bad}", ex.Code == "IMAGE_UNSUPPORTED", ex.Code);
                }
            }

            // 三方通吃的格式都要接受。
            foreach (var good in new[] { "image/png", "image/jpeg", "image/webp" })
            {
                try
                {
                    var parsed = ImageSupport.ParseDataUrl($"data:{good};base64,{TinyPngBase64}", "x");
                    report($"接受 {good}", parsed.MediaType == good, parsed.MediaType);
                }
                catch (ProviderException ex)
                {
                    report($"接受 {good}", false, ex.Message);
                }
            }

            // 非 data URL 与坏 base64 都应报错。
            foreach (var pair in new[]
            {
                new { Input = "https://example.com/a.png", Code = "IMAGE_INVALID" },
                new { Input = "data:image/png,notbase64", Code = "IMAGE_INVALID" },
                new { Input = "data:image/png;base64,!!!非法!!!", Code = "IMAGE_INVALID" },
                new { Input = "", Code = "IMAGE_EMPTY" },
            })
            {
                try
                {
                    ImageSupport.ParseDataUrl(pair.Input, "x");
                    report($"拒绝非法输入 {Trim(pair.Input)}", false, "未抛异常");
                }
                catch (ProviderException ex)
                {
                    report($"拒绝非法输入 {Trim(pair.Input)}", ex.Code == pair.Code, $"实际 {ex.Code}");
                }
            }

            // 超过 5MB 上限要拒绝。构造一个大于上限的载荷。
            var oversized = new string('A', (ImageSupport.MaxBytesPerImage / 3 + 100) * 4);
            try
            {
                ImageSupport.ParseDataUrl($"data:image/png;base64,{oversized}", "big.png");
                report("拒绝超大图片", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("拒绝超大图片", ex.Code == "IMAGE_TOO_LARGE" || ex.Code == "IMAGE_INVALID", ex.Code);
            }
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrEmpty(value)) { return "<空>"; }
            return value.Length > 30 ? value.Substring(0, 30) + "…" : value;
        }

        private static void TestOpenAiChatFormat(Action<string, bool, string> report)
        {
            var body = RequestBuilder.Build(RequestWithImage(ProtocolKind.OpenAiChatCompletions), stream: true);
            var messages = body["messages"] as JArray;
            var user = messages?.LastOrDefault() as JObject;
            var content = user?["content"] as JArray;

            report("Chat content 为数组", content != null, user?["content"]?.Type.ToString());
            if (content == null) { return; }

            var imageBlock = content.FirstOrDefault(b => b.Value<string>("type") == "image_url") as JObject;
            report("Chat 含 image_url 块", imageBlock != null, content.ToString());

            // image_url 是对象，其 url 字段放 data URL。
            var url = (imageBlock?["image_url"] as JObject)?.Value<string>("url");
            report("Chat image_url.url 为 data URL", url == TinyPngDataUrl, Trim(url));

            var textBlock = content.FirstOrDefault(b => b.Value<string>("type") == "text") as JObject;
            report("Chat 含 text 块", textBlock?.Value<string>("text") == "这是什么？", textBlock?.ToString());

            // 无图片时 content 应退回字符串，避免无谓的结构变化。
            var plain = new ChatRequest
            {
                Protocol = ProtocolKind.OpenAiChatCompletions,
                BaseUrl = "https://example.com/v1",
                Model = "m",
                IncludeTools = false,
            };
            plain.Messages.Add(ChatMessage.FromUser("纯文本"));
            var plainBody = RequestBuilder.Build(plain, stream: true);
            var plainUser = (plainBody["messages"] as JArray)?.LastOrDefault() as JObject;
            report(
                "无图片时 content 仍为字符串",
                plainUser?["content"]?.Type == JTokenType.String,
                plainUser?["content"]?.Type.ToString());
        }

        private static void TestResponsesFormat(Action<string, bool, string> report)
        {
            var body = RequestBuilder.Build(RequestWithImage(ProtocolKind.OpenAiResponses), stream: true);
            var input = body["input"] as JArray;
            var user = input?.LastOrDefault() as JObject;
            var content = user?["content"] as JArray;

            report("Responses content 为数组", content != null, user?["content"]?.Type.ToString());
            if (content == null) { return; }

            var imageBlock = content.FirstOrDefault(b => b.Value<string>("type") == "input_image") as JObject;
            report("Responses 用 input_image", imageBlock != null, content.ToString());

            // 与 Chat Completions 不同：这里 image_url 直接是字符串。
            report(
                "Responses image_url 为字符串",
                imageBlock?["image_url"]?.Type == JTokenType.String &&
                imageBlock.Value<string>("image_url") == TinyPngDataUrl,
                imageBlock?["image_url"]?.Type.ToString());

            var textBlock = content.FirstOrDefault(b => b.Value<string>("type") == "input_text") as JObject;
            report("Responses 用 input_text", textBlock != null, content.ToString());
        }

        private static void TestAnthropicFormat(Action<string, bool, string> report)
        {
            var body = RequestBuilder.Build(RequestWithImage(ProtocolKind.AnthropicMessages), stream: true);
            var messages = body["messages"] as JArray;
            var user = messages?.LastOrDefault() as JObject;
            var content = user?["content"] as JArray;

            report("Anthropic content 为数组", content != null, user?["content"]?.Type.ToString());
            if (content == null) { return; }

            var imageBlock = content.FirstOrDefault(b => b.Value<string>("type") == "image") as JObject;
            report("Anthropic 含 image 块", imageBlock != null, content.ToString());

            var source = imageBlock?["source"] as JObject;
            report("source.type=base64", source?.Value<string>("type") == "base64", source?.ToString());
            report("source.media_type 正确", source?.Value<string>("media_type") == "image/png", source?.ToString());
            // data 是纯 base64，不能带 data URL 前缀。
            report(
                "source.data 为纯 base64",
                source?.Value<string>("data") == TinyPngBase64,
                Trim(source?.Value<string>("data")));
            report(
                "Anthropic 不使用 data URL",
                source?.Value<string>("data")?.StartsWith("data:") != true,
                "");
        }

        private static void TestGeminiFormat(Action<string, bool, string> report)
        {
            var body = RequestBuilder.Build(RequestWithImage(ProtocolKind.GoogleGemini), stream: true);
            var contents = body["contents"] as JArray;
            var user = contents?.LastOrDefault() as JObject;
            var parts = user?["parts"] as JArray;

            report("Gemini parts 存在", parts != null, user?.ToString());
            if (parts == null) { return; }

            var imagePart = parts.FirstOrDefault(p => p["inlineData"] != null) as JObject;
            report("Gemini 用 inlineData", imagePart != null, parts.ToString());

            var inline = imagePart?["inlineData"] as JObject;
            // 字段名是驼峰 mimeType，不是 mime_type。
            report("inlineData.mimeType 正确", inline?.Value<string>("mimeType") == "image/png", inline?.ToString());
            report("inlineData.data 为纯 base64", inline?.Value<string>("data") == TinyPngBase64, "");

            var textPart = parts.FirstOrDefault(p => p["text"] != null) as JObject;
            report("Gemini 含 text part", textPart?.Value<string>("text") == "这是什么？", textPart?.ToString());
        }

        private static void TestImageOrdering(Action<string, bool, string> report)
        {
            // 官方建议图片置于文字之前，四种协议都应如此排列。
            var cases = new[]
            {
                new { Protocol = ProtocolKind.OpenAiChatCompletions, Container = "messages", Field = "content", ImageType = "image_url" },
                new { Protocol = ProtocolKind.OpenAiResponses, Container = "input", Field = "content", ImageType = "input_image" },
                new { Protocol = ProtocolKind.AnthropicMessages, Container = "messages", Field = "content", ImageType = "image" },
            };

            foreach (var c in cases)
            {
                var body = RequestBuilder.Build(RequestWithImage(c.Protocol), stream: true);
                var user = (body[c.Container] as JArray)?.LastOrDefault() as JObject;
                var content = user?[c.Field] as JArray;
                if (content == null || content.Count < 2)
                {
                    report($"{c.Protocol} 图片先于文字", false, "内容块不足");
                    continue;
                }

                report(
                    $"{c.Protocol} 图片先于文字",
                    content[0].Value<string>("type") == c.ImageType,
                    $"首块为 {content[0].Value<string>("type")}");
            }

            // Gemini 同样应图片在前。
            var gemini = RequestBuilder.Build(RequestWithImage(ProtocolKind.GoogleGemini), stream: true);
            var parts = ((gemini["contents"] as JArray)?.LastOrDefault() as JObject)?["parts"] as JArray;
            report(
                "Gemini 图片先于文字",
                parts != null && parts.Count >= 2 && parts[0]["inlineData"] != null,
                parts?.ToString());

            // 只有图片没有文字也要能构造出合法请求。
            var imageOnly = RequestWithImage(ProtocolKind.AnthropicMessages, text: string.Empty);
            var onlyBody = RequestBuilder.Build(imageOnly, stream: true);
            var onlyContent = ((onlyBody["messages"] as JArray)?.LastOrDefault() as JObject)?["content"] as JArray;
            report(
                "仅图片无文字时不产生空文本块",
                onlyContent != null && onlyContent.Count == 1 &&
                onlyContent[0].Value<string>("type") == "image",
                onlyContent?.ToString());
        }
    }
}
