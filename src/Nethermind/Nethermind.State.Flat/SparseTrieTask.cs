// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// M4 background task that consumes hashed state updates and proof results,
/// driving the sparse trie reveal-update-retry loop to compute the state root
/// concurrently with EVM execution.
/// </summary>
public sealed class SparseTrieTask(
    SparseRootComputer computer,
    Channel<HashedStateUpdate> stateUpdates,
    Channel<ProofResult> proofResults,
    CancellationToken ct,
    ILogManager logManager)
{
    private readonly TaskCompletionSource<Hash256> _rootResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _logger = logManager.GetClassLogger<SparseTrieTask>();

    public Task<Hash256> RootTask => _rootResult.Task;

    public async Task RunAsync()
    {
        try
        {
            bool updateChannelOpen = true;
            bool finished = false;

            while (!ct.IsCancellationRequested)
            {
                List<Task> waitTasks = [];
                if (updateChannelOpen)
                {
                    waitTasks.Add(stateUpdates.Reader.WaitToReadAsync(ct).AsTask()
                        .ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully && !t.Result)
                                updateChannelOpen = false;
                        }, TaskContinuationOptions.ExecuteSynchronously));
                }

                waitTasks.Add(proofResults.Reader.WaitToReadAsync(ct).AsTask());

                if (waitTasks.Count == 0) break;
                await Task.WhenAny(waitTasks);

                if (!updateChannelOpen) finished = true;

                while (stateUpdates.Reader.TryRead(out HashedStateUpdate? update))
                {
                    if (update.IsFinished) { finished = true; break; }
                    AccumulateUpdate(update);
                }

                while (proofResults.Reader.TryRead(out ProofResult? result))
                    ProcessProofResult(result);

                if (finished) break;
            }

            Hash256 root = computer.ComputeStateRoot();
            _rootResult.TrySetResult(root);
        }
        catch (OperationCanceledException)
        {
            _rootResult.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("SparseTrieTask failed", ex);
            _rootResult.TrySetException(ex);
        }
    }

    private void AccumulateUpdate(HashedStateUpdate update)
    {
        computer.SetAccountChanges(update.AccountUpdates);
        foreach (KeyValuePair<Hash256, Dictionary<Hash256, LeafUpdate>> kvp in update.StorageUpdates)
            computer.AddStorageChanges(kvp.Key, update.PreviousStorageRoots[kvp.Key], kvp.Value);
    }

    private void ProcessProofResult(ProofResult result) { }
}

public sealed class HashedStateUpdate
{
    public bool IsFinished { get; init; }
    public Dictionary<Hash256, LeafUpdate> AccountUpdates { get; init; } = [];
    public Dictionary<Hash256, Dictionary<Hash256, LeafUpdate>> StorageUpdates { get; init; } = [];
    public Dictionary<Hash256, Hash256> PreviousStorageRoots { get; init; } = [];
}

public sealed class ProofResult
{
    public DecodedMultiProof Proof { get; init; } = new();
}
