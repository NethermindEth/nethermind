// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Nested tx-processor pool internals. Two implementations of
/// <see cref="ITxProcessorWithWorldStateManager"/>:
///   * <see cref="ParallelTxProcessorWithWorldStateManager"/> rents/returns processors per
///     tx index from a bounded pool and stages each tx's BAL slice in <c>_perTxBal</c> so
///     the validator can merge them in canonical order. Each processor keeps a pooled
///     <see cref="ParentReaderLease"/> for the whole block — a snapshot of the parent-state
///     world from which the BAL-backed world state reads any value the suggested BAL doesn't
///     carry at the current index. The lease is attached per tx. Once the last tx slot is rented,
///     each worker releases the lease of the processor it returns, and the first one also releases
///     the leases of idle processors. The post-execution merge releases any left, normally none, the
///     next block's <c>Setup</c> covers a block that failed, and disposing a processor releases its lease.
///   * <see cref="SequentialTxProcessorWithWorldStateManager"/> reuses a single processor
///     for the whole block.
/// <see cref="TxProcessorWithWorldState"/> bundles the tx processor with its world states
/// (BAL-backed in parallel mode, plain in sequential).
/// </summary>
public partial class BlockAccessListManager
{
    private interface ITxProcessorWithWorldStateManager : IDisposable
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext, BalReadStoragePlan? readPlan);
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

        // A plain ETH transfer journals at most 8 changes: sender nonce, sender balance (gas, value, refund),
        // recipient existence and balance, and coinbase balance.
        private const int SmallBalChangeCapacity = 8;

        // A plain ETH transfer touches three accounts: sender, recipient and coinbase. Reads add no journal
        // entries, so a small slice must cap its spares itself to bound what the small pool retains.
        private const int SmallBalMaxPooledAccountChanges = 4;

        // Plain-transfer slices wait for the in-order merge, so far more than BalPoolSize are rented at once.
        private const int SmallBalPoolSize = 1024;

        private static readonly ObjectPool<BlockAccessListAtIndex> SmallBalPool =
            new DefaultObjectPool<BlockAccessListAtIndex>(
                new BalPooledObjectPolicy(static () => new(SmallBalChangeCapacity, SmallBalMaxPooledAccountChanges)), SmallBalPoolSize);

        private static readonly ObjectPool<BlockAccessListAtIndex> LargeBalPool =
            new DefaultObjectPool<BlockAccessListAtIndex>(new BalPooledObjectPolicy(static () => new()), BalPoolSize);

        static ParallelTxProcessorWithWorldStateManager()
        {
            for (int i = 0; i < BalPoolSize; i++)
            {
                LargeBalPool.Return(new());
            }
        }

        private Block? _currentBlock;
        private BlockExecutionContext _currentCtx;
        private int _lastBalIndex;
        private BalReadStoragePlan? _readPlan;

        // _inUse[i] is the processor currently bound to balIndex i.
        private TxProcessorWithWorldState?[] _inUse = new TxProcessorWithWorldState?[DefaultTxCount];

        // _perTxBal[i] holds its detached BAL until the validator merges it in order.
        private BlockAccessListAtIndex?[] _perTxBal = new BlockAccessListAtIndex?[DefaultTxCount];

        // processors are not shared statically between BAL managers
        private readonly ConcurrentQueue<TxProcessorWithWorldState> _processors = [];
        private readonly IWorldState _stateProvider;
        private readonly ILogManager _logManager;
        private readonly BalTxProcessorFactory _txProcessorFactory;
        private readonly ObjectPool<IReadOnlyTxProcessorSource>? _parentReaderEnvPool;
        private int _processorCount;

        // Transaction slots of the block not rented yet. Once it is zero, every processor returned from a transaction has
        // served its last one in the block, so its worker releases its parent reader instead of the block thread.
        private int _txRentsLeft;

        // Elects the one worker that releases the readers still kept by idle processors after the last rent.
        private int _keptReadersReleased;

        public ParallelTxProcessorWithWorldStateManager(
            IWorldState stateProvider,
            ILogManager logManager,
            PrewarmerEnvFactory? prewarmerEnvFactory,
            PreBlockCaches? preBlockCaches,
            IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory,
            BalTxProcessorFactory txProcessorFactory)
        {
            _stateProvider = stateProvider;
            _logManager = logManager;
            _txProcessorFactory = txProcessorFactory;
            _parentReaderEnvPool = CreateParentReaderEnvPool(prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory);
            for (int i = 0; i < ProcessorPoolSize; i++)
            {
                _processors.Enqueue(NewProcessor());
                _processorCount++;
            }
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, BalReadStoragePlan? readPlan)
        {
            _readPlan = readPlan;
            _currentBlock = block;
            _currentCtx = blockExecutionContext;

            int previousSize = _lastBalIndex + 1;
            int newLastBalIndex = block.Transactions.Length + 1;
            ReclaimAndResize(newLastBalIndex + 1, previousSize);
            _lastBalIndex = newLastBalIndex;

            // Normally released by the workers once the last transaction is rented; this covers a block that failed before it.
            ReleaseKeptParentReaders();
            _txRentsLeft = block.Transactions.Length;
            _keptReadersReleased = 0;
        }

        /// <summary>A parent reader serves one block: it reads that block's parent state through caches that change
        /// between blocks, and holds the snapshots under it until released.</summary>
        /// <remarks>Runs while no worker rents a processor, though other workers may still return theirs. It may miss a
        /// processor returned meanwhile, which the release at the post-execution merge then covers, and a processor
        /// visited twice releases nothing the second time. It rotates the queue instead of enumerating it, because
        /// enumerating a <see cref="ConcurrentQueue{T}"/> freezes its segments and makes the next enqueues allocate new ones.</remarks>
        public void ReleaseKeptParentReaders()
        {
            int pooled = Volatile.Read(ref _processorCount);
            for (int i = 0; i < pooled && _processors.TryDequeue(out TxProcessorWithWorldState? processor); i++)
            {
                processor.ReleaseParentReader();
                _processors.Enqueue(processor);
            }
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
            ParentReaderLease? parentReader = null;

            try
            {
                BlockHeader header = _currentBlock.Header;
                parentReader = processor.GetParentReader(header) ?? RentParentReader(header);
                // Install a fresh BAL before Setup so the worker has somewhere to record changes.
                processor.WorldState.SetGeneratingBlockAccessList(RentBal(idx));
                processor.Setup(_currentBlock, _currentCtx, (uint)idx, parentReader, _readPlan);
                _inUse[idx] = processor;
                if (IsTransactionSlot(idx)) Interlocked.Decrement(ref _txRentsLeft);
                return processor;
            }
            catch
            {
                // A kept lease is released with the processor below; only a lease the processor does not hold yet is disposed here.
                if (parentReader is not null && !processor.Keeps(parentReader)) parentReader.Dispose();
                if (processor.WorldState.GetGeneratingBlockAccessList() is { } generatedBal)
                {
                    ReturnBal(generatedBal);
                }
                processor.WorldState.SetGeneratingBlockAccessList(null);
                // Release the parent reader too, so the recycled slot isn't poisoned.
                processor.ReleaseParentReader();
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

            BlockAccessListAtIndex? slice = processor.WorldState.GetGeneratingBlockAccessList();
            if (slice is not null) slice.CoveredStorageReads = processor.WorldState.ReadCoverage?.ChargeableReadCount ?? 0;
            _perTxBal[idx] = slice;
            processor.WorldState.SetGeneratingBlockAccessList(null);
            processor.DetachParentReader();
            _inUse[idx] = null;

            // No later transaction of the block rents a processor, so this is its last use in the block: release the
            // reader here, on the worker, in parallel with the other workers.
            bool lastUse = idx == _lastBalIndex || (IsTransactionSlot(idx) && Volatile.Read(ref _txRentsLeft) <= 0);
            if (lastUse) processor.ReleaseParentReader();
            ReturnProcessor(processor);
            if (lastUse && IsTransactionSlot(idx) && Interlocked.Exchange(ref _keptReadersReleased, 1) == 0) ReleaseKeptParentReaders();
        }

        private bool IsTransactionSlot(int idx) => idx > 0 && idx < _lastBalIndex;

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
            // Normally a no-op: the workers released the readers after the last rent. This covers a transaction slot that
            // was never rented, and a processor returned to the pool after the worker that released the idle ones checked it.
            if (idx == _lastBalIndex) ReleaseKeptParentReaders();

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
                ReturnBal(source);
            }
        }

        public void NextTransaction() { }

        public void Rollback() { }

        public void Dispose()
        {
            foreach (TxProcessorWithWorldState? processor in _inUse)
            {
                processor?.Dispose();
            }

            while (_processors.TryDequeue(out TxProcessorWithWorldState? processor))
            {
                processor.Dispose();
            }

            // After the processors, which return their parent readers' sources to this pool.
            (_parentReaderEnvPool as IDisposable)?.Dispose();
        }

        private int ClampBalIndex(uint balIndex)
            => (int)uint.Min(balIndex, (uint)_lastBalIndex);

        // A tx without data is most likely a plain transfer; one that runs code grows its slice and moves to the large pool.
        private BlockAccessListAtIndex RentBal(int idx)
            => (IsTransactionSlot(idx) && _currentBlock!.Transactions[idx - 1].Data.IsEmpty ? SmallBalPool : LargeBalPool).Get();

        private static void ReturnBal(BlockAccessListAtIndex bal)
            => (bal.ChangeCapacity <= SmallBalChangeCapacity ? SmallBalPool : LargeBalPool).Return(bal);

        private TxProcessorWithWorldState NewProcessor()
            => new(true, _stateProvider, _logManager, _txProcessorFactory);

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
                p.Dispose();
                return;
            }
            _processors.Enqueue(p);
        }

        private ParentReaderLease? RentParentReader(BlockHeader targetBlock)
        {
            if (_parentReaderEnvPool is null)
            {
                return null;
            }

            IReadOnlyTxProcessorSource source = _parentReaderEnvPool.Get();
            if (!source.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? scope))
            {
                _parentReaderEnvPool.Return(source);
                ThrowParentStateUnavailable(targetBlock);
            }

            return new ParentReaderLease(source, _parentReaderEnvPool, scope, targetBlock);
        }

        [DoesNotReturn]
        private static void ThrowParentStateUnavailable(BlockHeader targetBlock)
            => throw new StateNotRetainedException($"Parent state is unavailable for block {targetBlock.ToString(BlockHeader.Format.Short)}.");

        private void ReclaimAndResize(int size, int previousSize)
        {
            for (int i = 0; i < previousSize; i++)
                if (_inUse[i] is not null) Return((uint)i);

            for (int i = 0; i < previousSize; i++)
            {
                if (_perTxBal[i] is { } bal)
                {
                    ReturnBal(bal);
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
                return provider.Create(new PrewarmerEnvPooledObjectPolicy(prewarmerEnvFactory, preBlockCaches));
            }

            return readOnlyTxProcessingEnvFactory is not null
                ? provider.Create(new ReadOnlyTxProcessingEnvPooledObjectPolicy(readOnlyTxProcessingEnvFactory))
                : null;
        }

    }

    private class SequentialTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState;

        public SequentialTxProcessorWithWorldStateManager(
            IWorldState stateProvider,
            ILogManager logManager,
            BalTxProcessorFactory txProcessorFactory)
        {
            _txProcessorWithWorldState = new(false, stateProvider, logManager, txProcessorFactory);
            _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(new());
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, BalReadStoragePlan? readPlan)
        {
            if (readPlan is not null)
                ThrowReadCoverageUnavailable();
            _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0u, parentReader: null);
        }

        [DoesNotReturn]
        private static void ThrowReadCoverageUnavailable()
            => throw new InvalidOperationException("Read coverage requires parallel execution.");

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void Dispose() => _txProcessorWithWorldState.Dispose();

        public void MergeAndReturnBal(uint _, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null)
        {
            BlockAccessListAtIndex slice = _txProcessorWithWorldState.WorldState.GetGeneratingBlockAccessList()!;
            target?.Merge(slice);
            onSlice?.Invoke(slice);
        }
    }

    private class TxProcessorWithWorldState : IDisposable
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly ITransactionProcessor TxProcessor;
        public readonly ITransactionProcessorAdapter TxProcessorAdapter;
        public ExecutionRequestsProcessor ExecutionRequests => field ??= new ExecutionRequestsProcessor(TxProcessor);
        private readonly IDisposable _virtualMachine;
        private readonly BlockAccessListBasedWorldState? _balWorldState;
        private ParentReaderLease? _parentReader;
        private bool _parentReaderAttached;
        private BalReadCoverage? _readCoverage;

        public TxProcessorWithWorldState(
            bool parallel,
            IWorldState stateProvider,
            ILogManager logManager,
            BalTxProcessorFactory txProcessorFactory)
        {
            IWorldState worldState = stateProvider;
            if (parallel)
            {
                // A build without the capability never constructs the parallel pool; failing here
                // keeps a mis-wired processor from producing a wrong BAL.
                if (ZkEvmFlag.IsActive) ThrowParallelExecutionUnavailable();
                _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
                worldState = _balWorldState;
            }
            WorldState = new TracedAccessWorldState(worldState, parallel);
            (TxProcessor, TxProcessorAdapter, _virtualMachine) = txProcessorFactory.Create(WorldState, parallel);
        }

        /// <summary>
        /// Releases the kept parent reader and hands the data stacks the processor's virtual machine keeps back to the
        /// shared pool.
        /// </summary>
        public void Dispose()
        {
            ReleaseParentReader();
            _virtualMachine.Dispose();
        }

        /// <summary>The parent reader kept for <paramref name="targetBlock"/>; one kept for another block is released.</summary>
        public ParentReaderLease? GetParentReader(BlockHeader targetBlock)
        {
            if (_parentReader is { } parentReader && ReferenceEquals(parentReader.TargetBlock, targetBlock)) return parentReader;
            ReleaseParentReader();
            return null;
        }

        /// <remarks>
        /// The processor keeps <paramref name="parentReader"/> after the slice. Reusing it in a later slice of the block is
        /// safe: the parent reader only reads the parent state, which no slice writes, and the BAL-backed world state
        /// resets its own per-slice caches in its <c>Setup</c> and <c>ClearParentReader</c>.
        /// </remarks>
        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader, BalReadStoragePlan? readPlan = null)
        {
            if (_parentReaderAttached) ThrowParentReaderStillAttached();

            if (!ReferenceEquals(_parentReader, parentReader))
            {
                _parentReader?.Dispose();
                _parentReader = parentReader;
            }

            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            if (_balWorldState is not null)
            {
                if (parentReader is null) ThrowParentReaderUnavailable();
                _balWorldState.SetParentReader(parentReader.WorldState);
                _parentReaderAttached = true;
                if (readPlan is not null && !ReferenceEquals(_readCoverage?.Plan, readPlan))
                    _readCoverage = readPlan.CreateCoverage();
                BalReadCoverage? coverage = readPlan is null ? null : _readCoverage;
                coverage?.StartSlice();
                WorldState.ReadCoverage = coverage;
                _balWorldState.Setup(block, coverage);
            }
        }

        /// <summary>Ends the slice's use of the parent reader and keeps the lease for the next slice of the block.</summary>
        public void DetachParentReader()
        {
            WorldState.ReadCoverage = null;
            _balWorldState?.ClearParentReader();
            _parentReaderAttached = false;
        }

        public bool Keeps(ParentReaderLease parentReader) => ReferenceEquals(_parentReader, parentReader);

        public void ReleaseParentReader()
        {
            DetachParentReader();
            _parentReader?.Dispose();
            _parentReader = null;
        }

        [DoesNotReturn]
        private static void ThrowParallelExecutionUnavailable()
            => throw new NotSupportedException("Parallel execution is not available in this build.");

        [DoesNotReturn]
        private static void ThrowParentReaderStillAttached()
            => throw new InvalidOperationException("Previous parent reader was not detached before reusing this processor.");

        [DoesNotReturn]
        private static void ThrowParentReaderUnavailable()
            => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
    }

    // RAII wrapper around a borrowed read-only tx-processing env: holds the pooled source
    // plus the scope built against the parent state root, and returns the source to its
    // pool when disposed. Each parallel processor keeps one for the block, so it reads the
    // parent state without contending on the mutable state provider.
    private sealed class ParentReaderLease(
        IReadOnlyTxProcessorSource source,
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        IReadOnlyTxProcessingScope scope,
        BlockHeader targetBlock) : IDisposable
    {
        private IReadOnlyTxProcessorSource? _source = source;
        private IReadOnlyTxProcessingScope? _scope = scope;

        public BlockHeader TargetBlock { get; } = targetBlock;

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

    /// <remarks>The parent reader only reads state, so the env's access-list hints are of no use here.</remarks>
    private sealed class PrewarmerEnvPooledObjectPolicy(
        PrewarmerEnvFactory envFactory, PreBlockCaches preBlockCaches) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create(preBlockCaches);
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private sealed class BalPooledObjectPolicy(Func<BlockAccessListAtIndex> create) : IPooledObjectPolicy<BlockAccessListAtIndex>
    {
        public BlockAccessListAtIndex Create() => create();

        public bool Return(BlockAccessListAtIndex obj)
        {
            obj.Reset();
            return true;
        }
    }
}
