// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Fluent builder for an isolated block-processing environment. It configures an Autofac child lifetime
/// scope and returns the resolved graph as <c>TWrapper</c> — a caller-defined, getter-only interface that
/// must implement <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/> and be disposed when the
/// environment is no longer needed (disposing it disposes the scope).
/// </summary>
/// <remarks>
/// Copy-on-mutate: each method returns a new builder, so a single injected instance can be reused — and
/// forked concurrently — to build independent environments.
/// </remarks>
public interface IProcessingEnvBuilder
{
    IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState);

    IProcessingEnvBuilder WithWorldState(IWorldState worldState);

    IProcessingEnvBuilder WithOverridableEnv(IOverridableEnv env);

    IProcessingEnvBuilder WithOverridableEnv();

    IProcessingEnvBuilder WithReplacedComponent<T>(T instance) where T : class;

    IProcessingEnvBuilder WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull;

    IProcessingEnvBuilder WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class;

    IProcessingEnvBuilder WithComponent<T>(T instance) where T : class;

    IProcessingEnvBuilder ThatDisposes(IDisposable disposable);

    IProcessingEnvBuilder WithBlockValidationConfiguration();

    IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure);

    TWrapper BuildAs<TWrapper>() where TWrapper : class;

    /// <summary>
    /// Builds the environment and returns its <see cref="IOverridableEnv{T}"/>; disposing the returned
    /// handle disposes the underlying scope. Use for the state-override RPC envs whose world state comes
    /// from <see cref="WithOverridableEnv(IOverridableEnv)"/>.
    /// </summary>
    IOverridableEnvHandle<T> BuildAsOverridableEnv<T>();
}

/// <summary>An <see cref="IOverridableEnv{T}"/> that owns and asynchronously disposes its environment scope.</summary>
public interface IOverridableEnvHandle<T> : IOverridableEnv<T>, IAsyncDisposable
{
}
