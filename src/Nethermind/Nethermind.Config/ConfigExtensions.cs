// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace Nethermind.Config;

public static class ConfigExtensions
{
    private static readonly ConcurrentDictionary<string, bool> PortOptions = new();

    public static string GetCategoryName(Type type)
    {
        if (type.IsAssignableTo(typeof(INoCategoryConfig)))
            return null;

        string categoryName = type.Name.RemoveEnd("Config");
        if (type.IsInterface) categoryName = categoryName.RemoveStart('I');
        return categoryName;
    }

    public static void AddPortOptionName(Type categoryType, string optionName) =>
        PortOptions.TryAdd(
            GetCategoryName(categoryType) is { } categoryName ? $"{categoryName}.{optionName}" : optionName,
            true
        );

    public static string[] GetPortOptionNames() =>
        PortOptions.Keys.OrderByDescending(static x => x).ToArray();

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
