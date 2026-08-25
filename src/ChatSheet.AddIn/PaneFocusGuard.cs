using System;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 把键盘焦点交回 Excel。
    ///
    /// 症状：在面板里打过字后点回表格，按 Ctrl+A 全选的是输入框里的文字，不是工作表。
    ///
    /// 成因：WebView2 的 Chromium 窗口属于另一个进程，被跨进程挂到本控件的窗口树下。
    /// 用户点面板时 Win32 焦点直接落到那个窗口，本控件（ActiveX 服务端）
    /// 从头到尾收不到 WM_SETFOCUS，于是 WinForms 的 ActiveX 层从未告诉 Excel
    /// 「窗格已 UI 激活」。Excel 因此认为焦点还在网格上，用户点单元格时
    /// 它不会再调 SetFocus——焦点就一直卡在 Chromium 窗口里，
    /// 鼠标点击照常生效（所以选中框会动），但按键全被面板吃掉。
    ///
    /// 已验证不可行的路线：面板报告取得焦点后调 WebView2 控件的 Focus()。
    /// ActiveX 宿主下 WinForms 不认为窗体处于激活状态（ActiveControl 为空），
    /// Focus() 直接返回 false；而它引起的 blur 又会触发下一次 focus，
    /// 形成每秒数百次的焦点循环，表现为 Excel 卡死。
    ///
    /// 因此改为在 Excel 的 UI 线程上装线程级鼠标钩子：
    /// 用户在面板之外按下鼠标时，若焦点仍在面板窗口树内，就先把焦点交给被点的窗口。
    /// 钩子只作用于本线程，Chromium 窗口在别的进程，面板内部的点击根本不会进来。
    /// 每次按下最多处理一次，不改变消息流向，也不会与宿主的焦点逻辑相互触发。
    /// </summary>
    internal sealed class PaneFocusGuard : IDisposable
    {
        private const int WH_MOUSE = 7;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;
        private const int WM_NCMBUTTONDOWN = 0x00A7;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEHOOKSTRUCT
        {
            public int PtX;
            public int PtY;
            public IntPtr Hwnd;
            public uint HitTestCode;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly IntPtr _paneHandle;

        /// <summary>委托必须由托管侧持有：只传给非托管钩子的话会被回收，回调时进程即崩。</summary>
        private readonly HookProc _proc;

        private IntPtr _hook;

        private PaneFocusGuard(IntPtr paneHandle)
        {
            _paneHandle = paneHandle;
            _proc = OnMouse;
        }

        /// <summary>
        /// 在当前线程装上钩子。必须在宿主的 UI 线程调用——
        /// 线程级钩子只能看到本线程窗口的消息，装错线程就完全不触发。
        /// </summary>
        internal static PaneFocusGuard Install(IntPtr paneHandle)
        {
            if (paneHandle == IntPtr.Zero)
            {
                Log.Warn("面板窗口句柄为空，焦点守卫未安装");
                return null;
            }

            var guard = new PaneFocusGuard(paneHandle);
            guard._hook = SetWindowsHookEx(WH_MOUSE, guard._proc, IntPtr.Zero, GetCurrentThreadId());

            if (guard._hook == IntPtr.Zero)
            {
                Log.Warn("安装焦点守卫失败，Win32 错误 " + Marshal.GetLastWin32Error());
                return null;
            }

            Log.Info("焦点守卫已安装");
            return guard;
        }

        /// <summary>
        /// 钩子回调。跑在宿主 UI 线程的每条鼠标消息上，因此必须极轻且绝不抛异常：
        /// 这里抛出会直接打断 Excel 的消息处理。
        /// </summary>
        private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && IsButtonDown((int)wParam))
                {
                    HandleButtonDown(lParam);
                }
            }
            catch
            {
                // 交回焦点只是体验优化，任何失败都不该影响宿主处理鼠标。
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private static bool IsButtonDown(int message)
        {
            switch (message)
            {
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_NCLBUTTONDOWN:
                case WM_NCRBUTTONDOWN:
                case WM_NCMBUTTONDOWN:
                    return true;
                default:
                    return false;
            }
        }

        private void HandleButtonDown(IntPtr lParam)
        {
            // 先查焦点：绝大多数点击都发生在焦点不在面板时，此时立刻返回，
            // 不去读结构体也不做窗口树判断。
            var focus = GetFocus();
            if (focus == IntPtr.Zero || !IsInPane(focus))
            {
                return;
            }

            var info = (MOUSEHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MOUSEHOOKSTRUCT));
            var target = info.Hwnd;
            if (target == IntPtr.Zero || IsInPane(target))
            {
                return;
            }

            // 焦点在面板里，而用户按下的是 Excel 自己的窗口：把焦点交给它。
            // 交给「被点的窗口」而不是固定交给网格，是为了让编辑栏、工作表标签等
            // 各自拿到本该属于它们的焦点；点击本身照常派发，宿主随后按正常路径处理。
            SetFocus(target);
        }

        private bool IsInPane(IntPtr hwnd)
        {
            return hwnd == _paneHandle || IsChild(_paneHandle, hwnd);
        }

        public void Dispose()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    Log.Info("焦点守卫已卸载");
                }
            }
            catch
            {
            }
            finally
            {
                _hook = IntPtr.Zero;
            }
        }
    }
}
