using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Storage;

namespace ChatSheet.AddIn.Providers
{
    internal static class WorkBuddyRuntime
    {
        private const string Repository = "https://acc-1258344699.cos.accelerate.myqcloud.com/@tencent-ai/codebuddy-code/releases";
        internal const string PackageName = "codebuddy-code-headless_Windows_x86_64.zip";
        internal const string HeadlessExecutableName = "codebuddy-headless.exe";
        internal static string NativePath => BuildNativePath(LocalApplicationDataDirectory());

        internal static string FindNativePath()
        {
            return FindNativePath(LocalApplicationDataDirectories());
        }

        internal static string FindNativePath(string localApplicationData)
        {
            return FindNativePath(string.IsNullOrWhiteSpace(localApplicationData)
                ? Array.Empty<string>()
                : new[] { localApplicationData });
        }

        internal static string NativePathDiagnostics()
        {
            var details = new List<string>();
            foreach (var localApplicationData in LocalApplicationDataDirectories())
            {
                var nativePath = BuildNativePath(localApplicationData);
                var managedPath = BuildManagedNativePath(localApplicationData);
                var root = Path.GetDirectoryName(Path.GetDirectoryName(nativePath ?? string.Empty) ?? string.Empty);
                var versions = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "Data", "versions");
                details.Add($"本地目录={localApplicationData} bridge={DescribeExecutable(managedPath)} " +
                    $"bin={DescribeExecutable(nativePath)} versions目录={SafeDirectoryExists(versions)}");
                if (!SafeDirectoryExists(versions)) { continue; }
                try
                {
                    foreach (var versionDirectory in Directory.GetDirectories(versions))
                    {
                        details.Add($"版本={Path.GetFileName(versionDirectory)} exe={DescribeExecutable(Path.Combine(versionDirectory, "codebuddy.exe"))}");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                {
                    details.Add("枚举版本异常=" + ex.GetType().Name);
                }
            }
            return details.Count == 0 ? "未解析到本地应用数据目录" : string.Join(" | ", details);
        }

        private static bool SafeDirectoryExists(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path); }
            catch { return false; }
        }

