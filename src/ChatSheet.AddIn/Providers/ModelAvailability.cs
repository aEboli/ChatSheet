using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>一个「连接 + 模型」当前的可用性判定。</summary>
    internal enum AvailabilityVerdict
    {
        /// <summary>没有任何证据。默认态，也是所有拿不准情形的归宿。</summary>
        Unknown = 0,

        /// <summary>请求到达过这个模型并得到回复。</summary>
        Available = 1,

        /// <summary>请求失败，且服务端原文点名了这个模型。</summary>
        Unavailable = 2,
    }

    /// <summary>
    /// 「这个模型现在能不能用」的判定缓存。
    ///
    /// 与 ModelCapabilities 是三个互不改写的维度：那边记「能不能调工具、能不能看图」，
    /// 这边只记「请求到得了模型吗」。不支持工具的模型是可用的——既有规范已经为它
    /// 准备了顾问模式，把它算成不可用会把一个规范认定可用的选项从用户眼前拿掉。
    ///
    /// 判定全部来自真实对话，不额外发请求：这份信息用户已经付过钱了。
    ///
    /// 作用域是 Excel 进程存续期间，不是「本次面板会话」。刻意不沿用
    /// ModelCapabilities 那句「重开面板重探一次」——那句话代码从来没兑现过：
    /// ComAddIn.EnsurePane 只在 _pane 为空时创建，关闭面板只改 IsVisible，
    /// 控件不销毁、AgentChannels 不重建，而这里的字典是进程级静态字段。
    /// 写一条做不到的规范比不写更糟。
    /// </summary>
    internal static class ModelAvailability
    {
        /// <summary>
        /// 键的比较忽略大小写。
        ///
        /// 必须如此：ChatClient.ExtractModelIds 用 OrdinalIgnoreCase 去重，
        /// 用户手填 GPT-4O 而目录里是 gpt-4o 时，若这里区分大小写，判定会落在
        /// 一个键上而行渲染查另一个键，表现是「转完了，行上什么都没变」。
        ///
        /// 刻意不改 ModelCapabilities 的 Ordinal：那是既有能力回退的行为。
        /// </summary>
        private static readonly ConcurrentDictionary<string, AvailabilityVerdict> Entries =
            new ConcurrentDictionary<string, AvailabilityVerdict>(StringComparer.OrdinalIgnoreCase);

        /// <summary>档案键。必须含连接：同一个模型名经不同网关转发，结论可以完全不同。</summary>
        internal static string KeyFor(string connectionKey, string model)
        {
            return (connectionKey ?? string.Empty) + " " + (model ?? string.Empty);
        }

        internal static AvailabilityVerdict For(string connectionKey, string model)
        {
            return Entries.TryGetValue(KeyFor(connectionKey, model), out var verdict)
                ? verdict
                : AvailabilityVerdict.Unknown;
        }

        /// <summary>
        /// 记一条判定。
        ///
        /// Unknown 表示「没有证据」，因此写入 Unknown 等于擦掉已有结论，
        /// 而不是覆盖成一个更弱的结论——限流那一轮不该把上一轮真实的「可用」抹掉。
        /// </summary>
        internal static void Record(string connectionKey, string model, AvailabilityVerdict verdict)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            if (verdict == AvailabilityVerdict.Unknown)
            {
                return;
            }

            // 判定不得比证据活得久：后一轮的结论直接盖掉前一轮。
            // 判过不可用的模型后来答了话就要改回可用，反之同理。
            Entries[KeyFor(connectionKey, model)] = verdict;
        }

        /// <summary>
        /// 作废一个连接的全部判定。
        ///
        /// ModelCapabilities 只有全清的 Reset，而这里必须按连接来：换密钥只说明
        /// 这一个连接能碰到的模型集合变了，牵连别的连接是多清。
        /// </summary>
        internal static void ResetConnection(string connectionKey)
        {
            var prefix = (connectionKey ?? string.Empty) + " ";
            foreach (var key in new List<string>(Entries.Keys))
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Entries.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// 一个连接下所有已有判定的模型。键是模型 ID，值是三态之一。
        ///
        /// 只给已有判定的：没判定就是「未确认」，由面板把缺席渲染成那个状态。
        /// 为整份目录逐个下发 Unknown 是白费——目录在面板侧，这边并不知道有哪些。
        /// </summary>
        internal static IReadOnlyDictionary<string, AvailabilityVerdict> SnapshotFor(string connectionKey)
        {
            var prefix = (connectionKey ?? string.Empty) + " ";
            var snapshot = new Dictionary<string, AvailabilityVerdict>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in Entries)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    snapshot[pair.Key.Substring(prefix.Length)] = pair.Value;
                }
            }

            return snapshot;
        }

        /// <summary>仅供测试使用。</summary>
        internal static void Reset()
        {
            Entries.Clear();
        }

        /// <summary>
        /// 把一轮失败折算成判定。
        ///
        /// 只有「客户端错误 + 原文点名了这个模型」才判不可用，其余一切判未知。
        /// 这条收得紧是因为误判的方向不对称：把账号问题判成不可用，一次限流就能
        /// 给用户天天在用的模型判死刑。
        /// </summary>
        internal static AvailabilityVerdict Classify(ProviderException ex, string model)
        {
            if (ex == null || string.IsNullOrWhiteSpace(model))
            {
                return AvailabilityVerdict.Unknown;
            }

            // 可重试即未知。这只是加速判据，不是唯一分流依据——IsTransientCode
            // 枚举的是六个具体 5xx，501/505/520/524 都不在表内，401 与超时也不在，
            // 那些由下面的「必须点名模型」兜住。
            if (RetryPolicy.IsTransientCode(ex.Code))
            {
                return AvailabilityVerdict.Unknown;
            }

            if (!CapabilitySignals.IsClientError(ex))
            {
                return AvailabilityVerdict.Unknown;
            }

            return BlamesModel(ex)
                ? AvailabilityVerdict.Unavailable
                : AvailabilityVerdict.Unknown;
        }

        /// <summary>
        /// 服务端原文是否在说「问题出在模型本身」。
        ///
        /// 只读 Detail，绝不读 Message。Message 尾部拼着 HintFor 的建议，
        /// 而 404 那句含「模型名」二字——读它会让每一个 404 都变成假阳性，
        /// 包括地址填错导致的裸 404，那会把名单里每个模型依次判死刑。
        ///
        /// Detail 为空（响应体解析失败）时一律判未知：没有证据就是没有证据。
        ///
        /// 刻意不校验「点的是不是当前这个模型」：本判据的两个用途都不需要它。
        /// 判可用性时模型已由调用方限定；作为能力判据的前置排除时，
        /// 「错误在说某个模型不存在」本身就足以说明它不是能力信号。
        /// </summary>
        internal static bool BlamesModel(ProviderException ex)
        {
            var detail = ex?.Detail;
            if (string.IsNullOrWhiteSpace(detail))
            {
                return false;
            }

            // 认字段名与固定措辞比认自然语言可靠：各家措辞五花八门，
            // 但 model_not_found 这类来自协议或官方文案，是固定的。
            var saysModelIsAtFault = CapabilitySignals.Mentions(
                detail,
                "model_not_found",
                "model not found",
                "does not exist",
                "doesn't exist",
                "no access to",
                "not have access to",
                "unknown model",
                "invalid model",
                "unsupported model",
                "model is not supported",
                "模型不存在",
                "无此模型",
                "未知模型",
                "无权访问该模型",
                "不支持该模型");

            // 刻意只认固定措辞，不做「原文里出现过模型名」这类兜底。
            //
            // 试过，不成立：网关报参数错误时常把整个请求体回显在 detail 里，
            // 而请求体必然含 "model":"gpt-4o" —— 模型名和 model 二字同时出现，
            // 于是每一条这样的 400 都会被读成「点名了模型」。
            // 分不清就判未知，这是规范定下的方向。
            return saysModelIsAtFault;
        }
    }
}
