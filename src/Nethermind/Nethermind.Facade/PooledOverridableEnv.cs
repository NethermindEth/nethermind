// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Facade;

/// <summary>
/// Pools independent <see cref="IOverridableEnv{T}"/> instances so concurrent calls with state
/// overrides can execute in parallel. Each pooled env owns its own <c>_worldScopeCloser</c>,
/// <see cref="SingleCallRequestState"/> and decorator chain, so there is no shared mutable state
/// between callers.
/// </summary>
/// <remarks>
/// Without this pool the bridge would have a single env guarded by a <c>SemaphoreSlim(1,1)</c>,
/// reducing override-path concurrency from <c>EthModuleConcurrentInstances</c> (pre-PR) down to 1.
/// The pool restores parallelism for the override path while keeping the no-override fast path on
/// <see cref="Nethermind.Blockchain.IShareableTxProcessorSource"/>.
/// </remarks>
public sealed class PooledOverridableEnv<T>(Func<IOverridableEnv<T>> factory, int maxRetained) : IOverridableEnv<T>, IDisposable
{
    private readonly ObjectPool<IOverridableEnv<T>> _pool =
        new DefaultObjectPool<IOverridableEnv<T>>(new EnvPoolPolicy(factory), maxRetained);

    public Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null)
    {
        IOverridableEnv<T> env = _pool.Get();
        Scope<T> innerScope;
        try
        {
            innerScope = env.BuildAndOverride(header, stateOverride);
        }
        catch
        {
            _pool.Return(env);
            throw;
        }
        return new Scope<T>(innerScope.Component, new PoolReleasingDisposable(env, innerScope, _pool));
    }

    public void Dispose() => (_pool as IDisposable)?.Dispose();

    private sealed class EnvPoolPolicy(Func<IOverridableEnv<T>> factory) : IPooledObjectPolicy<IOverridableEnv<T>>
    {
        public IOverridableEnv<T> Create() => factory();
        public bool Return(IOverridableEnv<T> obj) => true;
    }

    private sealed class PoolReleasingDisposable(IOverridableEnv<T> env, Scope<T> innerScope, ObjectPool<IOverridableEnv<T>> pool) : IDisposable
    {
        public void Dispose()
        {
            innerScope.Dispose();
            pool.Return(env);
        }
    }
}
