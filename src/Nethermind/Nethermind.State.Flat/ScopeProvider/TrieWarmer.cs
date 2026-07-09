// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private const int SlotBufferSize = 1024 * 8;
    private const int DisposeTimeoutMilliseconds = 1000;
    // Backlog depth past which extra workers cost less (in exec contention) than the cold
    // storage-merkle loads the un-warmed backlog would cause at commit.
    private const int BoostQueueDepth = 512;
    // Jobs drained per worker pass when the backlog is deep; batching lets same-contract slot
    // jobs share one traversal. Shallow queues drain one job at a time so a stalled cold read
    // in one worker never holds other queued jobs hostage.
    private const int JobBatchSize = 64;
    private const int BatchDrainQueueDepth = JobBatchSize * 2;

    private readonly ILogger _logger;

    private bool _isDisposed = false;
    private int _activeProcessors = 0;

    private readonly SpmcRingBuffer<SlotJob> _slotJobBuffer = new(SlotBufferSize);

    // Multi-producer jobs: BAL reads and prewarmer-driven warm hints.
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
    private readonly int _baseWorkerCount;
    private TaskCompletionSource<bool>? _processorsStopped;

    public TrieWarmer(ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        _logger = logManager.GetClassLogger<TrieWarmer>();

        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount / 2, 1)
            : configuredWorkerCount;
        _baseWorkerCount = Math.Max(workerCount, 2); // Min worker count is 2

        // Extra processors beyond the base count only run while the backlog is deeper than
        // BoostQueueDepth; boosted workers drain the burst to empty and then park again.
        int maxWorkerCount = Math.Max(_baseWorkerCount, Environment.ProcessorCount - 2);
        _processors = new Processor[maxWorkerCount];
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
        int activeProcessors = Volatile.Read(ref _activeProcessors);
        if (activeProcessors >= _processors.Length) return;

        long pending = PendingHint();
        if (pending == 0) return;

        int workerCap = pending >= BoostQueueDepth ? _processors.Length : _baseWorkerCount;
        if (activeProcessors >= workerCap) return;

        int desiredProcessors = (int)Math.Min(workerCap - activeProcessors, Math.Max(1, pending));
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
        Job[] batch = ArrayPool<Job>.Shared.Rent(JobBatchSize);
        UInt256[] indexBuffer = ArrayPool<UInt256>.Shared.Rent(JobBatchSize);
        try
        {
            while (true)
            {
                int drainLimit = PendingHint() >= BatchDrainQueueDepth ? JobBatchSize : 1;
                int count = 0;
                while (count < drainLimit && TryDequeue(out batch[count])) count++;
                if (count > 0)
                {
                    HandleJobs(batch.AsSpan(0, count), indexBuffer);
                    continue;
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
            ArrayPool<Job>.Shared.Return(batch, clearArray: true);
            ArrayPool<UInt256>.Shared.Return(indexBuffer);
            OnProcessorStopped();
        }
    }

    /// <summary>
    /// Dispatches a drained batch, grouping slot jobs by (storage tree, sequence) so each group
    /// warms in a single traversal instead of one full descent per slot.
    /// </summary>
    private static void HandleJobs(Span<Job> jobs, UInt256[] indexBuffer)
    {
        for (int i = 0; i < jobs.Length; i++)
        {
            object? target = jobs[i].scopeOrStorageTree;
            if (target is null) continue; // consumed by an earlier group

            if (target is ITrieWarmer.IAddressWarmer)
            {
                HandleJob(in jobs[i]);
                continue;
            }

            int sequenceId = jobs[i].sequenceId;
            int n = 0;
            indexBuffer[n++] = jobs[i].index;
            for (int j = i + 1; j < jobs.Length; j++)
            {
                if (ReferenceEquals(jobs[j].scopeOrStorageTree, target) && jobs[j].sequenceId == sequenceId)
                {
                    indexBuffer[n++] = jobs[j].index;
                    jobs[j] = default;
                }
            }

            if (n == 1)
            {
                HandleJob(in jobs[i]);
                continue;
            }

            try
            {
                ((ITrieWarmer.IStorageWarmer)target).WarmUpStorageTrieBatch(indexBuffer.AsSpan(0, n), sequenceId);
            }
            // Same racy-teardown tolerance as HandleJob; the batch decrements its own counters.
            catch (TrieNodeException) { }
            catch (NodeHashMismatchException) { }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) when (IsDisposedJobTarget(in jobs[i])) { }
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
