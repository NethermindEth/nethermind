// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// The trie warmer warms up the trie to speed up the final commit in block processing.
/// The goal is to have very low latency in the enqueue so that it does not slow down block processing.
/// Additionally, it must not take up a lot of CPU as prewarmer is also run concurrently. Taking up CPU cycle will
/// slow down other part of the processing also.
/// </summary>
public sealed class TrieWarmer : ITrieWarmer, IAsyncDisposable
{
    private const int BufferSize = 1024 * 16;
    private const int SlotBufferSize = 1024;
    private const int DisposeTimeoutMilliseconds = 1000;

    private readonly ILogger _logger;

    private bool _isDisposed = false;
    private int _activeProcessors = 0;

    private readonly SpmcRingBuffer<SlotJob> _slotJobBuffer = new(SlotBufferSize);

    // This was also used to store the job from prewarmer. It will be added back in another PR.
    private readonly MpmcRingBuffer<Job> _jobBufferMultiThreaded = new(BufferSize);

    // A job needs to be small, within one cache line (64B) ideally.
    private readonly record struct Job(
        // If its warming up address, its a scope, otherwise, its a storage tree.
        object scopeOrStorageTree,
        Address? path,
        UInt256 index,
        int sequenceId);

    // A slot hint from the main processing thread is called a lot, so it has its own dedicated queue with a smaller job struct.
    private readonly record struct SlotJob(
        ITrieWarmer.IStorageWarmer storageTree,
        UInt256 index,
        int sequenceId);

    private readonly Processor[] _processors;
    private TaskCompletionSource<bool>? _processorsStopped;

    // >1 enables coalescing of already-queued same-tree slot jobs into one MultiGet. 1 = off.
    private readonly int _batchSize;

    public TrieWarmer(ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        _logger = logManager.GetClassLogger<TrieWarmer>();

        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount / 2, 1)
            : configuredWorkerCount;
        workerCount = Math.Max(workerCount, 2); // Min worker count is 2

        _batchSize = Math.Max(1, Math.Min(flatDbConfig.TrieWarmerBatchSize, SlotBufferSize));

