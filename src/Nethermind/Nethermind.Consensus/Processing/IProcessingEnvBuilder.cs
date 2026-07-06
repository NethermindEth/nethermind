// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Fluent builder for an isolated block-processing environment. It configures an Autofac child lifetime
/// scope and returns the resolved graph as <c>TWrapper</c> — a caller-defined, getter-only interface.
/// <c>TWrapper</c> must implement <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/> and be
/// disposed when the environment is no longer needed (disposing it disposes the scope) — unless the
/// environment is built with <see cref="OwnedByParentLifetime"/>, in which case <c>TWrapper</c> must
/// <b>not</b> implement them, because the parent lifetime owns disposal.
/// </summary>
/// <remarks>
/// The DI-registered instance is immutable and can be reused — and forked concurrently — to build
/// independent environments; the first configuring call forks a private mutable builder that the rest of
/// the fluent chain accumulates into.
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

    /// <summary>
    /// Ties the built environment's scope to the builder's parent lifetime scope: the scope is registered
    /// with the parent's disposer, so the caller never disposes the result. <c>TWrapper</c> must then
    /// <b>not</b> implement <see cref="IDisposable"/>/<see cref="IAsyncDisposable"/> — the parent owns disposal.
    /// </summary>
    /// <remarks>
    /// Warning: the environment (and its world state) then lives as long as the parent scope — which for a
    /// root-resolved builder is the whole application lifetime. Use only for long-lived environments, never
    /// per-request ones.
    /// </remarks>
    IProcessingEnvBuilder OwnedByParentLifetime();

    TWrapper BuildAs<TWrapper>() where TWrapper : class;
}