        private static string DescribeExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return "路径为空"; }
            var exists = false;
            try { exists = File.Exists(path); }
            catch { }
            try
            {
                using (var file = File.OpenRead(path))
                {
                    var valid = file.ReadByte() == 'M' && file.ReadByte() == 'Z';
                    return $"存在={exists},PE={valid},长度={file.Length}";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException || ex is SecurityException)
            {
                return $"存在={exists},读取异常={ex.GetType().Name}";
            }
        }

        private static string FindNativePath(IEnumerable<string> localApplicationDataDirectories)
        {
            foreach (var localApplicationData in localApplicationDataDirectories ?? Array.Empty<string>())
            {
                var managedPath = BuildManagedNativePath(localApplicationData);
                if (IsExecutable(managedPath)) { return managedPath; }

                var nativePath = BuildNativePath(localApplicationData);
                if (string.IsNullOrWhiteSpace(nativePath)) { continue; }

                // 官方 bin 入口通常是符号链接；优先选安装器的真实版本文件，
                // 这样在禁用符号链接启动或 WPS 32 位宿主下也能稳定启动。
                var parent = Path.GetDirectoryName(nativePath);
                var root = parent == null ? null : Path.GetDirectoryName(parent);
                var versions = string.IsNullOrWhiteSpace(root)
                    ? null
                    : Path.Combine(root, "Data", "versions");
                try
                {
                    if (versions != null && Directory.Exists(versions))
                    {
                        var installed = Directory.GetDirectories(versions)
                            .OrderByDescending(path => Version.TryParse(
                                Path.GetFileName(path).Split('-')[0], out var version)
                                ? version : new Version())
                            .Select(path => Path.Combine(path, "codebuddy.exe"))
                            .FirstOrDefault(IsExecutable);
                        if (installed != null) { return installed; }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (IsExecutable(nativePath)) { return nativePath; }
            }

            return null;
        }

        private static bool IsExecutable(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return false; }

                // File.OpenRead follows Windows symbolic links/junctions.  Checking
                // the target bytes is the reliable test; rejecting ReparsePoint on
                // the directory entry incorrectly hides the official bin link.
                using (var file = File.OpenRead(path))
                {
                    return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (SecurityException) { return false; }
        }

        internal static string LocalApplicationDataDirectory()
        {
            return LocalApplicationDataDirectories().FirstOrDefault() ?? string.Empty;
        }

        internal static IReadOnlyList<string> LocalApplicationDataDirectories()
        {
            var directories = new List<string>();
            var specialFolder = SafeGetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localAppDataEnvironment = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var profile = SafeGetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
            {
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            }
            if (string.IsNullOrWhiteSpace(profile))
            {
                profile = (Environment.GetEnvironmentVariable("HOMEDRIVE") ?? string.Empty) +
                    (Environment.GetEnvironmentVariable("HOMEPATH") ?? string.Empty);
            }

            AddDirectory(directories, ResolveLocalApplicationDataDirectory(
                specialFolder, localAppDataEnvironment, profile));
            AddDirectory(directories, localAppDataEnvironment);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                AddDirectory(directories, Path.Combine(profile.Trim(), "AppData", "Local"));
            }

            return directories;
        }

        private static string SafeGetFolderPath(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (ArgumentException) { return string.Empty; }
            catch (InvalidOperationException) { return string.Empty; }
            catch (SecurityException) { return string.Empty; }
        }

        internal static string ResolveLocalApplicationDataDirectory(
            string specialFolder,
            string localAppDataEnvironment,
            string userProfile)
        {
            if (!string.IsNullOrWhiteSpace(specialFolder)) { return specialFolder.Trim(); }
            if (!string.IsNullOrWhiteSpace(localAppDataEnvironment)) { return localAppDataEnvironment.Trim(); }
            return string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile.Trim(), "AppData", "Local");
        }

        private static string BuildNativePath(string localApplicationData)
        {
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? null
                : Path.Combine(localApplicationData, "codebuddy", "bin", "codebuddy.exe");
        }

        private static string BuildManagedNativePath(string localApplicationData)
        {
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? null
                : Path.Combine(localApplicationData, "ChatSheet", "components", "codebuddy.exe");
        }

        private static void AddDirectory(ICollection<string> directories, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !directories.Contains(path.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(path.Trim());
            }
        }

        internal static ProcessStartInfo StartInfo(WorkBuddyAcpPaths paths, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(paths.NodePath) ? paths.CliPath : paths.NodePath,
                Arguments = (string.IsNullOrEmpty(paths.NodePath) ? "" : Quote(paths.CliPath) + " ") +
                    "--acp --no-session-persistence --tools \"\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // 桌面端和独立 CLI 必须使用同一份产品配置目录，否则 CLI 会看见
            // 另一套空账号，从而把已登录的国内/国际桌面端误报成未授权。
            if (!string.IsNullOrWhiteSpace(paths.ConfigDirectory))
            {
                // EnvironmentVariables 在部分 .NET Framework 宿主中会因 Path/PATH
                // 大小写重复而抛 ArgumentException；先取得去重后的可写环境字典。
                var environment = WritableEnvironment(startInfo);
                environment["WORKBUDDY_CONFIG_DIR"] = paths.ConfigDirectory;
                environment["CODEBUDDY_CONFIG_DIR"] = paths.ConfigDirectory;
                environment["WORKBUDDY_DATA_FOLDER_NAME"] =
                    Path.GetFileName(paths.ConfigDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var productConfig = DesktopProductConfig(paths);
                if (productConfig != null)
                {
                    environment["CODEBUDDY_HOST"] = "workbuddy-desktop";
                    environment["ACC_PRODUCT_CONFIG_PATH"] = productConfig;
                }
                SynchronizeLegacyEnvironment(startInfo, environment);
            }

            return startInfo;
        }

        internal static string DesktopProductConfig(WorkBuddyAcpPaths paths)
        {
            if (!WorkBuddyProvider.IsDesktopProductPath(paths, WorkBuddyPathScope.International) ||
                string.IsNullOrWhiteSpace(paths.ConfigDirectory)) { return null; }

            // 与桌面端子进程使用相同的官方产品快照；仍由 ACP 返回和校验模型。
            var directory = Path.Combine(paths.ConfigDirectory, "cache", "conversation-product-spill");
            try
            {
                return Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "acc-product-config-v3-*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault()
                    : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static IDictionary<string, string> WritableEnvironment(ProcessStartInfo startInfo)
        {
            try
            {
                return startInfo.Environment;
            }
            catch (ArgumentException)
            {
                // .NET Framework 4.8 stores the public Environment view lazily. A
                // duplicated Path/PATH inherited from a host can make that getter
                // fail before the child process is even started. Seed its private
                // dictionary with a case-insensitive, de-duplicated snapshot.
                var field = typeof(ProcessStartInfo).GetField(
                    "environment",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field == null)
                {
                    throw;
                }

                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
                {
                    var key = entry.Key as string;
                    if (!string.IsNullOrWhiteSpace(key) && !environment.ContainsKey(key))
                    {
                        environment[key] = entry.Value as string ?? string.Empty;
                    }
                }

                field.SetValue(startInfo, environment);
                SynchronizeLegacyEnvironment(startInfo, environment);
                return startInfo.Environment;
            }
        }

        private static void SynchronizeLegacyEnvironment(
            ProcessStartInfo startInfo,
            IDictionary<string, string> environment)
        {
            // .NET Framework Process.Start still builds its native environment
            // block from the legacy StringDictionary. Keep it in sync with the
            // newer Environment view when a host supplied duplicate Path keys.
            var field = typeof(ProcessStartInfo).GetField(
                "environmentVariables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var legacy = field?.GetValue(startInfo) as System.Collections.Specialized.StringDictionary;
            if (legacy == null)
            {
                return;
            }

            // Some .NET Framework builds expose Environment as a view over this
            // same StringDictionary. Snapshot first so clearing the legacy store
            // cannot also erase the values that still need to be copied back.
            var entries = environment.ToArray();
            legacy.Clear();
            foreach (var entry in entries)
            {
                legacy[entry.Key] = entry.Value ?? string.Empty;
            }
        }

        internal static object Status()
        {
            return Status(null);
        }

        internal static object Status(ConnectionMode? mode)
        {
            var available = mode.HasValue
                ? WorkBuddyProvider.TryFindPaths(mode.Value, out var paths)
                : WorkBuddyProvider.TryFindPaths(out paths);
            // codebuddy.exe 是国际版原生授权组件。国内模式即使机器上安装了它，
            // 也不能把它当作国内 WorkBuddy 的独立组件，否则设置页会误导用户，
            // 甚至让两个模式看起来共用同一套账号。
            var standalone = FindNativePath() != null &&
                (!mode.HasValue || mode.Value == ConnectionMode.AuthorizedInternational);
            return new
            {
                available,
                // 桌面端/嵌入式 ACP 也能完成浏览器授权；不要把登录能力绑定到独立安装状态。
                // available 只代表本次探测找到了 ACP；即使暂时没有路径，
                // 登录动作也必须可触发，以便重新探测并给出安装/启动提示。
                canLogin = mode.HasValue ? true : available,
                standalone,
                detail = standalone ? "国际版独立授权组件已安装"
                    : available ? (mode.HasValue && mode.Value == ConnectionMode.Authorized
                        ? "已检测到国内 WorkBuddy 授权组件"
                        : "已检测到可用授权组件")
                    : mode.HasValue && mode.Value == ConnectionMode.Authorized
                        ? "尚未检测到国内 WorkBuddy 授权组件"
                        : "尚未安装授权组件",
            };
        }

        // 与官方 install.ps1 相同的原生包、SHA256 校验和 install 命令。
        internal static async Task InstallAsync(Action<string> progress, CancellationToken token)
        {
            if (FindNativePath() != null) { return; }
            if (!Environment.Is64BitOperatingSystem)
            {
                throw new ProviderException("WORKBUDDY_INSTALL_UNSUPPORTED", "官方独立组件目前需要 64 位 Windows。");
            }

            var localAppData = LocalApplicationDataDirectory();
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new ProviderException("WORKBUDDY_INSTALL_PATH", "无法确定本机应用数据目录，请检查 Windows 用户环境变量。");
            }
            var root = Path.Combine(localAppData,
                "ChatSheet", "component-staging");
            Directory.CreateDirectory(root);
            using (var gate = new FileStream(Path.Combine(root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                var ct = timeout.Token;
                var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    progress?.Invoke("正在获取官方组件版本…");
                    var version = (await DownloadTextAsync(http, Repository + "/latest", ct).ConfigureAwait(false)).Trim();
                    if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$"))
                    {
                        throw new InvalidDataException();
                    }

                    const string package = PackageName;
                    var release = Repository + "/download/" + version + "/";
                    var checksums = await DownloadTextAsync(http, release + "checksums.txt", ct).ConfigureAwait(false);
                    var expected = ParseChecksum(checksums, package);
                    if (expected == null) { throw new InvalidDataException(); }

                    progress?.Invoke("正在下载独立授权组件…");
                    var archivePath = Path.Combine(staging, package);
                    using (var response = await http.GetAsync(release + package, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = File.Create(archivePath))
                        {
                            await input.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                        }
                    }

                    progress?.Invoke("正在校验并安装组件…");
                    using (var file = File.OpenRead(archivePath))
                    using (var sha = SHA256.Create())
                    {
                        var actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ProviderException("WORKBUDDY_INSTALL_CHECKSUM", "组件校验失败，未执行安装。请重新下载。");
                        }
                    }

                    var executable = Path.Combine(staging, HeadlessExecutableName);
                    using (var archive = ZipFile.OpenRead(archivePath))
                    {
                        var entry = archive.Entries.Single(e => string.Equals(e.Name, HeadlessExecutableName, StringComparison.OrdinalIgnoreCase));
                        entry.ExtractToFile(executable);
                    }

                    ct.ThrowIfCancellationRequested();
                    var installerPid = WorkBuddyProcess.StartDetached(
                        executable,
                        "install " + version,
                        staging);
                    try
                    {
                        var deadline = DateTime.UtcNow.AddMinutes(2);
                        while (FindNativePath() == null && DateTime.UtcNow < deadline)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (WorkBuddyProcess.HasProcessExited(installerPid))
                            {
                                // 官方安装器写入版本目录后可能还需要短暂释放文件句柄，
                                // 因此继续轮询而不是把进程退出立即判为失败。
                                await Task.Delay(250, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                            }
                        }
                        if (FindNativePath() == null)
                        {
                            throw new InvalidOperationException("官方安装器未生成可用的 codebuddy.exe。");
                        }
                    }
                    finally
                    {
                        WorkBuddyProcess.StopDetached(installerPid);
                    }
                }
                finally
                {
                    // 仅删除本次新建的 GUID 暂存目录；保留锁文件，避免并发安装锁失效。
                    try { Directory.Delete(staging, true); } catch { }
                }
            }
        }

        internal static string ParseChecksum(string text, string package)
        {
            foreach (var line in (text ?? "").Split('\n'))
            {
                var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == package && Regex.IsMatch(parts[0], "^[a-fA-F0-9]{64}$"))
                {
                    return parts[0];
                }
            }
            return null;
        }

        private static async Task<string> DownloadTextAsync(HttpClient http, string url, CancellationToken token)
        {
            using (var response = await http.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        internal static void Stop(Process process)
        {
            try { if (!process.HasExited) { process.Kill(); } } catch { }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
