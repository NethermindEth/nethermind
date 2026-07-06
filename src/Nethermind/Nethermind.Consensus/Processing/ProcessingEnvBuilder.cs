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
/// replays the accumulated actions against a fresh child of <paramref name="parentScope"/>.
/// </remarks>
public class ProcessingEnvBuilder(ILifetimeScope parentScope) : IProcessingEnvBuilder
{
    private readonly List<Action<ContainerBuilder>> _configure = [];
    private bool _worldStateConfigured;

    public IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure)
    {
        _configure.Add(configure);
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

    public TWrapper BuildAs<TWrapper>() where TWrapper : class, IDisposable
    {
        if (!_worldStateConfigured)
            throw new InvalidOperationException(
                $"A world state must be specified with {nameof(WithWorldState)} before building a processing environment.");

        return ProcessingEnvWrapperFactory.Create<TWrapper>(parentScope.BeginLifetimeScope(builder =>
        {
            foreach (Action<ContainerBuilder> configure in _configure) configure(builder);
        }));
    }
}
