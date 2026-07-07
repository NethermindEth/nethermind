// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Rents/returns processors per tx index from a bounded pool and stages each tx's BAL slice in
/// <c>_perTxBal</c> so the validator can merge them in canonical order. Each rented processor also
/// gets a pooled <see cref="ParentReaderLease"/> — a snapshot of the parent-state world from which
/// the BAL-backed world state reads any value the suggested BAL doesn't carry at the current index.
/// </summary>
public partial class ParallelBalEnvManager : IParallelBalEnvManager
{
    private const int DefaultTxCount = 10000;
    private static readonly int ProcessorPoolSize = RuntimeInformation.ProcessorCount;

    // BAL pool is larger since extra BALs are retained so they can be merged in order
    private static readonly int BalPoolSize = RuntimeInformation.ProcessorCount * 2;

    static ParallelBalEnvManager()
    {
        StaticPool<BlockAccessListAtIndex>.SetMaxPooledCount(BalPoolSize);
        for (int i = 0; i < BalPoolSize; i++)
        {
            StaticPool<BlockAccessListAtIndex>.Return(new());
        }
    }

    private Block? _currentBlock;
    private BlockExecutionContext _currentCtx;
    private int _lastBalIndex;
    private BlockHeader? _parentStateHeader;

    // _inUse[i] is the processor currently bound to balIndex i.
    private IBalProcessingEnv?[] _inUse = new IBalProcessingEnv?[DefaultTxCount];

    // _perTxBal[i] holds its detached BAL until the validator merges it in order.
    private BlockAccessListAtIndex?[] _perTxBal = new BlockAccessListAtIndex?[DefaultTxCount];

    // processors are not shared statically between BAL managers
    private readonly ConcurrentQueue<IBalProcessingEnv> _processors = [];
    private readonly IBalProcessingEnvFactory _envFactory;
    private readonly ObjectPool<IReadOnlyTxProcessorSource>? _parentReaderEnvPool;
    private int _processorCount;

    public ParallelBalEnvManager(
        IBalProcessingEnvFactory envFactory,
        PrewarmerEnvFactory? prewarmerEnvFactory = null,
        PreBlockCaches? preBlockCaches = null,
        IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null)
    {
        _envFactory = envFactory;
        _parentReaderEnvPool = CreateParentReaderEnvPool(prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory);
        for (int i = 0; i < ProcessorPoolSize; i++)
        {
            _processors.Enqueue(NewProcessor());
            _processorCount++;
        }
    }

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
    {
        _currentBlock = block;
        _currentCtx = blockExecutionContext;
        _parentStateHeader = null;
        if (_parentReaderEnvPool is not null)
        {
            if (parentStateRoot is null) ThrowNotInitialized(nameof(parentStateRoot));
            _parentStateHeader = CreateParentStateHeader(block, parentStateRoot);
        }

        int previousSize = _lastBalIndex + 1;
        int newLastBalIndex = block.Transactions.Length + 1;
        ReclaimAndResize(newLastBalIndex + 1, previousSize);
        _lastBalIndex = newLastBalIndex;
    }

    // Thread-safety note for _inUse / _perTxBal:
    //   Each balIndex slot has at most one writer at a time. Pre/post (idx 0 and
    //   _lastBalIndex) are written only by the main thread; tx slots (1..len) are
    //   each owned by a single parallel-loop iteration, so no two workers ever
    //   touch the same slot. Cross-thread reads (validator → worker's slot) happen
    //   strictly after the worker's gasResults[i-1].SetResult, whose pairing with
    //   GetResult() establishes the publication barrier. Plain reads/writes are
    //   therefore sufficient — Volatile/Interlocked would be redundant fencing.
    public IBalProcessingEnv Get(uint? balIndex = null)
    {
        if (_currentBlock is null) ThrowNotInitialized(nameof(_currentBlock));

        int idx = ClampBalIndex(balIndex ?? 0u);

        // Re-entrant Get for the same balIndex returns the already-acquired processor
        // (lets pre/post callers share state across calls — main thread only).
        IBalProcessingEnv? existing = _inUse[idx];
        if (existing is not null) return existing;

        IBalProcessingEnv processor = RentProcessor();
        ParentReaderLease? parentReader = RentParentReader();

        try
        {
            // Install a fresh BAL before Setup so the worker has somewhere to record changes.
            processor.WorldState.SetGeneratingBlockAccessList(StaticPool<BlockAccessListAtIndex>.Rent());
            processor.Setup(_currentBlock, _currentCtx, (uint)idx, parentReader);
            _inUse[idx] = processor;
            return processor;
        }
        catch
        {
            parentReader?.Dispose();
            if (processor.WorldState.GetGeneratingBlockAccessList() is { } generatedBal)
            {
                StaticPool<BlockAccessListAtIndex>.Return(generatedBal);
            }
            processor.WorldState.SetGeneratingBlockAccessList(null);
            // Detach any parent reader Setup may have installed so the recycled slot isn't poisoned.
            processor.ClearParentReader();
            ReturnProcessor(processor);
            throw;
        }
    }

