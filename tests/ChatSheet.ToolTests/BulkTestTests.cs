using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;

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
    /// 打的是真实的 ModelProbe.ProbeManyAsync，不是它的复刻。曾经这里放的是一份
    /// 抄来的调度形状，那只能证明我抄的那份对——改了真实实现照样全绿。现在起一个
    /// 进程内的并发 HTTP 服务，按模型名分流应答，并记录同时在飞的峰值。
    ///
    /// 服务必须并发处理连接。RetryResetTests 里那个 ScriptedServer 是刻意串行的
    /// （它被验的客户端一次只发一个请求），直接拿来用会把并发度压成 1，
    /// 那时无论实现对不对，峰值都是 1，断言永远是绿的。
    /// </summary>
    internal static class BulkTestTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestConcurrencyIsHonoured(report);
            TestSerialWhenAskedFor(report);
            TestOneFailureDoesNotStopBatch(report);
            TestCancellationKeepsResults(report);
            TestConcurrencyClamped(report);
            TestBatchHoldsTheSingleFlightGate(report);
        }

        /// <summary>
        /// 并发度确实达到给定值。
        ///
        /// 判据是「同时在飞的峰值」，不是耗时：耗时随机器负载漂，而峰值是确定的。
        /// 每个应答停一小会儿，好让并发真的叠起来。
        /// </summary>
        private static void TestConcurrencyIsHonoured(Action<string, bool, string> report)
        {
            var models = Enumerable.Range(0, 12).Select(i => "m" + i).ToList();
            int peak;
            var result = RunReal(models, concurrency: 5, peak: out peak);

            report(
                $"并发达到 5（实测峰值 {peak}）",
                peak >= 4 && peak <= 5,
                $"峰值 {peak}，期望 4-5（12 个模型、并发 5）");

            report(
                "并发不超过给定值",
                peak <= 5,
                $"峰值 {peak} 超过 5，闸门没生效");

            report(
                "每个模型都测到了",
                result.Count == models.Count,
                $"测了 {result.Count}/{models.Count}");

            report(
                "都判成可用（服务正常应答）",
                result.Values.All(v => v == "Available"),
                string.Join(",", result.Values.Distinct()));
        }

        /// <summary>
        /// 并发给 1 时必须真的串行。
        ///
        /// 这条是上一条的反例：没有它，「峰值 ≤ 5」在实现根本没并发时也成立。
        /// </summary>
        private static void TestSerialWhenAskedFor(Action<string, bool, string> report)
        {
            var models = Enumerable.Range(0, 6).Select(i => "s" + i).ToList();
            int peak;
            var result = RunReal(models, concurrency: 1, peak: out peak);

            report(
                $"并发给 1 时真的串行（峰值 {peak}）",
                peak == 1,
                $"峰值 {peak}，期望 1——说明并发数没被当回事");

            report(
                "串行也把每个都测到",
                result.Count == models.Count,
                $"测了 {result.Count}/{models.Count}");
        }

        /// <summary>单个失败不中断整批：它本身就是一条判定。</summary>
        private static void TestOneFailureDoesNotStopBatch(Action<string, bool, string> report)
        {
            var models = new List<string> { "ok1", "mock-absent", "ok2", "ok3" };
            int peak;
            var result = RunReal(models, concurrency: 2, peak: out peak, failOn: "mock-absent");

            report(
                "一个失败不中断整批",
                result.Count == models.Count,
                $"只测了 {result.Count}/{models.Count}");

            report(
                "失败的那个落成「不可用」（服务端点名了模型）",
                result.TryGetValue("mock-absent", out var verdict) && verdict == "Unavailable",
                $"实际 {(result.ContainsKey("mock-absent") ? result["mock-absent"] : "无判定")}");

            report(
                "其余仍判可用",
                models.Where(m => m != "mock-absent").All(m =>
                    result.TryGetValue(m, out var v) && v == "Available"),
                string.Join(",", result.Select(kv => kv.Key + "=" + kv.Value)));
        }

        /// <summary>取消在半路生效，已得结果保留。</summary>
        private static void TestCancellationKeepsResults(Action<string, bool, string> report)
        {
            var models = Enumerable.Range(0, 24).Select(i => "c" + i).ToList();
            int peak;
            var result = RunReal(
                models,
                concurrency: 3,
                peak: out peak,
                cancelAfter: done => done >= 4);

            report(
                $"取消在半路生效（测了 {result.Count}/{models.Count}）",
                result.Count < models.Count,
                $"取消后仍把 {result.Count} 个跑完了");

            report(
                "取消前已得的判定保留（那些请求已经付过钱）",
                result.Count >= 4,
                $"只剩 {result.Count} 条，取消把已付过钱的结果一起丢了");
        }

        /// <summary>并发数越界时收拢到合理区间，不接受 0 或负数。</summary>
        private static void TestConcurrencyClamped(Action<string, bool, string> report)
        {
            var models = new List<string> { "a", "b", "c" };

            int zeroPeak;
            var zero = RunReal(models, concurrency: 0, peak: out zeroPeak);
            report(
                $"并发给 0 时仍能跑完（收拢为至少 1，峰值 {zeroPeak}）",
                zero.Count == models.Count && zeroPeak >= 1,
                $"测了 {zero.Count}/{models.Count}，峰值 {zeroPeak}");

            int negPeak;
            var negative = RunReal(models, concurrency: -3, peak: out negPeak);
            report(
                "并发给负数时仍能跑完",
                negative.Count == models.Count,
                $"测了 {negative.Count}/{models.Count}");
        }

        /// <summary>
        /// 整批占着单飞闸门期间，零散的「试一下」必须排队等着，不能加入并发。
        ///
        /// 这一条是分层设计的全部理由：闸门若被放宽到 N，用户随手点的确认也会并发，
        /// 那时同时有几个在飞就说不清了。判据是峰值——若单个探测挤进了批内，
        /// 峰值会变成 4。
        /// </summary>
        private static void TestBatchHoldsTheSingleFlightGate(Action<string, bool, string> report)
        {
            var models = Enumerable.Range(0, 9).Select(i => "g" + i).ToList();

            using (var server = new ConcurrentServer(null, delayMs: 45))
            {
                var connection = Connection(server.BaseUrl);
                var single = new List<AvailabilityVerdict>();

                // 批跑起来之后再插一个零散探测。
                var batch = ModelProbe.ProbeManyAsync(
                    connection,
                    models,
                    3,
                    _ => (OutputLimitField?)null,
                    (m, v, done) => Task.CompletedTask,
                    CancellationToken.None);

                Thread.Sleep(60);

                // 先确认批真的占着闸门：不占着的话下面那条「不重叠」会因为
                // 根本没有竞争而白通过。
                var permitsDuringBatch = ModelProbe.GatePermits;

                var probe = ModelProbe.ProbeAsync(
                    connection, "lonely", null, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion) { single.Add(t.Result); }
                    });

                Task.WaitAll(batch, probe);

                // 判据是「区间不重叠」，不是全局峰值。峰值会被一个竞态骗到：
                // 服务端在写完响应之后才减在飞计数，而客户端此刻已认为请求结束，
                // 于是排在后面的探测会与「正在收尾」的批内连接重叠，峰值虚高一格。
                // 区间比较不受这个影响——它问的是「这个探测有没有等批跑完」。
                report(
                    $"批跑期间闸门被占着（剩余许可 {permitsDuringBatch}）",
                    permitsDuringBatch == 0,
                    "闸门没被占着，下面那条「不重叠」会因为没有竞争而白通过");

                var spans = server.Spans;
                long[] lonely;
                var haveLonely = spans.TryGetValue("lonely", out lonely);
                var batchEnd = spans
                    .Where(kv => kv.Key != "lonely")
                    .Select(kv => kv.Value[1])
                    .DefaultIfEmpty(0)
                    .Max();
                var overlapping = spans
                    .Where(kv => kv.Key != "lonely" && haveLonely)
                    .Count(kv => kv.Value[0] < lonely[1] && lonely[0] < kv.Value[1]);

                report(
                    "批跑期间零散探测排队等着，不与批内请求重叠" +
                        (haveLonely ? $"（重叠 {overlapping} 个）" : "（没拿到它的区间）"),
                    haveLonely && overlapping == 0,
                    haveLonely
                        ? $"零散探测 {lonely[0]}-{lonely[1]}ms，批内最晚结束于 {batchEnd}ms，" +
                            $"重叠 {overlapping} 个——说明它挤进了批内"
                        : "服务端没记到 lonely 的区间");

                report(
                    "它在批跑结束之后才开始（是排队，不是被丢掉）",
                    haveLonely && lonely[0] >= batchEnd - 5,
                    haveLonely
                        ? $"它 {lonely[0]}ms 开始，而批内最晚结束于 {batchEnd}ms"
                        : "没拿到区间");

                report(
                    "零散探测最终也跑完了",
                    single.Count == 1,
                    $"拿到 {single.Count} 条判定");

                report(
                    $"批内并发仍达到上限（峰值 {server.Peak}）",
                    server.Peak >= 3,
                    $"峰值 {server.Peak}，期望至少 3——否则这条断言没测到并发");
            }
        }

        private static ResolvedConnection Connection(string baseUrl)
        {
            return new ResolvedConnection
            {
                Protocol = ProtocolKind.OpenAiChatCompletions,
                BaseUrl = baseUrl,
                Token = "t",
            };
        }

        /// <summary>起一个进程内的并发 HTTP 服务，跑真实的 ProbeManyAsync。</summary>
        private static Dictionary<string, string> RunReal(
            IReadOnlyList<string> models,
            int concurrency,
            out int peak,
            string failOn = null,
            int delayMs = 45,
            Func<int, bool> cancelAfter = null)
        {
            var results = new Dictionary<string, string>();
            var sync = new object();

            using (var server = new ConcurrentServer(failOn, delayMs))
            using (var cts = new CancellationTokenSource())
            {
                var completed = 0;

                try
                {
                    ModelProbe.ProbeManyAsync(
                        Connection(server.BaseUrl),
                        models,
                        concurrency,
                        _ => (OutputLimitField?)null,
                        (model, verdict, done) =>
                        {
                            lock (sync) { results[model] = verdict.ToString(); }
                            var n = Interlocked.Increment(ref completed);
                            if (cancelAfter != null && cancelAfter(n)) { cts.Cancel(); }
                            return Task.CompletedTask;
                        },
                        cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // 取消是预期路径之一。
                }
                catch (AggregateException ex)
                    when (ex.InnerExceptions.Any(e => e is OperationCanceledException))
                {
                    // 同上。
                }

                peak = server.Peak;
            }

            return results;
        }

        /// <summary>
        /// 并发应答的最小 HTTP 服务，记录同时在飞的峰值。
        ///
        /// 与 RetryResetTests 里那个刻意串行的服务不同：这里必须并发，
        /// 否则并发度断言测的是服务而不是被测代码。
        /// </summary>
        private sealed class ConcurrentServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
            private readonly string _failOn;
            private readonly int _delayMs;
            private readonly object _gate = new object();
            private readonly Dictionary<string, long[]> _spans =
                new Dictionary<string, long[]>();
            private readonly System.Diagnostics.Stopwatch _clock =
                System.Diagnostics.Stopwatch.StartNew();
            private int _inFlight;
            private int _peak;

            internal ConcurrentServer(string failOn, int delayMs)
            {
                _failOn = failOn;
                _delayMs = delayMs;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Task.Run(AcceptLoop);
            }

            internal int Port { get; }

            internal string BaseUrl { get { return "http://127.0.0.1:" + Port + "/v1"; } }

            internal int Peak { get { lock (_gate) { return _peak; } } }

            /// <summary>每个模型被服务的起止时刻（毫秒）。用来判断有没有真的排队。</summary>
            internal Dictionary<string, long[]> Spans
            {
                get { lock (_gate) { return new Dictionary<string, long[]>(_spans); } }
            }

            private async Task AcceptLoop()
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient connection;
                    try
                    {
                        connection = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    // 每个连接一个任务：并发是这组断言的前提。
                    var captured = connection;
                    var ignored = Task.Run(() =>
                    {
                        try { Serve(captured); }
                        catch { }
                        finally { try { captured.Close(); } catch { } }
                    });
                }
            }

            private void Serve(TcpClient connection)
            {
                lock (_gate)
                {
                    _inFlight++;
                    if (_inFlight > _peak) { _peak = _inFlight; }
                }

                try
                {
                    using (var stream = connection.GetStream())
                    {
                        var model = ExtractModel(ReadRequest(stream));
                        var began = _clock.ElapsedMilliseconds;

                        // 停一会儿，好让并发叠起来。不停的话每个请求瞬间结束，
                        // 峰值永远是 1，并发度断言就测不到东西。
                        Thread.Sleep(_delayMs);

                        lock (_gate)
                        {
                            _spans[model] = new[] { began, _clock.ElapsedMilliseconds };
                        }

                        if (_failOn != null && model == _failOn)
                        {
                            // 点名模型的 404：这是唯一该判「不可用」的形状。
                            Write(
                                stream,
                                404,
                                "{\"error\":{\"message\":\"The model `" + model +
                                    "` does not exist\",\"code\":\"model_not_found\"}}",
                                false);
                            return;
                        }

                        var sse = new StringBuilder();
                        sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"index\":0}]}\n\n");
                        sse.Append("data: {\"choices\":[{\"delta\":{},\"index\":0,\"finish_reason\":\"stop\"}]}\n\n");
                        sse.Append("data: [DONE]\n\n");
                        Write(stream, 200, sse.ToString(), true);
                    }
                }
                finally
                {
                    lock (_gate) { _inFlight--; }
                }
            }

            /// <summary>读完请求头与请求体。不读完会让客户端那侧先看到连接重置。</summary>
            private static string ReadRequest(NetworkStream stream)
            {
                var head = new StringBuilder();
                var one = new byte[1];
                var length = 0;

                while (true)
                {
                    if (stream.Read(one, 0, 1) <= 0) { break; }
                    head.Append((char)one[0]);
                    if (head.Length >= 4 &&
                        head[head.Length - 1] == '\n' && head[head.Length - 2] == '\r' &&
                        head[head.Length - 3] == '\n' && head[head.Length - 4] == '\r')
                    {
                        break;
                    }
                }

                foreach (var line in head.ToString().Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(trimmed.Substring("Content-Length:".Length).Trim(), out length);
                    }
                }

                if (length <= 0) { return string.Empty; }

                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var got = stream.Read(buffer, read, length - read);
                    if (got <= 0) { break; }
                    read += got;
                }

                return Encoding.UTF8.GetString(buffer, 0, read);
            }

            private static string ExtractModel(string body)
            {
                var match = Regex.Match(body ?? string.Empty, "\"model\"\\s*:\\s*\"([^\"]*)\"");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            private static void Write(NetworkStream stream, int status, string body, bool sse)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = new StringBuilder();
                header.Append("HTTP/1.1 ").Append(status).Append(" X\r\n");
                header.Append("Content-Type: ")
                    .Append(sse ? "text/event-stream" : "application/json")
                    .Append("\r\n");
                header.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
                header.Append("Connection: close\r\n\r\n");

                var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            public void Dispose()
            {
                _stopping.Cancel();
                try { _listener.Stop(); } catch { }
                _stopping.Dispose();
            }
        }
    }
}
