using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn.Hosts
{
    /// <summary>
    /// 后期绑定辅助。Excel 与 WPS 表格的对象模型基本同构，但类型库不同，
    /// 引用任一方的 PIA 都会让另一方失效，因此对宿主对象一律用 IDispatch 反射调用。
    /// </summary>
    internal static class Com
    {
        private const BindingFlags GetFlags = BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public;
        private const BindingFlags SetFlags = BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public;
        private const BindingFlags CallFlags = BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public;

        /// <summary>
        /// 后期绑定调用必须使用 en-US 区域，不能用 InvariantCulture。
        ///
        /// Office 的 IDispatch 按 LCID 解析成员，只接受 en-US（1033）；
        /// InvariantCulture 的 LCID 是 0x7F，会被拒绝并抛出
        /// 0x80028018 TYPE_E_INVDATAREAD「格式太旧或是类型库无效」，
        /// 以及各种 0x800A03EC 通用失败。这个报错与实际原因毫无关联，
        /// 极易误导，故在此集中固定区域设置。
        /// </summary>
        private static readonly CultureInfo ComCulture = CultureInfo.GetCultureInfo("en-US");

        internal static object Get(object target, string name, params object[] args)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return target.GetType().InvokeMember(name, GetFlags, null, target, args, ComCulture);
        }

        internal static void Set(object target, string name, params object[] args)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            target.GetType().InvokeMember(name, SetFlags, null, target, args, ComCulture);
        }

        internal static object Call(object target, string name, params object[] args)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return target.GetType().InvokeMember(name, CallFlags, null, target, args, ComCulture);
        }

        /// <summary>取属性但不抛异常，用于宿主间存在差异的可选成员探测。</summary>
        internal static bool TryGet(object target, string name, out object value, params object[] args)
        {
            value = null;
            if (target == null)
            {
                return false;
            }

            try
            {
                value = Get(target, name, args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string GetString(object target, string name, string fallback = "")
        {
            return TryGet(target, name, out var value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : fallback;
        }

        /// <summary>
        /// 释放 COM 引用。宿主对象不及时释放会导致 Excel 进程无法退出，
        /// 所以所有中间对象都要显式回收。
        /// </summary>
        internal static void Release(object target)
        {
            try
            {
                if (target != null && Marshal.IsComObject(target))
                {
                    Marshal.ReleaseComObject(target);
                }
            }
            catch
            {
            }
        }
    }
}
