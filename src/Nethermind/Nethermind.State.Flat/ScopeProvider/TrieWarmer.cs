// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        int sequenceId,
        bool isDelete);

    // A slot hint from the main processing thread is called a lot, so it has its own dedicated queue with a smaller job struct.
    private readonly record struct SlotJob(
        ITrieWarmer.IStorageWarmer storageTree,
        UInt256 index,
        int sequenceId,
        bool isDelete);

    private readonly Processor[] _processors;
    private TaskCompletionSource<bool>? _processorsStopped;

    public TrieWarmer(ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        _logger = logManager.GetClassLogger<TrieWarmer>();

        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount * 3 / 4, 1)
            : configuredWorkerCount;
        workerCount = Math.Max(workerCount, 2); // Min worker count is 2

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
        int activeProcessors = Volatile.Read(ref _activeProcessors);
        if (activeProcessors >= _processors.Length) return;

        long pending = PendingHint();
        if (pending == 0) return;

        int desiredProcessors = (int)Math.Min(_processors.Length - activeProcessors, Math.Max(1, pending));
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
                while (TryDequeue(out Job job))
                {
                    HandleJobs(in job);
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

    private bool TryDequeue(out Job job)
    {
        if (_slotJobBuffer.TryDequeue(out SlotJob slotJob))
        {
            job = new Job(
                slotJob.storageTree,
                null,
                slotJob.index,
                slotJob.sequenceId,
                slotJob.isDelete);
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
                storageTree.WarmUpStorageTrie(job.index, job.sequenceId, job.isDelete);
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

    private void HandleJobs(in Job first)
    {
        long pendingCount = PendingHint();
        if (pendingCount < PatriciaTree.MinWarmUpBatchSize - 1 || !CanBatch(in first))
        {
            HandleJob(in first);
            return;
        }

        int batchSize = GetWarmUpBatchSize(pendingCount, Volatile.Read(ref _activeProcessors));
        if (batchSize < PatriciaTree.MinWarmUpBatchSize)
        {
            HandleJob(in first);
            return;
        }

        Job current = first;
        while (true)
        {
            if (!TryDequeue(out Job following))
            {
                HandleJob(in current);
                return;
            }

            if (!SameBatch(in current, in following))
            {
                try
                {
                    HandleJob(in current);
                }
                catch
                {
                    HandleJob(in following);
                    throw;
                }
                current = following;
            }
            else
            {
                if (!TryHandleBatch(in current, in following, batchSize, out Job pending)) return;
                current = pending;
            }

            pendingCount = PendingHint();
            if (pendingCount < PatriciaTree.MinWarmUpBatchSize - 1 || !CanBatch(in current))
            {
                HandleJob(in current);
                return;
            }
            batchSize = GetWarmUpBatchSize(pendingCount, Volatile.Read(ref _activeProcessors));
            if (batchSize < PatriciaTree.MinWarmUpBatchSize)
            {
                HandleJob(in current);
                return;
            }
        }
    }

    internal static int GetWarmUpBatchSize(long pending, int activeProcessors) =>
        (int)Math.Min(PatriciaTree.WarmUpBatchSize, (pending + 1) / Math.Max(1, activeProcessors));

    private static bool CanBatch(in Job job) => job.scopeOrStorageTree is FlatWorldStateScope or FlatStorageTree;

    private static bool SameBatch(in Job first, in Job next) =>
        first.sequenceId == next.sequenceId && (ReferenceEquals(first.scopeOrStorageTree, next.scopeOrStorageTree)
            || ReferenceEquals(GetBatchScope(in first), GetBatchScope(in next)));

    private static FlatWorldStateScope? GetBatchScope(in Job job) => job.scopeOrStorageTree switch
    {
        FlatWorldStateScope scope => scope,
        FlatStorageTree tree => tree.WarmupScope,
        _ => null
    };

    [InlineArray(PatriciaTree.WarmUpBatchSize)]
    private struct WarmUpHintBuffer
    {
        private FlatWorldStateScope.TrieWarmUpHint _first;
    }

    private bool TryHandleBatch(in Job first, in Job second, int batchSize, out Job pending)
    {
        Span<PatriciaTree.WarmUpEntry> entries = stackalloc PatriciaTree.WarmUpEntry[PatriciaTree.WarmUpBatchSize];
        FlatWorldStateScope scope = GetBatchScope(in first)!;
        int count = 2;
        pending = default;
        bool hasPending = false;
        bool completed = false;
        try
        {
            if (scope.IsWarmupStopped(first.sequenceId)) return false;
            entries[0] = ToWarmUpEntry(in first);
            if (!ReferenceEquals(first.scopeOrStorageTree, second.scopeOrStorageTree))
            {
                HandleMixedBatch(in first, in second, entries[..1], batchSize, scope, ref count, ref pending, ref hasPending);
                completed = true;
                return hasPending;
            }
            entries[1] = ToWarmUpEntry(in second);
            while (count < batchSize && TryDequeue(out Job next))
            {
                if (!SameBatch(in first, in next))
                {
                    pending = next;
                    hasPending = true;
                    break;
                }
                count++;
                if (!ReferenceEquals(first.scopeOrStorageTree, next.scopeOrStorageTree))
                {
                    HandleMixedBatch(in first, in next, entries[..(count - 1)], batchSize, scope, ref count, ref pending, ref hasPending);
                    completed = true;
                    return hasPending;
                }
                entries[count - 1] = ToWarmUpEntry(in next);
            }
            if (first.scopeOrStorageTree is FlatWorldStateScope)
                scope.WarmUpStateTrieBatch(entries[..count], first.sequenceId);
            else
                ((FlatStorageTree)first.scopeOrStorageTree).WarmUpStorageTrieBatch(entries[..count], first.sequenceId);
            completed = true;
        }
        catch (TrieNodeException) { completed = true; }
        catch (NodeHashMismatchException) { completed = true; }
        catch (ObjectDisposedException) { completed = true; }
        catch (NullReferenceException) when (IsDisposedJobTarget(in first)) { completed = true; }
        finally
        {
            scope.CompleteWarmups(count);
            if (!completed && hasPending) HandleJob(in pending);
        }
        return hasPending;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HandleMixedBatch(in Job first, in Job different, ReadOnlySpan<PatriciaTree.WarmUpEntry> prefix,
        int batchSize, FlatWorldStateScope scope, ref int count, ref Job pending, ref bool hasPending)
    {
        WarmUpHintBuffer hints = default;
        FlatStorageTree? storage = first.scopeOrStorageTree as FlatStorageTree;
        for (int i = 0; i < prefix.Length; i++) hints[i] = new(storage, prefix[i]);
        hints[count - 1] = new(different.scopeOrStorageTree as FlatStorageTree, ToWarmUpEntry(in different));
        while (count < batchSize && TryDequeue(out Job next))
        {
            if (!SameBatch(in first, in next))
            {
                pending = next;
                hasPending = true;
                break;
            }
            count++;
            hints[count - 1] = new(next.scopeOrStorageTree as FlatStorageTree, ToWarmUpEntry(in next));
        }
        scope.WarmUpTrieCohort(hints[..count], first.sequenceId);
    }

    private static PatriciaTree.WarmUpEntry ToWarmUpEntry(in Job job)
    {
        if (job.scopeOrStorageTree is FlatWorldStateScope) return new(job.path!.ToAccountPath);
        ValueHash256 key = default;
        StorageTree.ComputeKeyWithLookup(job.index, ref key);
        return new(key, job.isDelete);
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
        bool enqueued = _jobBufferMultiThreaded.TryEnqueue(new Job(scope, path, default, sequenceId, isDelete: false));
        if (enqueued) KickProcessors();
        return enqueued;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        bool enqueued = _slotJobBuffer.TryEnqueue(new SlotJob(storageTree, index, sequenceId, isDelete));
        if (enqueued) KickProcessors();
        return enqueued;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        bool enqueued = _jobBufferMultiThreaded.TryEnqueue(new Job(storageTree, null, index, sequenceId, isDelete));
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
