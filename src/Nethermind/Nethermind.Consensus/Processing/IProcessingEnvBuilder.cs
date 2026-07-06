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
    /// <see cref="WithWorldState(IWorldStateScopeProvider)"/> / <see cref="WithWorldState(IWorldState)"/>
    /// overloads must be called before <see cref="BuildAs{TWrapper}"/>. The provider is registered but
    /// not disposed by the environment; use <see cref="ThatDisposes"/> if the environment owns it.
    /// </remarks>
    IProcessingEnvBuilder WithWorldState(IWorldStateScopeProvider worldState);

    /// <summary>
    /// Binds the environment to an already-built <see cref="IWorldState"/> instance, registered
    /// directly (the witness / stateless case where a wrapping world state is constructed by hand).
    /// </summary>
    IProcessingEnvBuilder WithWorldState(IWorldState worldState);

    /// <summary>
    /// Replaces the registration of <typeparamref name="T"/> within the environment's scope with
    /// <paramref name="instance"/>. The instance is registered but not disposed by the environment; use
    /// <see cref="ThatDisposes"/> if the environment owns it.
    /// </summary>
    IProcessingEnvBuilder WithReplacedComponent<T>(T instance) where T : class;

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
    /// graph does not provide). The instance is registered but not disposed by the environment; use
    /// <see cref="ThatDisposes"/> if the environment owns it.
    /// </summary>
    IProcessingEnvBuilder WithComponent<T>(T instance) where T : class;

    /// <summary>
    /// Declares that <paramref name="disposable"/> is owned by the environment and must be disposed when
    /// the environment (its child scope) is disposed.
    /// </summary>
    /// <remarks>
    /// Use for resources the environment owns that Autofac does not otherwise track — e.g. a
    /// manually-created read-only trie store that no scope component resolves. Registering a component
    /// with <see cref="WithComponent"/> / <see cref="WithReplacedComponent{T}(T)"/> does not imply
    /// disposal; ownership is declared here, explicitly.
    /// </remarks>
    IProcessingEnvBuilder ThatDisposes(IDisposable disposable);

    /// <summary>
    /// Applies the registered <c>IBlockValidationModule</c>s (the validation transaction executor and
    /// related wiring) to the environment's scope; everything else is inherited from the parent scope
    /// and re-resolved against the environment's world state. The modules are resolved from the builder's
    /// parent scope, so callers do not pass them.
    /// </summary>
    IProcessingEnvBuilder WithBlockValidationConfiguration();

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
