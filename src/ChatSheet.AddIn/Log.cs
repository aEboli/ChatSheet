using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 文件日志。WPS 表格内无法挂调试器，加载项一旦在早期回调失败就只能靠日志定位，
    /// 所以日志本身必须绝不抛异常，也不能因为写不进磁盘而影响宿主。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly Lazy<string> LogPath = new Lazy<string>(ResolvePath, LazyThreadSafetyMode.ExecutionAndPublication);
        private const long MaxBytes = 2 * 1024 * 1024;

        internal static string CurrentPath
        {
            get
            {
                try
                {
                    return LogPath.Value;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// 解析日志路径。这里刻意避开可能抛异常的调用：
        /// 日志是宿主内唯一的诊断通道，一旦本方法抛出，Lazy 会缓存该异常，
        /// 导致此后所有写入静默失败，症状会伪装成「加载项根本没加载」。
        /// </summary>
        private static string ResolvePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatSheet",
                "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"addin-{SafeHostName()}.log");
        }

        /// <summary>
        /// 取宿主进程名。不使用 Process.MainModule：
        /// 它在部分宿主进程中会因权限或时序抛出异常。
        /// </summary>
        private static string SafeHostName()
        {
            try
            {
                var name = Process.GetCurrentProcess().ProcessName;
                if (!string.IsNullOrEmpty(name))
                {
                    return SanitizeFileName(name);
                }
            }
            catch
            {
            }

            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                {
                    return SanitizeFileName(Path.GetFileNameWithoutExtension(args[0]));
                }
            }
            catch
            {
            }

            return "host";
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return string.IsNullOrEmpty(value) ? "host" : value;
        }

        internal static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        internal static void Warn(string message)
        {
            Write("WARN ", message, null);
        }

        internal static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);

                if (ex != null)
                {
                    line.AppendLine().Append("        ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        line.AppendLine().Append(ex.StackTrace);
                    }

                    var inner = ex.InnerException;
                    var depth = 0;
                    while (inner != null && depth < 3)
                    {
                        line.AppendLine().Append("        内部异常: ").Append(inner.GetType().FullName)
                            .Append(": ").Append(inner.Message);
                        inner = inner.InnerException;
                        depth++;
                    }
                }

                var path = LogPath.Value;
                lock (Gate)
                {
                    Rotate(path);
                    // 写 BOM：日志要给人看，记事本和 PowerShell 在无 BOM 时按系统 ANSI
                    // 代码页解读 UTF-8，中文会显示成乱码。
                    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, line.ToString() + Environment.NewLine, encoding);
                    }
                    else
                    {
                        File.AppendAllText(path, line.ToString() + Environment.NewLine, encoding);
                    }
                }
            }
            catch
            {
                // 日志失败必须静默：宿主进程的稳定性优先于可观测性。
            }
        }

        private static void Rotate(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxBytes)
                {
                    return;
                }

                var archived = Path.ChangeExtension(path, ".1.log");
                if (File.Exists(archived))
                {
                    File.Delete(archived);
                }

                File.Move(path, archived);
            }
            catch
            {
            }
        }
    }
}
