namespace ChatSheet.AddIn
{
    /// <summary>
    /// COM 标识常量。这些值会写进注册表，一旦发布就不能再改，
    /// 否则旧注册项会变成指向不存在类型的孤儿项。
    /// scripts/install.ps1 必须与这里保持一致。
    /// </summary>
    internal static class ComIds
    {
        /// <summary>加载项入口类 CLSID。</summary>
        internal const string AddInClsid = "DC0DBDFD-88B8-4071-9174-39C2627813C8";

        /// <summary>加载项 ProgID，宿主用它在 Office 注册表路径下发现加载项。</summary>
        internal const string AddInProgId = "ChatSheet.AddIn";

        /// <summary>侧边栏宿主控件 CLSID。</summary>
        internal const string TaskPaneClsid = "0417A068-632B-4CAD-9390-3479277B03CB";

        /// <summary>
        /// 侧边栏控件 ProgID。ICTPFactory.CreateCTP 按 ProgID 实例化控件，
        /// 所以该控件必须注册为 ActiveX 控件（CLSID 下带 Control 子键）。
        /// </summary>
        internal const string TaskPaneProgId = "ChatSheet.TaskPane";

        /// <summary>面板标题，显示在宿主窗格标题栏。</summary>
        internal const string PaneTitle = "ChatSheet";
    }
}
