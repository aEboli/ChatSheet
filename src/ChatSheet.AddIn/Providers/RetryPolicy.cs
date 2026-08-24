using System;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 接口调用的重试策略。
    ///
    /// 只重试「再试一次可能就好」的故障：网络中断、网关抖动、限流。
    /// 密钥错误、地址写错、模型不存在这类配置问题重试多少次都一样，
    /// 反复重试只会把明确的错误推迟五次才显示给用户，因此一律直接失败。
    /// </summary>
    internal static class RetryPolicy
    {
        /// <summary>失败后最多重试的次数（不含首次尝试）。</summary>
        internal const int MaxRetries = 5;

        /// <summary>首次重试前的等待时长，之后逐次加倍。</summary>
        private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 等待上限。退避是为了给对端恢复的时间，不是让用户干等：
        /// 无上限地加倍会让最后一次重试前空等半分钟以上。
        /// </summary>
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

        /// <summary>判断这个错误是否值得重试。</summary>
        internal static bool IsTransient(Exception exception)
        {
            var provider = exception as ProviderException;
            if (provider == null)
            {
                // 非接口异常多为本地缺陷（参数、序列化），重试无意义。
                return false;
            }

            return IsTransientCode(provider.Code);
        }

        /// <summary>按错误码判断可重试性。错误码是唯一稳定的判据，消息文本会随服务端变化。</summary>
        internal static bool IsTransientCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            switch (code)
            {
                // 连不上、连接被重置、DNS 失败等，换一次连接常能成功。
                case "NETWORK_ERROR":

                // 408 请求超时、429 限流：对端明确表示「稍后再来」。
                case "HTTP_408":
                case "HTTP_429":

                // 5xx 全是服务端侧故障，与本地配置无关。
                case "HTTP_500":
                case "HTTP_502":
                case "HTTP_503":
                case "HTTP_504":
                case "HTTP_507":
                case "HTTP_509":
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 计算第 attempt 次重试前的等待时长（attempt 从 1 起）。
        /// 服务端给了 Retry-After 就听它的：它比本地的猜测准确。
        /// </summary>
        internal static TimeSpan DelayFor(int attempt, TimeSpan? serverHint = null)
        {
            if (serverHint.HasValue && serverHint.Value > TimeSpan.Zero)
            {
                // 仍要设上限，避免对端给出一个离谱的值把界面挂住。
                return serverHint.Value > MaxDelay ? MaxDelay : serverHint.Value;
            }

            if (attempt < 1)
            {
                attempt = 1;
            }

            // 指数退避：1s、2s、4s、8s，再往后维持上限。
            // 指数在第 5 次会到 16s，用 Min 压到上限。
            var scaled = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            var capped = Math.Min(scaled, MaxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(capped);
        }

        /// <summary>
        /// 最坏情况下全部重试的等待总时长。
        ///
        /// 给整体超时留预算时必须按它算：超时若只按单次请求设定，
        /// 重试还没走完就会被自己的超时掐断，重试机制形同不存在。
        /// </summary>
        internal static TimeSpan TotalBackoff
        {
            get
            {
                var total = TimeSpan.Zero;
                for (var attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    total += DelayFor(attempt);
                }

                return total;
            }
        }

        /// <summary>
        /// 供界面显示的重试说明。
        /// 写明第几次、共几次与等待多久，用户才能判断是该等还是该去改配置。
        /// </summary>
        internal static string Describe(int attempt, TimeSpan delay, string reason)
        {
            var seconds = Math.Max(1, (int)Math.Round(delay.TotalSeconds));
            var text = $"接口调用失败，{seconds} 秒后重试（第 {attempt}/{MaxRetries} 次）";
            return string.IsNullOrWhiteSpace(reason) ? text + "。" : text + "：" + reason;
        }
    }
}
