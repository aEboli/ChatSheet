using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn
{
    // 功能区回调的分派实现。
    //
    // 背景：功能区 XML 里的 onAction / getPressed / onLoad 等回调，宿主是按“方法名”
    // 经 IDispatch::GetIDsOfNames + Invoke 调用的。本类使用 ClassInterfaceType.None
    // （这是必须的：改成 AutoDual 会与 IDTExtensibility2 的 DispId 冲突），
    // 因此没有自动生成的类接口可供按名解析，回调会全部失效。
    //
    // 解法：实现 IReflect 自行接管名称解析与调用。CLR 在类实现 IReflect 时，
    // 会把 IDispatch 的成员解析委托给它，这是受支持的公开行为。
    // 同时把未知成员记入日志——功能区 XML 里的回调名写错时，
    // 否则只会表现为“按钮点了没反应”，极难定位。
    public sealed partial class ComAddIn : IReflect
    {
        private static readonly Type Self = typeof(ComAddIn);

        FieldInfo IReflect.GetField(string name, BindingFlags bindingAttr)
        {
            return Self.GetField(name, bindingAttr);
        }

        FieldInfo[] IReflect.GetFields(BindingFlags bindingAttr)
        {
            return Self.GetFields(bindingAttr);
        }

        MemberInfo[] IReflect.GetMember(string name, BindingFlags bindingAttr)
        {
            return Self.GetMember(name, bindingAttr);
        }

        MemberInfo[] IReflect.GetMembers(BindingFlags bindingAttr)
        {
            return Self.GetMembers(bindingAttr);
        }

        MethodInfo IReflect.GetMethod(string name, BindingFlags bindingAttr)
        {
            return Self.GetMethod(name, bindingAttr);
        }

        MethodInfo IReflect.GetMethod(
            string name,
            BindingFlags bindingAttr,
            Binder binder,
            Type[] types,
            ParameterModifier[] modifiers)
        {
            return Self.GetMethod(name, bindingAttr, binder, types, modifiers);
        }

        MethodInfo[] IReflect.GetMethods(BindingFlags bindingAttr)
        {
            return Self.GetMethods(bindingAttr);
        }

        PropertyInfo[] IReflect.GetProperties(BindingFlags bindingAttr)
        {
            return Self.GetProperties(bindingAttr);
        }

        PropertyInfo IReflect.GetProperty(string name, BindingFlags bindingAttr)
        {
            return Self.GetProperty(name, bindingAttr);
        }

        PropertyInfo IReflect.GetProperty(
            string name,
            BindingFlags bindingAttr,
            Binder binder,
            Type returnType,
            Type[] types,
            ParameterModifier[] modifiers)
        {
            return Self.GetProperty(name, bindingAttr, binder, returnType, types, modifiers);
        }

        Type IReflect.UnderlyingSystemType => Self;

        object IReflect.InvokeMember(
            string name,
            BindingFlags invokeAttr,
            Binder binder,
            object target,
            object[] args,
            ParameterModifier[] modifiers,
            CultureInfo culture,
            string[] namedParameters)
        {
            try
            {
                return Self.InvokeMember(
                    name,
                    invokeAttr,
                    binder,
                    target ?? this,
                    args,
                    modifiers,
                    culture,
                    namedParameters);
            }
            catch (MissingMemberException)
            {
                // 功能区 XML 的回调名与实现不一致时走到这里。
                // 必须记录：否则症状只是“点击无反应”，无从排查。
                Log.Error($"功能区回调 {name} 未找到对应实现，请检查 Resources\\Ribbon.xml", null);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                // 回调内部抛出的异常不能继续向宿主传播，否则会触发禁用加载项的提示。
                Log.Error($"功能区回调 {name} 执行失败", ex.InnerException ?? ex);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"功能区回调 {name} 分派失败", ex);
                return null;
            }
        }
    }
}
