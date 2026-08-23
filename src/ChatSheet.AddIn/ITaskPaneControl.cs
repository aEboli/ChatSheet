using System.Runtime.InteropServices;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 侧边栏控件对外的 COM 契约。显式声明接口而不用 AutoDual，
    /// 是为了避免把 UserControl 继承来的上百个成员全部暴露给宿主。
    /// </summary>
    [ComVisible(true)]
    [Guid("10309B08-6ED4-4C5C-A179-82ECB58A836B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface ITaskPaneControl
    {
    }
}
