using System.Collections.Generic;

namespace ChatSheet.AddIn.Providers
{
    internal enum ChatRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
        Tool = 3,
    }

    /// <summary>模型请求的工具调用。</summary>
    internal sealed class ToolCall
    {
        internal string Id { get; set; }

        internal string Name { get; set; }

        /// <summary>参数的 JSON 文本。流式场景下按增量拼接而成。</summary>
        internal string ArgumentsJson { get; set; }
    }

    internal sealed class ChatMessage
    {
        internal ChatRole Role { get; set; }

        internal string Content { get; set; }

        /// <summary>随本条消息发送的图片。仅用户消息会带。</summary>
        internal List<ImageAttachment> Images { get; } = new List<ImageAttachment>();

        /// <summary>助手消息附带的工具调用。</summary>
        internal List<ToolCall> ToolCalls { get; } = new List<ToolCall>();

        /// <summary>工具结果消息对应的调用标识。</summary>
        internal string ToolCallId { get; set; }

        /// <summary>工具名称，Gemini 与 Anthropic 的结果消息需要它。</summary>
        internal string ToolName { get; set; }

        internal static ChatMessage FromSystem(string content) =>
            new ChatMessage { Role = ChatRole.System, Content = content };

        internal static ChatMessage FromUser(string content) =>
            new ChatMessage { Role = ChatRole.User, Content = content };

        /// <summary>带图片的用户消息。</summary>
        internal static ChatMessage FromUser(string content, IEnumerable<ImageAttachment> images)
        {
            var message = new ChatMessage { Role = ChatRole.User, Content = content };
            if (images != null)
            {
                message.Images.AddRange(images);
            }

            return message;
        }

        internal static ChatMessage FromAssistant(string content) =>
            new ChatMessage { Role = ChatRole.Assistant, Content = content };

        internal static ChatMessage FromToolResult(string toolCallId, string toolName, string content) =>
            new ChatMessage
            {
                Role = ChatRole.Tool,
                ToolCallId = toolCallId,
                ToolName = toolName,
                Content = content,
            };
    }

    internal sealed class ChatRequest
    {
        internal ProtocolKind Protocol { get; set; }

        internal string BaseUrl { get; set; }

        internal string Token { get; set; }

        internal string Model { get; set; }

        internal List<ChatMessage> Messages { get; } = new List<ChatMessage>();

        internal ThinkingLevel Thinking { get; set; } = ThinkingLevel.Off;

        internal double? Temperature { get; set; }

        internal int? MaxOutputTokens { get; set; }

        /// <summary>是否附带工具声明。纯问答场景可关闭以省 token。</summary>
        internal bool IncludeTools { get; set; } = true;

        /// <summary>
        /// 强制指定 Anthropic 的思考控制方式，为空则按模型名推断。
        /// 用于服务端以 400 拒绝某种方式后自动改用另一种重试。
        /// </summary>
        internal AnthropicThinkingStyle? AnthropicStyleOverride { get; set; }
    }

    /// <summary>流式事件类型。</summary>
    internal enum ChatEventKind
    {
        /// <summary>正文增量。</summary>
        TextDelta = 0,

        /// <summary>思考过程增量。</summary>
        ThinkingDelta = 1,

        /// <summary>工具调用已完整解析。</summary>
        ToolCall = 2,

        /// <summary>本轮结束。</summary>
        Completed = 3,

        /// <summary>用量统计。</summary>
        Usage = 4,
    }

    internal sealed class ChatEvent
    {
        internal ChatEventKind Kind { get; set; }

        internal string Text { get; set; }

        internal ToolCall Call { get; set; }

        internal int PromptTokens { get; set; }

        internal int CompletionTokens { get; set; }

        /// <summary>结束原因，例如 stop、tool_calls、length。</summary>
        internal string FinishReason { get; set; }
    }
}
