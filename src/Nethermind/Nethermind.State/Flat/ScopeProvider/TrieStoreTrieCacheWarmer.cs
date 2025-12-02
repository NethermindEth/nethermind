// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Internal;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieStoreTrieCacheWarmer
{
    public void PushJob(
        WorldStateScope scope,
        Address? path,
        StorageTree? storageTree,
        UInt256? index);

    void OnNewScope();
}

public class NoopTrieStoreTrieCacheWarmer : ITrieStoreTrieCacheWarmer
{
    public void PushJob(WorldStateScope scope, Address? path, StorageTree? storageTree, UInt256? index)
    {
    }

    public void OnNewScope()
    {
    }
}

public sealed class TrieStoreTrieCacheWarmer : ITrieStoreTrieCacheWarmer
{
    private SpmcRingBuffer<Job> _jobBuffer = new SpmcRingBuffer<Job>(1024);

    // If path is not null, its an address warmup.
    // if storage tree is not null, its a storage warmup.
    private record struct Job(
        WorldStateScope scope,
        Address? path,
        StorageTree? storageTree,
        UInt256? index);

    Task? _warmerJob = null;
    private static Counter _trieWarmEr = Metrics.CreateCounter("triestore_trie_warmer", "hit rate", "type");

    private ConcurrentStack<WarmerWorkers> _awaitingWorkers = new ConcurrentStack<WarmerWorkers>();
    private WarmerWorkers? _mainWarmer = null;

    private bool TryDequeue(out Job job)
    {
        return _jobBuffer.TryDequeue(out job);
    }

    public TrieStoreTrieCacheWarmer(IProcessExitSource processExitSource, ILogManager logManager)
    {
        int processorCount = Environment.ProcessorCount;
        _warmerJob = Task.Run<Task>(async () =>
        {
            using ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(processorCount);
            for (int i = 0; i < processorCount; i++)
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
        (WorldStateScope scope,
            Address? address,
            StorageTree? storageTree,
            UInt256? index) = job;

        try
        {

            if (scope.ShouldStillWarmUpTrie)
            {
                if (address is not null)
                {
                    scope.WarmUpStateTrie(address);
                    _trieWarmEr.WithLabels("state").Inc();
                }
                else
                {
                    storageTree.WarUpStorageTrie(index.Value);
                    _trieWarmEr.WithLabels("storage").Inc();
                }
            }
            else
            {
                if (address is null)
                {
                    _trieWarmEr.WithLabels("state_skip").Inc();
                }
                else
                {
                    _trieWarmEr.WithLabels("storage_skip").Inc();
                }
            }
        }
        catch (TrieNodeException)
        {
            // It can be missing when the warmer lags so much behind that the node is now gone.
        }
        catch (ObjectDisposedException)
        {
            // Yea... this need to be fixed.
        }
        catch (NullReferenceException)
        {
            // Uhh....
        }
    }

    private class WarmerWorkers(TrieStoreTrieCacheWarmer mainWarmer, bool isMain)
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
        WorldStateScope scope,
        Address? path,
        StorageTree? storageTree,
        UInt256? index)
    {
        if (!_jobBuffer.TryEnqueue(new Job(scope, path, storageTree, index))) return;

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
