// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Reflection;

namespace Nethermind.Core.Extensions;

public static class MemberInfoExtensions
{
    public static T GetValue<T>(this MemberInfo memberInfo) =>
        memberInfo switch
        {
            PropertyInfo p => (T)p.GetValue(null)!,
            FieldInfo f => (T)f.GetValue(null)!,
            _ => throw new NotSupportedException("Should be use for field and property only")
        };

    public static Type GetMemberType(this MemberInfo memberInfo) =>
        memberInfo switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException("Should be use for field and property only")
        };

    public static void SetValue(this MemberInfo memberInfo, object value)
    {
        switch (memberInfo)
        {
            case PropertyInfo p:
                p.SetValue(null, value);
                break;
            case FieldInfo f:
                f.SetValue(null, value);
                break;
            default:
                throw new UnreachableException();
        }
    }
}
