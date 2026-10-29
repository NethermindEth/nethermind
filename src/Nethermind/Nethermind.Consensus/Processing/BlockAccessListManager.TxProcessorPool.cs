// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using CollectionsMarshal = System.Runtime.InteropServices.CollectionsMarshal;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Nested tx-processor pool internals. Two implementations of
/// <see cref="ITxProcessorWithWorldStateManager"/>:
///   * <see cref="ParallelTxProcessorWithWorldStateManager"/> rents/returns processors per
///     tx index from a bounded pool. Each processor records the indexes it runs into one
///     <see cref="BlockAccessListLog"/> for the block, so the block's list merges one log per
///     processor rather than one per transaction. Each processor keeps a pooled
///     <see cref="ParentReaderLease"/> for the whole block — a snapshot of the parent-state
///     world from which the BAL-backed world state reads any value the suggested BAL doesn't
///     carry at the current index. The lease is attached per tx. Once the last tx slot is rented,
///     each worker releases the lease of the processor it returns, and the first one also releases
///     the leases of idle processors. The post-execution merge releases any left, normally none, the
///     next block's <c>Setup</c> covers a block that failed, and disposing a processor releases its lease.
///   * <see cref="SequentialTxProcessorWithWorldStateManager"/> reuses a single processor
///     and a single log for the whole block.
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

        /// <summary>Returns the processors still held and seals the logs every index of the block was recorded in.</summary>
        ReadOnlySpan<BlockAccessListLog> SealLogs();
    }

    private class ParallelTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private const int DefaultTxCount = 10000;
        private static readonly int ProcessorPoolSize = RuntimeInformation.ProcessorCount;

        private Block? _currentBlock;
        private BlockExecutionContext _currentCtx;
        private BalReadStoragePlan? _readPlan;
        private int _lastBalIndex;

        // _inUse[i] is the processor currently bound to balIndex i.
        private TxProcessorWithWorldState?[] _inUse = new TxProcessorWithWorldState?[DefaultTxCount];

        // The logs of the block, one per processor that ran an index of it, and the cleared logs kept for later blocks.
        // A processor disposed by the pool leaves its log here, so the block's list still sees its indexes.
        private readonly List<BlockAccessListLog> _blockLogs = [];
        private readonly Stack<BlockAccessListLog> _freeLogs = new();
        private readonly Lock _logsLock = new();
        private int _blockGeneration;

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
            _currentBlock = block;
            _currentCtx = blockExecutionContext;
            _readPlan = readPlan;

            int previousSize = _lastBalIndex + 1;
            int newLastBalIndex = block.Transactions.Length + 1;
            ReclaimAndResize(newLastBalIndex + 1, previousSize);
            _lastBalIndex = newLastBalIndex;
            ReclaimLogs();

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

        // Thread-safety note for _inUse:
        //   Each balIndex slot has at most one writer at a time. Pre/post (idx 0 and
        //   _lastBalIndex) are written only by the main thread; tx slots (1..len) are
        //   each owned by a single parallel-loop iteration, so no two workers ever
        //   touch the same slot. Plain reads/writes are therefore sufficient.
        //   A processor's log is only written by the worker holding the processor, and
        //   the pool's queue orders one holder's writes before the next holder's.
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
                if (processor.LogGeneration != _blockGeneration)
                {
                    processor.Recorder.Log = RentLog();
                    processor.LogGeneration = _blockGeneration;
                }
                processor.WorldState.SetGeneratingBlockAccessList(processor.Recorder);
                processor.Setup(_currentBlock, _currentCtx, (uint)idx, parentReader, _readPlan);
                _inUse[idx] = processor;
                if (IsTransactionSlot(idx)) Interlocked.Decrement(ref _txRentsLeft);
                return processor;
            }
            catch
            {
                // A kept lease is released with the processor below; only a lease the processor does not hold yet is disposed here.
                if (parentReader is not null && !processor.Keeps(parentReader)) parentReader.Dispose();
                processor.WorldState.SetGeneratingBlockAccessList(null);
                // Release the parent reader too, so the recycled slot isn't poisoned.
                processor.ReleaseParentReader();
                ReturnProcessor(processor);
                throw;
            }
        }

        /// <summary>Recycles the processor immediately; what it recorded stays in its log for the block.</summary>
        public void Return(uint balIndex)
        {
            int idx = ClampBalIndex(balIndex);

            TxProcessorWithWorldState? processor = _inUse[idx];
            if (processor is null) return;

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

        /// <remarks>Runs once every index has executed, so no worker still writes a log.</remarks>
        public ReadOnlySpan<BlockAccessListLog> SealLogs()
        {
            Return(0u);
            Return((uint)_lastBalIndex);
            // Normally a no-op: the workers released the readers after the last rent. This covers a transaction slot that
            // was never rented, and a processor returned to the pool after the worker that released the idle ones checked it.
            ReleaseKeptParentReaders();

            ParallelUnbalancedWork.For(0, _blockLogs.Count, _blockLogs, static (i, logs) =>
            {
                logs[i].Seal();
                return logs;
            });
            return CollectionsMarshal.AsSpan(_blockLogs);
        }

        private BlockAccessListLog RentLog()
        {
            lock (_logsLock)
            {
                BlockAccessListLog log = _freeLogs.TryPop(out BlockAccessListLog? free) ? free : new();
                _blockLogs.Add(log);
                return log;
            }
        }

        private void ReclaimLogs()
        {
            _blockGeneration++;
            foreach (BlockAccessListLog log in _blockLogs)
            {
                log.Clear();
                _freeLogs.Push(log);
            }
            _blockLogs.Clear();
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

            if (_inUse.Length < size)
                Array.Resize(ref _inUse, Math.Max(2 * _inUse.Length, size));
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
        private readonly BlockAccessListLog[] _log = [new()];

        public SequentialTxProcessorWithWorldStateManager(
            IWorldState stateProvider,
            ILogManager logManager,
            BalTxProcessorFactory txProcessorFactory)
        {
            _txProcessorWithWorldState = new(false, stateProvider, logManager, txProcessorFactory);
            _txProcessorWithWorldState.Recorder.Log = _log[0];
            _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(_txProcessorWithWorldState.Recorder);
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, BalReadStoragePlan? _)
        {
            _log[0].Clear();
            _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0u, parentReader: null, readPlan: null);
        }

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction() => _txProcessorWithWorldState.WorldState.IncrementIndex();

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void Dispose() => _txProcessorWithWorldState.Dispose();

        public ReadOnlySpan<BlockAccessListLog> SealLogs()
        {
            _log[0].Seal();
            return _log;
        }
    }

    private class TxProcessorWithWorldState : IDisposable
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly BlockAccessIndexRecorder Recorder = new();
        public readonly ITransactionProcessor TxProcessor;
        public readonly ITransactionProcessorAdapter TxProcessorAdapter;
        public ExecutionRequestsProcessor ExecutionRequests => field ??= new ExecutionRequestsProcessor(TxProcessor);
        private readonly IDisposable _virtualMachine;
        private readonly BlockAccessListBasedWorldState? _balWorldState;
        private ParentReaderLease? _parentReader;
        private bool _parentReaderAttached;
        private BalReadCoverage? _readCoverage;

        /// <summary>The block generation <see cref="Recorder"/>'s log belongs to.</summary>
        public int LogGeneration;

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
        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader, BalReadStoragePlan? readPlan)
        {
            if (_parentReaderAttached) ThrowParentReaderStillAttached();

            if (!ReferenceEquals(_parentReader, parentReader))
            {
                _parentReader?.Dispose();
                _parentReader = parentReader;
            }

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
                WorldState.ReadCoverage = readPlan is null ? null : _readCoverage;
                _balWorldState.Setup(block);
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
}
