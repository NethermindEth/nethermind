// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace Nethermind.Config;
public static class ConfigExtensions
{
    // TODO Check if all usages are thread safe
    private static readonly HashSet<string> PortOptions = new();

    public static string GetCategoryName(Type type)
    {
        string categoryName = type.Name.RemoveEnd("Config");
        if (type.IsInterface) categoryName = categoryName.RemoveStart('I');
        return categoryName;
    }

    public static void AddPortOptionName(Type categoryType, string optionName) =>
        PortOptions.Add($"{GetCategoryName(categoryType)}.{optionName}");

    public static IEnumerable<string> GetPortOptionNames() =>
        PortOptions;

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
