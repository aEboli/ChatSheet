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

        /// <summary>
        /// 读取宿主的窗口状态，用于判定面板为什么开不出来。绝不抛异常。
        ///
        /// 关键在于分清两种读不到：
        /// Windows.Count 读失败说明宿主拒绝应答（忙或有模态对话框），要记成探测被拒；
        /// ProtectedViewWindows 读失败只说明宿主没有这个成员
        /// （该集合是 Office 2010 才有的，WPS 表格也未必提供），按 0 处理即可，
        /// 不能因此把状态判成宿主忙——那会把「没开工作簿」误报成「Excel 正忙」。
        /// </summary>
        internal static HostWindowState ReadWindowState(object application)
        {
            var state = new HostWindowState();

            if (application == null)
            {
                state.ProbeRejected = true;
                return state;
            }

            // 受保护的视图里的工作簿不属于 Workbooks，也不贡献 Window，
            // 所以这里数的是「能承载面板的窗口」，与只读无关。
            if (Com.TryGet(application, "Windows", out var windows) && windows != null)
            {
                try
                {
                    state.DocumentWindows = Convert.ToInt32(Com.Get(windows, "Count"));
                }
                catch (Exception ex)
                {
                    state.ProbeRejected = true;
                    Log.Warn("读取宿主窗口数失败：" + ex.Message);
                }
                finally
                {
                    Com.Release(windows);
                }
            }
            else
            {
                state.ProbeRejected = true;
            }

            if (Com.TryGet(application, "ProtectedViewWindows", out var protectedWindows) &&
                protectedWindows != null)
            {
                try
                {
                    state.ProtectedViewWindows = Convert.ToInt32(Com.Get(protectedWindows, "Count"));
                }
                catch (Exception ex)
                {
                    // 宿主没有这个成员时按 0 处理，不影响主判定。
                    Log.Warn("读取受保护的视图窗口数失败：" + ex.Message);
                }
                finally
                {
                    Com.Release(protectedWindows);
                }
            }

            return state;
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
