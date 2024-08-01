// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Reflection;

namespace Nethermind.Config;
public static class ConfigExtensions
{
    public static T GetDefaultValue<T>(this IConfig config, string propertyName)
    {
        Type type = config.GetType();
        Type interfaceType = type.GetInterface($"I{type.Name}");
        PropertyInfo propertyInfo = interfaceType.GetProperty(propertyName);
        ConfigItemAttribute attribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
        string defaultValue = attribute.DefaultValue;
        return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFrom(defaultValue);
    }
}