        _processors = new Processor[workerCount];
        for (int i = 0; i < _processors.Length; i++)
        {
            _processors[i] = new Processor(this);
        }
    }

    private sealed class Processor(TrieWarmer owner) : IThreadPoolWorkItem
    {
        private readonly TrieWarmer _owner = owner;
        private int _scheduled = 0;

        public bool TrySchedule()
        {
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return false;

            _owner.OnProcessorScheduled();
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return true;
        }

        public void ClearScheduled() => Volatile.Write(ref _scheduled, 0);

        public bool TryReacquireAfterEmptyCheck() => Interlocked.Exchange(ref _scheduled, 1) == 0;

        void IThreadPoolWorkItem.Execute() => _owner.Execute(this);
    }

    private bool HasReadyWork() => _slotJobBuffer.HasReadyItem || _jobBufferMultiThreaded.HasReadyItem;

    private long PendingHint() => _slotJobBuffer.EstimatedJobCount + _jobBufferMultiThreaded.EstimatedJobCount;

    private void KickProcessors()
    {
        long pending = PendingHint();
        int desiredProcessors = (int)Math.Min(_processors.Length, Math.Max(1, pending));
        int scheduledProcessors = 0;
        for (int i = 0; i < _processors.Length && scheduledProcessors < desiredProcessors; i++)
        {
            if (_processors[i].TrySchedule())
            {
                scheduledProcessors++;
            }
        }
    }

    private void Execute(Processor processor)
    {
        try
        {
            while (true)
            {
                if (_batchSize > 1)
                {
                    DrainSlotJobsBatched();
                }

                while (TryDequeue(out Job job))
                {
                    HandleJob(in job);
                }

                processor.ClearScheduled();
                Thread.MemoryBarrier();

                if (!HasReadyWork()) break;
                if (!processor.TryReacquireAfterEmptyCheck()) break;
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Error in trie warmer processor", ex);
            processor.ClearScheduled();
            KickProcessors();
        }
        finally
        {
            OnProcessorStopped();
        }
    }

    /// <summary>
    /// Drains up to <see cref="_batchSize"/> already-queued slot jobs from the dedicated slot buffer,
    /// groups them by (storage tree, sequenceId), and dispatches each group as a single coalesced
    /// MultiGet via <see cref="ITrieWarmer.IStorageWarmer.WarmUpStorageTrieBatch"/>; groups of one fall
    /// back to the single-read path. Never blocks/waits to fill a batch — it batches only what is
    /// already present (preserving the warmer's low-latency-enqueue design). Multiple worker threads may
    /// drain concurrently, so the drained set is NOT assumed contiguous-by-tree — hence the grouping.
    /// </summary>
    private void DrainSlotJobsBatched()
    {
        SlotJob[]? drained = null;
        int count = 0;

        while (count < _batchSize && _slotJobBuffer.TryDequeue(out SlotJob slotJob))
        {
            drained ??= ArrayPool<SlotJob>.Shared.Rent(_batchSize);
            drained[count++] = slotJob;
        }

        if (drained is null)
        {
            return;
        }

        try
        {
            DispatchDrainedSlotJobs(drained, count);
        }
        finally
        {
            ArrayPool<SlotJob>.Shared.Return(drained, clearArray: true);
        }
    }

    private void DispatchDrainedSlotJobs(SlotJob[] drained, int count)
    {
        Span<UInt256> indices = count <= 64 ? stackalloc UInt256[64] : new UInt256[count];

        // Group by (tree, sequenceId) using a simple O(n^2) scan: n <= _batchSize (small), and only
        // not-yet-dispatched entries are revisited. A processed entry is marked by nulling its tree.
        for (int i = 0; i < count; i++)
        {
            ITrieWarmer.IStorageWarmer? tree = drained[i].storageTree;
            if (tree is null) continue; // already grouped into an earlier batch

            int seq = drained[i].sequenceId;
            int groupCount = 0;
            indices[groupCount++] = drained[i].index;
            drained[i] = default; // mark consumed

            for (int j = i + 1; j < count; j++)
            {
                if (ReferenceEquals(drained[j].storageTree, tree) && drained[j].sequenceId == seq)
                {
                    indices[groupCount++] = drained[j].index;
                    drained[j] = default; // mark consumed
                }
            }

            if (groupCount == 1)
            {
                tree.WarmUpStorageTrie(indices[0], seq);
            }
            else
            {
                tree.WarmUpStorageTrieBatch(indices[..groupCount], seq, groupCount);
            }
        }
    }

    private bool TryDequeue(out Job job)
    {
        if (_slotJobBuffer.TryDequeue(out SlotJob slotJob))
        {
            job = new Job(
                slotJob.storageTree,
                null,
                slotJob.index,
                slotJob.sequenceId);
            return true;
        }

        return _jobBufferMultiThreaded.TryDequeue(out job);
    }

    private static void HandleJob(in Job job)
    {
        try
        {
            if (job.scopeOrStorageTree is ITrieWarmer.IAddressWarmer scope)
            {
                scope.WarmUpStateTrie(job.path!, job.sequenceId);
            }
            else
            {
                ITrieWarmer.IStorageWarmer storageTree = (ITrieWarmer.IStorageWarmer)job.scopeOrStorageTree;
                storageTree.WarmUpStorageTrie(job.index, job.sequenceId);
            }
        }
        // It can be missing when the warmer lags so much behind that the node is now gone.
        catch (TrieNodeException) { }
        // Because it runs in parallel, it could happen that the bundle changed, which causes this.
        catch (NodeHashMismatchException) { }
        // Because it runs in parallel, it could be that the scope is disposed of early.
        catch (ObjectDisposedException) { }
        // Scope disposal can null pooled snapshot maps while a queued warmup is already inside trie traversal.
        catch (NullReferenceException) when (IsDisposedJobTarget(in job)) { }
    }

    private static bool IsDisposedJobTarget(in Job job) =>
        job.scopeOrStorageTree switch
        {
            FlatWorldStateScope scope => scope.IsDisposed,
            FlatStorageTree storageTree => storageTree.IsDisposed,
            _ => false
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        // Address is not single threaded. In which case, might as well use the same buffer.
        bool enqueued = _jobBufferMultiThreaded.TryEnqueue(new Job(scope, path, default, sequenceId));
        if (enqueued) KickProcessors();
        return enqueued;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        bool enqueued = _slotJobBuffer.TryEnqueue(new SlotJob(storageTree, index, sequenceId));
        if (enqueued) KickProcessors();
        return enqueued;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        bool enqueued = _jobBufferMultiThreaded.TryEnqueue(new Job(storageTree, null, index, sequenceId));
        if (enqueued) KickProcessors();
        return enqueued;
    }

    public void OnEnterScope() { }

    public void OnExitScope() { }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        TaskCompletionSource<bool> processorsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _processorsStopped, processorsStopped);
        KickProcessors();

        if (Volatile.Read(ref _activeProcessors) == 0)
        {
            processorsStopped.TrySetResult(true);
        }

        bool processorsStoppedBeforeTimeout = await WaitForProcessorsToStopAsync(processorsStopped.Task).ConfigureAwait(false);
        if (!processorsStoppedBeforeTimeout && _logger.IsWarn)
        {
            _logger.Warn($"TrieWarmer processors ({Volatile.Read(ref _activeProcessors)}) did not stop within {DisposeTimeoutMilliseconds}ms during dispose");
        }
    }

    private void OnProcessorScheduled() => Interlocked.Increment(ref _activeProcessors);

    private void OnProcessorStopped()
    {
        if (Interlocked.Decrement(ref _activeProcessors) == 0)
        {
            Volatile.Read(ref _processorsStopped)?.TrySetResult(true);
        }
    }

    private static async ValueTask<bool> WaitForProcessorsToStopAsync(Task processorsStopped)
    {
        if (processorsStopped.IsCompleted) return true;

        Task timeout = Task.Delay(DisposeTimeoutMilliseconds);
        return await Task.WhenAny(processorsStopped, timeout).ConfigureAwait(false) == processorsStopped;
    }
}
