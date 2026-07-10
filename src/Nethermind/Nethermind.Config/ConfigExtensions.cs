// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Nethermind.Core;

namespace Nethermind.Config;

public static class ConfigExtensions
{
    private static readonly ConcurrentDictionary<string, bool> PortOptions = new();

    public static string? GetCategoryName(Type type)
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
        PortOptions.Select(static kvp => kvp.Key).OrderByDescending(static x => x).ToArray();

    public static T GetDefaultValue<T>(this IConfig config, string propertyName)
    {
        Type type = config.GetType();
        Type interfaceType = type.GetInterface($"I{type.Name}")!;
        PropertyInfo propertyInfo = interfaceType.GetProperty(propertyName)!;
        ConfigItemAttribute attribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>()!;
        string? defaultValue = attribute.DefaultValue;
        return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFrom(defaultValue!)!;
    }

    /// <summary>
    /// Enumerates configuration properties whose current value differs from the
    /// implementation's default. Useful for surfacing on startup which knobs the
    /// operator has actually changed, rather than dumping every value.
    /// </summary>
    /// <remarks>
    /// Defaults come from a freshly constructed instance of the implementing
    /// type, so initializers and constructors are honoured exactly as production
    /// wiring would do (no parsing of the <see cref="ConfigItemAttribute.DefaultValue"/> string).
    /// Properties flagged <see cref="ConfigItemAttribute.IsSensitive"/> are skipped.
    /// </remarks>
    /// <param name="configProvider">The provider to query.</param>
    /// <param name="onConfigError">
    /// Optional callback invoked when a single config interface cannot be enumerated
    /// (provider lookup or fresh-default construction throws). Enumeration of the
    /// remaining interfaces continues regardless. If <c>null</c>, failures are silent.
    /// </param>
    public static IEnumerable<NonDefaultConfigValue>
        GetNonDefaultValues(this IConfigProvider configProvider, Action<Type, Exception>? onConfigError = null)
    {
        ArgumentNullException.ThrowIfNull(configProvider);

        foreach (Type configInterface in TypeDiscovery.FindNethermindBasedTypes(typeof(IConfig)))
        {
            if (!configInterface.IsInterface) continue;

            IConfig current;
            IConfig fresh;
            string? category;
            try
            {
                current = configProvider.GetConfig(configInterface);
                fresh = (IConfig)Activator.CreateInstance(current.GetType())!;
                category = GetCategoryName(configInterface);
            }
            catch (Exception e)
            {
                onConfigError?.Invoke(configInterface, e);
                continue;
            }

            foreach (PropertyInfo property in configInterface.GetProperties())
            {
                if (!property.CanRead) continue;
                if (property.GetCustomAttribute<ConfigItemAttribute>()?.IsSensitive == true) continue;

                object? actual;
                object? defaultValue;
                try
                {
                    actual = property.GetValue(current);
                    defaultValue = property.GetValue(fresh);
                }
                catch (Exception e)
                {
                    onConfigError?.Invoke(configInterface, e);
                    continue;
                }

                if (!StructuralComparisons.StructuralEqualityComparer.Equals(actual, defaultValue))
                {
                    yield return new NonDefaultConfigValue(category, property.Name, actual, defaultValue);
                }
            }
        }
    }
}

public readonly record struct NonDefaultConfigValue(string? Category, string Name, object? CurrentValue, object? DefaultValue);
