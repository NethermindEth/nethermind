// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
internal sealed class FlatSparseTrieSession : IDisposable
{
    /// <summary>Below this many storage jobs a serial loop beats the parallel-work dispatch cost.</summary>
    private const int ParallelJobThreshold = 4;

    private static readonly AccountDecoder _accountDecoder = new();

    private readonly SnapshotBundle _bundle;
    private readonly ConcurrentQueue<StorageJob> _pendingStorageJobs = new();
    private readonly List<FlatStorageTree> _changedStorageTrees = [];

    // The session's warm storage tries, one per account touched in this scope's lifetime. A trie
    // is borrowed out (removed) while its account's job runs, rolled back in at publication, and
    // dropped on account deletion. It persists across the scope's block commits, so a multi-block
    // branch reuses warm tries directly; at the scope boundary it is handed to the cross-scope
    // retention cache. The parallel storage phase borrows entries concurrently, under _retainedLock.
    private Dictionary<Hash256AsKey, SparseTrie> _storageTries;
    private readonly Lock _retainedLock = new();

    // When false (no retention cache), tries are disposed at each commit so a multi-block scope
    // never accumulates a cross-block working set; when true they roll forward for reuse.
    private readonly bool _retentionEnabled;

    private SparseTrie? _stateTrie;
    private Hash256 _rootHash;
    private bool _stateDirty;
    private bool _poisoned;
    private bool _generationExtracted;

    /// <param name="checkedOut">A warm generation checked out of the retention cache on an exact
    /// parent-root match, or null for a cold scope. The session reuses and rolls it forward across
    /// its commits and hands the result back through <see cref="ExtractGeneration"/> at the scope
    /// boundary.</param>
    /// <param name="retentionEnabled">When true the scope has a retention cache: tries are kept
    /// warm across commits and offered to the cache at the scope boundary. When false they are
    /// disposed at each commit, so a multi-block scope holds only the current block's working set.</param>
    public FlatSparseTrieSession(SnapshotBundle bundle, Hash256 anchorStateRoot, RetainedGeneration? checkedOut = null, bool retentionEnabled = false)
    {
        _bundle = bundle;
        _rootHash = anchorStateRoot;
        _retentionEnabled = retentionEnabled;
        _storageTries = checkedOut?.StorageTries ?? [];
        if (checkedOut is not null)
        {
            _stateTrie = checkedOut.StateTrie;
            _stateTrie.RebindSource(new FlatTrieNodeReader(bundle, address: null));
        }
    }

    /// <summary>A sealed storage write batch: the final sparse delta for one account.</summary>
    internal readonly record struct StorageJob(
        FlatStorageTree Tree,
        ArrayPoolList<SparseTrieUpdate> Updates,
        bool HasClear,
        Action<Address, Hash256> OnRootUpdated);

    /// <summary>The parent state root before calculation, the calculated root afterward.</summary>
    public Hash256 RootHash => _rootHash;

    /// <summary>Warm tries currently held (state trie plus per-account storage tries); zero after a
    /// commit when retention is disabled. Test-observable to pin the per-commit disposal policy.</summary>
    internal int RetainedTrieCount => (_stateTrie is null ? 0 : 1) + _storageTries.Count;

    /// <summary>Called by inner storage batches, possibly from parallel flush workers.</summary>
    public void EnqueueStorageJob(in StorageJob job) => _pendingStorageJobs.Enqueue(job);

    /// <summary>
    /// Returns the warm storage trie retained for <paramref name="addressHash"/>, rebound to this
    /// scope's reader, or a fresh trie anchored at <paramref name="anchorRoot"/>. A retained trie
    /// whose root does not match the account's committed storage root (e.g. after a clear) is
    /// dropped rather than reused. Runs on the storage-phase worker that owns this account's job.
    /// </summary>
    public SparseTrie AdoptOrCreateStorageTrie(Hash256 addressHash, in ValueHash256 anchorRoot, int hint)
    {
        SparseTrie? retained;
        lock (_retainedLock)
        {
            _storageTries.Remove(addressHash, out retained);
        }

        if (retained is not null)
        {
            if (retained.RootHash == anchorRoot)
            {
                retained.RebindSource(new FlatTrieNodeReader(_bundle, addressHash));
                return retained;
            }

            retained.Dispose();
        }

        return new SparseTrie(new FlatTrieNodeReader(_bundle, addressHash), anchorRoot, hint);
    }

    /// <summary>Rolls a published storage trie back into the warm set for reuse by later blocks.</summary>
    private void ReturnStorageTrie(Hash256 addressHash, SparseTrie trie)
    {
        lock (_retainedLock)
        {
            _storageTries[addressHash] = trie;
        }
    }

