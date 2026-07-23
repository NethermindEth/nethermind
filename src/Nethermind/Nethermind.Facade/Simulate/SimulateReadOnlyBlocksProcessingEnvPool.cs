// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateReadOnlyBlocksProcessingEnvPool(
    Func<ISimulateReadOnlyBlocksProcessingEnv> factory,
    int maxConcurrent) : IDisposable
{
    private readonly ConcurrentStack<ISimulateReadOnlyBlocksProcessingEnv> _idle = new();
    private readonly ConcurrentDictionary<ISimulateReadOnlyBlocksProcessingEnv, byte> _tracked = new();
    private int _retainedCount;
    private int _activeCount;
    private volatile bool _disposed;

    public PooledScope Begin(BlockHeader? baseBlock)
    {
        ISimulateReadOnlyBlocksProcessingEnv env = Rent();
        try
        {
            SimulateReadOnlyBlocksProcessingScope scope = env.Begin(baseBlock);
            return new PooledScope(scope, env, this);
        }
        catch
        {
            ReleasePoisoned(env);
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryPop(out ISimulateReadOnlyBlocksProcessingEnv? env))
        {
            DisposeAndUntrack(env);
        }
        foreach (KeyValuePair<ISimulateReadOnlyBlocksProcessingEnv, byte> entry in _tracked)
        {
            DisposeAndUntrack(entry.Key);
        }
    }

    private ISimulateReadOnlyBlocksProcessingEnv Rent()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimulateReadOnlyBlocksProcessingEnvPool));

        int active = Interlocked.Increment(ref _activeCount);
        if (active > maxConcurrent)
        {
            Interlocked.Decrement(ref _activeCount);
            throw new ConcurrencyLimitReachedException(
                $"Unable to start new simulate request. Too many in-flight simulate calls. In-flight: {active - 1}.");
        }

        if (_idle.TryPop(out ISimulateReadOnlyBlocksProcessingEnv? env))
        {
            Interlocked.Decrement(ref _retainedCount);
            return env;
        }

        try
        {
            ISimulateReadOnlyBlocksProcessingEnv created = factory();
            _tracked.TryAdd(created, 0);
            return created;
        }
        catch
        {
            Interlocked.Decrement(ref _activeCount);
            throw;
        }
    }

    private void Release(ISimulateReadOnlyBlocksProcessingEnv env)
    {
        Interlocked.Decrement(ref _activeCount);

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

    private void ReleasePoisoned(ISimulateReadOnlyBlocksProcessingEnv env)
    {
        Interlocked.Decrement(ref _activeCount);
        DisposeAndUntrack(env);
    }

    private void DisposeAndUntrack(ISimulateReadOnlyBlocksProcessingEnv env)
    {
        if (_tracked.TryRemove(env, out _))
        {
            (env as IDisposable)?.Dispose();
        }
    }

    public readonly ref struct PooledScope(
        SimulateReadOnlyBlocksProcessingScope scope,
        ISimulateReadOnlyBlocksProcessingEnv env,
        SimulateReadOnlyBlocksProcessingEnvPool pool)
    {
        public SimulateReadOnlyBlocksProcessingScope Scope => scope;

        public void Dispose()
        {
            bool poisoned = false;
            try
            {
                scope.Dispose();
            }
            catch
            {
                poisoned = true;
                throw;
            }
            finally
            {
                if (poisoned) pool.ReleasePoisoned(env);
                else pool.Release(env);
            }
        }
    }
}
