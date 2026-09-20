using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// WorkBuddy ACP 标准流。WPS 拒绝直接创建子进程时，由 Windows WMI 服务
    /// 在宿主外启动同一命令，并用仅当前用户可访问的命名管道接回标准流。
    /// </summary>
    internal sealed class WorkBuddyProcess : IDisposable
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _inputPipe;
        private readonly NamedPipeServerStream _outputPipe;
        private readonly IntPtr _jobHandle;
        private readonly int _brokerProcessId;
        private bool _disposed;

        private WorkBuddyProcess(Process process)
        {
            _process = process;
            Input = process.StandardInput.BaseStream;
            Output = process.StandardOutput;
            _ = DrainAsync(process.StandardError);
        }

        private WorkBuddyProcess(
            NamedPipeServerStream inputPipe,
            NamedPipeServerStream outputPipe,
            IntPtr jobHandle,
            int brokerProcessId)
        {
            _inputPipe = inputPipe;
            _outputPipe = outputPipe;
            _jobHandle = jobHandle;
            _brokerProcessId = brokerProcessId;
            Input = inputPipe;
            Output = new StreamReader(outputPipe, Encoding.UTF8, true, 4096, leaveOpen: true);
            IsBrokered = true;
        }

        internal Stream Input { get; }

        internal StreamReader Output { get; }

        internal bool IsBrokered { get; }

        internal int BrokerProcessId => _brokerProcessId;

        internal bool HasExited
        {
            get
            {
                if (_disposed) { return true; }
                if (_process != null)
                {
                    try { return _process.HasExited; }
                    catch { return true; }
                }
                if (_brokerProcessId <= 0) { return false; }
                try
                {
                    using (var broker = Process.GetProcessById(_brokerProcessId))
                    {
                        return broker.HasExited;
                    }
                }
                catch { return true; }
            }
        }

        internal static WorkBuddyProcess Start(WorkBuddyAcpPaths paths, string workingDirectory)
        {
            return Start(paths, workingDirectory, false);
        }

        internal static WorkBuddyProcess Start(
            WorkBuddyAcpPaths paths,
            string workingDirectory,
            bool forceBroker)
        {
            if (paths == null) { throw new ArgumentNullException(nameof(paths)); }
            if (!forceBroker)
            {
                try
                {
                    return StartDirect(paths, workingDirectory);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    Log.Warn("WorkBuddy 直接启动被宿主拒绝，改用 WMI 服务与本机命名管道。");
                }
            }

            return StartBrokered(paths, workingDirectory);
        }

        private static WorkBuddyProcess StartDirect(WorkBuddyAcpPaths paths, string workingDirectory)
        {
            var process = new Process { StartInfo = WorkBuddyRuntime.StartInfo(paths, workingDirectory) };
            try
            {
                if (!process.Start())
                {
                    throw new Win32Exception("WorkBuddy ACP 进程未能启动。");
                }
                return new WorkBuddyProcess(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        private static WorkBuddyProcess StartBrokered(WorkBuddyAcpPaths paths, string workingDirectory)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var inputName = "ChatSheet.WorkBuddy.in." + nonce;
            var outputName = "ChatSheet.WorkBuddy.out." + nonce;
            var gateName = "ChatSheet.WorkBuddy.gate." + nonce;
            var security = CurrentUserPipeSecurity();
            var input = CreatePipe(inputName, PipeDirection.Out, security);
            var output = CreatePipe(outputName, PipeDirection.In, security);
            var gate = CreatePipe(gateName, PipeDirection.Out, security);
            var jobHandle = IntPtr.Zero;
            uint processId = 0;
            var assignedToJob = false;
            try
            {
                var waitInput = input.WaitForConnectionAsync();
                var waitOutput = output.WaitForConnectionAsync();
                var waitGate = gate.WaitForConnectionAsync();
                var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var arguments = BuildShellArguments(paths, inputName, outputName, gateName);
                Log.Info("WorkBuddy ACP 代理：命名管道已创建，正在请求 WMI 服务启动。");
                jobHandle = CreateKillOnCloseJob();
                processId = StartWithWmi(command, arguments, workingDirectory);
                AssignToJob(jobHandle, (int)processId);
                assignedToJob = true;
                if (!waitGate.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("WMI 代理未能连接启动闸门。");
                }
                var release = Encoding.ASCII.GetBytes("go\r\n");
                gate.Write(release, 0, release.Length);
                gate.Flush();
                if (!Task.WaitAll(new Task[] { waitInput, waitOutput }, TimeSpan.FromSeconds(15)))
                {
                    Log.Error($"WorkBuddy ACP 代理连接超时：PID={processId} " +
                        $"输入管道={waitInput.IsCompleted} 输出管道={waitOutput.IsCompleted}", null);
                    throw new TimeoutException("WMI 代理未能连接 WorkBuddy ACP 命名管道。");
                }
                Log.Info($"WorkBuddy ACP 已通过 WMI 服务与本机命名管道启动：PID={processId}。");
                var result = new WorkBuddyProcess(input, output, jobHandle, (int)processId);
                jobHandle = IntPtr.Zero;
                return result;
            }
            catch
            {
                if (jobHandle != IntPtr.Zero)
                {
                    CloseHandle(jobHandle);
                    jobHandle = IntPtr.Zero;
                }
                if (processId != 0 && !assignedToJob) { TryKillProcess((int)processId); }
                input.Dispose();
                output.Dispose();
                throw;
            }
            finally
            {
                gate.Dispose();
            }
        }

        private static NamedPipeServerStream CreatePipe(
            string name,
            PipeDirection direction,
            PipeSecurity security)
        {
            return new NamedPipeServerStream(
                name,
                direction,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                8192,
                8192,
                security,
                HandleInheritability.None);
        }

        private static PipeSecurity CurrentUserPipeSecurity()
        {
            var identity = WindowsIdentity.GetCurrent();
            var user = identity?.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            var security = new PipeSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
            return security;
        }

        internal static string BuildShellArguments(
            WorkBuddyAcpPaths paths,
            string inputName,
            string outputName,
            string gateName)
        {
            var executable = string.IsNullOrWhiteSpace(paths.NodePath) ? paths.CliPath : paths.NodePath;
            var command = new StringBuilder();
            command.Append("set /p \"CHATSHEET_GATE=\" < \"\\\\.\\pipe\\")
                .Append(gateName)
                .Append("\"&&");
            if (!string.IsNullOrWhiteSpace(paths.ConfigDirectory))
            {
                var config = ValidateCommandValue(paths.ConfigDirectory);
                command.Append("set \"WORKBUDDY_CONFIG_DIR=").Append(config).Append("\"&&")
                    .Append("set \"CODEBUDDY_CONFIG_DIR=").Append(config).Append("\"&&")
                    .Append("set \"WORKBUDDY_DATA_FOLDER_NAME=")
                    .Append(ValidateCommandValue(Path.GetFileName(paths.ConfigDirectory.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))
                    .Append("\"&&");
            }

            var productConfig = WorkBuddyRuntime.DesktopProductConfig(paths);
            if (productConfig != null)
            {
                command.Append("set \"CODEBUDDY_HOST=workbuddy-desktop\"&&")
                    .Append("set \"ACC_PRODUCT_CONFIG_PATH=")
                    .Append(ValidateCommandValue(productConfig)).Append("\"&&");
            }

            command.Append(QuoteCommandPath(executable));
            if (!string.IsNullOrWhiteSpace(paths.NodePath))
            {
                command.Append(' ').Append(QuoteCommandPath(paths.CliPath));
            }
            command.Append(" --acp --no-session-persistence --tools \"\"")
                .Append(" < \"\\\\.\\pipe\\").Append(inputName)
                .Append("\" > \"\\\\.\\pipe\\").Append(outputName)
                .Append("\" 2>nul");
            return "/d /v:off /s /c \"" + command + "\"";
        }

        private static string QuoteCommandPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
            {
                throw new InvalidDataException("WorkBuddy 启动路径无效。");
            }
            return "\"" + value + "\"";
        }

        private static string ValidateCommandValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '"', '\r', '\n', '%' }) >= 0)
            {
                throw new InvalidDataException("WorkBuddy 配置目录包含不支持的命令字符。");
            }
            return value;
        }

        internal static int StartDetached(string fileName, string arguments, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(fileName)) { throw new ArgumentNullException(nameof(fileName)); }
            try
            {
                using (var process = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = NormalizeWorkingDirectory(fileName, workingDirectory),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                } })
                {
                    if (!process.Start()) { throw new Win32Exception("进程未能启动。"); }
                    return process.Id;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                Log.Warn("宿主拒绝启动外部进程，改由 WMI 服务启动。");
                return (int)StartWithWmi(fileName, arguments, workingDirectory);
            }
        }

        internal static bool HasProcessExited(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        internal static void StopDetached(int processId)
        {
            if (processId <= 0) { return; }
            TryKillProcess(processId);
        }

        private static uint StartWithWmi(string fileName, string arguments, string workingDirectory)
        {
            var commandLine = QuoteCommandPath(fileName);
            if (!string.IsNullOrWhiteSpace(arguments)) { commandLine += " " + arguments; }
            var timeout = TimeSpan.FromSeconds(10);
            Log.Info("WorkBuddy WMI 代理：正在连接本机进程服务。");

            var connection = new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Timeout = timeout,
            };
            var scope = new ManagementScope(@"\\.\root\cimv2", connection);
            scope.Connect();
            Log.Info("WorkBuddy WMI 代理：本机进程服务已连接，正在创建进程。");

            using (var processClass = new ManagementClass(
                scope,
                new ManagementPath("Win32_Process"),
                new ObjectGetOptions { Timeout = timeout }))
            using (var input = processClass.GetMethodParameters("Create"))
            {
                input["CommandLine"] = commandLine;
                input["CurrentDirectory"] = NormalizeWorkingDirectory(fileName, workingDirectory);
                using (var output = processClass.InvokeMethod(
                    "Create",
                    input,
                    new InvokeMethodOptions { Timeout = timeout }))
                {
                    var result = Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue, CultureInfo.InvariantCulture);
                    var processId = Convert.ToUInt32(output?["ProcessId"] ?? 0u, CultureInfo.InvariantCulture);
                    if (result != 0 || processId == 0)
                    {
                        throw new Win32Exception((int)result, $"WMI 创建进程失败，返回码 {result}。");
                    }
                    Log.Info($"WorkBuddy WMI 代理：进程已创建，PID={processId}。");
                    return processId;
                }
            }
        }

        private static string NormalizeWorkingDirectory(string fileName, string workingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                return workingDirectory;
            }
            return Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 WorkBuddy 进程作业。");
            }

            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation))))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error, "无法配置 WorkBuddy 进程作业。");
            }
            return handle;
        }

        private static void AssignToJob(IntPtr jobHandle, int processId)
        {
            using (var process = Process.GetProcessById(processId))
            {
                if (!AssignProcessToJobObject(jobHandle, process.Handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法约束 WorkBuddy 代理进程生命周期。");
                }
            }
            Log.Info($"WorkBuddy WMI 代理：PID={processId} 已加入宿主生命周期作业。");
        }

        private static void TryKillProcess(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.HasExited) { process.Kill(); }
                }
            }
            catch { }
        }

        private static async Task DrainAsync(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) != null) { }
            }
            catch { }
        }

        internal void Stop()
        {
            if (_disposed) { return; }
            _disposed = true;

            try { Input.Close(); } catch { }
            if (_jobHandle != IntPtr.Zero)
            {
                try { CloseHandle(_jobHandle); } catch { }
            }
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(1000);
                    }
                }
                catch { }
            }
            try { Output.Dispose(); } catch { }
            try { _inputPipe?.Dispose(); } catch { }
            try { _outputPipe?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Stop();
        }

        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
