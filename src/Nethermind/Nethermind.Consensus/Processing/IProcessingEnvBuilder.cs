// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Fluent builder for an isolated block-processing environment. It configures an Autofac child lifetime
/// scope (overriding the world state and/or individual components) and exposes the resolved graph through
/// <see cref="BuildAs{TWrapper}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TWrapper</c> must be a simple, getter-only interface (read-only properties, no methods) that the
/// caller defines, and it must implement <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/>.
/// The returned wrapper owns the environment's child scope, so it must be disposed once the environment
/// is no longer needed — disposing it disposes the scope and everything it owns.
/// </para>
/// <para>
/// A builder is single-use and not thread-safe: accumulate the configuration with the <c>With*</c> /
/// <see cref="Configure"/> methods (a world state is mandatory), then call <see cref="BuildAs{TWrapper}"/>
/// once. Resolve a fresh builder (or inject <see cref="Func{IProcessingEnvBuilder}"/>) for each
/// environment. Because the components come from a real child scope, all plugin registrations, decorators
/// and composites in the parent container are honoured — unlike a hand-built processing stack.
/// </para>
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
}
