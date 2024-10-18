// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Features.AttributeFilters;

namespace Nethermind.Core;

public static class ContainerBuilderExtensions
{
    /// <summary>
    /// Add all properties as singleton. It get them ahead of time instead of lazily to prevent the final service provider
    /// from disposing it. To prevent a property from being included, use <see cref="SkipServiceCollectionAttribute"/>.
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ContainerBuilder AddPropertiesFrom<T>(this ContainerBuilder configuration, T source) where T : class
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
                configuration.RegisterInstance(val).As(propertyInfo.PropertyType);
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

    public static ContainerBuilder AddKeyedSingleton<T>(this ContainerBuilder builder, string key, T instance) where T : class
    {
        builder.RegisterInstance(instance)
            .Named<T>(key)
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder) where T : notnull
    {
        builder.RegisterType<T>()
            .As<T>()
            .AsSelf()
            .WithAttributeFiltering()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TImpl>(this ContainerBuilder builder) where TImpl : notnull where T : notnull
    {
        builder.RegisterType<TImpl>()
            .As<T>()
            .WithAttributeFiltering()
            .InstancePerLifetimeScope();

        return builder;
    }

    /// <summary>
    /// A convenient way of creating a service whose member can be configured indipendent of other instance of the same
    /// type (assuming the type is of lifetime scope). This is useful for same type with multiple configuration
    /// or a graph of multiple same type. The T is expected to be of a main container of sort that contains the
    /// main service of interest.
    /// Note: The T should dispose an injected ILifetimeScope on dispose as ILifetimeScope is not automatically disposed
    /// when parent scope is disposed.
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
