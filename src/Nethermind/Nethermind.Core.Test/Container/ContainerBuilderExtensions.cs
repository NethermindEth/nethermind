// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Core.Test.Container;

public static class ContainerBuilderExtensions
{
    /// <summary>
    /// Use with caution.
    /// Uses <see cref="AddSingletonWithAccessToPreviousRegistration{T}"/> to create another registration that re-instantiate
    /// current <see cref="T"/> in an inner lifecycle that can be re-configured. The use of inner lifetime scope may
    /// result in unexpected (or expected) behaviour. Use with caution.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configurer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ContainerBuilder UpdateSingleton<T>(this ContainerBuilder builder, Action<ContainerBuilder> configurer) where T : class
    {
        return builder.AddSingletonWithAccessToPreviousRegistration<T>((ctx, factory) =>
        {
            ILifetimeScope parentLifetime = ctx.Resolve<ILifetimeScope>();
            ILifetimeScope innerLifetime = parentLifetime.BeginLifetimeScope(configurer);
            parentLifetime.Disposer.AddInstanceForAsyncDisposal(innerLifetime);
            return factory(innerLifetime);
        });
    }

    /// <summary>
    /// Create another singleton registration for T with access to a factory function for its exact previous registration. Useful as
    /// decorator, but need to optionally instantiate previous configuration or instantiate multiple of previous configuration.
    /// </summary>
    public static ContainerBuilder AddSingletonWithAccessToPreviousRegistration<T>(this ContainerBuilder builder, Func<IComponentContext, Func<IComponentContext, T>, T> decoratorFunc) where T : class
    {
        Guid regId = Guid.NewGuid();
        Service thisService = new TypedService(typeof(T));
        const string metadataName = "registrationId";

        builder
            .Register(ctx =>
            {
                IComponentRegistration? registrationBeforeThis = null;
                bool wasFound = false;
                foreach (IComponentRegistration componentRegistration in ctx.ComponentRegistry.RegistrationsFor(thisService))
                {
                    if (wasFound)
                    {
                        registrationBeforeThis = componentRegistration;
                        break;
                    }

                    if (componentRegistration.Metadata.TryGetValue(metadataName, out var value) &&
                        value is Guid guidValue && guidValue == regId)
                    {
                        wasFound = true;
                    }
                }

                if (!wasFound) throw new InvalidOperationException("Missing current registration");
                if (registrationBeforeThis is null) throw new InvalidOperationException("Missing previous registration");

                Func<IComponentContext, T> prevFactory = innerCtx => (T)innerCtx.ResolveComponent(
                    new ResolveRequest(
                        thisService,
                        new ServiceRegistration(registrationBeforeThis.ResolvePipeline, registrationBeforeThis),
                        []));

                return decoratorFunc(ctx, prevFactory);
            })
            .As(thisService)
            .WithMetadata(metadataName, regId)
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder WithGenesisPostProcessor(this ContainerBuilder builder,
        Action<Block, IWorldState> postProcessor)
    {
        return builder.AddScoped<IGenesisPostProcessor, IWorldState>((worldState) => new FunctionalGenesisPostProcessor(worldState, postProcessor));
    }

    public static ContainerBuilder WithGenesisPostProcessor(this ContainerBuilder builder,
        Action<Block, IWorldState, ISpecProvider> postProcessor)
    {
        return builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) => new FunctionalGenesisPostProcessor(worldState,
            (block, state) =>
            {
                postProcessor(block, state, specProvider);
            }));
    }
}
