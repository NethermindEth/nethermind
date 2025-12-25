// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class TrieWarmer : ITrieWarmer
{
    private const int BufferSize = 1024;
    private MpmcRingBuffer<Job> _addressJob = new MpmcRingBuffer<Job>(BufferSize);
    private MpmcRingBuffer<Job> _jobBuffer = new MpmcRingBuffer<Job>(BufferSize);
    private MpmcRingBuffer<Job> _jobBufferMulti = new MpmcRingBuffer<Job>(BufferSize);

    // If path is not null, its an address warmup.
    // if storage tree is not null, its a storage warmup.
    // So this ideally need to be under 64 byte in size so that it fits within cache line
    private record struct Job(
        object scopeOrStorageTree,
        Address? path,
        UInt256 index,
        int sequenceId);

    Task? _warmerJob = null;
    private static Counter _trieWarmEr = DevMetric.Factory.CreateCounter("triestore_trie_warmer", "hit rate", "type");
    private static Counter.Child _bufferFull = _trieWarmEr.WithLabels("buffer_full");
    private static Counter.Child _bufferFullNoMainWorker = _trieWarmEr.WithLabels("buffer_full_no_main_worker");
    private ConcurrentStack<WarmerWorkers> _awaitingWorkers = new ConcurrentStack<WarmerWorkers>();
    private WarmerWorkers? _mainWarmer = null;
    private int _activeWorkers = 0;

    private bool TryDequeue(out Job job)
    {
        return _addressJob.TryDequeue(out job)
               || _jobBuffer.TryDequeue(out job)
               || _jobBufferMulti.TryDequeue(out job);
    }

    private int EstimatedJobCount => (int)(_addressJob.EstimatedJobCount +
                                           _jobBuffer.EstimatedJobCount +
                                           _jobBufferMulti.EstimatedJobCount);

    private void IncrementActiveWorkers()
    {
        Interlocked.Increment(ref _activeWorkers);
    }

    private void DecrementActiveWorkers()
    {
        Interlocked.Decrement(ref _activeWorkers);
    }

    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager)
    {
        int processorCount = Environment.ProcessorCount;
        _warmerJob = Task.Run<Task>(async () =>
        {
            using ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(processorCount);
            for (int i = 0; i < processorCount; i++)
            {
                bool isMain = i == 0;
                var worker = new WarmerWorkers(this, isMain);
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    worker.Run(processExitSource.Token);
                }, TaskCreationOptions.LongRunning));
            }

            await Task.WhenAll(tasks);;
        });
    }

    private static void HandleJob(Job job, bool isMain)
    {
        (object scopeOrStorageTree,
            Address? address,
            UInt256 index,
            int sequenceId) = job;

        try
        {
            if (scopeOrStorageTree is FlatWorldStateScope scope)
            {
                if (scope.WarmUpStateTrie(address!, sequenceId))
                {
                    _trieWarmEr.WithLabels("state").Inc();
                }
                else
                {
                    _trieWarmEr.WithLabels("state_skip").Inc();
                }
            }
            else
            {
                FlatStorageTree storageTree = (FlatStorageTree)scopeOrStorageTree;
                if (storageTree.WarUpStorageTrie(index, sequenceId))
                {
                    _trieWarmEr.WithLabels("storage").Inc();
                }
                else
                {
                    _trieWarmEr.WithLabels("storage_skip").Inc();
                }
            }
        }
        catch (TrieNodeException)
        {
            _trieWarmEr.WithLabels("err_trienode").Inc();
            // It can be missing when the warmer lags so much behind that the node is now gone.
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
        private bool _wakeUpViaQueue;

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
                        if (isMain && mainWarmer.EstimatedJobCount > mainWarmer._wakingUpWorker) mainWarmer.MaybeWakeOpOtherWorker();

                        HandleJob(job, isMain);
                    }
                    else
                    {
                        if (_spinWait.NextSpinWillYield)
                        {
                            if (!isMain)
                            {
                                _trieWarmEr.WithLabels("wait_not_main").Inc();
                                _resetEvent.Reset();
                                mainWarmer.QueueWorker(this);

                                _resetEvent.Wait(1, cancellationToken);
                            }
                            else
                            {
                                _trieWarmEr.WithLabels("wait_main").Inc();
                                _resetEvent.Reset();
                                mainWarmer.MainWarmerIdle(this);

                                _resetEvent.Wait(1, cancellationToken);
                            }
                            _spinWait.Reset();
                        }
                        else
                        {
                            _spinWait.SpinOnce();
                        }
                    }

                    if (_wakeUpViaQueue)
                    {
                        Interlocked.Decrement(ref mainWarmer._wakingUpWorker);
                        _wakeUpViaQueue = false;
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

        public void WakeUpViaQueue()
        {
            Interlocked.Increment(ref mainWarmer._wakingUpWorker);
            _wakeUpViaQueue = true;
            _resetEvent.Set();
        }

        public void WakeUp()
        {
            _resetEvent.Set();
        }
    }

    private void MainWarmerIdle(WarmerWorkers warmerWorkers)
    {
        _mainWarmer =  warmerWorkers;
    }

    private int _wakingUpWorker = 0;
    private void MaybeWakeOpOtherWorker()
    {
        if (_jobBuffer.EstimatedJobCount > 0 && _awaitingWorkers.TryPop(out WarmerWorkers? otherWorker))
        {
            otherWorker.WakeUpViaQueue();
        }
    }

    private void QueueWorker(WarmerWorkers worker)
    {
        _awaitingWorkers.Push(worker);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushJob(
        FlatWorldStateScope scope,
        Address? path,
        FlatStorageTree? storageTree,
        in UInt256? index,
        int sequenceId)
    {
        bool bufferFull = false;
        if (storageTree is null)
        {
            if (!_jobBuffer.TryEnqueue(new Job(scope, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
                bufferFull = true;
            }
        }
        else
        {
            if (!_jobBuffer.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
                bufferFull = true;
            }
        }

        WarmerWorkers? mainWarmer = _mainWarmer;
        if (mainWarmer is not null)
        {
            mainWarmer.WakeUp();
            _mainWarmer = null;
        }
        else
        {
            if (bufferFull)
            {
                _bufferFullNoMainWorker.Inc();
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

    public void PushAddressJob(FlatWorldStateScope scope, Address? path, int sequenceId)
    {
        if (!_addressJob.TryEnqueue(new Job(scope, path, default, sequenceId)))
        {
            _bufferFull.Inc();
        }

        MaybeWakeupFast();
    }

    public void PushSlotJob(FlatWorldStateScope scope, FlatStorageTree storageTree, Address path, in UInt256? index,
        int sequenceId)
    {
        if (!_jobBuffer.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId)))
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
            if (!_jobBufferMulti.TryEnqueue(new Job(scope, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
            }
        }
        else
        {
            if (!_jobBufferMulti.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
            }
        }

        MaybeWakeupFast();
    }

    public void OnNewScope()
    {
        // Drain any existing job
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_addressJob.TryDequeue(out Job _)) break;
        }
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_jobBuffer.TryDequeue(out Job _)) break;
        }
        for (int i = 0; i < BufferSize; i++)
        {
            if (!_jobBufferMulti.TryDequeue(out Job _)) break;
        }

        _mainWarmer?.WakeUp();
        _mainWarmer = null;
    }
}
