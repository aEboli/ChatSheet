using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    internal enum WorkBuddyAuthorizationState
    {
        Authorized,
        Unauthorized,
        Unavailable,
    }

    /// <summary>ACP 可执行文件所属的账号空间。</summary>
    internal enum WorkBuddyPathScope
    {
        Unknown,
        Domestic,
        International,
    }

    /// <summary>从 WorkBuddy ACP 目录中保留的非敏感模型信息。</summary>
    internal sealed class WorkBuddyModelInfo
    {
        internal string ModelId { get; set; }

        internal string Name { get; set; }

        internal bool? SupportsImages { get; set; }

        internal bool? SupportsReasoning { get; set; }

        internal bool? SupportsToolCall { get; set; }

        internal long? MaxInputTokens { get; set; }

        internal string CreditMultiplier { get; set; }
    }

    internal sealed class WorkBuddyModelsResult
    {
        internal WorkBuddyAuthorizationState State { get; set; }

        internal string Code { get; set; }

        internal string Detail { get; set; }

        internal string CurrentModelId { get; set; }

        internal IReadOnlyList<WorkBuddyModelInfo> Models { get; set; }

        internal bool IsAuthorized => State == WorkBuddyAuthorizationState.Authorized;
    }

    internal sealed class WorkBuddyAcpPaths
    {
        internal string NodePath { get; set; }

        internal string CliPath { get; set; }

        internal WorkBuddyPathScope Scope { get; set; } = WorkBuddyPathScope.Unknown;

        // 这些目录决定产品配置；官方认证另有共享存储，不能用它们隔离账号。
        // 只记录产品路径推导出的目录，不读取目录中的凭据内容。
        internal string ConfigDirectory { get; set; }
    }

    /// <summary>
    /// 通过 WorkBuddy 自带 CLI 的 ACP 读取当前登录态与模型目录。
    ///
    /// 这里刻意不读取 WorkBuddy 的认证文件。令牌由 WorkBuddy 自己的认证管理器
    /// 在 CLI 进程内使用，ChatSheet 只接收 session/new 的非敏感结果。
    /// </summary>
    internal static class WorkBuddyProvider
    {
        internal const string ProtocolId = "workbuddy-acp";

        private const string NotAuthorizedCode = "WORKBUDDY_NOT_AUTHORIZED";
        private const string UnavailableCode = "WORKBUDDY_UNAVAILABLE";
        private const string TimeoutCode = "WORKBUDDY_ACP_TIMEOUT";
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);
        private static readonly object PathLock = new object();
        private static readonly Dictionary<ConnectionMode, WorkBuddyAcpPaths> PreferredPaths =
            new Dictionary<ConnectionMode, WorkBuddyAcpPaths>();
        private static WorkBuddyAcpPaths PreferredAnyPath;

        internal static async Task<WorkBuddyModelsResult> GetModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await GetModelsAsync(ConnectionMode.Authorized, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<WorkBuddyModelsResult> GetModelsAsync(
            ConnectionMode mode,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!mode.IsWorkBuddy()) { throw new ArgumentException(nameof(mode)); }

            var candidates = FindPathCandidates(mode);
            if (candidates.Count == 0)
            {
                return Unavailable(mode == ConnectionMode.AuthorizedInternational
                    ? "未找到国际版授权组件，请安装或启动国际版 WorkBuddy。"
                    : "未找到国内版授权组件，请安装或启动国内版 WorkBuddy。");
            }

            WorkBuddyModelsResult firstUnauthorized = null;
            WorkBuddyModelsResult firstUnavailable = null;
            foreach (var paths in candidates)
            {
                var result = await GetModelsAsync(paths, cancellationToken).ConfigureAwait(false);
                if (result.IsAuthorized)
                {
                    RememberPreferredPath(mode, paths);
                    return result;
                }
                if (result.State == WorkBuddyAuthorizationState.Unauthorized) { firstUnauthorized = firstUnauthorized ?? result; }
                else { firstUnavailable = firstUnavailable ?? result; }
            }

            return firstUnauthorized ?? firstUnavailable ?? Unavailable("无法读取授权状态，请确认授权组件可运行。", UnavailableCode);
        }

        internal static async Task<WorkBuddyModelsResult> GetModelsAsync(
            WorkBuddyAcpPaths paths, CancellationToken cancellationToken)
        {
            var options = new WorkBuddyAcpOptions
            {
                Paths = paths,
                NodePath = paths.NodePath,
                CliPath = paths.CliPath,
                WorkingDirectory = Environment.CurrentDirectory,
                Timeout = QueryTimeout,
            };

            try
            {
                return await new WorkBuddyAcpClient(options).GetModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (WorkBuddyAcpException ex) when (ex.Code == NotAuthorizedCode)
            {
                return Unauthorized();
            }
            catch (WorkBuddyAcpException ex) when (ex.Code == TimeoutCode)
            {
                return Unavailable("读取 WorkBuddy 授权状态超时，请确认 WorkBuddy 正常运行。", ex.Code);
            }
            catch (WorkBuddyAcpException)
            {
                return Unavailable("无法读取 WorkBuddy 授权状态，请确认 WorkBuddy 版本和安装状态。", UnavailableCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Unavailable("读取 WorkBuddy 授权状态超时，请确认 WorkBuddy 正常运行。", TimeoutCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 不把进程启动异常、stderr 或路径细节送到面板；它们可能包含环境信息。
                return Unavailable("无法启动 WorkBuddy ACP，请确认安装完整后重试。", UnavailableCode);
            }
        }

        internal static IChatStreamClient CreateChatClient()
        {
            return CreateChatClient(ConnectionMode.Authorized);
        }

        internal static IChatStreamClient CreateChatClient(ConnectionMode mode)
        {
            if (!mode.IsWorkBuddy()) { throw new ArgumentException(nameof(mode)); }

            if (!TryFindPaths(mode, out var paths))
            {
                throw new ProviderException(
                    UnavailableCode,
                    mode == ConnectionMode.AuthorizedInternational
                        ? "未找到国际版授权组件，请安装或启动国际版 WorkBuddy。"
                        : "未找到国内版授权组件，请安装或启动国内版 WorkBuddy。");
            }

            return new WorkBuddyChatClient(paths);
        }

        internal static bool TryFindPaths(out WorkBuddyAcpPaths paths)
        {
            return TryFindPaths((ConnectionMode?)null, out paths);
        }

        internal static bool TryFindPaths(ConnectionMode mode, out WorkBuddyAcpPaths paths)
        {
            return TryFindPaths((ConnectionMode?)mode, out paths);
        }

        private static bool TryFindPaths(ConnectionMode? mode, out WorkBuddyAcpPaths paths)
        {
            var candidates = FindPathCandidates(mode);
            lock (PathLock)
            {
                var preferred = mode.HasValue
                    ? (PreferredPaths.TryGetValue(mode.Value, out var scoped) ? scoped : null)
                    : PreferredAnyPath;
                paths = preferred != null && candidates.Any(candidate => SamePath(candidate, preferred))
                    ? candidates.First(candidate => SamePath(candidate, preferred))
                    : candidates.FirstOrDefault();
            }
            return paths != null;
        }

        internal static bool TryFindStandalonePaths(out WorkBuddyAcpPaths paths)
        {
            var nativePath = WorkBuddyRuntime.FindNativePath();
            paths = nativePath == null ? null : new WorkBuddyAcpPaths
            {
                CliPath = nativePath,
                Scope = WorkBuddyPathScope.International,
                ConfigDirectory = ConfigDirectoryForScope(WorkBuddyPathScope.International),
            };
            return paths != null;
        }

        internal static IReadOnlyList<WorkBuddyAcpPaths> GetPathCandidates()
        {
            return FindPathCandidates((ConnectionMode?)null);
        }

        internal static IReadOnlyList<WorkBuddyAcpPaths> GetPathCandidates(ConnectionMode mode)
        {
            if (!mode.IsWorkBuddy()) { throw new ArgumentException(nameof(mode)); }
            return FindPathCandidates(mode);
        }

        private static List<WorkBuddyAcpPaths> FindPathCandidates(ConnectionMode? mode)
        {
            var all = new List<WorkBuddyAcpPaths>();
            var cliCandidates = new List<string>();
            var localAppDataDirectories = WorkBuddyRuntime.LocalApplicationDataDirectories();
            var programFilesRoots = ProgramFilesDirectories();

            // 国内桌面端与国际版 WorkBuddy/CodeBuddy 共用 CLI 入口，但安装目录不同。
            // 按固定目录探测可以在没有独立 headless 组件时继续使用桌面端 ACP。
            foreach (var productDirectory in new[] { "WorkBuddy", "WorkBuddyAI", "CodeBuddy" })
            {
                foreach (var localAppData in localAppDataDirectories)
                {
                    AddCliCandidate(cliCandidates, Path.Combine(
                        localAppData, "Programs", productDirectory, "resources", "app.asar.unpacked",
                        "cli", "bin", "codebuddy"));
                }
                foreach (var programFiles in programFilesRoots)
                {
                    AddCliCandidate(cliCandidates, Path.Combine(
                        programFiles, productDirectory, "resources", "app.asar.unpacked", "cli", "bin", "codebuddy"));
                }
            }

            foreach (var cliPath in cliCandidates.Where(File.Exists))
            {
                var scope = ScopeForCliPath(cliPath);
                var configDirectory = ConfigDirectoryForCliPath(cliPath, scope);
                if (string.Equals(Path.GetExtension(cliPath), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    AddPathCandidate(all, new WorkBuddyAcpPaths
                    {
                        CliPath = cliPath,
                        Scope = scope,
                        ConfigDirectory = configDirectory,
                    });
                }
                else
                {
                    var nodePath = FindBundledNode(scope);
                    if (nodePath == null && scope == WorkBuddyPathScope.Unknown)
                    {
                        nodePath = FindOnPath(new[] { "node.exe", "node" });
                    }
                    if (nodePath != null)
                    {
                        AddPathCandidate(all, new WorkBuddyAcpPaths
                        {
                            NodePath = nodePath,
                            CliPath = cliPath,
                            Scope = scope,
                            ConfigDirectory = configDirectory,
                        });
                    }
                }
            }

            // 优先复用桌面端当前账号；独立 CLI 的 .codebuddy 授权仍作为兜底。
            var nativePath = WorkBuddyRuntime.FindNativePath();
            if (nativePath != null)
            {
                AddPathCandidate(all, new WorkBuddyAcpPaths
                {
                    CliPath = nativePath,
                    Scope = WorkBuddyPathScope.International,
                    ConfigDirectory = ConfigDirectoryForScope(WorkBuddyPathScope.International),
                });
            }

            var pathCli = FindOnPath(new[] { "codebuddy.exe", "codebuddy" });
            if (pathCli != null && string.Equals(Path.GetExtension(pathCli), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                AddPathCandidate(all, new WorkBuddyAcpPaths
                {
                    CliPath = pathCli,
                    Scope = WorkBuddyPathScope.Unknown,
                });
            }
            else if (pathCli != null)
            {
                var pathNode = FindBundledNode(WorkBuddyPathScope.Unknown) ?? FindOnPath(new[] { "node.exe", "node" });
                if (pathNode != null)
                {
                    AddPathCandidate(all, new WorkBuddyAcpPaths
                    {
                        NodePath = pathNode,
                        CliPath = pathCli,
                        Scope = WorkBuddyPathScope.Unknown,
                    });
                }
            }

            var candidates = FilterCandidatesForMode(all, mode);
            lock (PathLock)
            {
                var preferred = mode.HasValue
                    ? (PreferredPaths.TryGetValue(mode.Value, out var scoped) ? scoped : null)
                    : PreferredAnyPath;
                if (preferred != null)
                {
                    candidates = candidates
                        .OrderBy(candidate => SamePath(candidate, preferred) ? 0 : 1)
                        .ToList();
                }
            }
            return candidates;
        }

        private static void AddPathCandidate(ICollection<WorkBuddyAcpPaths> candidates, WorkBuddyAcpPaths candidate)
        {
            var existing = candidates.FirstOrDefault(item => SamePath(item, candidate));
            if (existing == null)
            {
                candidates.Add(candidate);
            }
            else if (existing.Scope == WorkBuddyPathScope.Unknown && candidate.Scope != WorkBuddyPathScope.Unknown)
            {
                existing.Scope = candidate.Scope;
            }
        }

        private static List<WorkBuddyAcpPaths> FilterCandidatesForMode(
            IEnumerable<WorkBuddyAcpPaths> candidates,
            ConnectionMode? mode)
        {
            var list = candidates.ToList();
            if (!mode.HasValue) { return list; }

            var wanted = mode.Value == ConnectionMode.AuthorizedInternational
                ? WorkBuddyPathScope.International
                : WorkBuddyPathScope.Domestic;
            var matching = list.Where(candidate => candidate.Scope == wanted).ToList();
            if (matching.Count > 0)
            {
                // 同一接入模式可能同时存在桌面端和独立 CLI。桌面端的账号、模型
                // 目录才是用户在 WorkBuddy 中看到的权威来源；只要它存在，就不能
                // 让独立 CLI 的另一份目录参与竞选，否则会出现模型数量和倍率不一致。
                var desktop = matching.Where(candidate => IsDesktopProductPath(candidate, wanted)).ToList();
                return desktop.Count > 0 ? desktop : matching;
            }

            // 未标记的 PATH 入口只有在系统没有任何已分类产品时才可作为兜底。
            // 若已知另一套产品存在，宁可显示“未找到当前版本”，也不能把另一套账号冒充过来。
            return list.Any(candidate => candidate.Scope != WorkBuddyPathScope.Unknown)
                ? new List<WorkBuddyAcpPaths>()
                : list;
        }

        internal static bool IsDesktopProductPath(WorkBuddyAcpPaths candidate, WorkBuddyPathScope scope)
        {
            var normalized = (candidate?.CliPath ?? string.Empty).Replace('/', '\\');
            if (scope == WorkBuddyPathScope.International)
            {
                return normalized.IndexOf("\\WorkBuddyAI\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return scope == WorkBuddyPathScope.Domestic &&
                normalized.IndexOf("\\WorkBuddy\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static WorkBuddyPathScope ProductScope(string productDirectory)
        {
            var name = Path.GetFileName((productDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(name, "CodeBuddy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "WorkBuddyAI", StringComparison.OrdinalIgnoreCase)
                ? WorkBuddyPathScope.International
                : string.Equals(name, "WorkBuddy", StringComparison.OrdinalIgnoreCase)
                    ? WorkBuddyPathScope.Domestic
                    : WorkBuddyPathScope.Unknown;
        }

        private static WorkBuddyPathScope ScopeForCliPath(string cliPath)
        {
            var normalized = (cliPath ?? string.Empty).Replace('/', '\\');
            if (normalized.IndexOf("\\CodeBuddy\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("\\WorkBuddyAI\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WorkBuddyPathScope.International;
            }

            if (normalized.IndexOf("\\WorkBuddy\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WorkBuddyPathScope.Domestic;
            }

            return WorkBuddyPathScope.Unknown;
        }

        internal static string ConfigDirectoryForCliPath(string cliPath, WorkBuddyPathScope scope)
        {
            var normalized = (cliPath ?? string.Empty).Replace('/', '\\');
            var userProfile = UserProfileDirectory();
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }
            if (normalized.IndexOf("\\CodeBuddy\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Path.Combine(userProfile, ".codebuddy");
            }

            if (normalized.IndexOf("\\WorkBuddyAI\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Path.Combine(userProfile, ".workbuddy-ai");
            }

            if (normalized.IndexOf("\\WorkBuddy\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Path.Combine(userProfile, ".workbuddy");
            }

            return ConfigDirectoryForScope(scope);
        }

        internal static string ConfigDirectoryForScope(WorkBuddyPathScope scope)
        {
            var userProfile = UserProfileDirectory();
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }
            switch (scope)
            {
                case WorkBuddyPathScope.International:
                    return Path.Combine(userProfile, ".codebuddy");
                case WorkBuddyPathScope.Domestic:
                    return Path.Combine(userProfile, ".workbuddy");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Excel/WPS 以 32 位进程加载加载项时，.NET Framework 某些宿主不会填充
        /// SpecialFolder.UserProfile；环境变量仍然可靠，不能因此丢失本机 CLI。
        /// </summary>
        internal static string UserProfileDirectory()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                return profile;
            }

            profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(profile))
            {
                return profile;
            }

            return (Environment.GetEnvironmentVariable("HOMEDRIVE") ?? string.Empty) +
                (Environment.GetEnvironmentVariable("HOMEPATH") ?? string.Empty);
        }

        private static IReadOnlyList<string> ProgramFilesDirectories()
        {
            var roots = new List<string>();
            AddDirectory(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddDirectory(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            // WOW64 进程把 ProgramFiles 映射到 x86；ProgramW6432 保留真实的 64 位目录。
            AddDirectory(roots, Environment.GetEnvironmentVariable("ProgramW6432"));
            return roots;
        }

        private static void AddDirectory(ICollection<string> directories, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !directories.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(path);
            }
        }

        private static bool SamePath(WorkBuddyAcpPaths left, WorkBuddyAcpPaths right)
        {
            return string.Equals(left?.NodePath ?? string.Empty, right?.NodePath ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left?.CliPath ?? string.Empty, right?.CliPath ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left?.ConfigDirectory ?? string.Empty, right?.ConfigDirectory ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static void RememberPreferredPath(ConnectionMode mode, WorkBuddyAcpPaths paths)
        {
            lock (PathLock)
            {
                PreferredPaths[mode] = paths;
                PreferredAnyPath = paths;
            }
        }

        internal static void PreferPaths(WorkBuddyAcpPaths paths)
        {
            if (paths == null) { return; }
            lock (PathLock) { PreferredAnyPath = paths; }
        }

        internal static void PreferPaths(ConnectionMode mode, WorkBuddyAcpPaths paths)
        {
            if (paths == null || !mode.IsWorkBuddy()) { return; }
            RememberPreferredPath(mode, paths);
        }

        /// <summary>仅解析 ACP 的安全模型字段，供无进程单元测试复用。</summary>
        internal static IReadOnlyList<WorkBuddyModelInfo> ParseModels(JObject sessionResult)
        {
            var models = new List<WorkBuddyModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var available = sessionResult?.SelectToken("models.availableModels") as JArray;
            if (available == null)
            {
                return models;
            }

            foreach (var token in available)
            {
                string modelId;
                string name;
                JObject metadata;

                if (token.Type == JTokenType.String)
                {
                    modelId = token.Value<string>();
                    name = modelId;
                    metadata = null;
                }
                else
                {
                    var item = token as JObject;
                    modelId = item?.Value<string>("modelId") ?? item?.Value<string>("id");
                    name = item?.Value<string>("name");
                    metadata = item?["_meta"] as JObject;
                }

                modelId = (modelId ?? string.Empty).Trim();
                if (modelId.Length == 0 || !seen.Add(modelId))
                {
                    continue;
                }

                name = (name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    name = modelId;
                }

                models.Add(new WorkBuddyModelInfo
                {
                    ModelId = modelId,
                    Name = name,
                    SupportsImages = NullableBool(metadata, "supportsImages"),
                    SupportsReasoning = NullableBool(metadata, "supportsReasoning"),
                    SupportsToolCall = NullableBool(metadata, "supportsToolCall"),
                    MaxInputTokens = NullableLong(metadata, "maxInputTokens"),
                    CreditMultiplier = ParseCreditMultiplier(metadata),
                });
            }

            return models;
        }

        internal static string StatusId(WorkBuddyAuthorizationState state)
        {
            switch (state)
            {
                case WorkBuddyAuthorizationState.Authorized:
                    return "authorized";
                case WorkBuddyAuthorizationState.Unauthorized:
                    return "unauthorized";
                default:
                    return "unavailable";
            }
        }

        private static WorkBuddyModelsResult Unauthorized()
        {
            return new WorkBuddyModelsResult
            {
                State = WorkBuddyAuthorizationState.Unauthorized,
                Code = NotAuthorizedCode,
                Detail = "当前尚未授权，请点击浏览器登录。",
                Models = Array.Empty<WorkBuddyModelInfo>(),
            };
        }

        private static WorkBuddyModelsResult Unavailable(string detail, string code = UnavailableCode)
        {
            return new WorkBuddyModelsResult
            {
                State = WorkBuddyAuthorizationState.Unavailable,
                Code = code,
                Detail = detail,
                Models = Array.Empty<WorkBuddyModelInfo>(),
            };
        }

        private static WorkBuddyModelsResult Authorized(JObject sessionResult)
        {
            var models = ParseModels(sessionResult);
            var currentModelId = sessionResult?.SelectToken("models.currentModelId")?.Value<string>();
            return new WorkBuddyModelsResult
            {
                State = WorkBuddyAuthorizationState.Authorized,
                Code = string.Empty,
                Detail = $"WorkBuddy 已授权，已读取 {models.Count} 个可用模型。",
                CurrentModelId = (currentModelId ?? string.Empty).Trim(),
                Models = models,
            };
        }

        private static bool? NullableBool(JObject source, string name)
        {
            return source != null && source[name]?.Type == JTokenType.Boolean
                ? source.Value<bool?>(name)
                : (bool?)null;
        }

        private static long? NullableLong(JObject source, string name)
        {
            if (source == null || source[name] == null)
            {
                return null;
            }

            try
            {
                return source.Value<long?>(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>接受官方 x0.00 和 x0.00 credits 格式，不展示任意元数据。</summary>
        private static string ParseCreditMultiplier(JObject source)
        {
            var token = source?["credits"];
            if (token == null || token.Type != JTokenType.String)
            {
                return null;
            }

            var raw = (token.Value<string>() ?? string.Empty).Trim();
            const string creditsUnit = " credits";
            if (raw.EndsWith(creditsUnit, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(0, raw.Length - creditsUnit.Length).TrimEnd();
            }
            if (raw.Length < 2 || (raw[0] != 'x' && raw[0] != 'X'))
            {
                return null;
            }

            var number = raw.Substring(1);
            var hasDecimalPoint = false;
            foreach (var character in number)
            {
                if (character >= '0' && character <= '9')
                {
                    continue;
                }

                if (character == '.' && !hasDecimalPoint)
                {
                    hasDecimalPoint = true;
                    continue;
                }

                return null;
            }

            if (!decimal.TryParse(
                number,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value) || value < 0m)
            {
                return null;
            }

            return value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }

        private static void AddCliCandidate(ICollection<string> candidates, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
        }

        private static string FindBundledNode(WorkBuddyPathScope scope)
        {
            var userProfile = UserProfileDirectory();
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }
            var dataDirectories = scope == WorkBuddyPathScope.Domestic
                ? new[] { ".workbuddy" }
                : scope == WorkBuddyPathScope.International
                    ? new[] { ".workbuddy-ai", ".codebuddy" }
                    : new[] { ".workbuddy", ".workbuddy-ai", ".codebuddy" };
            foreach (var dataDirectory in dataDirectories)
            {
                var versions = Path.Combine(userProfile, dataDirectory, "binaries", "node", "versions");
                if (!Directory.Exists(versions))
                {
                    continue;
                }

                try
                {
                    var node = Directory.GetDirectories(versions)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => Path.Combine(path, "node.exe"))
                        .FirstOrDefault(File.Exists);
                    if (node != null)
                    {
                        return node;
                    }
                }
                catch (IOException)
                {
                    // 某个产品目录不可读时继续检查其他官方目录。
                }
                catch (UnauthorizedAccessException)
                {
                    // 某个产品目录不可读时继续检查其他官方目录。
                }
            }

            return null;
        }

        private static string FindOnPath(IEnumerable<string> names)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private sealed class WorkBuddyAcpOptions
        {
            internal WorkBuddyAcpPaths Paths { get; set; }

            internal string NodePath { get; set; }

            internal string CliPath { get; set; }

            internal string WorkingDirectory { get; set; }

            internal TimeSpan Timeout { get; set; }
        }

        private sealed class WorkBuddyAcpClient
        {
            private readonly WorkBuddyAcpOptions _options;

            internal WorkBuddyAcpClient(WorkBuddyAcpOptions options)
            {
                _options = options;
            }

            internal async Task<WorkBuddyModelsResult> GetModelsAsync(CancellationToken cancellationToken)
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(_options.Timeout);

                    using (var process = StartProcess())
                    {
                        try
                        {
                            await RequestAsync(
                                process,
                                1,
                                "initialize",
                                new JObject
                                {
                                    ["protocolVersion"] = 1,
                                    ["clientCapabilities"] = new JObject(),
                                    ["clientInfo"] = new JObject
                                    {
                                        ["name"] = "ChatSheet",
                                        ["version"] = typeof(WorkBuddyProvider).Assembly.GetName().Version.ToString(),
                                    },
                                },
                                timeout.Token,
                                cancellationToken).ConfigureAwait(false);

                            var session = await RequestAsync(
                                process,
                                2,
                                "session/new",
                                new JObject
                                {
                                    ["cwd"] = _options.WorkingDirectory,
                                    ["mcpServers"] = new JArray(),
                                },
                                timeout.Token,
                                cancellationToken).ConfigureAwait(false);

                            return Authorized(session);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new WorkBuddyAcpException(TimeoutCode, "WorkBuddy ACP 请求超时。");
                        }
                        finally
                        {
                            StopProcess(process);
                        }
                    }
                }
            }

            private WorkBuddyProcess StartProcess()
            {
                var paths = _options.Paths ?? new WorkBuddyAcpPaths
                    {
                        NodePath = _options.NodePath,
                        CliPath = _options.CliPath,
                    };
                var workingDirectory = Directory.Exists(_options.WorkingDirectory)
                    ? _options.WorkingDirectory
                    : Environment.CurrentDirectory;
                return WorkBuddyProcess.Start(paths, workingDirectory);
            }

            private static async Task<JObject> RequestAsync(
                WorkBuddyProcess process,
                int id,
                string method,
                JObject parameters,
                CancellationToken timeoutToken,
                CancellationToken callerToken)
            {
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters,
                };

                var bytes = Encoding.UTF8.GetBytes(request.ToString(Formatting.None) + Environment.NewLine);
                await process.Input.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await process.Input.FlushAsync().ConfigureAwait(false);

                while (true)
                {
                    var lineTask = process.Output.ReadLineAsync();
                    var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutToken);
                    var finished = await Task.WhenAny(lineTask, cancelTask).ConfigureAwait(false);
                    if (finished != lineTask)
                    {
                        if (callerToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(callerToken);
                        }

                        throw new WorkBuddyAcpException(TimeoutCode, "WorkBuddy ACP 请求超时。");
                    }

                    var line = await lineTask.ConfigureAwait(false);
                    if (line == null)
                    {
                        throw new WorkBuddyAcpException(UnavailableCode, "WorkBuddy ACP 进程已退出。");
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    line = line.TrimStart('\uFEFF');

                    JObject message;
                    try
                    {
                        message = JObject.Parse(line);
                    }
                    catch (JsonException)
                    {
                        // CLI 的非协议输出不能成为面板或日志中的信息来源。
                        continue;
                    }

                    if (message["id"] == null || message.Value<int?>("id") != id)
                    {
                        // 只检查通知的方法名，不扫描结果正文；模型名称或用户信息
                        // 中出现普通英文词不应改变授权状态。
                        if (message["id"] == null && IsAuthMarker(message.Value<string>("method") ?? string.Empty))
                        {
                            throw new WorkBuddyAcpException(NotAuthorizedCode, "WorkBuddy 当前未授权。");
                        }

                        continue;
                    }

                    var error = message["error"] as JObject;
                    if (error != null)
                    {
                        throw MapError(error);
                    }

                    return message["result"] as JObject
                        ?? throw new WorkBuddyAcpException(UnavailableCode, "WorkBuddy ACP 返回了无效结果。");
                }
            }

            private static bool IsAuthMarker(string text)
            {
                return text.IndexOf("auth_required", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("authentication required", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("not authenticated", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("unauthorized", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("not logged in", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("login required", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("please login", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("未授权", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("未登录", StringComparison.Ordinal) >= 0;
            }

            private static WorkBuddyAcpException MapError(JObject error)
            {
                var marker = string.Join(
                    " ",
                    error.Value<string>("code") ?? string.Empty,
                    error.Value<string>("message") ?? string.Empty,
                    error["data"]?.ToString(Formatting.None) ?? string.Empty).ToLowerInvariant();

                if (IsAuthMarker(marker))
                {
                    return new WorkBuddyAcpException(NotAuthorizedCode, "WorkBuddy 当前未授权。");
                }

                return new WorkBuddyAcpException("WORKBUDDY_ACP_ERROR", "WorkBuddy ACP 返回了错误。");
            }

            private static void StopProcess(WorkBuddyProcess process)
            {
                try { process?.Stop(); } catch { }
            }

        }

        private sealed class WorkBuddyAcpException : Exception
        {
            internal WorkBuddyAcpException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            internal string Code { get; }
        }
    }
}
