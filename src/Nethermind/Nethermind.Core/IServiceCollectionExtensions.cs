// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Features.AttributeFilters;
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

    public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder) where T : notnull
    {
        builder.RegisterType<T>()
            .As<T>()
            .WithAttributeFiltering()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder, T instance) where T : class
    {
        builder.RegisterInstance(instance)
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TImpl>(this ContainerBuilder builder) where TImpl : notnull where T : notnull
    {
        builder.RegisterType<TImpl>()
            .As<T>()
            .AsSelf()
            .WithAttributeFiltering()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder) where T : notnull
    {
        builder.RegisterType<T>()
            .As<T>()
            .AsSelf()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TImpl>(this ContainerBuilder builder) where TImpl : notnull where T : notnull
    {
        builder.RegisterType<TImpl>()
            .As<T>()
            .InstancePerLifetimeScope();

        return builder;
    }

    /// <summary>
    /// A convenient way of creating a service whose member can be configured indipendent of other instance of the same
    /// type (assuming the type is of lifetime scope). This is useful for same type with multiple configuration
    /// or a graph of multiple same type. The T is expected to be of a main container of sort that contains the
    /// main service of interest.
    /// Note: The T should dispose an injected ILifetimeScope as ILifetimeScope is not automatically disposed.
    /// TODO: Double check this behaviour
    /// </summary>
    public static ContainerBuilder RegisterNamedComponentInItsOwnLifetime<T>(this ContainerBuilder builder, string name, Action<ContainerBuilder> configurator) where T : notnull
    {
        builder.Register<ILifetimeScope, T>(ctx => ctx.BeginLifetimeScope(configurator).Resolve<T>())
            .Named<T>(name);

        return builder;
    }
}

/// <summary>
/// Mark a property so that it is not picked up by `AddPropertiesFrom`.
/// </summary>
public class SkipServiceCollectionAttribute : Attribute
{
}
