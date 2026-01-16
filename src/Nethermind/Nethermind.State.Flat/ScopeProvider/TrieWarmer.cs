// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
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

    private readonly ILogger _logger;

    private bool _isDisposed = false;

    private readonly SpmcRingBuffer<SlotJob> _slotJobBuffer = new(SlotBufferSize);

    // This was also used to store job from prewarmer. It will be added back in another PR.
    private readonly MpmcRingBuffer<Job> _jobBufferMultiThreaded = new(BufferSize);

    // A job need to be small, within one cache line (64B) ideally.
    private record struct Job(
        // If its warming up address, its a scope, otherwise, its a storage tree.
        object scopeOrStorageTree,
        Address? path,
        UInt256 index,
        int sequenceId);

    // A slot hint from the main processing thread is called a lot so it has its own dedicated queue with smaller job struct.
    private record struct SlotJob(
        ITrieWarmer.IStorageWarmer storageTree,
        UInt256 index,
        int sequenceId);

    private readonly Task? _warmerJob = null;
    private readonly int _secondaryWorkerCount;

    private int _pendingWakeUpSlots = 0;
    private int _activeSecondaryWorker = 0;
    private int _shouldWakeUpPrimaryWorker = 0;
    private readonly ManualResetEventSlim _primaryWorkerLatch = new ManualResetEventSlim();

    // Use a full semaphore instead of the slim variant to reduce the spin used and prefer to not wake up thread until
    // needed. Only the main worker spin.
    private readonly Semaphore _executionSlots;

    private CancellationTokenSource _cancelTokenSource;

    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        _logger = logManager.GetClassLogger<TrieWarmer>();

        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount - 1, 1)
            : configuredWorkerCount;
        workerCount = Math.Max(workerCount, 2); // Min worker count is 2
        _secondaryWorkerCount = workerCount - 1;

        _executionSlots = new Semaphore(0, _secondaryWorkerCount);

        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

        if (_secondaryWorkerCount > 0)
        {
            _warmerJob = Task.Run(() =>
            {
                using ArrayPoolList<Thread> tasks = new ArrayPoolList<Thread>(_secondaryWorkerCount);
                Thread primaryWorkerThread = new Thread(() =>
                {
                    RunPrimaryWorker(_cancelTokenSource.Token);
                });
                primaryWorkerThread.Name = "TrieWarmer-Primary";
                primaryWorkerThread.IsBackground = true;
                primaryWorkerThread.Start();
                tasks.Add(primaryWorkerThread);

                for (int i = 0; i < _secondaryWorkerCount; i++)
                {
                    Thread t = new Thread(() =>
                    {
                        RunSecondaryWorker(_cancelTokenSource.Token);
                    });
                    t.Name = $"TrieWarmer-Secondary-{i}";
                    t.Priority = ThreadPriority.Lowest;
                    t.IsBackground = true;
                    t.Start();
                    tasks.Add(t);
                }

                foreach (Thread thread in tasks)
                {
                    thread.Join();
                }
            });
        }
    }

    private void RunPrimaryWorker(CancellationToken cancellationToken)
    {
        SpinWait spinWait = new SpinWait();
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (TryDequeue(out Job job))
                {
                    spinWait.Reset();
                    MaybeWakeOpOtherWorker();

                    HandleJob(job);
                }
                else
                {
                    if (spinWait.NextSpinWillYield)
                    {
                        _primaryWorkerLatch.Reset();
                        _shouldWakeUpPrimaryWorker = 1;
                        _primaryWorkerLatch.Wait(1, cancellationToken);
                        _shouldWakeUpPrimaryWorker = 0;
                    }
                    else
                    {
                        spinWait.SpinOnce();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Error in primary warmup job ", ex);
        }
    }

    private void RunSecondaryWorker(CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _activeSecondaryWorker);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (TryDequeue(out Job job))
                {
                    HandleJob(job);
                }
                else
                {
                    Interlocked.Decrement(ref _activeSecondaryWorker);
                    if (WaitForExecutionSlot())
                    {
                        Interlocked.Decrement(ref _pendingWakeUpSlots);
                    }
                    Interlocked.Increment(ref _activeSecondaryWorker);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Error in warmup job ", ex);
        }
    }

    // Some wait but not forever so that it exit properly
    private bool WaitForExecutionSlot() => _executionSlots.WaitOne(500);

    private bool ShouldWakeUpMoreWorker()
    {
        // Assume that for each pending job, it go to the respective worker.
        int effectiveActiveWorker = _activeSecondaryWorker + _pendingWakeUpSlots;
        if (effectiveActiveWorker >= _secondaryWorkerCount) return false; // We cant wake up more worker

        // We should wake up more worker if the num of job is more than effective active worker

        // We go check the queue one by one because they each do a volatile read
        long jobCount = _jobBufferMultiThreaded.EstimatedJobCount;
        if (jobCount > effectiveActiveWorker) return true;

        jobCount += _slotJobBuffer.EstimatedJobCount;
        return jobCount > effectiveActiveWorker;
    }

    private bool MaybeWakeOpOtherWorker()
    {
        bool wokeUpWorker = false;

        // Release one by one until all job was dequeued
        while (ShouldWakeUpMoreWorker())
        {
            try
            {
                Interlocked.Increment(ref _pendingWakeUpSlots);
                _executionSlots.Release();
                wokeUpWorker = true;
            }
            catch (SemaphoreFullException)
            {
                Interlocked.Decrement(ref _pendingWakeUpSlots);
                break;
            }
        }

        return wokeUpWorker;
    }

    private bool MaybeWakeupFast()
    {
        // Skipping wakeup due to non atomic read is fine. Doing atomic operation all the time slows down measurably.
        if (_shouldWakeUpPrimaryWorker == 1)
        {
            _primaryWorkerLatch.Set();
            _shouldWakeUpPrimaryWorker = 0;
            return true;
        }

        return false;
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

    private static void HandleJob(Job job)
    {
        (object scopeOrStorageTree,
            Address? address,
            UInt256 index,
            int sequenceId) = job;

        try
        {
            if (scopeOrStorageTree is ITrieWarmer.IAddressWarmer scope)
            {
                scope.WarmUpStateTrie(address!, sequenceId);
            }
            else
            {
                ITrieWarmer.IStorageWarmer storageTree = (ITrieWarmer.IStorageWarmer)scopeOrStorageTree;
                storageTree.WarmUpStorageTrie(index, sequenceId);
            }
        }
        // It can be missing when the warmer lags so much behind that the node is now gone.
        catch (TrieNodeException) { }
        // Because it run in parallel, it could happen that the bundle changed which causes this.
        catch (NodeHashMismatchException) { }
        // Because it run in parallel, it could be that the scope is disposed early.
        catch (ObjectDisposedException) { }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
    {
        // Address is not single threaded. In which case, might as well use the same buffer.
        if (_jobBufferMultiThreaded.TryEnqueue(new Job(scope, path, default, sequenceId))) MaybeWakeupFast();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256? index, int sequenceId)
    {
        if (_slotJobBuffer.TryEnqueue(new SlotJob(storageTree, index.GetValueOrDefault(), sequenceId))) MaybeWakeupFast();
    }

    public void OnEnterScope()
    {
        // Drain any existing job
        for (int i = 0; i < SlotBufferSize; i++)
        {
            if (!_slotJobBuffer.TryDequeue(out SlotJob _)) break;
        }
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_jobBufferMultiThreaded.TryDequeue(out Job _)) break;
        }

        _primaryWorkerLatch.Set();
    }

    public void OnExitScope() { }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        _cancelTokenSource.Cancel();

        // Release semaphore so that worker detect the cancellation quickly
        while (true)
        {
            try
            {
                _executionSlots.Release();
            }
            catch (SemaphoreFullException)
            {
                break;
            }
        }

        if (_warmerJob is not null) await _warmerJob;
        _executionSlots.Dispose();
        _cancelTokenSource.Dispose();
    }
}
