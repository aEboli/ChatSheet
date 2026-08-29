using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 可用性判定验证。
    ///
    /// 这块判据的误判方向不对称：把账号问题读成「模型不可用」，一次限流就能给用户
    /// 天天在用的模型判死刑；反过来漏判只是少一个标注。所以反例比正例重要。
    ///
    /// 最要紧的一条是「判据不许读 Message」：BuildHttpErrorAsync 会把 HintFor 的建议
    /// 拼进 Message，而 404 那句含「模型名」二字。读 Message 会让地址填错导致的裸 404
    /// 给名单里每个模型依次判死刑。本文件用一条专门的反例锁住它。
    /// </summary>
    internal static class AvailabilityTests
    {
        private const string Model = "gpt-4o";
        private const string Connection = "CustomApi|openai|https://api.example.com/v1";

        internal static void Run(Action<string, bool, string> report)
        {
            TestClassification(report);
            TestMessageIsNotEvidence(report);
            TestCrossDimension(report);
            TestCaseFolding(report);
            TestStore(report);
            TestStructuralInvariant(report);
            TestFavorites(report);
        }

        /// <summary>只带 Message、不带 Detail：模拟「响应体解析不出原文」。</summary>
        private static ProviderException Http(int status, string message)
        {
            return new ProviderException("HTTP_" + status, message);
        }

        /// <summary>带服务端原文的错误。第二个参数是 Detail，即判据唯一该读的东西。</summary>
        private static ProviderException Http(int status, string detail, string composedMessage)
        {
            return new ProviderException("HTTP_" + status, composedMessage) { Detail = detail };
        }

        private static void TestClassification(Action<string, bool, string> report)
        {
            // 正例：原文点名了模型。
            report(
                "404 且原文说 model_not_found 判不可用",
                ModelAvailability.Classify(
                    Http(404, "model_not_found", "接口返回 404：model_not_found。请检查接口地址与模型名是否正确"),
                    Model) == AvailabilityVerdict.Unavailable,
                "");

            report(
                "404 且原文说 does not exist 判不可用",
                ModelAvailability.Classify(
                    Http(404, "The model 'gpt-4o' does not exist or you do not have access to it", "x"),
                    Model) == AvailabilityVerdict.Unavailable,
                "");

            report(
                "403 点名模型判不可用",
                ModelAvailability.Classify(
                    Http(403, "your key has no access to model gpt-4o", "x"),
                    Model) == AvailabilityVerdict.Unavailable,
                "");

            // 反例：裸 404。地址填错、网关路由缺失都长这样，与模型无关。
            report(
                "裸 404 判未知（原文没点名模型）",
                ModelAvailability.Classify(
                    Http(404, "Not Found", "接口返回 404 Not Found。请检查接口地址与模型名是否正确"),
                    Model) == AvailabilityVerdict.Unknown,
                "");

            // 反例：403 只说密钥。同一个状态码，说的是账号不是模型。
            report(
                "403 只说密钥判未知",
                ModelAvailability.Classify(
                    Http(403, "invalid api key", "x"),
                    Model) == AvailabilityVerdict.Unknown,
                "");

            report(
                "401 判未知",
                ModelAvailability.Classify(Http(401, "invalid api key", "x"), Model)
                    == AvailabilityVerdict.Unknown,
                "");

            // 429 在既有 CapabilityTests 里没有先例，本次新增。
            // 提案专门点出它：限流误判会给模型判死刑。
            report(
                "429 限流判未知",
                ModelAvailability.Classify(Http(429, "rate limit exceeded", "x"), Model)
                    == AvailabilityVerdict.Unknown,
                "");

            report(
                "503 判未知",
                ModelAvailability.Classify(Http(503, "service unavailable", "x"), Model)
                    == AvailabilityVerdict.Unknown,
                "");

            // IsTransientCode 的表里没有 501/520，这两条要靠「必须点名模型」兜住。
            report(
                "501 判未知（不在 IsTransientCode 表内）",
                ModelAvailability.Classify(Http(501, "not implemented", "x"), Model)
                    == AvailabilityVerdict.Unknown,
                "");

            report(
                "520 判未知（不在 IsTransientCode 表内）",
                ModelAvailability.Classify(Http(520, "web server returned an unknown error", "x"), Model)
                    == AvailabilityVerdict.Unknown,
                "");

            report(
                "网络故障判未知",
                ModelAvailability.Classify(
                    new ProviderException("NETWORK_ERROR", "无法连接") { Detail = null },
                    Model) == AvailabilityVerdict.Unknown,
                "");

            // 我方请求有问题：说的是我们自己，不是模型。
            report(
                "400 说参数不对判未知",
                ModelAvailability.Classify(
                    Http(400, "unsupported parameter: max_tokens", "x"),
                    Model) == AvailabilityVerdict.Unknown,
                "");

            report(
                "限流即使原文点名了模型也判未知（可重试优先）",
                ModelAvailability.Classify(
                    Http(429, "rate limit for model gpt-4o exceeded", "x"),
                    Model) == AvailabilityVerdict.Unknown,
                "");

            // 体内错误：网关以 200 开流再把错误放进帧里，异常码是 STREAM_ERROR
            // 而不是 HTTP_4xx。只认 4xx 会让「别名模型」恒判未知——那正是本能力
            // 要回答的最典型情形。这条是 verify-picker 的 mock-aliasbroken 抓到的，
            // 单测当时全绿。
            report(
                "体内错误点名模型时判不可用（STREAM_ERROR 不是 HTTP_4xx）",
                ModelAvailability.Classify(
                    new ProviderException("STREAM_ERROR", "服务端返回错误")
                    {
                        Detail = "model_not_found：The model 'gpt-4o' does not exist",
                    },
                    Model) == AvailabilityVerdict.Unavailable,
                "");

            report(
                "体内错误没点名模型时仍判未知",
                ModelAvailability.Classify(
                    new ProviderException("STREAM_ERROR", "服务端返回错误")
                    {
                        Detail = "upstream connection reset",
                    },
                    Model) == AvailabilityVerdict.Unknown,
                "");
        }

        /// <summary>
        /// 判据不许读 Message。
        ///
        /// 这几条是整套判定的地基：HintFor(404) 返回「请检查接口地址与模型名是否正确」，
        /// 含「模型名」二字，还会被 BuildHttpErrorAsync 拼进 Message。判据一旦读 Message，
        /// 每个 404 都成假阳性。
        /// </summary>
        private static void TestMessageIsNotEvidence(Action<string, bool, string> report)
        {
            // 与真实拼装逐字一致的 Message，Detail 为空。
            var composed = new ProviderException(
                "HTTP_404",
                "接口返回 404 Not Found。请检查接口地址与模型名是否正确");

            report(
                "Detail 为空而 Message 含「模型名」时判未知",
                ModelAvailability.Classify(composed, Model) == AvailabilityVerdict.Unknown,
                "Message 里的「模型名」来自我们自己的 HintFor，不是服务端说的");

            report(
                "Detail 为空时 BlamesModel 恒为假",
                !ModelAvailability.BlamesModel(composed),
                "");

            // Message 里连模型名本身都出现了，仍不算证据。
            var echoed = new ProviderException(
                "HTTP_404",
                $"接口返回 404 Not Found：model {Model} 未找到。请检查接口地址与模型名是否正确");

            report(
                "Message 提到模型名但 Detail 为空时仍判未知",
                ModelAvailability.Classify(echoed, Model) == AvailabilityVerdict.Unknown,
                "");

            // 原文只是回显了请求体（含模型名与 model 二字），并没有说模型有问题。
            // 这条曾经打红：早先的实现有一条「原文出现过模型名 + 提到 model」的兜底，
            // 而请求体回显必然同时满足两者，于是每条参数错误都被读成点名了模型。
            report(
                "原文只回显模型名、未谈模型问题时判未知",
                ModelAvailability.Classify(
                    Http(400, $"invalid request body: {{\"model\":\"{Model}\",\"stream\":true}}", "x"),
                    Model) == AvailabilityVerdict.Unknown,
                "");
        }

        /// <summary>
        /// 可用性与工具/视觉是三个互不改写的维度。
        ///
        /// 其中 gpt-image-1 那条是提案顺路修的既有缺陷：LooksLikeVisionUnsupported
        /// 认裸子串 "image"，于是一条「模型不存在」会被记成「不支持图片输入」，
        /// 白花一次视觉中转请求。
        /// </summary>
        private static void TestCrossDimension(Action<string, bool, string> report)
        {
            var absentImageModel = Http(
                404,
                "The model 'gpt-image-1' does not exist",
                "接口返回 404：The model 'gpt-image-1' does not exist。请检查接口地址与模型名是否正确");

            report(
                "名字含 image 的模型不存在时判不可用",
                ModelAvailability.Classify(absentImageModel, "gpt-image-1")
                    == AvailabilityVerdict.Unavailable,
                "");

            report(
                "名字含 image 的模型不存在时不判为缺视觉",
                !CapabilitySignals.LooksLikeVisionUnsupported(absentImageModel),
                "判成缺视觉会白花一次中转请求，并告诉用户「当前模型没有视觉能力」");

            report(
                "名字含 image 的模型不存在时不判为缺工具",
                !CapabilitySignals.LooksLikeToolUnsupported(absentImageModel),
                "");

            // 真的缺视觉时判据仍要成立：排除的只是「错误在说模型本身」。
            // 注意能力判据读的是 Message（面向用户那条），不是 Detail——
            // 这两条一旦传错位置就会假绿，本文件早先的版本正是这么打红的。
            report(
                "真·缺视觉仍判为缺视觉",
                CapabilitySignals.LooksLikeVisionUnsupported(
                    Http(400, "this model does not support image_url input")),
                "");

            report(
                "真·缺工具仍判为缺工具",
                CapabilitySignals.LooksLikeToolUnsupported(
                    Http(400, "unknown parameter: tool_choice")),
                "");

            // 缺能力的错误里若同时带着「模型不存在」的原文，能力判据要让位——
            // 那一条说的是模型本身，不是能力。
            report(
                "原文说模型不存在时能力判据让位",
                !CapabilitySignals.LooksLikeVisionUnsupported(
                    Http(400, "model_not_found", "this model does not support image_url input")),
                "");

            // 判定不写入能力档案。
            ModelAvailability.Reset();
            ModelCapabilities.Reset();
            var capability = ModelCapabilities.For(Connection, Model);
            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Unavailable);

            report(
                "判为不可用不改写工具形态",
                capability.ToolMode == ToolProtocolMode.Native,
                "");

            report(
                "判为不可用不改写视觉档案",
                !capability.VisionUnsupported,
                "");
        }

        private static void TestCaseFolding(Action<string, bool, string> report)
        {
            ModelAvailability.Reset();
            ModelAvailability.Record(Connection, "GPT-4O", AvailabilityVerdict.Available);

            report(
                "大小写不同的模型名命中同一条判定",
                ModelAvailability.For(Connection, "gpt-4o") == AvailabilityVerdict.Available,
                "目录用 OrdinalIgnoreCase 去重，判定的键必须同口径");

            report(
                "原文措辞的大小写不影响判定",
                ModelAvailability.BlamesModel(
                    Http(404, "The Model 'GPT-4O' Does Not Exist", "x")),
                "");
        }

        private static void TestStore(Action<string, bool, string> report)
        {
            ModelAvailability.Reset();

            report(
                "没有证据时判定为未知",
                ModelAvailability.For(Connection, Model) == AvailabilityVerdict.Unknown,
                "");

            // 判定不得比证据活得久，两个方向都要能翻案。
            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Unavailable);
            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Available);

            report(
                "判过不可用的模型答了话就改回可用",
                ModelAvailability.For(Connection, Model) == AvailabilityVerdict.Available,
                "");

            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Unavailable);
            report(
                "判过可用的模型点名失败就改回不可用",
                ModelAvailability.For(Connection, Model) == AvailabilityVerdict.Unavailable,
                "");

            // 写入未知不该擦掉已有结论：限流那一轮不能把上一轮真实的「可用」抹掉。
            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Available);
            ModelAvailability.Record(Connection, Model, AvailabilityVerdict.Unknown);
            report(
                "记入未知不覆盖已有结论",
                ModelAvailability.For(Connection, Model) == AvailabilityVerdict.Available,
                "");

            // 按连接隔离，且作废只清本连接。
            const string other = "LocalCli|Claude";
            ModelAvailability.Record(other, Model, AvailabilityVerdict.Available);
            ModelAvailability.ResetConnection(Connection);

            report(
                "作废本连接后该连接判定为未知",
                ModelAvailability.For(Connection, Model) == AvailabilityVerdict.Unknown,
                "");

            report(
                "作废本连接不牵连别的连接",
                ModelAvailability.For(other, Model) == AvailabilityVerdict.Available,
                "");

            ModelAvailability.Reset();
        }

        /// <summary>
        /// 结构不变量：可用性绝不能长到 ModelCapability 上。
        /// 一旦合流，「限流」就会通过能力档案改变工具形态。
        /// </summary>
        private static void TestStructuralInvariant(Action<string, bool, string> report)
        {
            var names = typeof(ModelCapability)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(p => p.Name)
                .ToList();

            var leaked = names
                .Where(n => n.IndexOf("Availab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Unavailab", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            report(
                "ModelCapability 上不出现可用性字段",
                leaked.Count == 0,
                leaked.Count == 0 ? "" : "泄漏字段：" + string.Join("、", leaked));
        }

        /// <summary>
        /// 名单落盘。用临时目录，绝不碰用户真实的 %LOCALAPPDATA%。
        ///
        /// 这一块必须测：名单丢了是用户手工标注的成果没了，而设置丢了只是回默认值。
        /// 既有 ProviderTests 对设置的读写零覆盖（FilePath 是 private static），
        /// 名单不能也这样，所以 FavoriteModels 的路径是可注入的。
        /// </summary>
        private static void TestFavorites(Action<string, bool, string> report)
        {
            var root = Path.Combine(
                Path.GetTempPath(), "ChatSheetFavTest-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                const string connA = "CustomApi|openai|https://a.example.com/v1";
                const string connB = "LocalCli|Claude";

                report(
                    "没有文件时读出空名单",
                    FavoriteModels.Load(connA, root).Count == 0,
                    "");

                FavoriteModels.Save(connA, new[] { "gpt-4o", "o3-mini" }, root);
                var loaded = FavoriteModels.Load(connA, root);

                report(
                    "写入后读回同一份名单",
                    loaded.Count == 2 && loaded[0] == "gpt-4o" && loaded[1] == "o3-mini",
                    "实际：" + string.Join("、", loaded));

                // 按连接隔离：另一个连接不该看到这份名单。
                report(
                    "名单按连接隔离",
                    FavoriteModels.Load(connB, root).Count == 0,
                    "");

                // 写另一个连接不能动到第一个连接的分组——这是「只校验当前连接那一组」
                // 的核心：照搬 DropModelFromOtherConnection 会在这里把 connA 删掉。
                FavoriteModels.Save(connB, new[] { "claude-sonnet-4" }, root);

                report(
                    "写入别的连接不影响本连接的分组",
                    FavoriteModels.Load(connA, root).Count == 2,
                    "");

                report(
                    "两个连接各自读到自己那份",
                    FavoriteModels.Load(connB, root).SequenceEqual(new[] { "claude-sonnet-4" }),
                    "");

                // 去重忽略大小写，与目录去重同口径。
                FavoriteModels.Save(connA, new[] { "gpt-4o", "GPT-4O", " gpt-4o " }, root);
                report(
                    "名单去重忽略大小写",
                    FavoriteModels.Load(connA, root).Count == 1,
                    "");

                // Toggle 双向。
                var added = FavoriteModels.Toggle(connA, "o3-mini", root);
                report("Toggle 加入返回真", added, "");
                report(
                    "Toggle 加入后在名单里",
                    FavoriteModels.Load(connA, root).Any(m => m == "o3-mini"),
                    "");

                var removed = FavoriteModels.Toggle(connA, "O3-MINI", root);
                report("Toggle 移出返回假（大小写不同也能移出）", !removed, "");
                report(
                    "Toggle 移出后不在名单里",
                    !FavoriteModels.Load(connA, root).Any(m => m.Equals("o3-mini", StringComparison.OrdinalIgnoreCase)),
                    "");

                // Add 幂等。
                FavoriteModels.Add(connA, "gpt-4o", root);
                report(
                    "Add 已在名单里的模型不产生重复",
                    FavoriteModels.Load(connA, root).Count == 1,
                    "");

                // File.Replace 必须留下备份：这是「保留原文件」在写入路径上唯一可兑现的形式。
                FavoriteModels.Save(connA, new[] { "gpt-4o", "gpt-4o-mini" }, root);
                report(
                    "覆盖写入后留下 .bak",
                    File.Exists(FavoriteModels.FilePathFor(root) + ".bak"),
                    "Delete + Move 在两步之间崩溃会同时失去新旧两份");

                // 损坏退回空名单，且绝不删掉原文件。
                var path = FavoriteModels.FilePathFor(root);
                File.WriteAllText(path, "{ 这不是 JSON");

                report(
                    "文件损坏时退回空名单",
                    FavoriteModels.Load(connA, root).Count == 0,
                    "");

                report(
                    "文件损坏时保留原文件供排查",
                    File.Exists(path),
                    "");
            }
            catch (Exception ex)
            {
                report("名单落盘验证", false, ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) { Directory.Delete(root, true); }
                }
                catch
                {
                }
            }
        }
    }
}
