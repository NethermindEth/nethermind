// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProcessingEnvBuilder"/>
/// <remarks>
/// Every typed shortcut funnels into <see cref="Configure"/>, so <see cref="BuildAs{TWrapper}"/> simply
/// replays the accumulated actions against a fresh child of <paramref name="parentScope"/>. Instances
/// passed to the <c>With*</c> methods are registered but never disposed by the builder; ownership is
/// declared explicitly with <see cref="ThatDisposes"/>.
/// </remarks>
public class ProcessingEnvBuilder(ILifetimeScope parentScope) : IProcessingEnvBuilder
{
    private readonly List<Action<ContainerBuilder>> _configure = [];
    private readonly List<IDisposable> _disposables = [];
    private bool _worldStateConfigured;

    public IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure)
    {
        _configure.Add(configure);
        return this;
    }

    public IProcessingEnvBuilder ThatDisposes(IDisposable disposable)
    {
        _disposables.Add(disposable);
        return this;
    }

    public IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState)
    {
        _worldStateConfigured = true;
        return Configure(builder => builder.AddScoped<IWorldStateScopeProvider>(worldState));
    }

    public IProcessingEnvBuilder WithWorldState(IWorldState worldState)
    {
        _worldStateConfigured = true;
        return Configure(builder => builder.AddScoped<IWorldState>(worldState));
    }

    public IProcessingEnvBuilder WithReplacedComponent<T>(T instance) where T : class =>
        Configure(builder => builder.AddScoped<T>(instance));

    public IProcessingEnvBuilder WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull =>
        Configure(builder => builder.AddScoped<TService, TImpl>());

    public IProcessingEnvBuilder WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class =>
        Configure(builder => builder.AddScoped<T>(factory));

    public IProcessingEnvBuilder WithComponent<T>(T instance) where T : class =>
        Configure(builder => builder.AddScoped<T>(instance));

    public IProcessingEnvBuilder WithBlockValidationConfiguration() =>
        Configure(builder => builder.AddModule(parentScope.Resolve<IBlockValidationModule[]>()));

    public TWrapper BuildAs<TWrapper>() where TWrapper : class
    {
        if (!_worldStateConfigured)
            throw new InvalidOperationException(
                $"A world state must be specified with {nameof(WithWorldState)} before building a processing environment.");

        ILifetimeScope scope = parentScope.BeginLifetimeScope(builder =>
        {
            foreach (Action<ContainerBuilder> configure in _configure) configure(builder);
        });

        // Give the scope ownership of the caller-declared resources so they are disposed with it, even
        // when no scope component ever resolves them.
        foreach (IDisposable disposable in _disposables)
            scope.Disposer.AddInstanceForDisposal(disposable);

        return ProcessingEnvWrapperFactory.Create<TWrapper>(scope);
    }
}
