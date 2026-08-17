// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Stack-based pool with a hard active-lease cap and matching idle retention. Rents that exceed the
/// cap wait briefly for a lease and then throw <see cref="ConcurrencyLimitReachedException"/>. Owns
/// every env it hands out so shutdown releases both idle and rented envs without leaking references
/// into outer DI disposers.
/// </summary>
public sealed class ShareableOverridableEnvSource<T>(
    Func<IOverridableEnv<T>> factory,
    int maxConcurrent) : IShareableOverridableEnvSource<T>
{
    /// <summary>How long an over-cap rent waits for a lease before rejecting, in milliseconds.</summary>
    /// <remarks>
    /// Offered concurrency is request rate × call latency, so a transient latency wobble pushes it
    /// past the cap and an instant rejection turns that wobble into hard "Too many requests"
    /// errors. A lease typically frees within one call's runtime, so waiting a few call-lengths
    /// converts a burst into a small queueing delay; sustained overload still rejects once the
    /// wait elapses. Waiters block a thread-pool thread each, but their number is bounded by the
    /// server's own request concurrency.
    /// </remarks>
    private const int RentWaitMs = 250;

    private readonly ConcurrentStack<IOverridableEnv<T>> _idle = new();
    private readonly ConcurrentDictionary<IOverridableEnv<T>, byte> _tracked = new();
    // maxCount tolerates a nonsensical cap of 0 (construction must not throw where rents used to).
    private readonly SemaphoreSlim _slots = new(Math.Max(maxConcurrent, 0), Math.Max(maxConcurrent, 1));
    private int _retainedCount;
    private volatile bool _disposed;

    public Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
    {
        IOverridableEnv<T> env = Rent();
        Scope<T> innerScope;
        try
        {
            innerScope = env.BuildAndOverride(header, stateOverride, blockOverride: blockOverride);
        }
        catch
        {
            // BuildAndOverride failed: env may have left _worldScopeCloser non-null. A future rent
            // of this slot would throw on the reentry guard, permanently shrinking the pool. Drop it.
            ReleasePoisoned(env);
            throw;
        }
        return new Scope<T>(innerScope.Component, new ReturnOnDispose(env, innerScope, this));
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryPop(out IOverridableEnv<T>? env))
        {
            DisposeAndUntrack(env);
        }
        // Still-rented envs are disposed too; a later Release becomes a no-op via TryRemove.
        foreach (KeyValuePair<IOverridableEnv<T>, byte> entry in _tracked)
        {
            DisposeAndUntrack(entry.Key);
        }
    }

    private IOverridableEnv<T> Rent()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ShareableOverridableEnvSource<T>));

        if (!_slots.Wait(RentWaitMs))
        {
            throw new ConcurrencyLimitReachedException(
                $"Unable to start new override request. Too many in-flight override calls (cap {maxConcurrent}).");
        }

        if (_idle.TryPop(out IOverridableEnv<T>? env))
        {
            Interlocked.Decrement(ref _retainedCount);
            return env;
        }

        try
        {
            IOverridableEnv<T> created = factory();
            _tracked.TryAdd(created, 0);
            return created;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    // Healthy return path: keep up to maxConcurrent, dispose the rest so they don't sit alive as
    // unreachable scopes until process shutdown.
    private void Release(IOverridableEnv<T> env)
    {
        _slots.Release();

        if (_disposed)
        {
            DisposeAndUntrack(env);
            return;
        }
        if (Interlocked.Increment(ref _retainedCount) > maxConcurrent)
        {
            Interlocked.Decrement(ref _retainedCount);
            DisposeAndUntrack(env);
            return;
        }
        _idle.Push(env);
    }

    // Poisoned envs are never returned to the pool: their internal state may be inconsistent and
    // the next rent would surface a confusing error on a different request.
    private void ReleasePoisoned(IOverridableEnv<T> env)
    {
        _slots.Release();
        DisposeAndUntrack(env);
    }

    private void DisposeAndUntrack(IOverridableEnv<T> env)
    {
        if (_tracked.TryRemove(env, out _))
        {
            (env as IDisposable)?.Dispose();
        }
    }

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
                if (poisoned) source.ReleasePoisoned(env);
                else source.Release(env);
            }
        }
    }
}
