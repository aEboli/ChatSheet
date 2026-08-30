using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 批量测试整份目录的并发行为。
    ///
    /// 这里盯的不是请求形态（那在 ProbeTests），而是「几十个模型一起跑」这件事本身
    /// 会出的错：
    ///
    ///   · 并发数发成 1 不会报错，只是慢十几倍——必须证明真的有多个在飞。
    ///   · 一个模型失败不该中断整批：它本身就是一条判定。
    ///   · 取消要能在半路生效，且已得结果保留——那些请求已经付过钱了。
    ///   · 整批只占一次单飞闸门：闸门若被放宽到 N，用户零散点的「试一下」也会并发，
    ///     那时同时有几个在飞谁也说不清。
    ///
    /// **这组断言打的是复刻的调度形状，不是真实的 ModelProbe.ProbeManyAsync。**
    /// 真实实现的发送走 HTTP，要打它得起一个并发的进程内服务；这里没有，
    /// 因此它只能证明「整批一次闸门 + 批内 N 路」这个形状本身对，
    /// 改了真实实现的调度方式，这组照样全绿。
    ///
    /// 覆盖这个缺口的是面板侧的 model-test-all.test.mjs（并发数与范围确实发了出去）
    /// 与真实宿主里的 verify-picker.ps1。真要在这里打真实实现，需要补一个并发应答的
    /// 进程内 HTTP 服务——RetryResetTests 里那个 ScriptedServer 是刻意串行的，
    /// 直接拿来用会把并发度压成 1，那时无论实现对不对峰值都是 1。
    /// </summary>
    internal static class BulkTestTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestConcurrencyIsHonoured(report);
            TestOneFailureDoesNotStopBatch(report);
            TestCancellationKeepsResults(report);
            TestConcurrencyClamped(report);
        }

        /// <summary>
        /// 并发度确实达到给定值。
        ///
        /// 判据是「同时在飞的峰值」，不是耗时：耗时会随机器负载漂，
        /// 而峰值是确定的。每个假探测停一小会儿，好让并发真的叠起来。
        /// </summary>
        private static void TestConcurrencyIsHonoured(Action<string, bool, string> report)
        {
            var inFlight = 0;
            var peak = 0;
            var gate = new object();

            var models = Enumerable.Range(0, 12).Select(i => "m" + i).ToList();

            var result = RunFake(models, concurrency: 5, onEach: () =>
            {
                lock (gate)
                {
                    inFlight++;
                    if (inFlight > peak) { peak = inFlight; }
                }

                Thread.Sleep(40);

                lock (gate)
                {
                    inFlight--;
                }
            });

            report(
                $"并发达到 5（峰值 {peak}）",
                peak >= 4 && peak <= 5,
                $"峰值 {peak}，期望 4-5（12 个模型、并发 5）");

            report(
                "并发不超过给定值",
                peak <= 5,
                $"峰值 {peak} 超过 5，说明闸门没生效");

            report(
                "每个模型都测到了",
                result.Count == models.Count,
                $"测了 {result.Count}/{models.Count}");
        }

        /// <summary>单个失败不中断整批：它本身就是一条判定。</summary>
        private static void TestOneFailureDoesNotStopBatch(Action<string, bool, string> report)
        {
            var models = new List<string> { "ok1", "boom", "ok2", "ok3" };

            var result = RunFake(models, concurrency: 2, onEach: null, failOn: "boom");

            report(
                "一个失败不中断整批",
                result.Count == models.Count,
                $"只测了 {result.Count}/{models.Count}");

            report(
                "失败的那个也得到一条判定",
                result.ContainsKey("boom"),
                "失败没有落成判定，那一行会永远停在未确认");
        }

        /// <summary>取消在半路生效，已得结果保留。</summary>
        private static void TestCancellationKeepsResults(Action<string, bool, string> report)
        {
            var models = Enumerable.Range(0, 20).Select(i => "c" + i).ToList();
            var done = 0;

            using (var cts = new CancellationTokenSource())
            {
                var result = RunFake(
                    models,
                    concurrency: 3,
                    onEach: () =>
                    {
                        Thread.Sleep(20);
                        if (Interlocked.Increment(ref done) == 5)
                        {
                            cts.Cancel();
                        }
                    },
                    token: cts.Token,
                    expectCancel: true);

                report(
                    "取消在半路生效（没有把 20 个跑完）",
                    result.Count < models.Count,
                    $"取消后仍测了 {result.Count}/{models.Count}");

                report(
                    "取消前已得的判定保留",
                    result.Count > 0,
                    "取消把已付过钱的结果一起丢了");
            }
        }

        /// <summary>并发数越界时收拢到合理区间，不接受 0 或负数。</summary>
        private static void TestConcurrencyClamped(Action<string, bool, string> report)
        {
            var models = new List<string> { "a", "b", "c" };

            var zero = RunFake(models, concurrency: 0, onEach: null);
            report(
                "并发给 0 时仍能跑完（收拢为至少 1）",
                zero.Count == models.Count,
                $"测了 {zero.Count}/{models.Count}");

            var negative = RunFake(models, concurrency: -3, onEach: null);
            report(
                "并发给负数时仍能跑完",
                negative.Count == models.Count,
                $"测了 {negative.Count}/{models.Count}");
        }

        /// <summary>
        /// 用假发送跑一遍 ProbeManyAsync 的并发骨架。
        ///
        /// 不连网：这里要验的是并发与失败传播，连网只会把断言变成对网络的断言。
        /// 因此复刻 ProbeManyAsync 的调度形状（整批一次闸门 + 批内 N 路），
        /// 并让它调用注入的假发送。真实实现与这里的形状必须一致——
        /// 若哪天改了调度方式，这组断言就不再代表它，那时应当同步改。
        /// </summary>
        private static Dictionary<string, string> RunFake(
            IReadOnlyList<string> models,
            int concurrency,
            Action onEach,
            string failOn = null,
            CancellationToken token = default(CancellationToken),
            bool expectCancel = false)
        {
            var results = new Dictionary<string, string>();
            var sync = new object();

            Func<string, Task<string>> send = model => Task.Run(() =>
            {
                onEach?.Invoke();
                if (failOn != null && model == failOn)
                {
                    throw new ProviderException("HTTP_404", "no such model: " + model);
                }
                return "Available";
            });

            try
            {
                FakeProbeMany(
                    models,
                    concurrency,
                    send,
                    (model, verdict) =>
                    {
                        lock (sync) { results[model] = verdict; }
                    },
                    token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (expectCancel)
            {
                // 预期内。
            }
            catch (AggregateException ex)
                when (expectCancel && ex.InnerExceptions.Any(e => e is OperationCanceledException))
            {
                // 预期内。
            }

            return results;
        }

        /// <summary>ProbeManyAsync 的调度形状：整批一次闸门，批内按 width 并发。</summary>
        private static async Task FakeProbeMany(
            IReadOnlyList<string> models,
            int concurrency,
            Func<string, Task<string>> send,
            Action<string, string> onResult,
            CancellationToken token)
        {
            var width = Math.Max(1, Math.Min(concurrency, 16));

            using (var slots = new SemaphoreSlim(width, width))
            {
                var tasks = new List<Task>(models.Count);

                foreach (var model in models)
                {
                    token.ThrowIfCancellationRequested();
                    await slots.WaitAsync(token).ConfigureAwait(false);

                    var captured = model;
                    tasks.Add(Task.Run(
                        async () =>
                        {
                            try
                            {
                                string verdict;
                                try
                                {
                                    verdict = await send(captured).ConfigureAwait(false);
                                }
                                catch (ProviderException)
                                {
                                    verdict = "Unavailable";
                                }

                                onResult(captured, verdict);
                            }
                            finally
                            {
                                slots.Release();
                            }
                        },
                        token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
    }
}
