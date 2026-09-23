// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Consensus.Processing;

/// <summary>A read-only block-processing environment that can be pinned to a base block for the length of a scope.</summary>
/// <typeparam name="TScope">The scope type handed out by <see cref="Begin"/>, disposed to release the environment.</typeparam>
public interface IReadOnlyBlockProcessingEnv<out TScope> where TScope : IDisposable
{
    TScope Begin(BlockHeader? baseBlock);
}

/// <summary>
/// Bounded pool of read-only block-processing environments, so concurrent callers each execute on their own
/// environment instead of serialising on a shared one.
/// </summary>
/// <remarks>
/// Environments are created lazily on first rent and retained up to <paramref name="maxConcurrent"/>, so a node that
/// never exercises the pool pays nothing for it. Over-subscription fails fast with
/// <see cref="ConcurrencyLimitReachedException"/> rather than queueing, which the JSON-RPC layer maps to a
/// "Too many requests" error. An environment whose scope throws on dispose is discarded rather than reused, since its
/// internal state is no longer trustworthy.
/// </remarks>
/// <param name="factory">Creates a new environment when no idle one is available.</param>
/// <param name="maxConcurrent">Upper bound on both in-flight scopes and retained idle environments.</param>
/// <param name="requestKind">Names the caller in the over-subscription error, e.g. <c>"simulate"</c>.</param>
public class ReadOnlyBlockProcessingEnvPool<TEnv, TScope>(
    Func<TEnv> factory,
    int maxConcurrent,
    string requestKind) : IDisposable
    where TEnv : IReadOnlyBlockProcessingEnv<TScope>
    where TScope : IDisposable
{
    private readonly ConcurrentStack<TEnv> _idle = new();
    private readonly ConcurrentDictionary<TEnv, byte> _tracked = new();
    private int _retainedCount;
    private int _activeCount;
    private volatile bool _disposed;

    public PooledScope Begin(BlockHeader? baseBlock)
    {
        TEnv env = Rent();
        try
        {
            TScope scope = env.Begin(baseBlock);
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
        while (_idle.TryPop(out TEnv? env))
        {
            DisposeAndUntrack(env);
        }
        foreach (KeyValuePair<TEnv, byte> entry in _tracked)
        {
            DisposeAndUntrack(entry.Key);
        }
    }

    private TEnv Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int active = Interlocked.Increment(ref _activeCount);
        if (active > maxConcurrent)
        {
            Interlocked.Decrement(ref _activeCount);
            throw new ConcurrencyLimitReachedException(
                $"Unable to start new {requestKind} request. Too many in-flight {requestKind} calls. In-flight: {active - 1}.");
        }

        if (_idle.TryPop(out TEnv? env))
        {
            Interlocked.Decrement(ref _retainedCount);
            return env;
        }

        try
        {
            TEnv created = factory();
            _tracked.TryAdd(created, 0);
            return created;
        }
        catch
        {
            Interlocked.Decrement(ref _activeCount);
            throw;
        }
    }

    private void Release(TEnv env)
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

    private void ReleasePoisoned(TEnv env)
    {
        Interlocked.Decrement(ref _activeCount);
        DisposeAndUntrack(env);
    }

    private void DisposeAndUntrack(TEnv env)
    {
        if (_tracked.TryRemove(env, out _))
        {
            (env as IDisposable)?.Dispose();
        }
    }

    public readonly ref struct PooledScope(
        TScope scope,
        TEnv env,
        ReadOnlyBlockProcessingEnvPool<TEnv, TScope> pool)
    {
        public TScope Scope => scope;

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
