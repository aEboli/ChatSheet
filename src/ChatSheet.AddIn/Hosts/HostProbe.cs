using System;
using System.Diagnostics;
using System.IO;

namespace ChatSheet.AddIn.Hosts
{
    internal enum HostKind
    {
        Unknown = 0,
        MicrosoftExcel = 1,
        WpsSpreadsheets = 2,
    }

    /// <summary>
    /// 宿主识别。两个宿主的对象模型同构，但少数成员和默认行为有差异，
    /// 需要先判定宿主种类才能选择兼容分支。
    /// </summary>
    internal static class HostProbe
    {
        internal static HostKind Detect(object application)
        {
            // 优先看进程名：比对象模型属性更稳，且在对象模型尚未就绪时也可用。
            var module = CurrentProcessName();
            if (module.Equals("et", StringComparison.OrdinalIgnoreCase) ||
                module.Equals("wps", StringComparison.OrdinalIgnoreCase) ||
                module.Equals("wpscloudsvr", StringComparison.OrdinalIgnoreCase))
            {
                return HostKind.WpsSpreadsheets;
            }

            if (module.Equals("excel", StringComparison.OrdinalIgnoreCase))
            {
                return HostKind.MicrosoftExcel;
            }

            // 退化路径：进程名不认识时看 Application.Name。
            var name = application == null ? string.Empty : Com.GetString(application, "Name");
            if (name.IndexOf("WPS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Kingsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("表格", StringComparison.Ordinal) >= 0)
            {
                return HostKind.WpsSpreadsheets;
            }

            if (name.IndexOf("Excel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return HostKind.MicrosoftExcel;
            }

            return HostKind.Unknown;
        }

        internal static string CurrentProcessName()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var path = process.MainModule?.FileName;
                    return string.IsNullOrEmpty(path)
                        ? process.ProcessName
                        : Path.GetFileNameWithoutExtension(path);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>供日志使用的宿主描述，绝不抛异常。</summary>
        internal static string DescribeSafely(object application)
        {
            try
            {
                var kind = Detect(application);
                var name = application == null ? "<null>" : Com.GetString(application, "Name", "<无 Name>");
                var version = application == null ? string.Empty : Com.GetString(application, "Version");
                var build = application == null ? string.Empty : Com.GetString(application, "Build");
                return $"{kind}（进程={CurrentProcessName()}.exe Name={name} Version={version} Build={build}）";
            }
            catch (Exception ex)
            {
                return "<宿主识别失败: " + ex.Message + ">";
            }
        }

        internal static string DisplayName(HostKind kind)
        {
            switch (kind)
            {
                case HostKind.MicrosoftExcel:
                    return "Microsoft Excel";
                case HostKind.WpsSpreadsheets:
                    return "WPS 表格";
                default:
                    return "未知宿主";
            }
        }
    }
}