    public void Return(uint balIndex)
    {
        int idx = ClampBalIndex(balIndex);

        IBalProcessingEnv? processor = _inUse[idx];
        if (processor is null) return;

        _perTxBal[idx] = processor.WorldState.GetGeneratingBlockAccessList();
        processor.WorldState.SetGeneratingBlockAccessList(null);
        processor.ClearParentReader();
        _inUse[idx] = null;
        ReturnProcessor(processor);
    }

    /// <summary>
    /// Merges the per-tx BAL into <paramref name="target"/> in caller-controlled order, then
    /// returns it to the pool. Idempotent w.r.t. <see cref="Return"/>: also detaches the BAL
    /// for pre/post callers that never went through Return. <paramref name="target"/> may be
    /// null to skip the merge; <paramref name="onSlice"/> still fires either way.
    /// </summary>
    public void MergeAndReturnBal(uint balIndex, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null)
    {
        int idx = ClampBalIndex(balIndex);

        Return((uint)idx);

        BlockAccessListAtIndex? source = _perTxBal[idx];
        if (source is null) return;

        _perTxBal[idx] = null;
        try
        {
            target?.Merge(source);
            onSlice?.Invoke(source);
        }
        finally
        {
            StaticPool<BlockAccessListAtIndex>.Return(source);
        }
    }

    /// <inheritdoc/>
    /// <remarks>No-op: each tx runs in its own rented env, so there is no shared per-block cursor to
    /// advance (the sequential manager, by contrast, clears and increments its single world state here).</remarks>
    public void NextTransaction() { }

    /// <inheritdoc/>
    /// <remarks>No-op: a failed tx's env is simply never merged, so there is nothing to undo at the
    /// manager level.</remarks>
    public void Rollback() { }

    public void Dispose()
    {
        foreach (IBalProcessingEnv? env in _inUse) env?.Dispose();
        while (_processors.TryDequeue(out IBalProcessingEnv? env)) env.Dispose();
        (_parentReaderEnvPool as IDisposable)?.Dispose();
    }

    private int ClampBalIndex(uint balIndex)
        => (int)uint.Min(balIndex, (uint)_lastBalIndex);

    private IBalProcessingEnv NewProcessor() => _envFactory.Create(parallel: true);

    private IBalProcessingEnv RentProcessor()
    {
        if (Volatile.Read(ref _processorCount) > 0 && _processors.TryDequeue(out IBalProcessingEnv? p))
        {
            Interlocked.Decrement(ref _processorCount);
            return p;
        }
        return NewProcessor();
    }

    private void ReturnProcessor(IBalProcessingEnv p)
    {
        if (Interlocked.Increment(ref _processorCount) > ProcessorPoolSize)
        {
            Interlocked.Decrement(ref _processorCount);
            return;
        }
        _processors.Enqueue(p);
    }

    private ParentReaderLease? RentParentReader()
    {
        if (_parentReaderEnvPool is null)
        {
            return null;
        }

        if (_parentStateHeader is null) ThrowNotInitialized(nameof(_parentStateHeader));

        IReadOnlyTxProcessorSource source = _parentReaderEnvPool.Get();
        try
        {
            return new ParentReaderLease(source, _parentReaderEnvPool, source.Build(_parentStateHeader));
        }
        catch
        {
            _parentReaderEnvPool.Return(source);
            throw;
        }
    }

    private void ReclaimAndResize(int size, int previousSize)
    {
        for (int i = 0; i < previousSize; i++)
            if (_inUse[i] is not null) Return((uint)i);

        for (int i = 0; i < previousSize; i++)
        {
            if (_perTxBal[i] is { } bal)
            {
                StaticPool<BlockAccessListAtIndex>.Return(bal);
                _perTxBal[i] = null;
            }
        }

        if (_inUse.Length < size)
            Array.Resize(ref _inUse, Math.Max(2 * _inUse.Length, size));
        if (_perTxBal.Length < size)
            Array.Resize(ref _perTxBal, Math.Max(2 * _perTxBal.Length, size));
    }

    private static ObjectPool<IReadOnlyTxProcessorSource>? CreateParentReaderEnvPool(
        PrewarmerEnvFactory? prewarmerEnvFactory,
        PreBlockCaches? preBlockCaches,
        IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory)
    {
        DefaultObjectPoolProvider provider = new() { MaximumRetained = ProcessorPoolSize };
        if (prewarmerEnvFactory is not null && preBlockCaches is not null)
        {
            return provider.Create(new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(prewarmerEnvFactory, preBlockCaches));
        }

        return readOnlyTxProcessingEnvFactory is not null
            ? provider.Create(new ReadOnlyTxProcessingEnvPooledObjectPolicy(readOnlyTxProcessingEnvFactory))
            : null;
    }

    private static BlockHeader CreateParentStateHeader(Block block, Hash256 stateRoot)
    {
        Hash256 parentHash = block.ParentHash ?? Keccak.Zero;
        return new BlockHeader(
            parentHash,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            block.Number == 0 ? 0 : block.Number - 1,
            0,
            0,
            [])
        {
            StateRoot = stateRoot,
            Hash = parentHash,
        };
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");
}
