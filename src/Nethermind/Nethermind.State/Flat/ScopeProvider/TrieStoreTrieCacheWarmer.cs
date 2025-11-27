// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;
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
}

public class NoopTrieStoreTrieCacheWarmer : ITrieStoreTrieCacheWarmer
{
    public void PushJob(WorldStateScope scope, Address? path, StorageTree? storageTree, UInt256? index)
    {
    }
}

public class TrieStoreTrieCacheWarmer : ITrieStoreTrieCacheWarmer
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
    private ManualResetEventSlim _resetEvent = new ManualResetEventSlim(initialState: false);

    public TrieStoreTrieCacheWarmer(IProcessExitSource processExitSource, ILogManager logManager)
    {
        _warmerJob = Task.Run<Task>(async () =>
        {
            int completed = 0;
            ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(Environment.ProcessorCount);
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                bool isMain = i == 0;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        while (true)
                        {
                            if (processExitSource.Token.IsCancellationRequested) break;

                            if (_jobBuffer.TryDequeue(out var job))
                            {
                                if (isMain)
                                {
                                    if (_jobBuffer.EstimatedJobCount > 2) _resetEvent.Set();
                                }

                                (WorldStateScope scope,
                                    Address? address,
                                    StorageTree? storageTree,
                                    UInt256? index) = job;

                                if (scope.ShouldStillWarmUpTrie)
                                {
                                    if (address is not null)
                                    {
                                        scope.WarmUpStateTrie(address);
                                    }
                                    else
                                    {
                                        storageTree.WarUpStorageTrie(index.Value);
                                    }
                                }
                                /*
                                if (_commitReached || !ReferenceEquals(scope, _currentScope))
                                {
                                    if (path is null)
                                    {
                                        _trieWarmEr.WithLabels("state_skip").Inc();
                                    }
                                    else
                                    {
                                        _trieWarmEr.WithLabels("storage_skip").Inc();
                                    }

                                    continue;
                                }

                                if (path is null)
                                {
                                    tree.Get(address.ToAccountPath.ToCommitment().Bytes, root);
                                    _trieWarmEr.WithLabels("state").Inc();
                                }
                                else
                                {
                                    PatriciaTree storageTree = new PatriciaTree(trieStore.GetTrieStore(address), logManager);
                                    ValueHash256 key = new ValueHash256();
                                    State.StorageTree.ComputeKeyWithLookup(path.Value, key.BytesAsSpan);
                                    storageTree.Get(key.BytesAsSpan, root);
                                    _trieWarmEr.WithLabels("storage").Inc();
                                }
                                */
                            }
                            else
                            {
                                if (!isMain)
                                {
                                    _trieWarmEr.WithLabels("wait_not_main").Inc();
                                    _resetEvent.Wait(processExitSource.Token);
                                    _resetEvent.Reset();
                                }
                                else
                                {
                                    _trieWarmEr.WithLabels("wait_main").Inc();
                                    if (_resetEvent.Wait(1, processExitSource.Token))
                                    {
                                        _resetEvent.Reset();
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
                        Console.Error.WriteLine(ex);
                        throw;
                    }
                    finally
                    {
                        completed++;
                    }
                }));
            }

            await Task.WhenAll(tasks);;
        });
    }

    private long _pushCount = 0;
    public void PushJob(
        WorldStateScope scope,
        Address? path,
        StorageTree? storageTree,
        UInt256? index)
    {
        // WARNING: Very hot!
        if (_jobBuffer.TryClaim(out var slot))
        {
            _jobBuffer[slot] = new Job(scope, path, storageTree, index);
            _jobBuffer.Publish(slot);
            if ((_pushCount++) % 10 == 0) _resetEvent.Set();
        }
    }
}
