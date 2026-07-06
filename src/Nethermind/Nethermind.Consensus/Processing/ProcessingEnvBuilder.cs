// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProcessingEnvBuilder"/>
public class ProcessingEnvBuilder : IProcessingEnvBuilder
{
    private readonly ILifetimeScope _parentScope;
    private readonly bool _mutable;
    private readonly List<Action<ContainerBuilder>> _configure = [];
    private readonly List<IDisposable> _disposables = [];
    private bool _worldStateConfigured;
    private bool _ownedByParent;

    public ProcessingEnvBuilder(ILifetimeScope parentScope) : this(parentScope, mutable: false) { }

    private ProcessingEnvBuilder(ILifetimeScope parentScope, bool mutable)
    {
        _parentScope = parentScope;
        _mutable = mutable;
    }

    // The DI-registered instance is immutable so it can be reused and forked concurrently; the first
    // configuring call forks a private mutable builder that subsequent calls accumulate into in place.
    private ProcessingEnvBuilder Mutable() => _mutable ? this : new ProcessingEnvBuilder(_parentScope, mutable: true);

    public IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure)
    {
        ProcessingEnvBuilder builder = Mutable();
        builder._configure.Add(configure);
        return builder;
    }

    public IProcessingEnvBuilder ThatDisposes(IDisposable disposable)
    {
        ProcessingEnvBuilder builder = Mutable();
        builder._disposables.Add(disposable);
        return builder;
    }

    public IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState) =>
        WithWorldStateConfigured(builder => builder.AddScoped<IWorldStateScopeProvider>(worldState));

    public IProcessingEnvBuilder WithWorldState(IWorldState worldState) =>
        WithWorldStateConfigured(builder => builder.AddScoped<IWorldState>(worldState));

    public IProcessingEnvBuilder WithOverridableEnv(IOverridableEnv env) =>
        WithWorldStateConfigured(builder => builder.AddModule(env)); // the env module supplies the (overridden) world state

    public IProcessingEnvBuilder WithOverridableEnv()
    {
        // Create and own an env from the world-state manager (the environment disposes it). Unlike the
        // parameterized overload this owns the env's disposal; the caller opens the world-state scope on
        // demand through the resolved IOverridableEnv<T> rather than it being built up front.
        IOverridableEnv env = _parentScope.Resolve<IOverridableEnvFactory>().Create();
        IProcessingEnvBuilder result = env is IDisposable disposableEnv ? ThatDisposes(disposableEnv) : this;
        return result.WithOverridableEnv(env);
    }

    public IProcessingEnvBuilder WithReplacedComponent<T>(T instance) where T : class =>
        Configure(builder => builder.AddScoped<T>(instance));

    public IProcessingEnvBuilder WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull =>
        Configure(builder => builder.AddScoped<TService, TImpl>());

    public IProcessingEnvBuilder WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class =>
        Configure(builder => builder.AddScoped<T>(factory));

    public IProcessingEnvBuilder WithComponent<T>(T instance) where T : class =>
        Configure(builder => builder.AddScoped<T>(instance));

    public IProcessingEnvBuilder WithComponent<T>() where T : notnull =>
        Configure(builder => builder.AddScoped<T>());

    public IProcessingEnvBuilder WithComponent<TService, TImpl>() where TImpl : TService where TService : notnull =>
        Configure(builder => builder.AddScoped<TService, TImpl>());

    public IProcessingEnvBuilder WithDecorator<TService, TDecorator>() where TService : class where TDecorator : TService =>
        Configure(builder => builder.AddDecorator<TService, TDecorator>());

    public IProcessingEnvBuilder WithBlockValidationConfiguration() =>
        Configure(builder => builder.AddModule(_parentScope.Resolve<IBlockValidationModule[]>()));

    public IProcessingEnvBuilder OwnedByParentLifetime()
    {
        ProcessingEnvBuilder builder = Mutable();
        builder._ownedByParent = true;
        return builder;
    }

    public TWrapper BuildAs<TWrapper>() where TWrapper : class
    {
        ILifetimeScope scope = BuildScope();

        // A type registered in the scope (via With*/Configure) is the caller's real component, so resolve it.
        // Otherwise TWrapper is a caller-defined interface to surface the scope through, so synthesize it.
        if (scope.IsRegistered<TWrapper>())
        {
            if (!_ownedByParent)
            {
                scope.Dispose();
                throw new InvalidOperationException(
                    $"A component resolved by {nameof(BuildAs)}<T> cannot dispose its scope; call {nameof(OwnedByParentLifetime)} so the parent lifetime owns it.");
            }
            return scope.Resolve<TWrapper>();
        }

        return ProcessingEnvWrapperFactory.Create<TWrapper>(scope, ownedExternally: _ownedByParent);
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

        // When owned by the parent lifetime, hand the scope to the parent's disposer so the caller never
        // disposes it and the wrapper need not be disposable.
        if (_ownedByParent)
            _parentScope.Disposer.AddInstanceForAsyncDisposal(scope);

        return scope;
    }

    private IProcessingEnvBuilder WithWorldStateConfigured(Action<ContainerBuilder> configure)
    {
        ProcessingEnvBuilder builder = Mutable();
        builder._configure.Add(configure);
        builder._worldStateConfigured = true;
        return builder;
    }
}
