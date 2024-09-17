// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Core;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ForwardServiceAsSingleton<T>(this IServiceCollection configuration, IServiceProvider serviceProvider) where T : class
    {
        configuration.AddSingleton(serviceProvider.GetRequiredService<T>());
        return configuration;
    }
}
