// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Fluent builder for an isolated block-processing environment. It configures an Autofac child
/// lifetime scope (overriding the world state and/or individual components) and exposes the resolved
/// graph through a caller-defined wrapper interface produced by <see cref="BuildAs{TWrapper}"/>.
/// </summary>
/// <remarks>
/// A builder is single-use and not thread-safe: accumulate the configuration with the <c>With*</c> /
/// <see cref="Configure"/> methods, then call <see cref="BuildAs{TWrapper}"/> once. Resolve a fresh
/// builder (or inject <see cref="Func{IProcessingEnvBuilder}"/>) for each environment. Because the
/// components come from a real child scope, all plugin registrations, decorators and composites in the
/// parent container are honoured — unlike a hand-built processing stack.
/// </remarks>
public interface IProcessingEnvBuilder
{
    /// <summary>
    /// Binds the environment's world state to <paramref name="worldState"/>. Downstream scoped
    /// components (<see cref="IWorldState"/>, the transaction processor, etc.) are built over it.
    /// </summary>
    /// <remarks>
    /// This is the common case — pass a provider from <c>IWorldStateManager</c> (e.g.
    /// <c>CreateResettableWorldState()</c>, <c>GlobalWorldState</c>, or
    /// <c>CreateOverridableWorldScope().WorldState</c>). One of the <see cref="WithWorldState(IWorldStateScopeProvider)"/>
    /// / <see cref="WithWorldState(IWorldState)"/> overloads must be called before
    /// <see cref="BuildAs{TWrapper}"/>. The provider is externally owned and is not disposed with the scope.
    /// </remarks>
    IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState);

    /// <summary>
    /// Binds the environment to an already-built <see cref="IWorldState"/> instance, registered
    /// directly (the witness / stateless case where a wrapping world state is constructed by hand).
    /// </summary>
    IProcessingEnvBuilder WithWorldState(IWorldState worldState);

    /// <summary>
    /// Replaces the registration of <typeparamref name="T"/> within the environment's scope with
    /// <paramref name="instance"/>. The instance is externally owned and is not disposed with the scope.
    /// </summary>
    IProcessingEnvBuilder WithReplacedComponent<T>(T instance) where T : class;

    /// <summary>
    /// Escape hatch to apply arbitrary registrations to the environment's child scope (decorators,
    /// composites, whole modules) that the typed shortcuts do not cover.
    /// </summary>
    /// <remarks>Configuration actions are applied in the order they were added.</remarks>
    IProcessingEnvBuilder Configure(Action<ContainerBuilder> configure);

    /// <summary>
    /// Builds the child scope and returns an implementation of <typeparamref name="TWrapper"/> whose
    /// property getters resolve components from the scope. Disposing the returned wrapper disposes the
    /// scope.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TWrapper"/> must be a <b>public</b> interface exposing only read-only
    /// properties plus <see cref="IDisposable"/>; the implementation is generated once per interface at
    /// runtime and cached. Every property is resolved from the scope when the wrapper is constructed, so
    /// a missing registration surfaces here (fail-fast).
    /// </remarks>
    /// <exception cref="InvalidOperationException">No world state was specified via <c>WithWorldState</c>.</exception>
    TWrapper BuildAs<TWrapper>() where TWrapper : class, IDisposable;
}
