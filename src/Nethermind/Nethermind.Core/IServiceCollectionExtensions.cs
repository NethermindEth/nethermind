// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    /// Used for initialization. Some property in INethermindApi may be null at the time of configuration,
    /// so this just make it slightly easier to use IServiceCollection.
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="service"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddSingletonIfNotNull<T>(this IServiceCollection configuration, T? service) where T : class
    {
        if (service != null)
            return configuration.AddSingleton<T>(service);
        return configuration;
    }
}
