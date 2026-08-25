using System;
using System.Collections.Generic;
using System.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 统一的思考强度档位。
    ///
    /// 取值取三家的并集，映射时再收敛到各协议实际支持的值：
    ///   OpenAI  reasoning_effort：none / minimal / low / medium / high / xhigh / max
    ///   Anthropic output_config.effort：low / medium / high（默认）/ xhigh / max
    ///   Gemini  thinking_level：minimal / low / medium / high
    /// 各模型只支持其中的子集，因此映射一律做就近降级而非报错。
    /// </summary>
    internal enum ThinkingLevel
    {
        /// <summary>不思考。Anthropic 侧对应 thinking.type=disabled。</summary>
        Off = 0,
        Minimal = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        XHigh = 5,
        Max = 6,
    }

    /// <summary>Anthropic 的思考控制方式。两代模型互不兼容，必须分别处理。</summary>
    internal enum AnthropicThinkingStyle
    {
        /// <summary>
        /// 新方式：thinking.type=adaptive + output_config.effort。
        /// 适用于 4.6 及更新的模型；4.7 及更新的模型只接受这种方式。
        /// </summary>
        Adaptive = 0,

        /// <summary>
        /// 旧方式：thinking.type=enabled + budget_tokens。
        /// 仅适用于 4.5 及更早的模型；在 4.7+ 上会返回 400。
        /// </summary>
        Budget = 1,
    }

    internal static class Thinking
    {
        /// <summary>
        /// 界面档位清单。
        ///
        /// 标签一律用英文原名（Off/Low/High…），与三家协议的参数取值逐字一致。
        /// 此前用中文（「低」「超高」），但档位名同时也是排查时要在日志、
        /// 请求体和官方文档之间来回对照的东西，多一层翻译就多一次心算，
        /// 「超高」到底对应 xhigh 还是 max 并不能从字面看出来。
        /// 说明文字仍用中文——那是解释用途，不是标识符。
        /// </summary>
        internal static readonly IReadOnlyList<ThinkingOption> Options = new List<ThinkingOption>
        {
            new ThinkingOption(ThinkingLevel.Off, "Off", "Off", "不思考，最快，适合简单改动"),
            new ThinkingOption(ThinkingLevel.Minimal, "Minimal", "Minimal", "仅 OpenAI 与 Gemini 支持，其他协议按 Low 处理"),
            new ThinkingOption(ThinkingLevel.Low, "Low", "Low", "速度优先，适合明确的小任务"),
            new ThinkingOption(ThinkingLevel.Medium, "Medium", "Medium", "速度与质量平衡"),
            new ThinkingOption(ThinkingLevel.High, "High", "High", "多数模型的默认档，适合复杂表格逻辑"),
            new ThinkingOption(ThinkingLevel.XHigh, "XHigh", "XHigh", "长链路任务；不支持时按 High 处理"),
            new ThinkingOption(ThinkingLevel.Max, "Max", "Max", "不限制思考开销；不支持时按 High 处理"),
        };

        internal static bool TryParse(string value, out ThinkingLevel level)
        {
            if (Enum.TryParse(value, ignoreCase: true, out level))
            {
                return true;
            }

            level = ThinkingLevel.Off;
            return false;
        }

        /// <summary>
        /// OpenAI 系的 reasoning_effort 值。返回 null 表示不传该参数。
        /// none 是官方取值，用于明确要求不推理。
        /// </summary>
        internal static string OpenAiEffort(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Off: return "none";
                case ThinkingLevel.Minimal: return "minimal";
                case ThinkingLevel.Low: return "low";
                case ThinkingLevel.Medium: return "medium";
                case ThinkingLevel.High: return "high";
                case ThinkingLevel.XHigh: return "xhigh";
                case ThinkingLevel.Max: return "max";
                default: return null;
            }
        }

        /// <summary>
        /// Anthropic 的 output_config.effort 值。
        /// 该参数没有 none/minimal 档，minimal 就近降级为 low；
        /// 关闭思考由 thinking.type=disabled 表达，不通过 effort。
        /// </summary>
        internal static string AnthropicEffort(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Minimal:
                case ThinkingLevel.Low:
                    return "low";
                case ThinkingLevel.Medium: return "medium";
                case ThinkingLevel.High: return "high";
                case ThinkingLevel.XHigh: return "xhigh";
                case ThinkingLevel.Max: return "max";
                default: return null;
            }
        }

        /// <summary>Gemini 的 thinking_level 值。该参数只有四档。</summary>
        internal static string GeminiLevel(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Minimal: return "minimal";
                case ThinkingLevel.Low: return "low";
                case ThinkingLevel.Medium: return "medium";
                case ThinkingLevel.High:
                case ThinkingLevel.XHigh:
                case ThinkingLevel.Max:
                    // Gemini 没有超出 high 的档位，就近取 high。
                    return "high";
                default: return null;
            }
        }

        /// <summary>
        /// 把档位换算成 Anthropic 旧接口的 budget_tokens。
        /// 下限 1024 是官方硬性要求，且必须小于 max_tokens。
        /// </summary>
        internal static int? AnthropicBudget(ThinkingLevel level, int maxTokens)
        {
            if (level == ThinkingLevel.Off)
            {
                return null;
            }

            double ratio;
            switch (level)
            {
                case ThinkingLevel.Minimal: ratio = 0.15; break;
                case ThinkingLevel.Low: ratio = 0.25; break;
                case ThinkingLevel.Medium: ratio = 0.45; break;
                case ThinkingLevel.High: ratio = 0.65; break;
                default: ratio = 0.8; break;
            }

            var budget = (int)(maxTokens * ratio);
            // 留出至少 512 给正文，否则思考会吃掉全部输出预算。
            var ceiling = Math.Max(1024, maxTokens - 512);
            return Math.Max(1024, Math.Min(budget, ceiling));
        }

        /// <summary>
        /// 这些模型系列支持 output_config.effort 与 adaptive 思考。
        /// 名单来自官方 effort 文档的「Supported models」。
        /// </summary>
        private static readonly string[] EffortCapableMarkers =
        {
            "opus-4-5", "opus-4.5",
            "sonnet-4-6", "sonnet-4.6",
            "opus-4-6", "opus-4.6",
            "opus-4-7", "opus-4.7",
            "opus-4-8", "opus-4.8",
            "opus-5", "sonnet-5", "fable-5", "mythos-5", "mythos-preview",
        };

        /// <summary>
        /// 只支持旧方式（enabled + budget_tokens）的模型系列。
        ///
        /// 刻意列举「旧」而非「新」：新模型会不断出现，若采用白名单式的
        /// 「新模型清单」，每出一个新模型都会被误判为旧模型；
        /// 而旧模型集合是封闭的，不会再增加。
        /// </summary>
        private static readonly string[] BudgetOnlyMarkers =
        {
            "sonnet-4-5", "sonnet-4.5",
            "haiku-4-5", "haiku-4.5",
            "opus-4-1", "opus-4.1",
            "sonnet-4-0", "sonnet-4.0",
            "opus-4-0", "opus-4.0",
            "claude-3-7", "claude-3.7",
            "claude-3-5", "claude-3.5",
            "claude-3-opus", "claude-3-sonnet", "claude-3-haiku",
        };

        /// <summary>
        /// 判定该模型应使用哪种思考控制方式。
        ///
        /// 依据模型名做启发式判断：模型名是请求前唯一可得的信息。
        /// 未知名称（例如代理服务自定义的模型名）一律按新方式处理——
        /// 新模型明确拒绝旧参数，而旧模型对 effort 只是忽略，代价更小。
        ///
        /// 判断错也不致命：ChatClient 收到相关 400 时会自动改用另一种方式重试。
        /// </summary>
        internal static AnthropicThinkingStyle StyleFor(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return AnthropicThinkingStyle.Adaptive;
            }

            var normalized = model.ToLowerInvariant();

            // 明确属于旧模型集合的才用旧方式。
            if (BudgetOnlyMarkers.Any(m => normalized.Contains(m)) &&
                !EffortCapableMarkers.Any(m => normalized.Contains(m)))
            {
                return AnthropicThinkingStyle.Budget;
            }

            return AnthropicThinkingStyle.Adaptive;
        }

        /// <summary>各协议实际支持的档位，供界面标注哪些会被降级。</summary>
        internal static IReadOnlyList<string> SupportedLevels(ProtocolKind protocol)
        {
            switch (protocol)
            {
                case ProtocolKind.AnthropicMessages:
                    return new[] { "Off", "Low", "Medium", "High", "XHigh", "Max" };
                case ProtocolKind.GoogleGemini:
                    return new[] { "Off", "Minimal", "Low", "Medium", "High" };
                default:
                    return new[] { "Off", "Minimal", "Low", "Medium", "High", "XHigh", "Max" };
            }
        }
    }

    internal sealed class ThinkingOption
    {
        internal ThinkingOption(ThinkingLevel level, string id, string label, string hint)
        {
            Level = level;
            Id = id;
            Label = label;
            Hint = hint;
        }

        internal ThinkingLevel Level { get; }

        internal string Id { get; }

        internal string Label { get; }

        internal string Hint { get; }
    }
}
