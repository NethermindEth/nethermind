// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Default <see cref="IShareableOverridableEnvSource{T}"/> backed by a custom stack-based pool with
/// a soft retention cap. Envs beyond the cap are disposed immediately on return rather than
/// silently dropped, and envs whose scope disposal threw mid-cleanup are also disposed eagerly
/// (rather than handed back in a corrupted state).
/// </summary>
public sealed class ShareableOverridableEnvSource<T>(
    Func<IOverridableEnv<T>> factory,
    int maxRetained) : IShareableOverridableEnvSource<T>
{
    private readonly ConcurrentStack<IOverridableEnv<T>> _idle = new();
    private int _retainedCount;
    private volatile bool _disposed;

    public Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null)
    {
        IOverridableEnv<T> env = Rent();
        Scope<T> innerScope;
        try
        {
            innerScope = env.BuildAndOverride(header, stateOverride);
        }
        catch
        {
            // BuildAndOverride failed: env may have left _worldScopeCloser non-null. A future rent
            // of this slot would throw on the reentry guard, permanently shrinking the pool. Drop it.
            DisposeEnv(env);
            throw;
        }
        return new Scope<T>(innerScope.Component, new ReturnOnDispose(env, innerScope, this));
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryPop(out IOverridableEnv<T>? env))
        {
            DisposeEnv(env);
        }
    }

    private IOverridableEnv<T> Rent()
    {
        if (_idle.TryPop(out IOverridableEnv<T>? env))
        {
            Interlocked.Decrement(ref _retainedCount);
            return env;
        }
        return factory();
    }

    // Healthy return path: keep up to maxRetained, dispose the rest so they don't sit alive as
    // unreachable scopes until process shutdown.
    private void Release(IOverridableEnv<T> env)
    {
        if (_disposed)
        {
            DisposeEnv(env);
            return;
        }
        if (Interlocked.Increment(ref _retainedCount) > maxRetained)
        {
            Interlocked.Decrement(ref _retainedCount);
            DisposeEnv(env);
            return;
        }
        _idle.Push(env);
    }

    // Poisoned envs are never returned to the pool: their internal state may be inconsistent and
    // the next rent would surface a confusing error on a different request.
    private void DisposePoisoned(IOverridableEnv<T> env) => DisposeEnv(env);

    private static void DisposeEnv(IOverridableEnv<T> env) => (env as IDisposable)?.Dispose();

    private sealed class ReturnOnDispose(
        IOverridableEnv<T> env, Scope<T> innerScope, ShareableOverridableEnvSource<T> source) : IDisposable
    {
        public void Dispose()
        {
            bool poisoned = false;
            try
            {
                innerScope.Dispose();
            }
            catch
            {
                poisoned = true;
                throw;
            }
            finally
            {
                if (poisoned) source.DisposePoisoned(env);
                else source.Release(env);
            }
        }
    }
}
