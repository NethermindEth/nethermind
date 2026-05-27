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

    /// <summary>
    /// Accumulator for account updates across multiple batches. SparseRootComputer.SetAccountChanges
    /// REPLACES its internal dict, so we must merge multiple per-tx batches here and pass the final
    /// merged dict once at end-of-block. Last-writer-wins per account is correct (matches block semantics).
    /// </summary>
    private readonly Dictionary<Hash256, LeafUpdate> _accountAccumulator = [];

    /// <summary>Accumulator for per-contract storage updates. Same semantics as account accumulator.</summary>
    private readonly Dictionary<Hash256, Dictionary<Hash256, LeafUpdate>> _storageAccumulator = [];
    private readonly Dictionary<Hash256, Hash256> _prevStorageRoots = [];

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

            // Flush accumulated updates into the computer ONCE before computing root.
            computer.SetAccountChanges(_accountAccumulator);
            foreach (KeyValuePair<Hash256, Dictionary<Hash256, LeafUpdate>> kvp in _storageAccumulator)
                computer.AddStorageChanges(kvp.Key, _prevStorageRoots[kvp.Key], kvp.Value);

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
        // Merge account updates — last-writer-wins per key (matches block semantics for
        // multiple Commit(commitRoots:false) calls touching the same account in a block).
        foreach (KeyValuePair<Hash256, LeafUpdate> kvp in update.AccountUpdates)
            _accountAccumulator[kvp.Key] = kvp.Value;

        // Merge storage updates per-contract — last-writer-wins per slot.
        foreach (KeyValuePair<Hash256, Dictionary<Hash256, LeafUpdate>> kvp in update.StorageUpdates)
        {
            if (!_storageAccumulator.TryGetValue(kvp.Key, out Dictionary<Hash256, LeafUpdate>? slots))
            {
                slots = [];
                _storageAccumulator[kvp.Key] = slots;
            }
            foreach (KeyValuePair<Hash256, LeafUpdate> slot in kvp.Value)
                slots[slot.Key] = slot.Value;

            // First-seen prevRoot wins (it's the parent block's value, doesn't change mid-block).
            if (!_prevStorageRoots.ContainsKey(kvp.Key))
                _prevStorageRoots[kvp.Key] = update.PreviousStorageRoots[kvp.Key];
        }
    }

    /// <summary>
    /// Reveals proof nodes returned by an out-of-band proof worker (M4 ProofWorkerPool).
    /// Without this, proof workers' results are silently discarded and the sparse trie
    /// keeps hitting blinded nodes on the retry loop.
    /// </summary>
    private void ProcessProofResult(ProofResult result)
    {
        if (result.Proof.AccountNodes.Count > 0)
            computer.Trie.AccountTrie.RevealNodes(result.Proof.AccountNodes);
        foreach (KeyValuePair<Hash256, List<ProofNode>> kvp in result.Proof.StorageNodes)
            computer.Trie.GetOrCreateStorageTrie(kvp.Key).RevealNodes(kvp.Value);
    }
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
