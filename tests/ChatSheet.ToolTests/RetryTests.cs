using System;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 重试策略验证。
    ///
    /// 重点不是「会不会重试」，而是「该不该重试」：把配置错误当成暂时故障
    /// 反复重试，只会把一条本该立刻显示的错误推迟几十秒才告诉用户。
    /// </summary>
    internal static class RetryTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestClassification(report);
            TestBackoff(report);
            TestServerHint(report);
            TestDescription(report);
        }

        private static void TestClassification(Action<string, bool, string> report)
        {
            // 值得重试：换一次连接或等一会儿可能就成功。
            var transient = new[]
            {
                "NETWORK_ERROR",
                "HTTP_408",
                "HTTP_429",
                "HTTP_500",
                "HTTP_502",
                "HTTP_503",
                "HTTP_504",
            };

            foreach (var code in transient)
            {
                report($"{code} 应重试", RetryPolicy.IsTransientCode(code), "判定为不可重试");
            }

            // 不该重试：重试多少次结果都一样，只是延迟报错。
            var permanent = new[]
            {
                "HTTP_400",
                "HTTP_401",
                "HTTP_403",
                "HTTP_404",
                "HTTP_422",
                "MODEL_REQUIRED",
                "BASE_URL_REQUIRED",
                "TOKEN_REQUIRED",
                "SETTINGS_SAVE_FAILED",
            };

            foreach (var code in permanent)
            {
                report($"{code} 不应重试", !RetryPolicy.IsTransientCode(code), "判定为可重试");
            }

            report("空错误码不应重试", !RetryPolicy.IsTransientCode(null) && !RetryPolicy.IsTransientCode(string.Empty), "");

            // 异常入口：只认 ProviderException，其余属本地缺陷。
            report(
                "ProviderException 按错误码判定",
                RetryPolicy.IsTransient(new ProviderException("HTTP_503", "服务不可用")) &&
                    !RetryPolicy.IsTransient(new ProviderException("HTTP_401", "密钥无效")),
                "");

            report(
                "非接口异常不重试",
                !RetryPolicy.IsTransient(new InvalidOperationException("本地缺陷")),
                "本地异常被判定为可重试");
        }

        private static void TestBackoff(Action<string, bool, string> report)
        {
            var first = RetryPolicy.DelayFor(1);
            var second = RetryPolicy.DelayFor(2);
            var third = RetryPolicy.DelayFor(3);

            report("首次退避 1 秒", first == TimeSpan.FromSeconds(1), first.ToString());
            report("退避逐次加倍", second == TimeSpan.FromSeconds(2) && third == TimeSpan.FromSeconds(4),
                $"第二次 {second}，第三次 {third}");

            // 无上限地加倍会让最后一次重试前空等很久。
            var last = RetryPolicy.DelayFor(RetryPolicy.MaxRetries);
            report("退避有上限", last <= TimeSpan.FromSeconds(8), last.ToString());

            // attempt 传 0 或负数不应算出零等待或负等待。
            report("非法次数按首次处理", RetryPolicy.DelayFor(0) == first && RetryPolicy.DelayFor(-3) == first, "");

            // 超时预算按它来设，必须与逐次退避之和一致。
            var expected = TimeSpan.Zero;
            for (var attempt = 1; attempt <= RetryPolicy.MaxRetries; attempt++)
            {
                expected += RetryPolicy.DelayFor(attempt);
            }

            report(
                "退避总时长等于逐次之和",
                RetryPolicy.TotalBackoff == expected,
                $"总计 {RetryPolicy.TotalBackoff}，预期 {expected}");

            report("重试次数为 5", RetryPolicy.MaxRetries == 5, RetryPolicy.MaxRetries.ToString());
        }

        private static void TestServerHint(Action<string, bool, string> report)
        {
            // 服务端说了等多久就等多久，比本地猜测准。
            var hint = TimeSpan.FromSeconds(3);
            report("采用服务端建议", RetryPolicy.DelayFor(1, hint) == hint, RetryPolicy.DelayFor(1, hint).ToString());

            // 但离谱的值不能把界面挂住。
            var absurd = RetryPolicy.DelayFor(1, TimeSpan.FromMinutes(10));
            report("服务端建议仍受上限约束", absurd <= TimeSpan.FromSeconds(8), absurd.ToString());

            // 零或负值视为无建议，回退本地退避。
            report(
                "无效建议回退本地退避",
                RetryPolicy.DelayFor(2, TimeSpan.Zero) == RetryPolicy.DelayFor(2) &&
                    RetryPolicy.DelayFor(2, TimeSpan.FromSeconds(-5)) == RetryPolicy.DelayFor(2),
                "");
        }

        private static void TestDescription(Action<string, bool, string> report)
        {
            var text = RetryPolicy.Describe(2, TimeSpan.FromSeconds(2), "连接被重置");

            // 用户要能判断「该等还是该去改配置」，因此三项信息都得有。
            report(
                "重试说明含次数、总数与原因",
                text.Contains("第 2/5 次") && text.Contains("2 秒") && text.Contains("连接被重置"),
                text);

            // 不足一秒的等待不应显示成 0 秒。
            var quick = RetryPolicy.Describe(1, TimeSpan.FromMilliseconds(200), null);
            report("不足一秒显示为 1 秒", quick.Contains("1 秒"), quick);
        }
    }
}
