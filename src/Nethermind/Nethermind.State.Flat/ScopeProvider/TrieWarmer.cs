// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Config;
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
    private readonly ManualResetEventSlim _processorsIdle = new(initialState: true);
    private readonly CancellationTokenSource _cancelTokenSource;

    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        _logger = logManager.GetClassLogger<TrieWarmer>();

        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount / 2, 1)
            : configuredWorkerCount;
        workerCount = Math.Max(workerCount, 2); // Min worker count is 2

        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

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
        for (int i = 0; i < desiredProcessors; i++)
        {
            _processors[i].TrySchedule();
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
        // When the scope is disposed, it set some of the dictionary to null to prevent corrupting later state
        catch (NullReferenceException) { }
    }

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
    public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256? index, int sequenceId)
    {
        if (Volatile.Read(ref _isDisposed)) return false;

        bool enqueued = _slotJobBuffer.TryEnqueue(new SlotJob(storageTree, index.GetValueOrDefault(), sequenceId));
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

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return ValueTask.CompletedTask;

        _cancelTokenSource.Cancel();
        KickProcessors();

        bool processorsStopped = WaitForProcessorsToStop();
        if (!processorsStopped && _logger.IsWarn)
        {
            _logger.Warn($"TrieWarmer processors ({Volatile.Read(ref _activeProcessors)}) did not stop within {DisposeTimeoutMilliseconds}ms during dispose");
        }

        _cancelTokenSource.Dispose();
        // A push can pass the disposed check before DisposeAsync starts and still need to schedule
        // accepted work. Keep the process-lifetime wait handle alive so that race cannot observe a
        // disposed event while flushing the accepted job.

        return ValueTask.CompletedTask;
    }

    private void OnProcessorScheduled()
    {
        Interlocked.Increment(ref _activeProcessors);
        _processorsIdle.Reset();
    }

    private void OnProcessorStopped()
    {
        if (Interlocked.Decrement(ref _activeProcessors) == 0)
        {
            _processorsIdle.Set();
        }
    }

    private bool WaitForProcessorsToStop()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _activeProcessors) != 0)
        {
            int remainingMilliseconds = DisposeTimeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds;
            if (remainingMilliseconds <= 0) return Volatile.Read(ref _activeProcessors) == 0;
            if (!_processorsIdle.Wait(remainingMilliseconds)) return Volatile.Read(ref _activeProcessors) == 0;
        }

        return true;
    }
}
