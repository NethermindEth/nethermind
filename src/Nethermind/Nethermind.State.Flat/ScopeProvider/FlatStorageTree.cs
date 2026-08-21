// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private readonly StorageTree _tree;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;

    // Sparse root state: the committed anchor until a job is calculated, EmptyTreeHash after a
    // clear, the calculated root afterwards. The trie itself exists only while a block has
    // uncommitted storage changes for this account.
    private SparseTrie? _sparseTrie;
    private Hash256 _sparseRoot;
    private Hash256? _diagnosticRoot;
    private bool _inChangedSet;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    public FlatStorageTree(
        FlatWorldStateScope scope,
        ITrieWarmer trieCacheWarmer,
        SnapshotBundle bundle,
        IFlatDbConfig config,
        ConcurrencyController concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _scope = scope;
        _trieCacheWarmer = trieCacheWarmer;
        _bundle = bundle;
        _address = address;
        _addressHash = address.ToAccountPath.ToHash256();
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

        StorageTrieStoreAdapter storageTrieAdapter = new(bundle, concurrencyQuota, _addressHash);
        _tree = new StorageTree(storageTrieAdapter, storageRoot, logManager)
        {
            RootHash = storageRoot
        };

        _sparseRoot = storageRoot;
        _config = config;
    }

    public Hash256 RootHash => _sparseRoot;

    internal Address Address => _address;

    internal Hash256 AddressHash => _addressHash;

    internal bool IsDisposed => _scope.IsDisposed;

    public byte[] Get(in UInt256 index)
    {
        byte[]? value = _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx);
        if (value is null || value.Length == 0)
        {
            value = StorageTree.ZeroBytes;
        }

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            byte[] treeValue = _tree.Get(index);
            if (!Bytes.AreEqual(treeValue, value))
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue?.ToHexString()} vs Flat: {value?.ToHexString()}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }

        return value!;
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index, byte[]? value) => WarmUpSlot(index);

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            int sequenceId = _scope.HintSequenceId;
            _scope.IncrementOutstandingWarmups();
            if (!_trieCacheWarmer.PushSlotJob(this, index, sequenceId)
                && !_trieCacheWarmer.PushSlotJobMpmc(this, index, sequenceId))
            {
                _scope.DecrementOutstandingWarmups();
            }
        }
    }

    // Called by trie warmer.
    public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
    {
        try
        {
            if (_scope.HintSequenceId != sequenceId || _scope._pausePrewarmer)
            {
                return false;
            }

            if (!_bundle.TryLeaseReadOnlyBundle())
            {
                return false;
            }

            try
            {
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);
                _scope.SparseSession.PrefetchStorage(_addressHash.ValueHash256, _sparseRoot.ValueHash256, in key);
                return true;
            }
            finally
            {
                _bundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    private void Set(UInt256 slot, byte[] value) => _bundle.SetChangedSlot(_address, slot, value);

    public void SelfDestruct()
    {
        ClearFlat();
        ResetSparseTrie();
        // A deleted account's storage is gone; drop any warm trie retained for it so it neither
        // leaks nor is reused if the account is later recreated.
        _scope.SparseSession.DiscardRetainedStorage(_addressHash);
    }

    /// <summary>Flat-plane clear only — safe from parallel flush workers, no sparse-trie access.</summary>
    /// <remarks>The sparse reset for a cleared batch happens on the storage-phase owner via the
    /// job's HasClear flag; an account deletion resets eagerly through <see cref="SelfDestruct"/>
    /// on the outer thread because its discarded job never reaches the phase.</remarks>
    private void ClearFlat()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    private void ResetSparseTrie()
    {
        _sparseTrie?.Dispose();
        _sparseTrie = null;
        _sparseRoot = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);

        // The diagnostic Patricia batch commits through the mutable tree exactly as the
        // pre-sparse pipeline did; its root is compared against the sparse root per job.
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch? diagnosticBatch = _config.VerifyWithTrie
            ? new(estimatedEntries, _tree, (_, patriciaRoot) => _diagnosticRoot = patriciaRoot, _address, commit: true)
            : null;

        return new SparseStorageWriteBatch(this, estimatedEntries, onRootUpdated, diagnosticBatch);
    }

    /// <summary>Applies one sealed job and returns its sparse trie for root calculation.</summary>
    internal SparseTrie ApplySparseJob(in FlatSparseTrieSession.StorageJob job, bool clearAlreadyApplied = false)
    {
        if (job.HasClear && !clearAlreadyApplied) ResetSparseTrie();

        // Reuse a warm trie retained for this account (rebound to this scope's reader) when the
        // retention cache holds one; otherwise build cold. A cleared account's anchor is empty,
        // so a stale retained trie is dropped rather than adopted.
        _sparseTrie ??= _scope.SparseSession.AdoptOrCreateStorageTrie(_addressHash, _sparseRoot.ValueHash256, job.Updates.Count);
        _sparseTrie.Apply(job.Updates.AsSpan());
        return _sparseTrie;
    }

    internal void AdoptSparseTrie(SparseTrie trie)
    {
        Debug.Assert(_sparseTrie is null);
        _sparseTrie = trie;
    }

    /// <summary>Validates and reports this account's calculated sparse root.</summary>
    /// <remarks>Pairs with <see cref="ApplySparseJob"/>: the returned trie must already have had
    /// its root calculated.</remarks>
    internal void CompleteSparseJob(in FlatSparseTrieSession.StorageJob job)
    {
        _sparseRoot = _sparseTrie!.RootHash.ToCommitment();
        if (_diagnosticRoot is not null)
        {
            if (_diagnosticRoot != _sparseRoot)
            {
                ThrowStorageRootMismatch(_address, _sparseRoot, _diagnosticRoot);
            }

            _diagnosticRoot = null;
        }

        job.OnRootUpdated(_address, _sparseRoot);
    }

    /// <summary>First-time registration into the session's changed set for publication.</summary>
    internal bool MarkInChangedSet()
    {
        if (_inChangedSet) return false;
        _inChangedSet = true;
        return true;
    }

    /// <summary>Publishes this block's staged storage nodes into the bundle.</summary>
    internal void PublishSparseNodes()
    {
        if (_sparseTrie is null) return;

        using ArrayPoolList<SparseTrieStagedNode> staged = new(_sparseTrie.UnpublishedNodeCapacityHint);
        _sparseTrie.DrainUnpublished(staged);
        if (staged.Count > 0)
        {
            using ArrayPoolListRef<(TreePath, TrieNode)> buffer =
                FlatSparseTrieSession.BuildPublicationBuffer(staged.AsSpan());
            _bundle.PublishStorageNodes(
                _bundle.GetStorageNodeDestination(_addressHash),
                _addressHash,
                buffer.AsSpan());
        }
    }

    /// <summary>Drops the trie without publishing; the session calls this after publication and on abort.</summary>
    internal void DropSparseTrie()
    {
        _sparseTrie?.Dispose();
        _sparseTrie = null;
        _inChangedSet = false;
    }

    /// <summary>Hands the warm trie to the session for reuse, leaving this tree without one;
    /// returns null when the account was deleted this block and its trie already dropped.</summary>
    internal SparseTrie? TakeSparseTrie()
    {
        SparseTrie? trie = _sparseTrie;
        _sparseTrie = null;
        _inChangedSet = false;
        return trie;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageRootMismatch(Address address, Hash256 sparseRoot, Hash256 diagnosticRoot) =>
        throw new TrieException($"Sparse storage root {sparseRoot} of {address} does not match the Patricia root {diagnosticRoot}");

    private class SparseStorageWriteBatch(
        FlatStorageTree storageTree,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated,
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch? diagnosticBatch) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly ArrayPoolList<SparseTrieUpdate> _updates = new(estimatedEntries);
        private ValueHash256 _keyBuff;
        private bool _hasClear;
        private bool _wasSetCalled;

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            StorageTree.ComputeKeyWithLookup(index, ref _keyBuff);
            PatriciaTree.BulkSetEntry entry = StorageTree.CreateBulkSetEntry(in _keyBuff, value);
            _updates.Add(new SparseTrieUpdate(entry.Path, entry.Value));

            diagnosticBatch?.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            if (_wasSetCalled) ThrowMustClearFirst();
            _hasClear = true;

            diagnosticBatch?.Clear();
            storageTree.ClearFlat();
        }

        public void Dispose()
        {
            try
            {
                diagnosticBatch?.Dispose();
            }
            catch
            {
                // The updates never transfer and nothing may calculate on top of the lost batch.
                _updates.Dispose();
                storageTree._scope.SparseSession.Poison();
                throw;
            }

            if (_wasSetCalled || _hasClear)
            {
                storageTree._scope.SparseSession.PrepareStorageJob(
                    new FlatSparseTrieSession.StorageJob(storageTree, _updates, _hasClear, onRootUpdated));
            }
            else
            {
                _updates.Dispose();
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowMustClearFirst() =>
            throw new InvalidOperationException("Must call clear first in a storage write batch");
    }

    // Trie-less scope: only the flat overlay is written; there is no storage trie to maintain.
    private sealed class FlatOverlayStorageWriteBatch(FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value) => storageTree.Set(index, value);

        public void Clear() => storageTree.SelfDestruct();

        public void Dispose() { }
    }
}
