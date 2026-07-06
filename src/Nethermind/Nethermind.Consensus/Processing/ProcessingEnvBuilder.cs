// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProcessingEnvBuilder"/>
public class ProcessingEnvBuilder : IProcessingEnvBuilder
{
    private readonly ILifetimeScope _parentScope;
    private readonly Action<ContainerBuilder>[] _configure;
    private readonly IDisposable[] _disposables;
    private readonly bool _worldStateConfigured;

    public ProcessingEnvBuilder(ILifetimeScope parentScope) : this(parentScope, [], [], false) { }

    private ProcessingEnvBuilder(
        ILifetimeScope parentScope,
        Action<ContainerBuilder>[] configure,
        IDisposable[] disposables,
        bool worldStateConfigured)
    {
        _parentScope = parentScope;
        _configure = configure;
        _disposables = disposables;
        _worldStateConfigured = worldStateConfigured;
    }

    public IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure) =>
        new ProcessingEnvBuilder(_parentScope, [.. _configure, configure], _disposables, _worldStateConfigured);

    public IProcessingEnvBuilder ThatDisposes(IDisposable disposable) =>
        new ProcessingEnvBuilder(_parentScope, _configure, [.. _disposables, disposable], _worldStateConfigured);

    public IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState) =>
        WithWorldStateConfigured(builder => builder.AddScoped<IWorldStateScopeProvider>(worldState));

    public IProcessingEnvBuilder WithWorldState(IWorldState worldState) =>
        WithWorldStateConfigured(builder => builder.AddScoped<IWorldState>(worldState));

    public IProcessingEnvBuilder WithOverridableEnv(IOverridableEnv env) =>
        WithWorldStateConfigured(builder => builder.AddModule(env)); // the env module supplies the (overridden) world state

    public IProcessingEnvBuilder WithOverridableEnv()
    {
        // Create and own an env from the world-state manager: the environment disposes both the env and
        // its opened (un-overridden) world-state scope when it is disposed.
        IOverridableEnv env = _parentScope.Resolve<IOverridableEnvFactory>().Create();
        IProcessingEnvBuilder result = env is IDisposable disposableEnv ? ThatDisposes(disposableEnv) : this; // disposed last
        return result
            .ThatDisposes(env.BuildAndOverride(null)) // disposed before the env
            .WithOverridableEnv(env);
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
        Configure(builder => builder.AddModule(_parentScope.Resolve<IBlockValidationModule[]>()));

    public TWrapper BuildAs<TWrapper>() where TWrapper : class =>
        ProcessingEnvWrapperFactory.Create<TWrapper>(BuildScope());

    public IOverridableEnvHandle<T> BuildAsOverridableEnv<T>()
    {
        ILifetimeScope scope = BuildScope();
        return new OverridableEnvHandle<T>(scope, scope.Resolve<IOverridableEnv<T>>());
    }

    private ILifetimeScope BuildScope()
    {
        if (!_worldStateConfigured)
            throw new InvalidOperationException(
                $"A world state must be specified with {nameof(WithWorldState)} before building a processing environment.");

        ILifetimeScope scope = _parentScope.BeginLifetimeScope(builder =>
        {
            foreach (Action<ContainerBuilder> configure in _configure) configure(builder);
        });

        // Give the scope ownership of the caller-declared resources so they are disposed with it, even
        // when no scope component ever resolves them.
        foreach (IDisposable disposable in _disposables)
            scope.Disposer.AddInstanceForDisposal(disposable);

        return scope;
    }

    private IProcessingEnvBuilder WithWorldStateConfigured(Action<ContainerBuilder> configure) =>
        new ProcessingEnvBuilder(_parentScope, [.. _configure, configure], _disposables, worldStateConfigured: true);

    private sealed class OverridableEnvHandle<T>(ILifetimeScope scope, IOverridableEnv<T> env) : IOverridableEnvHandle<T>
    {
        public Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null) =>
            env.BuildAndOverride(header, stateOverride, specOverride, blockOverride);

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
