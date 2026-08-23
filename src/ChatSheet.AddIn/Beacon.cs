using System;
using System.Globalization;
using Microsoft.Win32;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 加载信标。把关键生命周期事件写进 HKCU，作为不依赖文件系统的第二诊断通道。
    ///
    /// 存在的理由：宿主内无法附加调试器，若文件日志本身出问题（目录不可写、
    /// 路径解析抛异常等），「加载项未加载」与「已加载但日志失效」这两种情况
    /// 从外部看完全一样。注册表写入路径更短、失败面更小，可以区分二者。
    /// </summary>
    internal static class Beacon
    {
        private const string KeyPath = @"Software\ChatSheet\Diagnostics";

        internal static void Mark(string stage, string detail = null)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null)
                    {
                        return;
                    }

                    var host = SafeProcessName();
                    var value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(detail))
                    {
                        value += " | " + detail;
                    }

                    key.SetValue($"{host}:{stage}", value, RegistryValueKind.String);
                }
            }
            catch
            {
                // 诊断通道本身绝不能影响宿主。
            }
        }

        private static string SafeProcessName()
        {
            try
            {
                var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                return string.IsNullOrEmpty(name) ? "host" : name;
            }
            catch
            {
                return "host";
            }
        }
    }
}
