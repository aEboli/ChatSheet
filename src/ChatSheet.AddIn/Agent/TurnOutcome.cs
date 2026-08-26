using System;

namespace ChatSheet.AddIn.Agent
{
    /// <summary>本步为何没能自然结束。</summary>
    internal enum StepStall
    {
        /// <summary>正常结束，可以收尾。</summary>
        None = 0,

        /// <summary>输出被上限截断，模型话没说完。</summary>
        Truncated = 1,

        /// <summary>既没有正文也没有工具调用，等于什么都没产出。</summary>
        Empty = 2,
    }

    /// <summary>
    /// 判断一步是否属于「被打断」而非「说完了」。
    ///
    /// 这是「分析完数据就停下来等人手动继续」的根源：输出上限（默认 8192）
    /// 在开着高档思考时很容易被推理本身吃光，服务端于是截断——正文是空的，
    /// 工具调用也没发出。上层若只看「没有待执行的工具调用」就判定本轮结束，
    /// 表现出来就是任务做了一半自己停住，用户必须再发一条消息才能接上。
    ///
    /// 三条判据缺一不可：
    ///   - 结束原因：最直接，但不少中转网关根本不回 finish_reason（实测日志里
    ///     结束原因为「未提供」）。
    ///   - 用量贴顶：输出 token 恰好等于上限，几乎只可能是被截断。
    ///     留 2% 余量是因为部分服务端的计数与请求上限略有出入。
    ///   - 空产出：无论是不是截断，这一步都没有任何进展，直接收尾就是把
    ///     未完成的任务丢给用户。
    /// </summary>
    internal static class TurnOutcome
    {
        /// <summary>连续自动续跑的上限。超过说明每次都被截断，再续也只是空转。</summary>
        internal const int MaxAutoContinues = 3;

        /// <summary>各协议表示「达到输出上限」的结束原因。</summary>
        internal static bool IsLengthFinish(string finishReason)
        {
            if (string.IsNullOrWhiteSpace(finishReason))
            {
                return false;
            }

            var value = finishReason.Trim();
            return Eq(value, "length")             // OpenAI Chat Completions
                || Eq(value, "max_tokens")        // Anthropic Messages
                || Eq(value, "MAX_TOKENS")        // Gemini
                || Eq(value, "response.incomplete") // OpenAI Responses
                || Eq(value, "incomplete");
        }

        /// <summary>
        /// 判断本步的停顿类型。
        /// </summary>
        /// <param name="finishReason">服务端给的结束原因，可为空。</param>
        /// <param name="hasToolCalls">本步是否产出了工具调用。</param>
        /// <param name="textLength">本步正文长度。</param>
        /// <param name="completionTokens">本步输出 token 数，0 表示服务端没报。</param>
        /// <param name="maxOutputTokens">请求里给的输出上限。</param>
        internal static StepStall Classify(
            string finishReason,
            bool hasToolCalls,
            int textLength,
            int completionTokens,
            int maxOutputTokens)
        {
            var truncated = IsLengthFinish(finishReason) || HitsOutputCap(completionTokens, maxOutputTokens);

            if (truncated)
            {
                // 已经发出工具调用时不算停顿：工具结果会自然带出下一步，
                // 截断只会体现为参数残缺，那由参数解析那条路径去解释。
                return hasToolCalls ? StepStall.None : StepStall.Truncated;
            }

            if (!hasToolCalls && textLength == 0)
            {
                return StepStall.Empty;
            }

            return StepStall.None;
        }

        /// <summary>输出用量是否贴住上限。留 2% 余量容忍服务端计数偏差。</summary>
        private static bool HitsOutputCap(int completionTokens, int maxOutputTokens)
        {
            if (completionTokens <= 0 || maxOutputTokens <= 0)
            {
                return false;
            }

            return completionTokens >= (int)(maxOutputTokens * 0.98);
        }

        /// <summary>
        /// 参数 JSON 是否像是被输出上限截断，而不是模型写错了格式。
        ///
        /// 分开判断是为了给模型不同的指示：格式写错应当改写，被截断则必须
        /// 减小单次数据量——否则它会用同样的参数一再重发，每次都断在同一处
        /// （实测日志里连续三步都断在 position 51）。
        /// </summary>
        internal static bool LooksTruncatedJson(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson))
            {
                return false;
            }

            var text = argumentsJson.TrimEnd();
            if (text.Length == 0)
            {
                return false;
            }

            // 截断的共同特征是括号没配平：结尾少了收束符号。
            // 逐字符扫描并跳过字符串字面量，避免把数据里的括号计进去。
            var depth = 0;
            var inString = false;
            var escaped = false;

            foreach (var c in text)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                switch (c)
                {
                    case '"': inString = true; break;
                    case '{':
                    case '[': depth++; break;
                    case '}':
                    case ']': depth--; break;
                }
            }

            // 字符串没闭合，或还有没收束的括号，都说明内容在中途断掉了。
            return inString || depth > 0;
        }

        /// <summary>被截断的参数该怎么回给模型。</summary>
        internal static string DescribeTruncatedArguments(string toolName, int argumentsLength)
        {
            return $"{toolName} 的参数在传输中被输出长度上限截断（已收到 {argumentsLength} 个字符，" +
                "JSON 不完整）。不要用同样的参数重发，那会断在同一处。请把这次写入拆成多次：" +
                "每次只写一部分行（建议不超过 100 行），逐批完成。";
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
