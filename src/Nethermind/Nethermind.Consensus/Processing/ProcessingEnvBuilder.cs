// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProcessingEnvBuilder"/>
/// <remarks>
/// Every typed shortcut funnels into <see cref="Configure"/>, so <see cref="BuildAs{TWrapper}"/> simply
/// replays the accumulated actions against a fresh child of <paramref name="parentScope"/>. Instances
/// registered as owned (the default) are tracked and disposed with the scope, regardless of whether the
/// wrapper ever resolves them.
/// </remarks>
public class ProcessingEnvBuilder(ILifetimeScope parentScope) : IProcessingEnvBuilder
{
    private readonly List<Action<ContainerBuilder>> _configure = [];
    private readonly List<object> _ownedInstances = [];
    private bool _worldStateConfigured;

    public IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure)
    {
        _configure.Add(configure);
        return this;
    }

    public IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState, bool externallyOwned = false)
    {
        _worldStateConfigured = true;
        return AddInstance(worldState, externallyOwned);
    }

    public IProcessingEnvBuilder WithWorldState(IWorldState worldState, bool externallyOwned = false)
    {
        _worldStateConfigured = true;
        return AddInstance(worldState, externallyOwned);
    }

    public IProcessingEnvBuilder WithReplacedComponent<T>(T instance, bool externallyOwned = false) where T : class =>
        AddInstance(instance, externallyOwned);

    public IProcessingEnvBuilder WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull =>
        Configure(builder => builder.AddScoped<TService, TImpl>());

    public IProcessingEnvBuilder WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class =>
        Configure(builder => builder.AddScoped<T>(factory));

    public IProcessingEnvBuilder WithComponent<T>(T instance, bool externallyOwned = false) where T : class =>
        AddInstance(instance, externallyOwned);

    public TWrapper BuildAs<TWrapper>() where TWrapper : class
    {
        if (!_worldStateConfigured)
            throw new InvalidOperationException(
                $"A world state must be specified with {nameof(WithWorldState)} before building a processing environment.");

        ILifetimeScope scope = parentScope.BeginLifetimeScope(builder =>
        {
            foreach (Action<ContainerBuilder> configure in _configure) configure(builder);
        });

        // Registrations for provided instances are externally owned at the Autofac level, so give the
        // scope ownership of the ones the caller kept by adding them to its disposer directly — this
        // disposes them even when the wrapper never resolves them.
        foreach (object owned in _ownedInstances)
        {
            switch (owned)
            {
                case IAsyncDisposable asyncDisposable:
                    scope.Disposer.AddInstanceForAsyncDisposal(asyncDisposable);
                    break;
                case IDisposable disposable:
                    scope.Disposer.AddInstanceForDisposal(disposable);
                    break;
            }
        }

        return ProcessingEnvWrapperFactory.Create<TWrapper>(scope);
    }

    private IProcessingEnvBuilder AddInstance<T>(T instance, bool externallyOwned) where T : class
    {
        if (!externallyOwned) _ownedInstances.Add(instance);
        return Configure(builder => builder.AddScoped<T>(instance));
    }
}
