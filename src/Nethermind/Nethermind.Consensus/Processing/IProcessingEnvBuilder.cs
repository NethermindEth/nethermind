// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Processing;

/// <summary>Source of fresh <see cref="IDsl"/> instances, one per block-processing environment.</summary>
public interface IProcessingEnvBuilder
{
    IDsl NewEnv();

    /// <summary>
    /// Fluent builder for an isolated block-processing environment. It configures an Autofac child lifetime
    /// scope and returns the resolved graph as <c>TWrapper</c> — a caller-defined, getter-only interface that
    /// must implement <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/> and be disposed when the
    /// environment is no longer needed (disposing it disposes the scope).
    /// </summary>
    public interface IDsl
    {
        IDsl WithWorldState(IWorldStateScopeProvider worldState);

        IDsl WithWorldState(IWorldState worldState);

        IDsl WithOverridableEnv(IOverridableEnv env);

        IDsl WithOverridableEnv();

        IDsl WithReplacedComponent<T>(T instance) where T : class;

        IDsl WithReplacedComponent<TService, TImpl>() where TImpl : TService where TService : notnull;

        IDsl WithReplacedComponent<T>(Func<IComponentContext, T> factory) where T : class;

        IDsl WithComponent<T>(T instance) where T : class;

        IDsl ThatDisposes(IDisposable disposable);

        IDsl WithBlockValidationConfiguration();

        IDsl Configure(Action<ContainerBuilder> configure);

        TWrapper BuildAs<TWrapper>() where TWrapper : class;
    }
}
