// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Default <see cref="IShareableOverridableEnvSource{T}"/> backed by a
/// <see cref="DefaultObjectPool{T}"/>. Each pooled env is produced lazily by the supplied factory,
/// owns its own world-scope state, and is returned to the pool when its <see cref="Scope{T}"/> is
/// disposed.
/// </summary>
public sealed class ShareableOverridableEnvSource<T> : IShareableOverridableEnvSource<T>
{
    private readonly ObjectPool<IOverridableEnv<T>> _pool;
    private readonly ConcurrentBag<IOverridableEnv<T>> _allEnvs = [];

    public ShareableOverridableEnvSource(Func<IOverridableEnv<T>> factory, int maxRetained)
    {
        // Tracking factory: records every env it ever produces so Dispose can clean them up. The
        // underlying DefaultObjectPool only retains up to maxRetained — anything beyond that gets
        // dropped on Return and would leak its underlying lifetime scope without this list.
        IOverridableEnv<T> TrackingFactory()
        {
            IOverridableEnv<T> env = factory();
            _allEnvs.Add(env);
            return env;
        }
        _pool = new DefaultObjectPool<IOverridableEnv<T>>(new EnvPoolPolicy(TrackingFactory), maxRetained);
    }

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
        return new Scope<T>(innerScope.Component, new ReturnOnDispose(env, innerScope, _pool));
    }

    public void Dispose()
    {
        while (_allEnvs.TryTake(out IOverridableEnv<T>? env))
        {
            (env as IDisposable)?.Dispose();
        }
    }

    private sealed class EnvPoolPolicy(Func<IOverridableEnv<T>> factory) : IPooledObjectPolicy<IOverridableEnv<T>>
    {
        public IOverridableEnv<T> Create() => factory();
        public bool Return(IOverridableEnv<T> obj) => true;
    }

    // try/finally guarantees the pool slot is released even if the inner scope's Dispose throws —
    // OverridableEnv.Reset touches world state and is not guaranteed to be exception-safe.
    private sealed class ReturnOnDispose(IOverridableEnv<T> env, Scope<T> innerScope, ObjectPool<IOverridableEnv<T>> pool) : IDisposable
    {
        public void Dispose()
        {
            try { innerScope.Dispose(); }
            finally { pool.Return(env); }
        }
    }
}
