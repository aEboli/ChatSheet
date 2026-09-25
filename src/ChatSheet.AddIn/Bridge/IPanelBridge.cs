using System;
using Microsoft.Web.WebView2.Core;

namespace ChatSheet.AddIn.Bridge
{
    /// <summary>宿主面板桥的最小生命周期与推送接口。</summary>
    internal interface IPanelBridge : IDisposable
    {
        Func<int, int, double, int> WidthAdjuster { get; set; }
        Func<int> WidthPersister { get; set; }
        Func<string, bool> ThemeApplier { get; set; }
        void Start();
        void PostNavigate(string route);
        void PostRaw(object message);
    }
}
