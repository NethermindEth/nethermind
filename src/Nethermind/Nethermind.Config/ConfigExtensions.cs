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
using Nethermind.Core.Extensions;

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
    /// The default is taken from a freshly constructed instance of the
    /// implementing type, so initializers and constructors are honoured exactly
    /// as production wiring would do (no parsing of the <see cref="ConfigItemAttribute.DefaultValue"/> string).
    /// </remarks>
    /// <param name="configProvider">The provider to query.</param>
    /// <returns>
    /// Tuples of <c>(category, propertyName, currentValue, defaultValue)</c> for
    /// every property whose current value is not equal to its default.
    /// </returns>
    public static IEnumerable<(string Category, string Name, object? CurrentValue, object? DefaultValue)>
        GetNonDefaultValues(this IConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);

        foreach (Type configInterface in TypeDiscovery
                     .FindNethermindBasedTypes(typeof(IConfig))
                     .Where(static t => t.IsInterface))
        {
            Type? implementation = configInterface.GetDirectInterfaceImplementation();
            if (implementation is null) continue;

            IConfig current;
            try
            {
                current = configProvider.GetConfig(configInterface);
            }
            catch (ArgumentException)
            {
                continue;
            }

            IConfig fresh;
            try
            {
                fresh = (IConfig)Activator.CreateInstance(implementation)!;
            }
            catch (MissingMethodException)
            {
                continue;
            }

            string category = GetCategoryName(configInterface) ?? string.Empty;

            foreach (PropertyInfo property in configInterface.GetProperties())
            {
                if (!property.CanRead) continue;

                object? actual = property.GetValue(current);
                object? defaultValue = property.GetValue(fresh);

                if (!ValuesEqual(actual, defaultValue))
                {
                    yield return (category, property.Name, actual, defaultValue);
                }
            }
        }
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null) return b is null;
        if (b is null) return false;
        if (a is string || b is string) return a.Equals(b);
        if (a is IEnumerable enumerableA && b is IEnumerable enumerableB)
        {
            IEnumerator iteratorA = enumerableA.GetEnumerator();
            IEnumerator iteratorB = enumerableB.GetEnumerator();
            try
            {
                while (true)
                {
                    bool hasA = iteratorA.MoveNext();
                    bool hasB = iteratorB.MoveNext();
                    if (hasA != hasB) return false;
                    if (!hasA) return true;
                    if (!ValuesEqual(iteratorA.Current, iteratorB.Current)) return false;
                }
            }
            finally
            {
                (iteratorA as IDisposable)?.Dispose();
                (iteratorB as IDisposable)?.Dispose();
            }
        }
        return a.Equals(b);
    }
}
