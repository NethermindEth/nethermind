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
    public static ContainerBuilder AddLast<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) =>
        builder
            .EnsureOrderedComponents<T>()
            .AddDecorator<OrderedComponents<T>>((ctx, orderedComponents) =>
            {
                orderedComponents.AddLast(factory(ctx));
                return orderedComponents;
            });

    public static ContainerBuilder AddFirst<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) =>
        builder
            .EnsureOrderedComponents<T>()
            .AddDecorator<OrderedComponents<T>>((ctx, orderedComponents) =>
            {
                orderedComponents.AddFirst(factory(ctx));
                return orderedComponents;
            });

    private static ContainerBuilder EnsureOrderedComponents<T>(this ContainerBuilder builder)
    {
        string registeredMarker = $"Registerd OrderedComponents For {typeof(T).Name}";
        if (!builder.Properties.TryAdd(registeredMarker, null))
        {
            return builder;
        }

        // Prevent registering separately which has no explicit ordering
        builder.RegisterBuildCallback(scope =>
        {
            if (scope.ComponentRegistry.ServiceRegistrationsFor(new TypedService(typeof(T))).Any())
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
