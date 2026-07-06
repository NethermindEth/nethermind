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
    /// Pass a provider from <c>IWorldStateManager</c> (e.g. <c>CreateResettableWorldState()</c>,
    /// <c>GlobalWorldState</c>, or <c>CreateOverridableWorldScope().WorldState</c>). One of the
    /// <see cref="WithWorldState(IWorldStateScopeProvider, bool)"/> / <see cref="WithWorldState(IWorldState, bool)"/>
    /// overloads must be called before <see cref="BuildAs{TWrapper}"/>. Pass
    /// <paramref name="externallyOwned"/> <c>true</c> for a shared/manager-owned provider (e.g.
    /// <c>GlobalWorldState</c>) that must never be disposed by the environment.
    /// </remarks>
    /// <param name="externallyOwned">When <c>false</c> (default) the environment owns the instance and
    /// disposes it (if disposable) when the wrapper is disposed.</param>
    IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState, bool externallyOwned = false);

    /// <summary>
    /// Binds the environment to an already-built <see cref="IWorldState"/> instance, registered
    /// directly (the witness / stateless case where a wrapping world state is constructed by hand).
    /// </summary>
    /// <inheritdoc cref="WithWorldState(IWorldStateScopeProvider, bool)" path="/param"/>
    IProcessingEnvBuilder WithWorldState(IWorldState worldState, bool externallyOwned = false);

    /// <summary>
    /// Replaces the registration of <typeparamref name="T"/> within the environment's scope with
    /// <paramref name="instance"/>.
    /// </summary>
    /// <param name="externallyOwned">When <c>false</c> (default) the environment owns the instance and
    /// disposes it (if disposable) when the wrapper is disposed; pass <c>true</c> for a shared instance.</param>
    IProcessingEnvBuilder WithReplacedComponent<T>(T instance, bool externallyOwned = false) where T : class;

    /// <summary>
    /// Replaces the registration of <typeparamref name="TService"/> within the environment's scope with
    /// a fresh, scope-owned <typeparamref name="TImpl"/>.
    /// </summary>
    IProcessingEnvBuilder WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull;

    /// <summary>
    /// Replaces the registration of <typeparamref name="T"/> within the environment's scope with a
    /// scope-owned instance produced by <paramref name="factory"/>.
    /// </summary>
    IProcessingEnvBuilder WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class;

    /// <summary>
    /// Adds <paramref name="instance"/> as a component of the environment's scope (a service the base
    /// graph does not provide).
    /// </summary>
    /// <inheritdoc cref="WithReplacedComponent{T}(T, bool)" path="/param"/>
    IProcessingEnvBuilder WithComponent<T>(T instance, bool externallyOwned = false) where T : class;

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
    /// properties plus <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/> (so the scope is
    /// released); the implementation is generated once per interface at runtime and cached. Every
    /// property is resolved from the scope when the wrapper is constructed, so a missing registration
    /// surfaces here (fail-fast).
    /// </remarks>
    /// <exception cref="InvalidOperationException">No world state was specified via <c>WithWorldState</c>.</exception>
    TWrapper BuildAs<TWrapper>() where TWrapper : class;
}
