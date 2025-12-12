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

public interface ITrieWarmer
{
    public void PushJob(
        FlatWorldStateScope scope,
        Address? path,
        FlatStorageTree? storageTree,
        in UInt256? index,
        int sequenceId);

    void OnNewScope();
}

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushJob(FlatWorldStateScope scope, Address? path, FlatStorageTree? storageTree, in UInt256? index, int sequenceId)
    {
    }

    public void OnNewScope()
    {
    }
}

public sealed class TrieWarmer : ITrieWarmer
{
    private SpmcRingBuffer<Job> _jobBuffer = new SpmcRingBuffer<Job>(512);

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
    private ConcurrentStack<WarmerWorkers> _awaitingWorkers = new ConcurrentStack<WarmerWorkers>();
    private WarmerWorkers? _mainWarmer = null;

    private bool TryDequeue(out Job job)
    {
        return _jobBuffer.TryDequeue(out job);
    }

    public TrieWarmer(IProcessExitSource processExitSource, ILogManager logManager)
    {
        int workerCount = Environment.ProcessorCount;
        _warmerJob = Task.Run<Task>(async () =>
        {
            using ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(workerCount);
            for (int i = 0; i < workerCount; i++)
            {
                bool isMain = i == 0;
                var worker = new WarmerWorkers(this, isMain);
                tasks.Add(Task.Run(() =>
                {
                    worker.Run(processExitSource.Token);
                }));
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
                if (scope.WarmUpStateTrie(address, sequenceId))
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
                        mainWarmer.MaybeWakeOpOtherWorker();

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

    private void MainWarmerIdle(WarmerWorkers warmerWorkers)
    {
        _mainWarmer =  warmerWorkers;
    }

    private void MaybeWakeOpOtherWorker()
    {
        if (_jobBuffer.EstimatedJobCount > 0 && _awaitingWorkers.TryPop(out WarmerWorkers otherWorker)) otherWorker.WakeUp();
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
        if (path is not null)
        {
            if (!_jobBuffer.TryEnqueue(new Job(scope, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
                return;
            }
        }
        else
        {
            if (!_jobBuffer.TryEnqueue(new Job(storageTree, path, index.GetValueOrDefault(), sequenceId)))
            {
                _bufferFull.Inc();
                return;
            }
        }

        WarmerWorkers? mainWarmer = _mainWarmer;
        if (mainWarmer is not null)
        {
            mainWarmer.WakeUp();
            _mainWarmer = null;
        }
    }

    public void OnNewScope()
    {
        _mainWarmer?.WakeUp();
        _mainWarmer = null;
    }
}
