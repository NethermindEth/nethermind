// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Reflection;

namespace Nethermind.Core.Extensions;

public static class MemberInfoExtensions
{
    public static T GetValue<T>(this MemberInfo memberInfo)
    {
        if (memberInfo is PropertyInfo p)
        {
            return (T)p.GetValue(null)!;
        }
        else if (memberInfo is FieldInfo f)
        {
            return (T)f.GetValue(null)!;
        }

        throw new UnreachableException("Should be use for field and property only");
    }

    public static Type GetMemberType(this MemberInfo memberInfo)
    {
        if (memberInfo is PropertyInfo property)
        {
            return property.PropertyType;
        }
        else if (memberInfo is FieldInfo field)
        {
            return field.FieldType;
        }
        else
        {
            throw new UnreachableException();
        }
    }
}
