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
/// The goal is to have very low latency in the enqueue so that it does not slow down block processing. An exception
/// is the PushJobMulti which is called from prewarmer.
/// Additionally, it must not take up a lot of CPU as prewarmer is also run concurrently. Taking up CPU cycle will
/// slow down other part of the processing also.
/// </summary>
public sealed class TrieWarmer : ITrieWarmer, IAsyncDisposable
{
    private const int BufferSize = 1024 * 16;
    private const int SlotBufferSize = 1024;

    private readonly SpmcRingBuffer<SlotJob> _slotJobBuffer = new(SlotBufferSize);
    private readonly MpmcRingBuffer<Job> _jobBufferMulti = new(BufferSize);

    // A job need to be small, within one cache line (64B) ideally.
    private record struct Job(
        // If its warming up address, its a scope, otherwise, its a storage tree.
        object scopeOrStorageTree,
        Address? path,
        UInt256 index,
        int sequenceId,
        bool isWrite);

    // A slot hint from the main processing thread is called a lot so it has its own dedicated queue.
    private record struct SlotJob(
        ITrieWarmer.IStorageWarmer storageTree,
        UInt256 index,
        int sequenceId,
        bool isWrite);

    private Task? _warmerJob = null;

    private readonly int _secondaryWorkerCount;

    private int _pendingWakeUpSlots = 0;
    private int _activeSecondaryWorker = 0;
    private int _shouldWakeUpPrimaryWorker = 0;
    private readonly ManualResetEventSlim _primaryWorkerLatch = new ManualResetEventSlim();

    // Use a full semaphore instead of the slim variant to reduce the spin used and prefer to not wake up thread until
    // needed. Only the main worker spin.
    private readonly Semaphore _executionSlots;

    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager, IFlatDbConfig flatDbConfig)
    {
        int configuredWorkerCount = flatDbConfig.TrieWarmerWorkerCount;
        int workerCount = configuredWorkerCount == -1
            ? Math.Max(Environment.ProcessorCount - 1, 1)
            : configuredWorkerCount;
        workerCount = Math.Max(workerCount, 2); // Min worker count is 2
        _secondaryWorkerCount = workerCount - 1;

        _executionSlots = new Semaphore(0, _secondaryWorkerCount);

        if (_secondaryWorkerCount > 0)
        {
            _warmerJob = Task.Run(() =>
            {
                using ArrayPoolList<Thread> tasks = new ArrayPoolList<Thread>(_secondaryWorkerCount);
                Thread primaryWorkerThread = new Thread(() =>
                {
                    RunPrimaryWorker(processExitSource.Token);
                });
                primaryWorkerThread.Name = "TrieWarmer-Primary";
                primaryWorkerThread.Start();
                tasks.Add(primaryWorkerThread);

                for (int i = 0; i < _secondaryWorkerCount; i++)
                {
                    Thread t = new Thread(() =>
                    {
                        RunSecondaryWorker(processExitSource.Token);
                    });
                    t.Name = $"TrieWarmer-Secondary-{i}";
                    t.Priority = ThreadPriority.Lowest;
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

                if (TryDequeue(out var job))
                {
                    spinWait.Reset();
                    MaybeWakeOpOtherWorker();

                    HandleJob(job, true);
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error in warmup job " + ex);
        }
    }

    public void RunSecondaryWorker(CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _activeSecondaryWorker);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (TryDequeue(out var job))
                {
                    HandleJob(job, false);
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error in warmup job " + ex);
        }
    }

    private bool WaitForExecutionSlot()
    {
        // Some wait but not forever so that it exit properly
        return _executionSlots.WaitOne(500);
    }

    private bool ShouldWakeUpMoreWorker()
    {
        // Assume that for each pending job, it go to the respective worker.
        int effectiveActiveWorker = _activeSecondaryWorker + _pendingWakeUpSlots;
        if (effectiveActiveWorker >= _secondaryWorkerCount) return false; // We cant wake up more worker

        // We should wake up more worker if the num of job is more than effective active worker

        // We go check the queue one by one because they each do a volatile read
        long jobCount = _jobBufferMulti.EstimatedJobCount;
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

    private void MaybeWakeOpOtherWorkerSingle()
    {
        if (!ShouldWakeUpMoreWorker()) return;

        // Yes, this could mean that two concurrent attempt both does not fill the worker count, but its fine, primary worker
        // should do this more properly.
        if (Interlocked.Increment(ref _pendingWakeUpSlots) + _activeSecondaryWorker > _secondaryWorkerCount)
        {
            Interlocked.Decrement(ref _pendingWakeUpSlots);
            return;
        }

        try
        {
            _executionSlots.Release();
        }
        catch (SemaphoreFullException)
        {
            Console.Error.WriteLine($"Throw 1, {_activeSecondaryWorker}:{_secondaryWorkerCount}");
            Interlocked.Decrement(ref _pendingWakeUpSlots);
        }
    }

    private bool MaybeWakeupFast()
    {
        // Skipping wakeup due to non atomic read is fine. Doing atomic operation all the time is really slow
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
                slotJob.sequenceId,
                slotJob.isWrite);
            return true;
        }

        return _jobBufferMulti.TryDequeue(out job);
    }

    private static void HandleJob(Job job, bool isMain)
    {
        (object scopeOrStorageTree,
            Address? address,
            UInt256 index,
            int sequenceId,
            bool isWrite) = job;

        try
        {
            if (scopeOrStorageTree is ITrieWarmer.IAddressWarmer scope)
            {
                scope.WarmUpStateTrie(address!, sequenceId, isWrite);
            }
            else
            {
                ITrieWarmer.IStorageWarmer storageTree = (ITrieWarmer.IStorageWarmer)scopeOrStorageTree;
                storageTree.WarmUpStorageTrie(index, sequenceId, isWrite);
            }
        }
        catch (TrieNodeException)
        {
            // It can be missing when the warmer lags so much behind that the node is now gone.
        }
        catch (NodeHashMismatchException)
        {
            // Because it run in parallel, it could happen that the bundle changed which causes this.
        }
        catch (ObjectDisposedException)
        {
            // Because it run in parallel, it could be that the scope is disposed early.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId, bool isWrite)
    {
        // Address is not single threaded. In which case, might as well use the same buffer.
        if (_jobBufferMulti.TryEnqueue(new Job(scope, path, default, sequenceId, isWrite)))
        {
            MaybeWakeupFast();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256? index, int sequenceId, bool isWrite)
    {
        if (_slotJobBuffer.TryEnqueue(new SlotJob(storageTree, index.GetValueOrDefault(), sequenceId, isWrite)))
        {
            MaybeWakeupFast();
        }
    }

    public void PushJobMulti(ITrieWarmer.IAddressWarmer scope, Address? path, ITrieWarmer.IStorageWarmer? storageTree, in UInt256? index,
        int sequenceId, bool isWrite)
    {
        bool queued;

        if (storageTree is null)
        {
            queued = _jobBufferMulti.TryEnqueue(new Job(scope, path, index.GetValueOrDefault(), sequenceId, isWrite));
        }
        else
        {
            queued = _jobBufferMulti.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId, isWrite));
        }

        if (!queued) return;
        if (MaybeWakeupFast()) return;
        MaybeWakeOpOtherWorkerSingle(); // Multi does not block main block processing, so we can do slow things here.
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
            if (!_jobBufferMulti.TryDequeue(out Job _)) break;
        }

        _primaryWorkerLatch.Set();
    }

    public void OnExitScope()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (_warmerJob is not null) await _warmerJob;
        _executionSlots.Dispose();
    }
}
