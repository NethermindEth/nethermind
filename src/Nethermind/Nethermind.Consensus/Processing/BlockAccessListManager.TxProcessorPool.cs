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
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Nested tx-processor pool internals. Two implementations of
/// <see cref="ITxProcessorWithWorldStateManager"/>:
///   * <see cref="ParallelTxProcessorWithWorldStateManager"/> rents/returns processors per
///     tx index from a bounded pool and stages each tx's BAL slice in <c>_perTxBal</c> so
///     the validator can merge them in canonical order. Each rented processor also gets a
///     pooled <see cref="ParentReaderLease"/> — a snapshot of the parent-state world from
///     which the BAL-backed world state reads any value the suggested BAL doesn't carry at
///     the current index.
///   * <see cref="SequentialTxProcessorWithWorldStateManager"/> reuses a single processor
///     for the whole block.
/// <see cref="TxProcessorWithWorldState"/> bundles the tx processor with its world states
/// (BAL-backed in parallel mode, plain in sequential).
/// </summary>
public partial class BlockAccessListManager
{
    private interface ITxProcessorWithWorldStateManager : IDisposable
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot);
        TxProcessorWithWorldState Get(uint? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0u);
        TxProcessorWithWorldState GetPostExecution() => Get(uint.MaxValue);
        void NextTransaction();
        void Rollback();
        void MergeAndReturnBal(uint balIndex, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null);
    }

    private class ParallelTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private const int DefaultTxCount = 10000;
        private static readonly int ProcessorPoolSize = RuntimeInformation.ProcessorCount;

        // BAL pool is larger since extra BALs are retained so they can be merged in order
        private static readonly int BalPoolSize = RuntimeInformation.ProcessorCount * 2;

        static ParallelTxProcessorWithWorldStateManager()
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
        private TxProcessorWithWorldState?[] _inUse = new TxProcessorWithWorldState?[DefaultTxCount];

        // _perTxBal[i] holds its detached BAL until the validator merges it in order.
        private BlockAccessListAtIndex?[] _perTxBal = new BlockAccessListAtIndex?[DefaultTxCount];

        // processors are not shared statically between BAL managers
        private readonly ConcurrentQueue<TxProcessorWithWorldState> _processors = [];
        private readonly ConcurrentBag<TxProcessorWithWorldState> _allProcessors = [];
        private readonly IBlockhashProvider _blockHashProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _stateProvider;
        private readonly ILogManager _logManager;
        private readonly ITransactionProcessorFactory _txProcessorFactory;
        private readonly ObjectPool<IReadOnlyTxProcessorSource>? _parentReaderEnvPool;
        private int _processorCount;
        private readonly CodeInfoRepositoryFactory _codeInfoRepositoryFactory;

        public ParallelTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager,
            PrewarmerEnvFactory? prewarmerEnvFactory,
            PreBlockCaches? preBlockCaches,
            IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory,
            ITransactionProcessorFactory txProcessorFactory,
            CodeInfoRepositoryFactory codeInfoRepositoryFactory)
        {
            _blockHashProvider = blockHashProvider;
            _specProvider = specProvider;
            _stateProvider = stateProvider;
            _logManager = logManager;
            _txProcessorFactory = txProcessorFactory;
            _codeInfoRepositoryFactory = codeInfoRepositoryFactory;
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
            ClearParentReaders();
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
        public TxProcessorWithWorldState Get(uint? balIndex = null)
        {
            if (_currentBlock is null) ThrowNotInitialized(nameof(_currentBlock));

            int idx = ClampBalIndex(balIndex ?? 0u);

            // Re-entrant Get for the same balIndex returns the already-acquired processor
            // (lets pre/post callers share state across calls — main thread only).
            TxProcessorWithWorldState? existing = _inUse[idx];
            if (existing is not null) return existing;

            TxProcessorWithWorldState processor = RentProcessor();

            try
            {
                ParentReaderLease? parentReader = GetOrCreateParentReader(processor);
                // Install a fresh BAL before Setup so the worker has somewhere to record changes.
                processor.WorldState.SetGeneratingBlockAccessList(StaticPool<BlockAccessListAtIndex>.Rent());
                processor.Setup(_currentBlock, _currentCtx, (uint)idx, parentReader);
                _inUse[idx] = processor;
                return processor;
            }
            catch
            {
                if (processor.WorldState.GetGeneratingBlockAccessList() is { } generatedBal)
                {
                    StaticPool<BlockAccessListAtIndex>.Return(generatedBal);
                }
                processor.WorldState.SetGeneratingBlockAccessList(null);
                // Dispose any parent reader Setup may have installed so the recycled slot isn't poisoned.
                processor.ClearParentReader();
                ReturnProcessor(processor);
                throw;
            }
        }

        /// <summary>
        /// Detaches the worker's populated BAL into the per-tx slot and recycles the processor
        /// immediately, so workers never block on the validator.
        /// </summary>
        public void Return(uint balIndex)
        {
            int idx = ClampBalIndex(balIndex);

            TxProcessorWithWorldState? processor = _inUse[idx];
            if (processor is null) return;

            _perTxBal[idx] = processor.WorldState.GetGeneratingBlockAccessList();
            processor.WorldState.SetGeneratingBlockAccessList(null);
            processor.DetachParentReader();
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

        public void NextTransaction() { }

        public void Rollback() { }

        public void Dispose()
        {
            ClearParentReaders();
            (_parentReaderEnvPool as IDisposable)?.Dispose();
        }

        private int ClampBalIndex(uint balIndex)
            => (int)uint.Min(balIndex, (uint)_lastBalIndex);

        private TxProcessorWithWorldState NewProcessor()
        {
            TxProcessorWithWorldState processor = new(true, _blockHashProvider, _specProvider, _stateProvider, _logManager, _txProcessorFactory, _codeInfoRepositoryFactory);
            _allProcessors.Add(processor);
            return processor;
        }

        private TxProcessorWithWorldState RentProcessor()
        {
            if (Volatile.Read(ref _processorCount) > 0 && _processors.TryDequeue(out TxProcessorWithWorldState? p))
            {
                Interlocked.Decrement(ref _processorCount);
                return p;
            }
            return NewProcessor();
        }

        private void ReturnProcessor(TxProcessorWithWorldState p)
        {
            if (Interlocked.Increment(ref _processorCount) > ProcessorPoolSize)
            {
                Interlocked.Decrement(ref _processorCount);
                p.ClearParentReader();
                return;
            }
            _processors.Enqueue(p);
        }

        private ParentReaderLease? GetOrCreateParentReader(TxProcessorWithWorldState processor)
        {
            if (_parentReaderEnvPool is null)
            {
                return null;
            }

            if (_parentStateHeader is null) ThrowNotInitialized(nameof(_parentStateHeader));

            if (processor.ParentReader is { } existingParentReader)
            {
                return existingParentReader;
            }

            IReadOnlyTxProcessorSource source = _parentReaderEnvPool.Get();
            try
            {
                ParentReaderLease newParentReader = new(source, _parentReaderEnvPool, source.Build(_parentStateHeader));
                processor.ParentReader = newParentReader;
                return newParentReader;
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

        private void ClearParentReaders()
        {
            foreach (TxProcessorWithWorldState processor in _allProcessors)
            {
                processor.ClearParentReader();
            }
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
    }

    private class SequentialTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState;

        public SequentialTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager,
            ITransactionProcessorFactory txProcessorFactory,
            CodeInfoRepositoryFactory codeInfoRepositoryFactory)
        {
            _txProcessorWithWorldState = new(false, blockHashProvider, specProvider, stateProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory);
            _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(new());
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
            => _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0u, parentReader: null);

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void Dispose() { }

        public void MergeAndReturnBal(uint _, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null)
        {
            BlockAccessListAtIndex slice = _txProcessorWithWorldState.WorldState.GetGeneratingBlockAccessList()!;
            target?.Merge(slice);
            onSlice?.Invoke(slice);
        }
    }

    private class TxProcessorWithWorldState
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly ITransactionProcessor TxProcessor;
        public readonly ExecuteTransactionProcessorAdapter TxProcessorAdapter;
        private readonly BlockAccessListBasedWorldState? _balWorldState;
        private bool _parentReaderAttached;

        public ParentReaderLease? ParentReader { get; set; }

        public TxProcessorWithWorldState(
            bool parallel,
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager,
            ITransactionProcessorFactory txProcessorFactory,
            CodeInfoRepositoryFactory codeInfoRepositoryFactory)
        {

            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            IWorldState worldState = stateProvider;
            if (parallel)
            {
                _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
                worldState = _balWorldState;
            }
            WorldState = new TracedAccessWorldState(worldState, parallel);
            ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(WorldState);
            TxProcessor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager, parallel);
            TxProcessorAdapter = new(TxProcessor);
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
        {
            if (_parentReaderAttached) ThrowParentReaderStillAttached();

            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            if (_balWorldState is not null)
            {
                if (parentReader is null) ThrowParentReaderUnavailable();
                _balWorldState.SetParentReader(parentReader.WorldState);
                _balWorldState.Setup(block);
                _parentReaderAttached = true;
            }
        }

        public void DetachParentReader()
        {
            _balWorldState?.ClearParentReader();
            _parentReaderAttached = false;
        }

        public void ClearParentReader()
        {
            DetachParentReader();
            ParentReader?.Dispose();
            ParentReader = null;
        }

        [DoesNotReturn]
        private static void ThrowParentReaderStillAttached()
            => throw new InvalidOperationException("Previous parent reader was not cleared before reusing this processor.");

        [DoesNotReturn]
        private static void ThrowParentReaderUnavailable()
            => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
    }

    // RAII wrapper around a borrowed read-only tx-processing env: holds the pooled source
    // plus the scope built against the parent state root, and returns the source to its
    // pool when disposed. Used by parallel workers so each tx gets its own snapshot reader
    // without contending on the mutable state provider.
    private sealed class ParentReaderLease(
        IReadOnlyTxProcessorSource source,
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        IReadOnlyTxProcessingScope scope) : IDisposable
    {
        private IReadOnlyTxProcessorSource? _source = source;
        private IReadOnlyTxProcessingScope? _scope = scope;

        public IWorldState WorldState => _scope?.WorldState ?? ThrowDisposed();

        public void Dispose()
        {
            IReadOnlyTxProcessingScope? scope = _scope;
            IReadOnlyTxProcessorSource? src = _source;
            _scope = null;
            _source = null;
            scope?.Dispose();
            if (src is not null) envPool.Return(src);
        }

        [DoesNotReturn]
        private static IWorldState ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ParentReaderLease));
    }

    private sealed class ReadOnlyTxProcessingEnvPooledObjectPolicy(
        IReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }
}
