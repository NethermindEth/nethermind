// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>
/// A set of dsl to register components where the order matter. Internally it has an <see cref="OrderedComponents{T}"/>
/// and uses decorators to add the items. The order of invocation matter, but there is explicit method like <see cref="AddFirst{T}"/>
/// allowing component that should appear first to appear first.
/// </summary>
public static class OrderedComponentsContainerBuilderExtensions
{
    private const string OrderedMarkerPrefix = "Registered OrderedComponents For ";
    private const string CompositeMarkerPrefix = "Registered OrderedComponents Composite For ";

    public static ContainerBuilder AddLast<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) =>
        builder
            .EnsureOrderedComponents<T>()
            .AddDecorator<OrderedComponents<T>>((ctx, orderedComponents) =>
            {
                orderedComponents.AddLast(factory(ctx));
                return orderedComponents;
            });

    public static ContainerBuilder AddLast<T, TImpl>(this ContainerBuilder builder) where TImpl : class, T =>
        builder
            .AddLast<T>(ctx => ctx.Resolve<TImpl>())
            .Add<TImpl>();

    public static ContainerBuilder AddFirst<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) =>
        builder
            .EnsureOrderedComponents<T>()
            .AddDecorator<OrderedComponents<T>>((ctx, orderedComponents) =>
            {
                orderedComponents.AddFirst(factory(ctx));
                return orderedComponents;
            });

    public static ContainerBuilder AddFirst<T, TImpl>(this ContainerBuilder builder) where TImpl : class, T =>
        builder
            .AddFirst<T>(ctx => ctx.Resolve<TImpl>())
            .Add<TImpl>();

    /// <summary>
    /// Register a composite type that wraps the ordered components into a single <typeparamref name="T"/> service.
    /// Unlike <see cref="ContainerBuilderExtensions.AddComposite{T, TComposite}"/> which uses Autofac's
    /// <c>RegisterComposite</c> (collecting direct <typeparamref name="T"/> registrations),
    /// this method registers <typeparamref name="TComposite"/> via <c>RegisterType</c> so it receives
    /// <c>T[]</c> from <see cref="OrderedComponents{T}"/>. It also relaxes the ordered components
    /// safety check to allow this single <typeparamref name="T"/> registration.
    /// </summary>
    public static ContainerBuilder AddCompositeOrderedComponents<T, TComposite>(this ContainerBuilder builder) where T : class where TComposite : class, T
    {
        builder.EnsureOrderedComponents<T>();

        string compositeMarker = CompositeMarkerPrefix + typeof(T).Name;
        if (!builder.Properties.TryAdd(compositeMarker, null))
            return builder;

        builder.RegisterType<TComposite>()
            .As<T>()
            .AsSelf();

        return builder;
    }

    /// <summary>
    /// Clear all previously registered ordered components for <typeparamref name="T"/>.
    /// Useful when a plugin needs to disable all ordered policies (e.g., Hive).
    /// </summary>
    public static ContainerBuilder ClearOrderedComponents<T>(this ContainerBuilder builder) =>
        builder.AddDecorator<OrderedComponents<T>>((_, orderedComponents) =>
        {
            orderedComponents.Clear();
            return orderedComponents;
        });

    private static ContainerBuilder EnsureOrderedComponents<T>(this ContainerBuilder builder)
    {
        string registeredMarker = OrderedMarkerPrefix + typeof(T).Name;
        if (!builder.Properties.TryAdd(registeredMarker, null))
        {
            return builder;
        }

        // Prevent registering separately which has no explicit ordering
        builder.RegisterBuildCallback(scope =>
        {
            string decoratorMarker = CompositeMarkerPrefix + typeof(T).Name;
            bool hasDecorator = builder.Properties.ContainsKey(decoratorMarker);
            int registrationCount = scope.ComponentRegistry.ServiceRegistrationsFor(new TypedService(typeof(T))).Count();
            int expectedCount = hasDecorator ? 1 : 0;
            if (registrationCount > expectedCount)
            {
                throw new InvalidOperationException(
                    $"Service of type {typeof(T).Name} must only be registered with one of DSL in {nameof(OrderedComponentsContainerBuilderExtensions)}");
            }
        });

        // Not a singleton which allow it to work seamlessly with scoped lifetime with additional component
        builder.Add<OrderedComponents<T>>();

        builder
            .Register((ctx) => ctx.Resolve<OrderedComponents<T>>().Components)
            .As<IEnumerable<T>>()
            .Fixed();

        builder.Register((ctx) => ctx.Resolve<OrderedComponents<T>>().Components.ToArray())
            .As<IReadOnlyList<T>>()
            .As<T[]>()
            .Fixed();

        builder.Register((ctx) => ctx.Resolve<OrderedComponents<T>>().Components.ToList())
            .As<IList<T>>()
            .Fixed();

        return builder;
    }
}
