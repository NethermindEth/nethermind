// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State;

public class TrieStoreTrieCacheWarmer
{
    private SpmcRingBuffer<Job> _jobBuffer = new SpmcRingBuffer<Job>(1024);

    private record struct Job(
        TrieStoreScopeProvider.TrieStoreWorldStateBackendScope Scope,
        Address Address,
        UInt256? Slot,
        Hash256 Root);

    Task? _warmerJob = null;
    private bool _commitReached;
    private static Counter _trieWarmEr = Metrics.CreateCounter("triestore_trie_warmer", "hit rate", "type");
    private TrieStoreScopeProvider.TrieStoreWorldStateBackendScope? _currentScope;
    private ManualResetEventSlim _resetEvent = new ManualResetEventSlim(initialState: false);

    public TrieStoreTrieCacheWarmer(ITrieStore trieStore, IProcessExitSource processExitSource, ILogManager logManager)
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
                    PatriciaTree tree = new PatriciaTree(trieStore.GetTrieStore(null), logManager);
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

                                (TrieStoreScopeProvider.TrieStoreWorldStateBackendScope scope,
                                    Address address,
                                    UInt256? path,
                                    Hash256 root) = job;

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
                                    StorageTree.ComputeKeyWithLookup(path.Value, key.BytesAsSpan);
                                    storageTree.Get(key.BytesAsSpan, root);
                                    _trieWarmEr.WithLabels("storage").Inc();
                                }
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

    public void OnStartWriteBatch()
    {
    }

    public void OnCommit()
    {
        _commitReached = true;
    }

    private long _pushCount = 0;
    private void PushJob(Address address, in UInt256? path, Hash256 rootHash)
    {
        // WARNING: Very hot!
        if (_jobBuffer.TryClaim(out var slot))
        {
            _jobBuffer[slot] = new Job(_currentScope, address, path, rootHash);
            _jobBuffer.Publish(slot);
            if ((_pushCount++) % 100 == 0) _resetEvent.Set();
        }
    }

    public void HintAccountRead(Address address, Hash256 rootHash)
    {
        PushJob(address, null, rootHash);
    }

    public void HintGet(Address address, in UInt256 index, Hash256 rootHash)
    {
        PushJob(address, index, rootHash);
    }

    internal void OnScope(TrieStoreScopeProvider.TrieStoreWorldStateBackendScope scope)
    {
        _currentScope = scope;
        _commitReached = false;
    }

    public void OnScopeDone()
    {
        _currentScope = null;
    }
}
