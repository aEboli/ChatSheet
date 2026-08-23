using System;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn.Interop
{
    // 手写 Office 扩展性接口声明，不引用 Office / Extensibility PIA。
    //
    // 为什么手写：PIA 与 Office 版本绑定，且 WPS 表格不提供 PIA；
    // 宿主只按 IID 查询接口，手写声明对 Excel 与 WPS 表格通用，
    // 构建机也不需要安装 Office 开发组件。
    //
    // 声明必须与官方 PIA 逐项一致，以下签名从本机 GAC 中的
    // Extensibility 7.0.3300.0 与 office 15.0.0.0 反射导出后对齐：
    //
    // 1) 不要标注 InterfaceTypeAttribute。这些接口是 dual
    //    （TypeLibType 4160/4288 含 FDUAL），默认值正是 InterfaceIsDual。
    //    若误标成 InterfaceIsIDispatch，vtable 中不会有方法槽位，
    //    宿主按 dual 走 vtable 调用会失败：表现为对象构造成功、
    //    但 OnConnection 永远进不去，Excel 随后弹出
    //    「加载项出现问题，是否禁用」对话框。
    //
    // 2) custom 参数只有 In 标记，不是 In/Out。
    //
    // 3) 参数的 MarshalAs 必须与 PIA 完全一致，这是最容易漏掉且后果最隐蔽的一条：
    //    Application / AddInInst 为 UnmanagedType.IDispatch(26)，
    //    custom 为 UnmanagedType.SafeArray(29)。
    //    custom 实际是 SAFEARRAY(VARIANT)，缺少 SafeArray 标注时参数封送会失败，
    //    宿主调用直接落空——对象能构造，但 OnConnection 的方法体永远进不去，
    //    宿主随后把 LoadBehavior 改成 2 并禁用加载项。

    public enum ext_ConnectMode
    {
        ext_cm_AfterStartup = 0,
        ext_cm_Startup = 1,
        ext_cm_External = 2,
        ext_cm_CommandLine = 3,
        ext_cm_Solution = 4,
        ext_cm_UISetup = 5,
    }

    public enum ext_DisconnectMode
    {
        ext_dm_HostShutdown = 0,
        ext_dm_UserClosed = 1,
        ext_dm_UISetupComplete = 2,
        ext_dm_SolutionClosed = 3,
    }

    /// <summary>加载项入口接口。Excel 由 msaddndr.dll 提供，WPS 表格由 ksaddndr.dll 提供。</summary>
    [ComImport]
    [Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FDispatchable)]
    public interface IDTExtensibility2
    {
        [DispId(1)]
        void OnConnection(
            [In, MarshalAs(UnmanagedType.IDispatch)] object Application,
            [In] ext_ConnectMode ConnectMode,
            [In, MarshalAs(UnmanagedType.IDispatch)] object AddInInst,
            [In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(2)]
        void OnDisconnection(
            [In] ext_DisconnectMode RemoveMode,
            [In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(3)]
        void OnAddInsUpdate([In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(4)]
        void OnStartupComplete([In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(5)]
        void OnBeginShutdown([In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);
    }

    /// <summary>
    /// 侧边栏工厂。返回值声明为 object 并以后期绑定访问，
    /// 避免引入整个 CustomTaskPane 类型定义，也规避两个宿主类型库的差异。
    /// </summary>
    [ComImport]
    [Guid("000C033D-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FNonExtensible | TypeLibTypeFlags.FDispatchable)]
    public interface ICTPFactory
    {
        [DispId(1)]
        [return: MarshalAs(UnmanagedType.Interface)]
        object CreateCTP([In] string CTPAxID, [In] string CTPTitle, [In, Optional] object CTPParentWindow);
    }

    /// <summary>宿主在侧边栏工厂就绪时回调。</summary>
    [ComImport]
    [Guid("000C033E-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FNonExtensible | TypeLibTypeFlags.FDispatchable)]
    public interface ICustomTaskPaneConsumer
    {
        [DispId(1)]
        void CTPFactoryAvailable([In] ICTPFactory CTPFactoryInst);
    }

    /// <summary>功能区扩展，宿主启动时索取 customUI XML。</summary>
    [ComImport]
    [Guid("000C0396-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FDispatchable)]
    public interface IRibbonExtensibility
    {
        [DispId(1)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetCustomUI([In] string RibbonID);
    }

    /// <summary>功能区回调收到的控件对象。</summary>
    [ComImport]
    [Guid("000C0395-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FNonExtensible | TypeLibTypeFlags.FDispatchable)]
    public interface IRibbonControl
    {
        [DispId(0)]
        string Id { get; }

        [DispId(1)]
        object Context { get; }

        [DispId(2)]
        string Tag { get; }
    }
}
