// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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

/// <summary>Builds a <see cref="IWitnessGeneratingBlockProcessingEnv"/> on demand and pools fully-wired entries for reuse.</summary>
/// <remarks>Entries are reset on return; the pool is soft-capped, with surplus and poisoned entries disposed rather than pooled.</remarks>
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

        WitnessHeaderRecorder headerRecorder = new();

        IReadOnlyTrieStore trieStore = worldStateManager.CreateReadOnlyTrieStore();
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState baseWorldState = new WorldState(
            new TrieStoreScopeProvider(trieStore, readOnlyDbProvider.CodeDb, logManager), logManager);

        IHeaderStore headerStore = rootLifetimeScope.Resolve<IHeaderStore>();
        WitnessCapturingHeaderFinder capturingHeaderFinder = new(headerStore, headerRecorder);
        // Proof-collection walks go through the global (non-capturing) reader; the trieStore serves execution-path reads.
        WitnessGeneratingWorldState witnessWorldState = new(
            baseWorldState, worldStateManager.GlobalStateReader, trieStore, headerRecorder, headerStore);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(witnessWorldState)
            .AddScoped<WitnessGeneratingWorldState>(witnessWorldState)
            .AddScoped<IHeaderFinder>(capturingHeaderFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ICodeCache>(NoopCodeCache.Instance)
            .AddScoped<IBlockAccessListManager>(ctx => new BlockAccessListManager(
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<ISpecProvider>(),
                ctx.Resolve<IBlockhashProvider>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IWithdrawalProcessorFactory>(),
                codeInfoRepositoryFactory: ctx.Resolve<CodeInfoRepositoryFactory>(),
                transactionProcessorFactory: ctx.Resolve<ITransactionProcessorFactory>()))
            .AddModule(validationModules)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv, WitnessGeneratingBlockProcessingEnv>());

        IWitnessGeneratingBlockProcessingEnv env = envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();
        IBlockhashCache blockhashCache = envLifetimeScope.Resolve<IBlockhashCache>();
        return new PooledEntry(envLifetimeScope, trieStore, readOnlyDbProvider, headerRecorder, witnessWorldState, blockhashCache, env);
    }

    private void Return(PooledEntry entry)
    {
        // Past the soft cap (or disposed): dispose rather than pool, so burst traffic can't pin surplus WorldState stacks.
        if (_disposed || Volatile.Read(ref _poolCount) >= MaxPoolSize)
        {
            entry.Dispose();
            return;
        }

        // Reset before pooling so an entry never pins its last call's buffers; a throwing reset poisons it — dispose instead.
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

        // A Dispose that drained between the guard and this push would orphan the entry; re-check and drain.
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

        /// <summary>Tears down the Autofac scope first, then the manually-created read-only trie store it borrowed.</summary>
        public void Dispose()
        {
            Scope.Dispose();
            trieStore.Dispose();
        }

        /// <summary>Wipes per-call accumulators so the entry is safe for the next rent.</summary>
        /// <remarks>The inner WorldState's caches are already cleared by <c>WorldState.BeginScope</c>'s scope-exit reset; only the witness-specific accumulators (and the per-entry-growing blockhash cache) are cleared here.</remarks>
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
            // Idempotent: a second Dispose must not re-pool the entry, or two rents would share it and race on the accumulators.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                factory.Return(entry);
            }
        }
    }
}
