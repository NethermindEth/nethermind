// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class TrieWarmer : ITrieWarmer, IAsyncDisposable
{
    private const int BufferSize = 1024 * 16;
    private const int SlotBufferSize = 1024;

    private MpmcRingBuffer<Job> _addressJob = new MpmcRingBuffer<Job>(BufferSize);
    private SpmcRingBuffer<Job> _slotJobBuffer = new SpmcRingBuffer<Job>(SlotBufferSize);
    private MpmcRingBuffer<Job> _jobBufferMulti = new MpmcRingBuffer<Job>(BufferSize);

    // If path is not null, its an address warmup.
    // if storage tree is not null, its a storage warmup.
    // So this ideally need to be under 64 byte in size so that it fits within cache line
    private record struct Job(
        object scopeOrStorageTree,
        Address? path,
        UInt256 index,
        int sequenceId,
        long startTime);

    Task? _warmerJob = null;
    private static Counter _trieWarmEr = DevMetric.Factory.CreateCounter("triestore_trie_warmer", "hit rate", "type");
    private static Counter.Child _bufferFull = _trieWarmEr.WithLabels("buffer_full");
    private static Counter.Child _bufferFullNoMainWorker = _trieWarmEr.WithLabels("buffer_full_no_main_worker");

    private static Histogram _serviceTimeHistogram = DevMetric.Factory.CreateHistogram("trie_warmer_service_time_elapsed", "time elapsed", new HistogramConfiguration()
    {
        LabelNames = ["is_main", "category"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 10, 10)
    });

    private static Histogram _workTime = DevMetric.Factory.CreateHistogram("trie_warmer_work_time_elapsed", "time elapsed", new HistogramConfiguration()
    {
        LabelNames = ["is_main", "category"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 10, 10)
    });

    private long _slots = 0;
    private Semaphore _executionSlots;
    private WarmerWorkers? _mainWarmer = null;

    private bool TryDequeue(out Job job)
    {
        return _addressJob.TryDequeue(out job)
               || _slotJobBuffer.TryDequeue(out job)
               || _jobBufferMulti.TryDequeue(out job);
    }

    private int EstimatedJobCount => (int)(_addressJob.EstimatedJobCount +
                                           _slotJobBuffer.EstimatedJobCount +
                                           _jobBufferMulti.EstimatedJobCount);

    private readonly int _workerCount;
    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager)
    {
        _workerCount = Math.Max(Environment.ProcessorCount - 1, 1);
        _executionSlots = new Semaphore(0, _workerCount);
        _slots = 0;
        _warmerJob = Task.Run(() =>
        {
            using ArrayPoolList<Thread> tasks = new ArrayPoolList<Thread>(_workerCount);
            for (int i = 0; i < _workerCount; i++)
            {
                bool isMain = i == 0;
                var worker = new WarmerWorkers(this, isMain);

                Thread t = new Thread(() =>
                {
                    worker.Run(processExitSource.Token);
                });
                t.IsBackground = true;
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

    private static void HandleJob(Job job, bool isMain)
    {
        (object scopeOrStorageTree,
            Address? address,
            UInt256 index,
            int sequenceId,
            long startTime) = job;

        long sw = Stopwatch.GetTimestamp();
        try
        {
            if (scopeOrStorageTree is FlatWorldStateScope scope)
            {
                _serviceTimeHistogram.WithLabels(isMain.ToString(), "state").Observe(sw - startTime);
                if (scope.WarmUpStateTrie(address!, sequenceId))
                {
                    _workTime.WithLabels(isMain.ToString(), "state").Observe(Stopwatch.GetTimestamp() - sw);
                    _trieWarmEr.WithLabels("state").Inc();
                }
                else
                {
                    _workTime.WithLabels(isMain.ToString(), "state_skip").Observe(Stopwatch.GetTimestamp() - sw);
                    _trieWarmEr.WithLabels("state_skip").Inc();
                }
            }
            else
            {
                _serviceTimeHistogram.WithLabels(isMain.ToString(), "storage").Observe(sw - startTime);
                FlatStorageTree storageTree = (FlatStorageTree)scopeOrStorageTree;
                if (storageTree.WarUpStorageTrie(index, sequenceId))
                {
                    _workTime.WithLabels(isMain.ToString(), "storage").Observe(Stopwatch.GetTimestamp() - sw);
                    _trieWarmEr.WithLabels("storage").Inc();
                }
                else
                {
                    _workTime.WithLabels(isMain.ToString(), "storage_skip").Observe(Stopwatch.GetTimestamp() - sw);
                    _trieWarmEr.WithLabels("storage_skip").Inc();
                }
            }
        }
        catch (TrieNodeException)
        {
            _trieWarmEr.WithLabels("err_trienode").Inc();
            // It can be missing when the warmer lags so much behind that the node is now gone.
        }
        catch (NodeHashMismatchException)
        {
            _trieWarmEr.WithLabels("node_hash_mismatch").Inc();
            // Because it run in parallel, it could happen that the bundle changed which causes this.
        }
        catch (ObjectDisposedException)
        {
            _trieWarmEr.WithLabels("err_disposed").Inc();
            // Yea... this need to be fixed.
        }
        catch (NullReferenceException)
        {
            _trieWarmEr.WithLabels("err_null").Inc();
            // Uhh....
        }
    }

    private class WarmerWorkers(TrieWarmer mainWarmer, bool isMain)
    {
        private ManualResetEventSlim _resetEvent = new ManualResetEventSlim();
        private SpinWait _spinWait = new SpinWait();

        public void Run(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    if (mainWarmer.TryDequeue(out var job))
                    {
                        _spinWait.Reset();
                        if (isMain) mainWarmer.MaybeWakeOpOtherWorker();

                        HandleJob(job, isMain);
                    }
                    else
                    {
                        if (!isMain)
                        {
                            _trieWarmEr.WithLabels("wait_not_main").Inc();
                            mainWarmer.WaitForExecutionSlot();
                            Interlocked.Decrement(ref mainWarmer._slots);
                        }
                        else
                        {
                            if (_spinWait.NextSpinWillYield)
                            {
                                _trieWarmEr.WithLabels("wait_main").Inc();
                                _resetEvent.Reset();
                                mainWarmer.MainWarmerIdle(this);

                                _resetEvent.Wait(1, cancellationToken);
                            }
                            else
                            {
                                _spinWait.SpinOnce();
                            }
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

        public void WakeUp()
        {
            _resetEvent.Set();
        }
    }

    private void WaitForExecutionSlot()
    {
        // Some wait but not forever so that it exit properly
        _executionSlots.WaitOne(100);
    }

    private void MainWarmerIdle(WarmerWorkers warmerWorkers)
    {
        _mainWarmer =  warmerWorkers;
    }

    private void MaybeWakeOpOtherWorker()
    {
        if (EstimatedJobCount > 0 && _slots < Math.Min(EstimatedJobCount, _workerCount))
        {
            try
            {
                Interlocked.Increment(ref _slots);
                _executionSlots.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void MaybeWakeupFast()
    {
        WarmerWorkers? mainWarmer = _mainWarmer;
        if (mainWarmer is not null)
        {
            mainWarmer.WakeUp();
            _mainWarmer = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddressJob(FlatWorldStateScope scope, Address? path, int sequenceId)
    {
        if (!_addressJob.TryEnqueue(new Job(scope, path, default, sequenceId, Stopwatch.GetTimestamp())))
        {
            _bufferFull.Inc();
        }

        MaybeWakeupFast();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushSlotJob(FlatStorageTree storageTree, Address path, in UInt256? index,
        int sequenceId)
    {
        if (!_slotJobBuffer.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId, Stopwatch.GetTimestamp())))
        {
            _bufferFull.Inc();
        }

        MaybeWakeupFast();
    }

    public void PushJobMulti(FlatWorldStateScope scope, Address? path, FlatStorageTree? storageTree, in UInt256? index,
        int sequenceId)
    {
        if (storageTree is null)
        {
            if (!_addressJob.TryEnqueue(new Job(scope, path, index.GetValueOrDefault(), sequenceId, Stopwatch.GetTimestamp())))
            {
                _bufferFull.Inc();
            }
        }
        else
        {
            if (!_jobBufferMulti.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId, Stopwatch.GetTimestamp())))
            {
                _bufferFull.Inc();
            }
        }

        MaybeWakeupFast();
        MaybeWakeOpOtherWorker(); // Multi does not block main block processing, so we can do slow things here.
    }

    public void OnNewScope()
    {
        // Drain any existing job
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_addressJob.TryDequeue(out Job _)) break;
        }
        for (int i = 0; i < SlotBufferSize; i++)
        {
            if (!_slotJobBuffer.TryDequeue(out Job _)) break;
        }
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_jobBufferMulti.TryDequeue(out Job _)) break;
        }

        _mainWarmer?.WakeUp();
        _mainWarmer = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_warmerJob is not null) await _warmerJob;
        _executionSlots.Dispose();
    }
}
