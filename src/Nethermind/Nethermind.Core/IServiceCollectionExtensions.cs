// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Core;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ForwardServiceAsSingleton<T>(this IServiceCollection configuration, IServiceProvider baseServiceProvider) where T : class
    {
        T? theService = baseServiceProvider.GetService<T>();
        if (theService != null)
        {
            configuration.AddSingleton(baseServiceProvider.GetRequiredService<T>());
        }
        else
        {
            // It could be that this is in a test where the service was not registered and any dependency will be
            // replaced anyway. While using a factory function like this seems like it would have the same behaviour
            // as getting it directly first, it has one critical difference. When the final IServiceProvider is
            // disposed, it would also call the Dispose function of the service as it assume that it created and
            // therefore owned the service.
            configuration.AddSingleton((sp) => baseServiceProvider.GetRequiredService<T>());
        }

        return configuration;
    }

    /// <summary>
    /// Add all properties as singleton. It get them ahead of time instead of lazily to prevent the final service provider
    /// from disposing it. To prevent a property from being included, use <see cref="SkipServiceCollectionAttribute"/>.
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddPropertiesFrom<T>(this IServiceCollection configuration, T source) where T : class
    {
        Type t = typeof(T);

        IEnumerable<PropertyInfo> properties = t
            .GetProperties(BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(p => p.GetCustomAttribute<SkipServiceCollectionAttribute>() == null);

        foreach (PropertyInfo propertyInfo in properties)
        {
            object? val = propertyInfo.GetValue(source);
            if (val != null)
            {
                configuration = configuration.AddSingleton(propertyInfo.PropertyType, val);
            }
        }

        return configuration;
    }
}

/// <summary>
/// Mark a property so that it is not picked up by `AddPropertiesFrom`.
/// </summary>
public class SkipServiceCollectionAttribute : Attribute
{
}
