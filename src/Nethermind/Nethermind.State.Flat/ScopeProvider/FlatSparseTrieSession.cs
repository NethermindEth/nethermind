// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Scope-owned sparse root calculation: collects sealed per-account storage jobs from write
/// batches, runs one whole-trie storage phase, applies final account updates to the state trie,
/// and publishes every staged node into the bundle at commit.
/// </summary>
/// <remarks>
/// Single mutable owner per trie: inner storage batches only seal jobs (no trie access), the
/// storage phase gives each job's trie to exactly one worker, and the state trie is only touched
/// from the outer write-batch/commit path. Any calculation or publication exception poisons the
/// session; every later mutation, calculation, and publication entry point throws, so a poisoned
/// scope can never commit a root that may not match the applied writes. <see cref="RootHash"/>
/// stays readable and always holds the last successfully calculated root (or the anchor).
/// </remarks>
internal sealed class FlatSparseTrieSession(SnapshotBundle bundle, Hash256 anchorStateRoot) : IDisposable
{
    /// <summary>Below this many storage jobs a serial loop beats the parallel-work dispatch cost.</summary>
    private const int ParallelJobThreshold = 4;

    private static readonly AccountDecoder _accountDecoder = new();

    private readonly ConcurrentQueue<StorageJob> _pendingStorageJobs = new();
    private readonly List<FlatStorageTree> _changedStorageTrees = [];
    private SparseTrie? _stateTrie;
    private Hash256 _rootHash = anchorStateRoot;
    private bool _stateDirty;
    private bool _poisoned;

    /// <summary>A sealed storage write batch: the final sparse delta for one account.</summary>
    internal readonly record struct StorageJob(
        FlatStorageTree Tree,
        ArrayPoolList<SparseTrieUpdate> Updates,
        bool HasClear,
        Action<Address, Hash256> OnRootUpdated);

    /// <summary>The parent state root before calculation, the calculated root afterward.</summary>
    public Hash256 RootHash => _rootHash;

    /// <summary>Called by inner storage batches, possibly from parallel flush workers.</summary>
    public void EnqueueStorageJob(in StorageJob job) => _pendingStorageJobs.Enqueue(job);

    /// <summary>Marks the session unusable after a failure outside its own guarded paths.</summary>
    internal void Poison() => _poisoned = true;

    /// <summary>
    /// Drains the sealed storage jobs, calculates each account's whole storage trie, and reports
    /// every completed root through its job's callback.
    /// </summary>
    /// <param name="getFinalAccount">The batch-final account per address; a job whose final
    /// account is deleted is discarded (the Flat clear and account deletion are already
    /// recorded) so its root is never calculated.</param>
    public void RunStoragePhase(Func<Address, Account?> getFinalAccount)
    {
        GuardPoisoned();
        if (_pendingStorageJobs.IsEmpty) return;

        using ArrayPoolList<StorageJob> jobs = new(_pendingStorageJobs.Count);
        try
        {
            while (_pendingStorageJobs.TryDequeue(out StorageJob job))
            {
                bool keep;
                try
                {
                    keep = getFinalAccount(job.Tree.Address) is not null;
                }
                catch
                {
                    // The in-flight job is in neither the queue nor the tracked list yet.
                    job.Updates.Dispose();
                    throw;
                }

                if (keep)
                {
                    jobs.Add(job);
                }
                else
                {
                    job.Updates.Dispose();
                }
            }

            if (jobs.Count >= ParallelJobThreshold)
            {
                ParallelUnbalancedWork.For(0, jobs.Count, i => jobs[i].Tree.ProcessSparseJob(jobs[i]));
            }
            else
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    jobs[i].Tree.ProcessSparseJob(jobs[i]);
                }
            }
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Tree.MarkInChangedSet()) _changedStorageTrees.Add(jobs[i].Tree);
                jobs[i].Updates.Dispose();
            }
        }
    }

    /// <summary>Applies the batch-final accounts to the state trie; the root is recalculated lazily.</summary>
    public void ApplyStateUpdates(Dictionary<AddressAsKey, Account?> dirtyAccounts)
    {
        GuardPoisoned();
        if (dirtyAccounts.Count == 0) return;

        try
        {
            using ArrayPoolList<SparseTrieUpdate> updates = new(dirtyAccounts.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> kv in dirtyAccounts)
            {
                KeccakCache.ComputeTo(kv.Key.Value.Bytes, out ValueHash256 keccak);
                byte[]? accountBytes = kv.Value is null ? null
                    : kv.Value.IsTotallyEmpty ? StateTree.EmptyAccountRlp.Bytes
                    : _accountDecoder.EncodeAsBytes(kv.Value);
                updates.Add(new SparseTrieUpdate(in keccak, accountBytes));
            }

            _stateTrie ??= new SparseTrie(new FlatTrieNodeReader(bundle, address: null), _rootHash.ValueHash256, dirtyAccounts.Count);
            _stateTrie.Apply(updates.AsSpan());
            _stateDirty = true;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
    }

    /// <summary>Recalculates the state root when writes intervened since the last calculation.</summary>
    public void UpdateRootHash()
    {
        GuardPoisoned();
        if (!_stateDirty) return;

        try
        {
            _rootHash = _stateTrie!.CalculateRoot().ToCommitment();
            _stateDirty = false;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
    }

    /// <summary>
    /// Publishes every staged storage and state node into the bundle's changed-node maps (ahead
    /// of snapshot collection) and resets the session to anchor the next block at the new root.
    /// </summary>
    public void Publish()
    {
        GuardPoisoned();
        if (!_pendingStorageJobs.IsEmpty)
        {
            _poisoned = true;
            throw new InvalidOperationException("Storage jobs sealed after the storage phase would never be calculated");
        }

        try
        {
            foreach (FlatStorageTree tree in _changedStorageTrees)
            {
                tree.PublishSparseNodes();
            }

            if (_stateTrie is not null)
            {
                using ArrayPoolList<SparseTrieStagedNode> staged = new(64);
                _stateTrie.DrainUnpublished(staged);
                if (staged.Count > 0)
                {
                    bundle.PublishStateNodes([BuildPublicationBuffer(staged)]);
                }
            }
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            DropStorageTries();
            _stateTrie?.Dispose();
            _stateTrie = null;
            _stateDirty = false;
        }
    }

    private void DropStorageTries()
    {
        foreach (FlatStorageTree tree in _changedStorageTrees)
        {
            tree.DropSparseTrie();
        }

        _changedStorageTrees.Clear();
    }

    /// <summary>Converts staged records into the sealed <see cref="TrieNode"/> publication shape.</summary>
    internal static List<(TreePath, TrieNode)> BuildPublicationBuffer(ArrayPoolList<SparseTrieStagedNode> staged)
    {
        List<(TreePath, TrieNode)> buffer = new(staged.Count);
        foreach (SparseTrieStagedNode node in staged)
        {
            buffer.Add((node.Path, new TrieNode(NodeType.Unknown, node.Hash.ToCommitment(), node.Rlp)));
        }

        return buffer;
    }

    private void GuardPoisoned()
    {
        if (_poisoned) ThrowPoisoned();

        static void ThrowPoisoned() =>
            throw new InvalidOperationException("Sparse trie session is poisoned by an earlier calculation failure");
    }

    public void Dispose()
    {
        while (_pendingStorageJobs.TryDequeue(out StorageJob job)) job.Updates.Dispose();
        DropStorageTries();
        _stateTrie?.Dispose();
        _stateTrie = null;
    }
}
