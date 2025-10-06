// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Prometheus;

namespace Nethermind.State;

public class TrieStoreTrieCacheWarmer
{
    private ChannelWriter<(Address, UInt256?)>? _trieWarmerJobs;
    Task? _warmerJob = null;
    private bool _commitReached;
    private static Counter _trieWarmEr = Metrics.CreateCounter("triestore_trie_warmer", "hit rate", "type");
    private TrieStoreScopeProvider.TrieStoreWorldStateBackendScope? _currentScope;

    public TrieStoreTrieCacheWarmer(IProcessExitSource processExitSource)
    {
        var chan = Channel.CreateBounded<(Address, UInt256?)>(new BoundedChannelOptions(1024)
        {
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        _trieWarmerJobs = chan.Writer;
        _warmerJob = Task.Run<Task>(async () =>
        {
            int completed = 0;
            ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(Environment.ProcessorCount);
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var reader = chan?.Reader;
                        if (reader is null)
                        {
                            Console.Error.WriteLine("Reader is null");
                            return;
                        }

                        await foreach (var job in reader.ReadAllAsync(processExitSource.Token))
                        {
                            (Address address, UInt256? path) = job;
                            var scope = _currentScope;
                            if (scope is null) continue;

                            if (_commitReached)
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
                                scope._backingStateTree.Get(address.ToAccountPath.ToCommitment());
                                _trieWarmEr.WithLabels("state").Inc();
                            }
                            else
                            {
                                scope.LookupStorageTree(address).Get(path.Value);
                                _trieWarmEr.WithLabels("storage").Inc();
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
            _trieWarmerJobs = null;
        });
    }

    public void OnStartWriteBatch()
    {
        _trieWarmerJobs?.TryComplete();
    }

    public void OnCommit()
    {
        _trieWarmerJobs?.TryComplete();
    }

    public void OnCommitDone()
    {
        _commitReached = true; // At the end, then we stop it
        _warmerJob?.Wait();
    }

    public void HintAccountRead(Address address)
    {
        _trieWarmerJobs?.TryWrite((address, null));
    }

    public void HintGet(Address address, in UInt256 index)
    {
        _trieWarmerJobs?.TryWrite((address, index));
    }

    internal void OnScope(TrieStoreScopeProvider.TrieStoreWorldStateBackendScope scope)
    {
        _currentScope = scope;
    }

    public void OnScopeDone()
    {
        _currentScope = null;
    }
}
