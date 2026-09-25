using System;
using System.Globalization;
using System.Reflection;

namespace ChatWord.AddIn
{
    public sealed partial class WordComAddIn : IReflect
    {
        private static readonly Type Self = typeof(WordComAddIn);

        FieldInfo IReflect.GetField(string name, BindingFlags flags) => Self.GetField(name, flags);
        FieldInfo[] IReflect.GetFields(BindingFlags flags) => Self.GetFields(flags);
        MemberInfo[] IReflect.GetMember(string name, BindingFlags flags) => Self.GetMember(name, flags);
        MemberInfo[] IReflect.GetMembers(BindingFlags flags) => Self.GetMembers(flags);
        MethodInfo IReflect.GetMethod(string name, BindingFlags flags) => Self.GetMethod(name, flags);
        MethodInfo IReflect.GetMethod(string name, BindingFlags flags, Binder binder, Type[] types, ParameterModifier[] modifiers)
            => Self.GetMethod(name, flags, binder, types, modifiers);
        MethodInfo[] IReflect.GetMethods(BindingFlags flags) => Self.GetMethods(flags);
        PropertyInfo[] IReflect.GetProperties(BindingFlags flags) => Self.GetProperties(flags);
        PropertyInfo IReflect.GetProperty(string name, BindingFlags flags) => Self.GetProperty(name, flags);
        PropertyInfo IReflect.GetProperty(string name, BindingFlags flags, Binder binder, Type type, Type[] types, ParameterModifier[] modifiers)
            => Self.GetProperty(name, flags, binder, type, types, modifiers);
        Type IReflect.UnderlyingSystemType => Self;

        object IReflect.InvokeMember(string name, BindingFlags flags, Binder binder, object target,
            object[] args, ParameterModifier[] modifiers, CultureInfo culture, string[] namedParameters)
        {
            try { return Self.InvokeMember(name, flags, binder, target ?? this, args, modifiers, culture, namedParameters); }
            catch (TargetInvocationException ex)
            {
                ChatSheet.AddIn.Log.Error("Word Ribbon 回调失败：" + name, ex.InnerException ?? ex);
                return null;
            }
            catch (Exception ex)
            {
                ChatSheet.AddIn.Log.Error("Word Ribbon 分派失败：" + name, ex);
                return null;
            }
        }
    }
}