    /// <summary>Drops any warm storage trie held for a now-deleted or cleared account.</summary>
    public void DiscardRetainedStorage(Hash256 addressHash)
    {
        SparseTrie? retained;
        lock (_retainedLock)
        {
            _storageTries.Remove(addressHash, out retained);
        }

        retained?.Dispose();
    }

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
                ParallelUnbalancedWork.For(
                    0,
                    jobs.Count,
                    jobs,
                    static (i, jobs) =>
                    {
                        jobs[i].Tree.ProcessSparseJob(jobs[i], canBeParallel: false);
                        return jobs;
                    });
            }
            else
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    jobs[i].Tree.ProcessSparseJob(jobs[i], canBeParallel: true);
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

            _stateTrie ??= new SparseTrie(new FlatTrieNodeReader(_bundle, address: null), _rootHash.ValueHash256, dirtyAccounts.Count);
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
            _rootHash = _stateTrie!.CalculateRoot(canBeParallel: true).ToCommitment();
            _stateDirty = false;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
    }

    /// <summary>
    /// Publishes every staged storage and state node into the bundle's changed-node maps, ahead of
    /// snapshot collection. The tries are left intact so the committed generation can either be
    /// retained (see <see cref="ExtractGeneration"/>) or disposed with the session.
    /// </summary>
    public void Publish()
    {
        GuardPoisoned();
        if (!_pendingStorageJobs.IsEmpty)
        {
            _poisoned = true;
            ThrowStorageJobsSealed();
        }

        try
        {
            foreach (FlatStorageTree tree in _changedStorageTrees)
            {
                tree.PublishSparseNodes();
                // With retention, roll the warm trie back into the session set so the next block
                // in this scope reuses it; without it, dispose per commit so a multi-block scope
                // holds no cross-block working set. A deleted account's tree yields null.
                SparseTrie? trie = tree.TakeSparseTrie();
                if (trie is null) continue;
                if (_retentionEnabled) ReturnStorageTrie(tree.AddressHash, trie);
                else trie.Dispose();
            }

            _changedStorageTrees.Clear();

            if (_stateTrie is not null)
            {
                using ArrayPoolList<SparseTrieStagedNode> staged = new(_stateTrie.UnpublishedNodeCapacityHint);
                _stateTrie.DrainUnpublished(staged);
                if (staged.Count > 0)
                {
                    using ArrayPoolListRef<(TreePath, TrieNode)> buffer = BuildPublicationBuffer(staged.AsSpan());
                    _bundle.PublishStateNodes(buffer.AsSpan());
                }

                if (!_retentionEnabled)
                {
                    _stateTrie.Dispose();
                    _stateTrie = null;
                }
            }

            _stateDirty = false;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
    }

    /// <summary>
    /// Transfers the warm tries out of the session into a retainable generation keyed by
    /// <paramref name="newStateRoot"/> (the last committed state root): the state trie plus every
    /// warm storage trie. Called once at the scope boundary after a clean commit; the session then
    /// owns nothing, so its dispose is a no-op and the cache (or its rejection path) owns the tries.
    /// </summary>
    public RetainedGeneration ExtractGeneration(in ValueHash256 newStateRoot)
    {
        GuardPoisoned();
        // No state change ever materialized a trie; anchor an empty one so the generation is
        // still valid for the next block (which will reveal from the committed reader).
        _stateTrie ??= new SparseTrie(new FlatTrieNodeReader(_bundle, address: null), newStateRoot);

        RetainedGeneration generation = new(newStateRoot, _stateTrie, _storageTries);
        _stateTrie = null;
        _storageTries = [];
        _generationExtracted = true;
        return generation;
    }

    /// <summary>Converts staged records into the sealed <see cref="TrieNode"/> publication shape.</summary>
    /// <remarks>The staged array is already the final owned RLP; the explicit
    /// <see cref="CappedArray{T}"/> binds the adopting <see cref="TrieNode"/> constructor —
    /// a bare <c>byte[]</c> would bind the <see cref="ReadOnlySpan{T}"/> overload, which
    /// copies every array again.</remarks>
    internal static ArrayPoolListRef<(TreePath, TrieNode)> BuildPublicationBuffer(ReadOnlySpan<SparseTrieStagedNode> staged)
    {
        ArrayPoolListRef<(TreePath, TrieNode)> buffer = new(staged.Length);
        try
        {
            for (int i = 0; i < staged.Length; i++)
            {
                ref readonly SparseTrieStagedNode node = ref staged[i];
                buffer.Add((node.Path, new TrieNode(NodeType.Unknown, node.Hash.ToCommitment(), new CappedArray<byte>(node.Rlp))));
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private void GuardPoisoned()
    {
        if (_poisoned) ThrowPoisoned();

        [DoesNotReturn, StackTraceHidden]
        static void ThrowPoisoned() =>
            throw new InvalidOperationException("Sparse trie session is poisoned by an earlier calculation failure");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageJobsSealed() =>
        throw new InvalidOperationException("Storage jobs sealed after the storage phase would never be calculated");

    public void Dispose()
    {
        while (_pendingStorageJobs.TryDequeue(out StorageJob job)) job.Updates.Dispose();

        // A generation transferred to the cache owns its tries; the session must not touch them.
        // Otherwise (no cache, or a scope that never reached a clean commit) the session owns every
        // trie it built or checked out and releases them here: those still held by a tree touched
        // in an aborted, not-yet-published block, plus the warm set and the state trie.
        if (_generationExtracted) return;

        foreach (FlatStorageTree tree in _changedStorageTrees)
        {
            tree.DropSparseTrie();
        }

        _changedStorageTrees.Clear();
        _stateTrie?.Dispose();
        _stateTrie = null;

        foreach (KeyValuePair<Hash256AsKey, SparseTrie> kv in _storageTries)
        {
            kv.Value.Dispose();
        }

        _storageTries.Clear();
    }
}
