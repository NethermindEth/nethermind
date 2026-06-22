// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnvScope : IDisposable
{
    IWitnessGeneratingBlockProcessingEnv Env { get; }
}

public interface IWitnessGeneratingBlockProcessingEnvFactory
{
    IWitnessGeneratingBlockProcessingEnvScope CreateScope();
}

/// <summary>
/// Builds a <see cref="IWitnessGeneratingBlockProcessingEnv"/> on demand and pools entries for reuse.
/// </summary>
/// <remarks>
/// Each rent returns a fully-wired env (own WorldState stack, capturing trie-store wrapper, header
/// finder, per-entry <see cref="WitnessCaptureSession"/>, Autofac child scope). The first rent on an
/// empty pool pays full construction cost; subsequent rents reuse a pooled entry. Entries are reset on
/// return (so a pooled entry never pins its last call's witness buffers) and the pool is soft-capped —
/// surplus and poisoned entries are disposed rather than pooled. Disposing the factory drains the pool.
/// </remarks>
public class WitnessGeneratingBlockProcessingEnvFactory(
    ILifetimeScope rootLifetimeScope,
    IWorldStateManager worldStateManager,
    IDbProvider dbProvider,
    IBlockValidationModule[] validationModules,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory, IDisposable
{
    // LIFO so the warmest (most-recently-returned) entry is reused first.
    private readonly ConcurrentStack<PooledEntry> _pool = new();

    // Soft cap; entries returned beyond it are disposed rather than pooled.
    private static readonly int MaxPoolSize = Math.Max(2, Environment.ProcessorCount * 2);
    private int _poolCount;
    private volatile bool _disposed;

    public IWitnessGeneratingBlockProcessingEnvScope CreateScope()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryPop(out PooledEntry? entry))
        {
            Interlocked.Decrement(ref _poolCount);
        }
        else
        {
            entry = BuildEntry();
        }

        return new RentedScope(this, entry);
    }

    private PooledEntry BuildEntry()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);

        // Per-entry session + recorders. The session is armed once for the entry's lifetime (the env's
        // components are wired directly, not via the main-pipeline proxy); Reset() clears the recorder
        // data between rents while leaving the session armed at the same recorder instances.
        WitnessCaptureSession session = new();
        WitnessHeaderRecorder headerRecorder = new();

        IReadOnlyTrieStore trieStore = worldStateManager.CreateReadOnlyTrieStore();
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState baseWorldState = new WorldState(
            new TrieStoreScopeProvider(trieStore, readOnlyDbProvider.CodeDb, logManager), logManager);

        IHeaderStore headerStore = rootLifetimeScope.Resolve<IHeaderStore>();
        WitnessCapturingHeaderFinder capturingHeaderFinder = new(headerStore, session);
        // Proof-collection walks go through the global (non-capturing) reader; the capturing trieStore
        // serves execution-path reads (not account proof collection). headerStore is the undecorated source BuildHeaders walks.
        WitnessGeneratingWorldState witnessWorldState = new(
            baseWorldState, worldStateManager.GlobalStateReader, trieStore, headerRecorder, headerStore);

        session.TryArm(witnessWorldState, headerRecorder);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(witnessWorldState)
            .AddScoped<WitnessGeneratingWorldState>(witnessWorldState)
            .AddScoped<IHeaderFinder>(capturingHeaderFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            // The whole sandbox re-execution records a witness, so its BlockAccessListManager runs in
            // witness mode unconditionally (sequential + non-caching code reads).
            .AddScoped<WitnessExecutionPredicate>(new WitnessExecutionPredicate(static () => true))
            .AddModule(validationModules)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv, WitnessGeneratingBlockProcessingEnv>());

        IWitnessGeneratingBlockProcessingEnv env = envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();
        IBlockhashCache blockhashCache = envLifetimeScope.Resolve<IBlockhashCache>();
        return new PooledEntry(envLifetimeScope, trieStore, readOnlyDbProvider, headerRecorder, witnessWorldState, blockhashCache, env);
    }

    private void Return(PooledEntry entry)
    {
        // Past the soft cap (or after the factory is disposed): dispose rather than pool, so burst
        // traffic can't pin surplus WorldState stacks until root-scope shutdown. A small overshoot is
        // tolerated when threads race past the check; root-scope disposal still reclaims any straggler.
        if (_disposed || Volatile.Read(ref _poolCount) >= MaxPoolSize)
        {
            entry.Dispose();
            return;
        }

        // Reset before pooling so a returned entry never pins its last call's witness buffers. A reset
        // that throws (e.g. a DB disposed mid-shutdown) poisons the entry — dispose it rather than pool.
        try
        {
            entry.Reset();
        }
        catch
        {
            entry.Dispose();
            return;
        }

        Interlocked.Increment(ref _poolCount);
        _pool.Push(entry);

        // A Dispose that drained between our guard and this push would leave the entry orphaned;
        // re-check and drain ourselves.
        if (_disposed)
        {
            while (_pool.TryPop(out PooledEntry? stale))
            {
                Interlocked.Decrement(ref _poolCount);
                stale.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        while (_pool.TryPop(out PooledEntry? entry))
        {
            Interlocked.Decrement(ref _poolCount);
            entry.Dispose();
        }
    }

    private sealed class PooledEntry(
        ILifetimeScope scope,
        IReadOnlyTrieStore trieStore,
        IReadOnlyDbProvider dbProvider,
        WitnessHeaderRecorder headerRecorder,
        WitnessGeneratingWorldState worldState,
        IBlockhashCache blockhashCache,
        IWitnessGeneratingBlockProcessingEnv env) : IDisposable
    {
        public ILifetimeScope Scope { get; } = scope;
        public IWitnessGeneratingBlockProcessingEnv Env { get; } = env;

        /// <summary>Tears down the entry: the Autofac scope (and everything it owns) first, then the
        /// manually-created read-only trie store the scope's components borrowed.</summary>
        public void Dispose()
        {
            Scope.Dispose();
            trieStore.Dispose();
        }

        /// <summary>Wipes per-call accumulators so the entry is safe for the next rent.</summary>
        /// <remarks>
        /// The inner WorldState's per-call caches are already cleared by <c>WorldState.BeginScope</c>'s
        /// scope-exit <c>Reset(true)</c>; only the witness-specific accumulators are cleared here. The
        /// session stays armed at the same recorder instances — clearing the recorders is enough. The
        /// blockhash cache is content-addressed (never stale) but grows per entry, so it's cleared too.
        /// </remarks>
        public void Reset()
        {
            headerRecorder.Reset();
            worldState.Reset();
            dbProvider.ClearTempChanges();
            blockhashCache.Clear();
        }
    }

    private sealed class RentedScope(WitnessGeneratingBlockProcessingEnvFactory factory, PooledEntry entry)
        : IWitnessGeneratingBlockProcessingEnvScope
    {
        private int _disposed;

        public IWitnessGeneratingBlockProcessingEnv Env => entry.Env;

        public void Dispose()
        {
            // Idempotent: a second Dispose (defensive double-using, programmer error, exception cleanup
            // racing with a finally block) MUST NOT re-pool the same entry — that would let two
            // subsequent rents observe the same instance and race on the witness accumulators.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                factory.Return(entry);
            }
        }
    }
}
