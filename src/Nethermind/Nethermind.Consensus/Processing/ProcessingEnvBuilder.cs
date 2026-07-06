// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using IDsl = Nethermind.Consensus.Processing.IProcessingEnvBuilder.IDsl;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProcessingEnvBuilder"/>
public class ProcessingEnvBuilder(ILifetimeScope parentScope) : IProcessingEnvBuilder
{
    public IDsl NewEnv() => new Dsl(parentScope);

    /// <inheritdoc cref="IProcessingEnvBuilder.IDsl"/>
    /// <remarks>
    /// Single-use and not thread-safe; obtain one from <see cref="NewEnv"/> per environment.
    /// <see cref="BuildAs{TWrapper}"/> replays the accumulated configuration against a fresh child of the
    /// parent scope.
    /// </remarks>
    private sealed class Dsl(ILifetimeScope parentScope) : IDsl
    {
        private readonly List<Action<ContainerBuilder>> _configure = [];
        private readonly List<IDisposable> _disposables = [];
        private bool _worldStateConfigured;

        public IDsl Configure(Action<ContainerBuilder> configure)
        {
            _configure.Add(configure);
            return this;
        }

        public IDsl ThatDisposes(IDisposable disposable)
        {
            _disposables.Add(disposable);
            return this;
        }

        public IDsl WithWorldState(IWorldStateScopeProvider worldState)
        {
            _worldStateConfigured = true;
            return Configure(builder => builder.AddScoped<IWorldStateScopeProvider>(worldState));
        }

        public IDsl WithWorldState(IWorldState worldState)
        {
            _worldStateConfigured = true;
            return Configure(builder => builder.AddScoped<IWorldState>(worldState));
        }

        public IDsl WithOverridableEnv(IOverridableEnv env)
        {
            _worldStateConfigured = true; // the env module supplies the (overridden) world state
            return Configure(builder => builder.AddModule(env));
        }

        public IDsl WithOverridableEnv()
        {
            // Create and own an env from the world-state manager: the environment disposes both the env
            // and its opened (un-overridden) world-state scope when it is disposed.
            IOverridableEnv env = parentScope.Resolve<IOverridableEnvFactory>().Create();
            if (env is IDisposable disposableEnv) ThatDisposes(disposableEnv); // added first → disposed last
            ThatDisposes(env.BuildAndOverride(null));                          // added last → disposed first
            return WithOverridableEnv(env);
        }

        public IDsl WithReplacedComponent<T>(T instance) where T : class =>
            Configure(builder => builder.AddScoped<T>(instance));

        public IDsl WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull =>
            Configure(builder => builder.AddScoped<TService, TImpl>());

        public IDsl WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class =>
            Configure(builder => builder.AddScoped<T>(factory));

        public IDsl WithComponent<T>(T instance) where T : class =>
            Configure(builder => builder.AddScoped<T>(instance));

        public IDsl WithBlockValidationConfiguration() =>
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
}
